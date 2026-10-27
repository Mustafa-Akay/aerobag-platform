using System.Text;
using AeroBag.Infrastructure;
using Npgsql;

namespace AeroBag.IntegrationTests;

/// <summary>Faz 4.3: uygulama rolünün yetkileri ve olay günlüğünün değiştirilemezliği. Ayrıca ham metinde kişisel veri kalmaması.</summary>
public class AppendOnlyTests(PostgresFixture fx) : IClassFixture<PostgresFixture>
{
    private static readonly LookupKeyHasher Hasher = new(Encoding.UTF8.GetBytes("test-secret-0123456789abcdef"));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    private static string Bsm(string tag, string pnr) =>
        $"BSM\n.V/1LAYT\n.F/ZZ0601/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/DEMIRCI/ZEYNEP\n.L/{pnr}\nENDBSM";

    private async Task<string> SeedAsync(NpgsqlDataSource db)
    {
        var tag = NewTag();
        var p = new BaggageEventProcessor(db, new FixedClock(new DateTimeOffset(2026, 10, 22, 14, 0, 0, TimeSpan.Zero)), Hasher);
        var r = await p.ProcessAsync(Bsm(tag, "QW7RTY"), "test");
        Assert.Equal(ProcessResult.Accepted, r.Status);
        return tag;
    }

    private static async Task<PostgresException> FailsAsync(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task App_role_can_write_events_but_cannot_change_or_delete_them()
    {
        await using var app = NpgsqlDataSource.Create(fx.AppConnectionString);
        await SeedAsync(app);

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await FailsAsync(app, "UPDATE baggage_events SET outcome = 'APPLIED'")).SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await FailsAsync(app, "DELETE FROM baggage_events")).SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await FailsAsync(app, "TRUNCATE baggage_events CASCADE")).SqlState);
    }

    [Fact]
    public async Task App_role_has_no_delete_anywhere_and_cannot_read_dead_letters_or_create_tables()
    {
        await using var app = NpgsqlDataSource.Create(fx.AppConnectionString);
        foreach (var table in new[] { "flights", "passengers", "bags", "outbox_messages", "exit_deadlines", "flight_context", "ml_bag_outcomes", "dead_letter_messages" })
        {
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await FailsAsync(app, $"DELETE FROM {table}")).SqlState);
        }
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await FailsAsync(app, "SELECT count(*) FROM dead_letter_messages")).SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await FailsAsync(app, "CREATE TABLE evil (x int)")).SqlState);
    }

    [Fact]
    public async Task Even_the_table_owner_cannot_rewrite_the_event_log()
    {
        await SeedAsync(fx.Db);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await FailsAsync(fx.Db, "UPDATE baggage_events SET outcome = 'APPLIED'")).SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await FailsAsync(fx.Db, "DELETE FROM baggage_events")).SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await FailsAsync(fx.Db, "TRUNCATE baggage_events CASCADE")).SqlState);
    }

    [Fact]
    public async Task Stored_raw_text_contains_no_name_or_pnr()
    {
        var tag = await SeedAsync(fx.Db);
        await using var cmd = fx.Db.CreateCommand(
            "SELECT e.raw_text FROM baggage_events e JOIN bags b ON b.bag_id = e.bag_id WHERE b.bag_tag_number = @t");
        cmd.Parameters.AddWithValue("t", tag);
        var raw = (string)(await cmd.ExecuteScalarAsync())!;

        Assert.Contains(tag, raw);
        Assert.DoesNotContain("DEMIRCI", raw);
        Assert.DoesNotContain("ZEYNEP", raw);
        Assert.DoesNotContain("QW7RTY", raw);
    }

    [Fact]
    public async Task Dead_letter_raw_text_contains_no_name_or_pnr()
    {
        var p = new BaggageEventProcessor(fx.Db, new FixedClock(DateTimeOffset.UtcNow), Hasher);
        var marker = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var broken = $"BSM\n.V/1LAYT\n.P/GIZLI{marker}/AD\n.L/ZX{marker[..4]}\n.BOZUK SATIR";
        var r = await p.ProcessAsync(broken, "test");
        Assert.Equal(ProcessResult.Rejected, r.Status);

        await using var cmd = fx.Db.CreateCommand("SELECT count(*) FROM dead_letter_messages WHERE raw_text LIKE @m OR error LIKE @m");
        cmd.Parameters.AddWithValue("m", $"%{marker[..4]}%");
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());

        await using var all = fx.Db.CreateCommand("SELECT count(*) FROM dead_letter_messages WHERE raw_text LIKE '%.P/***%'");
        Assert.True((long)(await all.ExecuteScalarAsync())! >= 1);
    }
}
