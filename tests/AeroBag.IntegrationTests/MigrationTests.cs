using AeroBag.Infrastructure.Migrations;
using Npgsql;

namespace AeroBag.IntegrationTests;

/// <summary>W1: göç aracı. Her test kapsayıcıda kendine ait, boş bir veritabanı açar.</summary>
public class MigrationTests(PostgresFixture fx) : IClassFixture<PostgresFixture>
{
    private async Task<NpgsqlDataSource> FreshDatabaseAsync()
    {
        var name = "mig_" + Guid.NewGuid().ToString("N")[..12];
        await using (var cmd = fx.Db.CreateCommand($"CREATE DATABASE {name}"))
        {
            await cmd.ExecuteNonQueryAsync();
        }
        return NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(fx.ConnectionString) { Database = name }.ConnectionString);
    }

    private static readonly Migration[] Synthetic =
    [
        new(1, "first", "CREATE TABLE a (x int);"),
        new(2, "second", "CREATE TABLE b (y int);"),
        new(3, "third", "ALTER TABLE a ADD COLUMN z int;"),
    ];

    private static async Task<bool> TableExistsAsync(NpgsqlDataSource db, string table)
    {
        await using var cmd = db.CreateCommand("SELECT to_regclass(@t) IS NOT NULL");
        cmd.Parameters.AddWithValue("t", table);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Real_migrations_build_the_schema_and_a_second_run_does_nothing()
    {
        await using var db = await FreshDatabaseAsync();
        var first = await SchemaMigrator.ApplyAsync(db, EmbeddedMigrations.Load());
        var second = await SchemaMigrator.ApplyAsync(db, EmbeddedMigrations.Load());

        Assert.Equal(EmbeddedMigrations.LatestVersion, first.Applied.Count);
        Assert.Empty(second.Applied);
        Assert.Equal(EmbeddedMigrations.LatestVersion, await SchemaMigrator.CurrentVersionAsync(db));
        Assert.True(await TableExistsAsync(db, "baggage_events"));
    }

    [Fact]
    public async Task Upgrading_from_0001_to_0002_keeps_flight_context_rows_without_a_carousel()
    {
        await using var db = await FreshDatabaseAsync();
        await SchemaMigrator.ApplyAsync(db, EmbeddedMigrations.Load().Take(1).ToList());
        await using (var ins = db.CreateCommand(
            """
            INSERT INTO flight_context (flight_id, on_block_utc, wide, rain, bags, stand_m, chute_m, speed_mps, arrivals_30, handler)
            VALUES ('ZZ0001-20261022', '2026-10-22T12:00:00Z', false, false, 10, 500, 50, 0.5, 2, 'HANDLER_A')
            """))
        {
            await ins.ExecuteNonQueryAsync();
        }

        var result = await SchemaMigrator.ApplyAsync(db, EmbeddedMigrations.Load());

        Assert.Contains(result.Applied, m => m.Version == 2);
        await using var sel = db.CreateCommand("SELECT bags, carousel FROM flight_context WHERE flight_id = 'ZZ0001-20261022'");
        await using var r = await sel.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(10, r.GetInt32(0));
        Assert.True(r.IsDBNull(1));
    }

    [Fact]
    public async Task Only_pending_migrations_are_applied_and_data_is_kept()
    {
        await using var db = await FreshDatabaseAsync();
        await SchemaMigrator.ApplyAsync(db, Synthetic[..2]);
        await using (var ins = db.CreateCommand("INSERT INTO a (x) VALUES (42)"))
        {
            await ins.ExecuteNonQueryAsync();
        }

        var result = await SchemaMigrator.ApplyAsync(db, Synthetic);

        Assert.Equal(new[] { 3 }, result.Applied.Select(m => m.Version));
        await using var sel = db.CreateCommand("SELECT x FROM a");
        Assert.Equal(42, await sel.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Changed_applied_migration_stops_the_tool()
    {
        await using var db = await FreshDatabaseAsync();
        await SchemaMigrator.ApplyAsync(db, Synthetic[..1]);

        var tampered = new[] { new Migration(1, "first", "CREATE TABLE a (x bigint);") };
        var ex = await Assert.ThrowsAsync<MigrationException>(() => SchemaMigrator.ApplyAsync(db, tampered));
        Assert.Contains("değiştirilmiş", ex.Message);
    }

    [Fact]
    public async Task Line_ending_differences_do_not_count_as_a_change()
    {
        await using var db = await FreshDatabaseAsync();
        await SchemaMigrator.ApplyAsync(db, [new Migration(1, "first", "CREATE TABLE a (x int);\nCREATE TABLE c (y int);")]);
        var result = await SchemaMigrator.ApplyAsync(db, [new Migration(1, "first", "﻿CREATE TABLE a (x int);\r\nCREATE TABLE c (y int);")]);
        Assert.Empty(result.Applied);
    }

    [Fact]
    public async Task Failing_migration_leaves_nothing_behind()
    {
        await using var db = await FreshDatabaseAsync();
        Migration[] broken = [Synthetic[0], new(2, "broken", "CREATE TABLE b (y int); SELECT * FROM no_such_table;")];

        var ex = await Assert.ThrowsAsync<MigrationException>(() => SchemaMigrator.ApplyAsync(db, broken));
        Assert.Contains("geri alındı", ex.Message);
        Assert.False(await TableExistsAsync(db, "b"));
        Assert.Equal(1, await SchemaMigrator.CurrentVersionAsync(db));
    }

    [Fact]
    public async Task Database_newer_than_the_code_stops_the_tool()
    {
        await using var db = await FreshDatabaseAsync();
        await SchemaMigrator.ApplyAsync(db, Synthetic);
        var ex = await Assert.ThrowsAsync<MigrationException>(() => SchemaMigrator.ApplyAsync(db, Synthetic[..2]));
        Assert.Contains("koddan yeni", ex.Message);
    }

    [Fact]
    public async Task Legacy_database_created_with_init_sql_is_detected()
    {
        await using var db = await FreshDatabaseAsync();
        await using (var cmd = db.CreateCommand("CREATE TABLE flights (flight_id varchar(40) PRIMARY KEY)"))
        {
            await cmd.ExecuteNonQueryAsync();
        }
        var ex = await Assert.ThrowsAsync<MigrationException>(() => SchemaMigrator.ApplyAsync(db, Synthetic));
        Assert.Contains("docker compose down -v", ex.Message);
    }

    [Fact]
    public async Task App_role_can_read_the_version_but_cannot_migrate()
    {
        // Fikstürün veritabanı göçlerle kurulmuştur.
        await using var app = NpgsqlDataSource.Create(fx.AppConnectionString);
        Assert.Equal(EmbeddedMigrations.LatestVersion, await SchemaMigrator.CurrentVersionAsync(app));

        await using var cmd = app.CreateCommand("CREATE TABLE evil (x int)");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }
}
