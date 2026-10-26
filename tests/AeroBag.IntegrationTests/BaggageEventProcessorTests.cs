using AeroBag.Infrastructure;

namespace AeroBag.IntegrationTests;

public class BaggageEventProcessorTests(PostgresFixture fx) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private BaggageEventProcessor NewProcessor() =>
        new(fx.Db, new FixedClock(Now), new LookupKeyHasher(System.Text.Encoding.UTF8.GetBytes("test-secret-0123456789abcdef")));

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    private static string Bsm(string tag) =>
        $"BSM\n.V/1LAYT\n.F/ZZ0124/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/AB3K7Q\nENDBSM";

    private static string Bpm(string tag, string point, string time = "212100") =>
        $"BPM\n.V/1LAYT\n.J/S/{point}/22OCT/{time}Z\n.F/ZZ0124/22OCT/AYT\n.N/{tag}001\nENDBPM";

    private async Task<long> CountAsync(string sql, string? tag = null)
    {
        await using var cmd = fx.Db.CreateCommand(sql);
        if (tag is not null)
        {
            cmd.Parameters.AddWithValue("t", tag);
        }
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<string?> ScalarStringAsync(string sql, string tag)
    {
        await using var cmd = fx.Db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("t", tag);
        return (await cmd.ExecuteScalarAsync()) as string;
    }

    private const string EventCountSql =
        "SELECT count(*) FROM baggage_events e JOIN bags b ON b.bag_id = e.bag_id WHERE b.bag_tag_number = @t";

    [Fact]
    public async Task Bsm_creates_bag_event_and_outbox_row()
    {
        var tag = NewTag();
        var result = await NewProcessor().ProcessAsync(Bsm(tag), "test");

        Assert.Equal(ProcessResult.Accepted, result.Status);
        Assert.Equal("APPLIED", result.Outcome);
        Assert.Equal("CHECKED_IN", result.ToState);
        Assert.Equal("CHECKED_IN",
            await ScalarStringAsync("SELECT current_state FROM bags WHERE bag_tag_number = @t", tag));
        Assert.Equal(1, await CountAsync(EventCountSql, tag));
        Assert.Equal(1, await CountAsync(
            "SELECT count(*) FROM outbox_messages WHERE partition_key = @t AND processed_at_utc IS NULL", tag));
    }

    [Fact]
    public async Task Same_message_twice_is_stored_once()
    {
        var tag = NewTag();
        var p = NewProcessor();

        var first = await p.ProcessAsync(Bsm(tag), "test");
        var second = await p.ProcessAsync(Bsm(tag), "test");

        Assert.Equal(ProcessResult.Accepted, first.Status);
        Assert.Equal(ProcessResult.Duplicate, second.Status);
        Assert.Equal(1, await CountAsync(EventCountSql, tag));
    }

    [Fact]
    public async Task Out_of_order_scan_is_logged_but_does_not_move_state_backwards()
    {
        var tag = NewTag();
        var p = NewProcessor();

        var chute = await p.ProcessAsync(Bpm(tag, "CHUTE04"), "test");
        var security = await p.ProcessAsync(Bpm(tag, "SEC01", "212200"), "test");

        Assert.Equal("ANOMALY", chute.Outcome);
        Assert.Equal("IGNORED_STALE", security.Outcome);
        Assert.Equal("INBOUND_CHUTE",
            await ScalarStringAsync("SELECT current_state FROM bags WHERE bag_tag_number = @t", tag));
        Assert.Equal(2, await CountAsync(EventCountSql, tag));
    }

    [Fact]
    public async Task Claiming_from_belt_opens_a_pending_exit_deadline()
    {
        var tag = NewTag();
        var p = NewProcessor();

        await p.ProcessAsync(Bsm(tag), "test");
        await p.ProcessAsync(Bpm(tag, "CLAIM04"), "test");

        Assert.Equal(1, await CountAsync(
            """
            SELECT count(*) FROM exit_deadlines d JOIN bags b ON b.bag_id = d.bag_id
            WHERE b.bag_tag_number = @t AND d.status = 'PENDING'
              AND d.due_at_utc = TIMESTAMPTZ '2026-10-22 21:24:00+00'
            """, tag));
    }

    [Fact]
    public async Task Unparseable_message_goes_to_dead_letter_and_is_rejected()
    {
        var before = await CountAsync("SELECT count(*) FROM dead_letter_messages");

        var result = await NewProcessor().ProcessAsync("BSM\n.V/1LAYT\nENDBSM", "test");

        Assert.Equal(ProcessResult.Rejected, result.Status);
        Assert.Contains("MissingField", result.Error);
        Assert.Equal(before + 1, await CountAsync("SELECT count(*) FROM dead_letter_messages"));
    }

    [Fact]
    public async Task Concurrent_duplicates_are_stored_exactly_once()
    {
        var tag = NewTag();
        var message = Bsm(tag);

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => NewProcessor().ProcessAsync(message, "test")));

        Assert.Equal(1, results.Count(r => r.Status == ProcessResult.Accepted));
        Assert.Equal(19, results.Count(r => r.Status == ProcessResult.Duplicate));
        Assert.Equal(1, await CountAsync(EventCountSql, tag));
    }
}
