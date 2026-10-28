using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace AeroBag.Infrastructure.Migrations;

/// <summary>Tek bir göç dosyası. Özet satır sonlarından ve BOM'dan bağımsızdır.</summary>
public sealed record Migration(int Version, string Name, string Sql)
{
    public string Checksum { get; } = ComputeChecksum(Sql);

    public static string ComputeChecksum(string sql)
    {
        var normalized = sql.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    public override string ToString() => $"{Version:D4}_{Name}";
}

public sealed class MigrationException(string message) : Exception(message);

public sealed record MigrationResult(IReadOnlyList<Migration> Applied, int CurrentVersion);

public sealed record MigrationStatus(int? CurrentVersion, IReadOnlyList<Migration> Pending);

/// <summary>Göç listesini dosya adlarından kurar ve doğrular.</summary>
public static partial class MigrationSet
{
    /// <summary>Ad biçimi NNNN_ad.sql; sıra 1'den başlar, tekrar ve boşluk yoktur.</summary>
    public static IReadOnlyList<Migration> From(IEnumerable<(string FileName, string Sql)> files)
    {
        var list = new List<Migration>();
        foreach (var (fileName, sql) in files)
        {
            var m = FilePattern().Match(fileName);
            if (!m.Success)
            {
                throw new MigrationException($"Geçersiz göç dosyası adı: '{fileName}' (beklenen: 0001_kisa_ad.sql).");
            }
            list.Add(new Migration(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[2].Value, sql));
        }

        list.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Version != i + 1)
            {
                throw new MigrationException($"Göç sırası bozuk: {i + 1} bekleniyordu, {list[i]} bulundu (tekrar ya da boşluk var).");
            }
        }
        return list;
    }

    [GeneratedRegex(@"^(\d{4})_([a-z0-9_]+)\.sql$")]
    private static partial Regex FilePattern();
}

/// <summary>db/migrations klasöründeki dosyalar bu derlemeye gömülüdür (AeroBag.Infrastructure.csproj).</summary>
public static class EmbeddedMigrations
{
    private const string Prefix = "migrations.";
    private static readonly Lazy<IReadOnlyList<Migration>> All = new(LoadAll);

    public static IReadOnlyList<Migration> Load() => All.Value;

    public static int LatestVersion => All.Value.Count == 0 ? 0 : All.Value[^1].Version;

    private static IReadOnlyList<Migration> LoadAll()
    {
        var asm = typeof(EmbeddedMigrations).Assembly;
        var files = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(n => (n[Prefix.Length..], Read(asm, n)));
        return MigrationSet.From(files);
    }

