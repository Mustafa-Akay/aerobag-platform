using AeroBag.Infrastructure.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AeroBag.IntegrationTests;

/// <summary>Gerçek bir PostgreSQL konteyneri başlatır ve şemayı göç aracıyla (db/migrations) kurar. Docker gerekir.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public NpgsqlDataSource Db { get; private set; } = null!;

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>API'nin kullandığı kısıtlı rol (db/migrations/0001_init.sql: aerobag_app). Yetkilerin yeterli olduğunu API testleri doğrular.</summary>
    public string AppConnectionString => new NpgsqlConnectionStringBuilder(ConnectionString) { Username = "aerobag_app", Password = "aerobag_app_dev" }.ConnectionString;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Db = NpgsqlDataSource.Create(_container.GetConnectionString());

        await SchemaMigrator.ApplyAsync(Db, EmbeddedMigrations.Load());
    }

    public async Task DisposeAsync()
    {
        if (Db is not null)
        {
            await Db.DisposeAsync();
        }
        await _container.DisposeAsync();
    }
}
