using System.Text;
using AeroBag.Application.Ml;
using AeroBag.Application.Time;
using AeroBag.Infrastructure;

namespace AeroBag.IntegrationTests;

/// <summary>
/// W2: sorgular "şimdi"yi operasyon saatinden alır. Sabit saatle aynı veri farklı "şimdi"lerde farklı ve doğru sonuç verir;
/// bu, gerçek saatle (Clock:Mode=real) çalışmanın da doğru olduğunu gösterir.
/// </summary>
public class OperationalClockTests(PostgresFixture fx) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Wall = new(2026, 10, 22, 23, 0, 0, TimeSpan.Zero);
    private static readonly LookupKeyHasher Hasher = new(Encoding.UTF8.GetBytes("test-secret-0123456789abcdef"));

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DateTimeOffset At(string hhmmss) => DateTimeOffset.ParseExact($"20261022{hhmmss}", "yyyyMMddHHmmss",
        System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    private BaggageEventProcessor Processor() => new(fx.Db, new FixedTime(Wall), Hasher);

    private async Task<string> BagAsync(string flightNo, params (string Point, string Time)[] scans)
    {
        var tag = NewTag();
        await Processor().ProcessAsync($"BSM\n.V/1LAYT\n.F/{flightNo}/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/CLK123\nENDBSM", "test");
        foreach (var (point, time) in scans)
        {
            await Processor().ProcessAsync($"BPM\n.V/1LAYT\n.J/S/{point}/22OCT/{time}Z\n.F/{flightNo}/22OCT/AYT\n.N/{tag}001\nENDBPM", "test");
        }
        return tag;
    }

    [Fact]
    public async Task Event_clock_is_the_latest_non_bsm_event_time()
    {
        await BagAsync("ZZ0901", ("CAROUSEL01", "120000"));
        var now = await new EventDerivedClock(fx.Db).NowAsync();
        Assert.NotNull(now);
        Assert.True(now >= At("120000"));
    }

    [Fact]
    public async Task Stalled_count_follows_the_given_clock()
    {
        await BagAsync("ZZ0902", ("CAROUSEL01", "100000"));

        var before = (await new OpsQueries(fx.Db, new FixedOperationalClock(At("101900"))).FlightsAsync()).Single(f => f.FlightId == "ZZ0902-20261022");
        var after = (await new OpsQueries(fx.Db, new FixedOperationalClock(At("102100"))).FlightsAsync()).Single(f => f.FlightId == "ZZ0902-20261022");
        var noClock = (await new OpsQueries(fx.Db, new FixedOperationalClock(null)).FlightsAsync()).Single(f => f.FlightId == "ZZ0902-20261022");

        Assert.Equal(0, before.Stalled);
        Assert.Equal(1, after.Stalled);
        Assert.Equal(0, noClock.Stalled);
    }

    [Fact]
    public async Task Exit_deadline_worker_uses_the_given_clock()
    {
        var tag = await BagAsync("ZZ0903", ("CLAIM01", "110000"));

        var early = new ExitDeadlineService(fx.Db, new FixedTime(Wall), new FixedOperationalClock(At("110259")));
        var late = new ExitDeadlineService(fx.Db, new FixedTime(Wall), new FixedOperationalClock(At("110301")));

        await early.ProcessDueAsync();
        Assert.Equal("CLAIMED_FROM_BELT", await StateAsync(tag));
        await late.ProcessDueAsync();
        Assert.Equal("EXIT_OVERDUE", await StateAsync(tag));
    }

    [Fact]
    public async Task Sla_elapsed_time_comes_from_the_given_clock()
    {
        var id = "ZZ0904-20261022";
        await BagAsync("ZZ0904", ("LOAD01", "080000"));
        await new FlightContextStore(fx.Db).UpsertAsync(new FlightContext(id, At("120000"), false, false, 1, 500, 50, 0.5, 2, "HANDLER_A"));

        var sla = (await new SlaQueries(fx.Db, new NullMlPredictor(), new FixedOperationalClock(At("121000"))).GetAsync()).Single(f => f.FlightId == id);
        Assert.Equal(600, sla.ElapsedSec);
    }

    [Fact]
    public async Task Pending_ml_outcomes_wait_ten_minutes_after_the_last_carousel_by_the_given_clock()
    {
        var id = "ZZ0905-20261022";
        await BagAsync("ZZ0905", ("CAROUSEL01", "130000"));
        var store = new FlightContextStore(fx.Db);
        await store.UpsertAsync(new FlightContext(id, At("124500"), false, false, 1, 500, 50, 0.5, 2, "HANDLER_A"));

        Assert.DoesNotContain(await store.PendingOutcomesAsync(At("130959")), p => p.Context.FlightId == id);
        Assert.Contains(await store.PendingOutcomesAsync(At("131000")), p => p.Context.FlightId == id);
    }

    [Fact]
    public async Task Passenger_eta_is_relative_to_the_given_clock()
    {
        var tag = NewTag();
        var pnr = "Q" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        await Processor().ProcessAsync($"BSM\n.V/1LAYT\n.F/ZZ0906/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/KAYA/ALI\n.L/{pnr}\nENDBSM", "test");
        await Processor().ProcessAsync($"BUM\n.V/1LAYT\n.F/ZZ0906/22OCT/AYT\n.N/{tag}001\n.U/STAND104/20261022T140000Z\nENDBUM", "test");

        var queries = new PassengerQueries(fx.Db, Hasher, new EtaStatistics(fx.Db), new FixedTime(Wall),
            operationalClock: new FixedOperationalClock(At("140100")));
        var result = await queries.LookupAsync(pnr, "KAYA");

        Assert.NotNull(result);
        Assert.Equal(At("140100"), result.AsOfUtc);
        var eta = Assert.Single(result.Bags).Eta;
        Assert.NotNull(eta);
        Assert.Equal((int)(eta.P50Utc - At("140100")).TotalSeconds, eta.P50SecondsFromNow);
    }

    private async Task<string> StateAsync(string tag)
    {
        await using var cmd = fx.Db.CreateCommand("SELECT current_state FROM bags WHERE bag_tag_number = @t");
        cmd.Parameters.AddWithValue("t", tag);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }
}
