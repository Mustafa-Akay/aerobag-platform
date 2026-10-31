using Npgsql;
using NpgsqlTypes;

namespace AeroBag.Infrastructure.Db;

/// <summary>
/// Npgsql komut ve parametre kurucuları. Sınıflar bunları <c>using static</c> ile kısa adlarıyla kullanır
/// (<c>P</c>, <c>V</c>, <c>PB</c>, <c>J</c>, <c>Cmd</c>). Davranış, W12 öncesindeki sınıf içi kopyalarla aynıdır.
/// </summary>
public static class DbParams
{
    /// <summary>Tipi değerden çıkarılan parametre; null → <see cref="DBNull"/>.</summary>
    public static NpgsqlParameter P(string name, object? value) => new(name, value ?? DBNull.Value);

    /// <summary>Boş olabilen metin parametresi (tipi açık: COALESCE içinde de doğru çözülür).</summary>
    public static NpgsqlParameter V(string name, string? value) =>
        new(name, NpgsqlDbType.Varchar) { Value = (object?)value ?? DBNull.Value };

    /// <summary>Bayt dizisi (HMAC anahtarları).</summary>
    public static NpgsqlParameter PB(string name, byte[] bytes) => new(name, NpgsqlDbType.Bytea) { Value = bytes };

    /// <summary>JSONB parametresi; değer önceden serileştirilmiş JSON metnidir.</summary>
    public static NpgsqlParameter J(string name, string json) => new(name, NpgsqlDbType.Jsonb) { Value = json };

    /// <summary>Verilen işlem içinde, parametreleri eklenmiş komut. Çağıran <c>await using</c> ile kapatır.</summary>
    public static NpgsqlCommand Cmd(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, params NpgsqlParameter[] parameters)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddRange(parameters);
        return cmd;
    }

    /// <summary>Sonuç döndürmeyen komutu işlem içinde çalıştırır.</summary>
    public static async Task ExecAsync(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct, string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var cmd = Cmd(conn, tx, sql, parameters);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
