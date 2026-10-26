using System.Text;
using AeroBag.Infrastructure;

namespace AeroBag.IntegrationTests;

public class ExitDeadlineTests(PostgresFixture fx) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Wall = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly LookupKeyHasher Hasher = new(Encoding.UTF8.GetBytes("test-secret-0123456789abcdef"));

    private BaggageEventProcessor Processor() => new(fx.Db, new FixedClock(Wall), Hasher);

    private ExitDeadlineService Service() => new(fx.Db, new FixedClock(Wall));

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    private static string Bsm(string tag) =>
        $"BSM\n.V/1LAYT\n.F/ZZ0777/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/AB3K7Q\nENDBSM";

    private static string Bpm(string tag, string point, string time) =>
        $"BPM\n.V/1LAYT\n.J/S/{point}/22OCT/{time}Z\n.F/ZZ0777/22OCT/AYT\n.N/{tag}001\nENDBPM";

    private static DateTimeOffset At(string hhmmss) =>
        new(2026, 10, 22, int.Parse(hhmmss[..2]), int.Parse(hhmmss[2..4]), int.Parse(hhmmss[4..]), TimeSpan.Zero);

    private async Task<string?> ScalarAsync(string sql, string tag)
    {
        await using var cmd = fx.Db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("t", tag);
        return (await cmd.ExecuteScalarAsync())?.ToString();
    }

    private Task<string?> StateAsync(string tag) =>
        ScalarAsync("SELECT current_state FROM bags WHERE bag_tag_number = @t", tag);

    private Task<string?> DeadlineAsync(string tag) =>
        ScalarAsync("SELECT d.status FROM exit_deadlines d JOIN bags b ON b.bag_id = d.bag_id WHERE b.bag_tag_number = @t", tag);

    private async Task<string> ClaimedBagAsync(string claimTime)
    {
        var tag = NewTag();
        await Processor().ProcessAsync(Bsm(tag), "test");
        await Processor().ProcessAsync(Bpm(tag, "CLAIM01", claimTime), "test");
        return tag;
    }

    [Fact]
    public async Task Bag_not_read_at_exit_becomes_overdue_once_the_deadline_has_passed()
    {
        var tag = await ClaimedBagAsync("100000");   // süre 10:03:00'da dolar

        await Service().ProcessDueAsync(At("100259"), 100);
        Assert.Equal("CLAIMED_FROM_BELT", await StateAsync(tag));
        Assert.Equal("PENDING", await DeadlineAsync(tag));

        await Service().ProcessDueAsync(At("100300"), 100);
        Assert.Equal("EXIT_OVERDUE", await StateAsync(tag));
        Assert.Equal("OVERDUE", await DeadlineAsync(tag));
        Assert.Equal("1", await ScalarAsync(
            "SELECT count(*) FROM baggage_events e JOIN bags b ON b.bag_id = e.bag_id WHERE b.bag_tag_number = @t AND e.to_state = 'EXIT_OVERDUE'", tag));
        Assert.Equal("1", await ScalarAsync(
            "SELECT count(*) FROM outbox_messages WHERE partition_key = @t AND payload->>'toState' = 'EXIT_OVERDUE'", tag));
    }

    [Fact]
    public async Task Running_the_worker_again_does_not_create_a_second_overdue_event()
    {
        var tag = await ClaimedBagAsync("110000");

        await Service().ProcessDueAsync(At("111000"), 100);
        await Service().ProcessDueAsync(At("112000"), 100);

        Assert.Equal("1", await ScalarAsync(
            "SELECT count(*) FROM baggage_events e JOIN bags b ON b.bag_id = e.bag_id WHERE b.bag_tag_number = @t AND e.to_state = 'EXIT_OVERDUE'", tag));
    }

    [Fact]
    public async Task Bag_read_at_exit_in_time_is_cleared_and_never_overdue()
    {
        var tag = await ClaimedBagAsync("120000");
        await Processor().ProcessAsync(Bpm(tag, "EXIT01", "120130"), "test");

        Assert.Equal("EXIT_CLEARED", await StateAsync(tag));
        Assert.Equal("CLEARED", await DeadlineAsync(tag));

        await Service().ProcessDueAsync(At("123000"), 100);
        Assert.Equal("EXIT_CLEARED", await StateAsync(tag));
    }

    [Fact]
    public async Task Late_exit_read_after_overdue_clears_the_bag()
    {
        var tag = await ClaimedBagAsync("130000");
        await Service().ProcessDueAsync(At("131000"), 100);
        Assert.Equal("EXIT_OVERDUE", await StateAsync(tag));

        await Processor().ProcessAsync(Bpm(tag, "EXIT01", "131500"), "test");

        Assert.Equal("EXIT_CLEARED", await StateAsync(tag));
        Assert.Equal("CLEARED", await DeadlineAsync(tag));
    }

    [Fact]
    public async Task Virtual_clock_is_the_latest_non_bsm_event_time()
    {
        var tag = await ClaimedBagAsync("140000");
        var now = await Service().VirtualNowAsync();
        Assert.NotNull(now);
        Assert.True(now >= At("140000"));
    }

    [Fact]
    public async Task Flight_summary_counts_exit_read_and_exit_overdue_inside_claimed()
    {
        var overdue = await ClaimedBagAsync("150000");
        var cleared = await ClaimedBagAsync("150000");
        await Processor().ProcessAsync(Bpm(cleared, "EXIT01", "150100"), "test");
        await Service().ProcessDueAsync(At("153000"), 100);

        var f = (await new OpsQueries(fx.Db).FlightsAsync()).Single(x => x.FlightId == "ZZ0777-20261022");

        Assert.Equal("EXIT_OVERDUE", await StateAsync(overdue));
        Assert.True(f.ExitOverdue >= 1 && f.ExitRead >= 1);
        Assert.True(f.Claimed >= f.ExitOverdue + f.ExitRead);
    }
}