    private static string Read(Assembly asm, string name)
    {
        using var stream = asm.GetManifestResourceStream(name) ?? throw new MigrationException($"Gömülü göç okunamadı: {name}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// Göçleri sırayla uygular. Tablo sahibi hesabıyla çalışır (uygulama rolü DDL yapamaz).
/// - Aynı anda iki göç aracı çalışmasın diye oturum düzeyinde danışma kilidi alınır.
/// - Her göç kendi işleminde uygulanır ve sürüm aynı işlemde kaydedilir; hata olursa hiçbir şey kalmaz.
/// - Uygulanmış bir göçün dosyası değişmişse ya da veritabanı koddan yeniyse durur.
/// - Göç tablosu olmayan ama tabloları olan eski bir veritabanında (db/init.sql dönemi) durur.
/// </summary>
public static class SchemaMigrator
{
    private const long LockKey = 0x4165526F426167; // "AeRoBag"

    public static async Task<MigrationResult> ApplyAsync(NpgsqlDataSource ownerDb, IReadOnlyList<Migration> migrations, CancellationToken ct = default)
    {
        await using var conn = await ownerDb.OpenConnectionAsync(ct);
        await ExecAsync(conn, null, "SELECT pg_advisory_lock(@k)", ct, ("k", LockKey));
        try
        {
            if (!await TableExistsAsync(conn, "schema_migrations", ct))
            {
                if (await TableExistsAsync(conn, "flights", ct))
                {
                    throw new MigrationException(
                        "Bu veritabanı göç sisteminden önce (db/init.sql ile) kurulmuş: tablolar var ama göç kaydı yok. " +
                        "Demo verisi olduğu için bir kez sıfırlayın: docker compose down -v ve docker compose up -d, sonra göç aracını çalıştırın.");
                }
                await ExecAsync(conn, null,
                    """
                    CREATE TABLE schema_migrations (
                      version         INT          PRIMARY KEY,
                      name            VARCHAR(100) NOT NULL,
                      checksum        CHAR(64)     NOT NULL,
                      applied_at_utc  TIMESTAMPTZ  NOT NULL DEFAULT now()
                    )
                    """, ct);
            }

            var applied = await AppliedAsync(conn, ct);
            Verify(applied, migrations);

            var done = new List<Migration>();
            foreach (var m in migrations.Where(m => !applied.ContainsKey(m.Version)))
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                try
                {
                    await using (var cmd = new NpgsqlCommand(m.Sql, conn, tx))
                    {
                        await cmd.ExecuteNonQueryAsync(ct);
                    }
                    await ExecAsync(conn, tx, "INSERT INTO schema_migrations (version, name, checksum) VALUES (@v, @n, @c)", ct,
                        ("v", m.Version), ("n", m.Name), ("c", m.Checksum));
                    await tx.CommitAsync(ct);
                }
                catch (PostgresException ex)
                {
                    await tx.RollbackAsync(ct);
                    throw new MigrationException($"{m} uygulanamadı, geri alındı: {ex.MessageText} (SQLSTATE {ex.SqlState}).");
                }
                done.Add(m);
            }

            return new MigrationResult(done, migrations.Count == 0 ? 0 : migrations[^1].Version);
        }
        finally
        {
            await ExecAsync(conn, null, "SELECT pg_advisory_unlock(@k)", CancellationToken.None, ("k", LockKey));
        }
    }

    public static async Task<MigrationStatus> StatusAsync(NpgsqlDataSource db, IReadOnlyList<Migration> migrations, CancellationToken ct = default)
    {
        var current = await CurrentVersionAsync(db, ct);
        return new MigrationStatus(current, migrations.Where(m => current is null || m.Version > current).ToList());
    }

    /// <summary>Uygulanmış en yüksek sürüm; göç tablosu yoksa null. Uygulama rolüyle de çalışır (yalnızca SELECT).</summary>
    public static async Task<int?> CurrentVersionAsync(NpgsqlDataSource db, CancellationToken ct = default)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        if (!await TableExistsAsync(conn, "schema_migrations", ct))
        {
            return null;
        }
        await using var cmd = new NpgsqlCommand("SELECT max(version) FROM schema_migrations", conn);
        return await cmd.ExecuteScalarAsync(ct) is int v ? v : 0;
    }

    private static void Verify(IReadOnlyDictionary<int, string> applied, IReadOnlyList<Migration> migrations)
    {
        var byVersion = migrations.ToDictionary(m => m.Version);
        foreach (var (version, checksum) in applied.OrderBy(kv => kv.Key))
        {
            if (!byVersion.TryGetValue(version, out var m))
            {
                throw new MigrationException($"Veritabanında {version} numaralı göç uygulanmış ama kodda yok: veritabanı koddan yeni.");
            }
            if (!string.Equals(m.Checksum, checksum.Trim(), StringComparison.Ordinal))
            {
                throw new MigrationException($"{m} uygulandıktan sonra değiştirilmiş (özet uyuşmuyor). Uygulanmış göç değiştirilmez; yeni bir göç dosyası ekleyin.");
            }
        }
    }

    private static async Task<Dictionary<int, string>> AppliedAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var result = new Dictionary<int, string>();
        await using var cmd = new NpgsqlCommand("SELECT version, checksum FROM schema_migrations", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            result[r.GetInt32(0)] = r.GetString(1);
        }
        return result;
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection conn, string table, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", conn);
        cmd.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Text) { Value = $"public.{table}" });
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct, params (string Name, object Value)[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
