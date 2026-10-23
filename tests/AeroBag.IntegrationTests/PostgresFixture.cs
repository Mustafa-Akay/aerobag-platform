using Npgsql;
using Testcontainers.PostgreSql;

namespace AeroBag.IntegrationTests;

/// <summary>Gerçek bir PostgreSQL konteyneri başlatır ve db/init.sql şemasını uygular. Docker gerekir.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    public NpgsqlDataSource Db { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Db = NpgsqlDataSource.Create(_container.GetConnectionString());

        var sql = await File.ReadAllTextAsync(FindInitSql());
        await using var cmd = Db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (Db is not null)
        {
            await Db.DisposeAsync();
        }
        await _container.DisposeAsync();
    }

    private static string FindInitSql()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "db", "init.sql");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("db/init.sql bulunamadı.");
    }
}
