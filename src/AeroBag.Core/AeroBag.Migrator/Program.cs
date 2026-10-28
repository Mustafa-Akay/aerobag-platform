using AeroBag.Infrastructure.Migrations;
using Npgsql;

// AeroBag göç aracı: db/migrations altındaki göçleri tablo sahibi hesabıyla uygular.
// Bağlantı: --connection "<dizi>" ya da AEROBAG_MIGRATION_CONNECTION; ikisi de yoksa yalnızca yerel demo için olan varsayılan.
// Seçenekler: --status (yalnızca listeler, değiştirmez).

const string DemoOwnerConnection = "Host=localhost;Port=5433;Database=aerobag;Username=aerobag;Password=aerobag_dev";

var connection = Arg("--connection") ?? Environment.GetEnvironmentVariable("AEROBAG_MIGRATION_CONNECTION") ?? DemoOwnerConnection;
var statusOnly = args.Contains("--status");

try
{
    var migrations = EmbeddedMigrations.Load();
    await using var db = NpgsqlDataSource.Create(connection);
    var target = new NpgsqlConnectionStringBuilder(connection);
    Console.WriteLine($"Veritabanı: {target.Host}:{target.Port}/{target.Database} (kullanıcı {target.Username}); koddaki son göç {EmbeddedMigrations.LatestVersion}.");

    if (statusOnly)
    {
        var status = await SchemaMigrator.StatusAsync(db, migrations);
        Console.WriteLine(status.CurrentVersion is null ? "Göç tablosu yok (boş ya da eski veritabanı)." : $"Uygulanmış son göç: {status.CurrentVersion}.");
        foreach (var m in status.Pending)
        {
            Console.WriteLine($"  bekliyor: {m}");
        }
        if (status.Pending.Count == 0)
        {
            Console.WriteLine("Bekleyen göç yok.");
        }
        return 0;
    }

    var result = await SchemaMigrator.ApplyAsync(db, migrations);
    foreach (var m in result.Applied)
    {
        Console.WriteLine($"  uygulandı: {m}");
    }
    Console.WriteLine(result.Applied.Count == 0
        ? $"Şema güncel (sürüm {result.CurrentVersion})."
        : $"{result.Applied.Count} göç uygulandı; şema sürümü {result.CurrentVersion}.");
    return 0;
}
catch (MigrationException ex)
{
    Console.Error.WriteLine($"Göç durdu: {ex.Message}");
    return 1;
}
catch (NpgsqlException ex) when (ex is not PostgresException)
{
    Console.Error.WriteLine($"Veritabanına bağlanılamadı ({ex.Message}). docker compose up -d çalışıyor mu?");
    return 2;
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
