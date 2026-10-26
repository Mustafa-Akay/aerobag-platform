using System.Text;
using AeroBag.Application.Ml;
using AeroBag.Infrastructure;

namespace AeroBag.IntegrationTests;

public class MlLookupTests(PostgresFixture fx) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeMl(Func<FlightContext, MlFlightForecast?> flight, Func<FlightContext, BagDurationForecast?>? bag = null) : IMlPredictor
    {
        public int FlightCalls { get; private set; }
        public int BagCalls { get; private set; }

        public Task<MlFlightForecast?> PredictAsync(FlightContext ctx, CancellationToken ct = default)
        {
            FlightCalls++;
            return Task.FromResult(flight(ctx));
        }

        public Task<BagDurationForecast?> PredictBagAsync(FlightContext ctx, CancellationToken ct = default)
        {
            BagCalls++;
            return Task.FromResult(bag?.Invoke(ctx));
        }

        public Task<int> ReportBagOutcomesAsync(IReadOnlyList<BagOutcome> items, CancellationToken ct = default) =>
            Task.FromResult(items.Count);

        public Task<bool> ReportOutcomeAsync(string flightId, double firstMin, double lastMin, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private static readonly LookupKeyHasher Hasher = new(Encoding.UTF8.GetBytes("test-secret-0123456789abcdef"));

    private static readonly MlFlightForecast Forecast = new(
        new MinuteQuantiles(10, 13, 18), new MinuteQuantiles(4, 7, 12), new MinuteQuantiles(16, 20, 28));

    private static readonly BagDurationForecast BagForecast = new(240, 330, 480);

    private BaggageEventProcessor Processor() => new(fx.Db, new FixedClock(Now), Hasher);

    private PassengerQueries Queries(IMlPredictor ml) =>
        new(fx.Db, Hasher, new EtaStatistics(fx.Db), new FixedClock(Now), ml, new FlightContextStore(fx.Db));

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    private static string NewPnr() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static string Bsm(string flightNo, string tag, string pnr) =>
        $"BSM\n.V/1LAYT\n.F/{flightNo}/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/{pnr}\nENDBSM";

    private static string Bum(string flightNo, string tag, string hhmmss = "121000") =>
        $"BUM\n.V/1LAYT\n.F/{flightNo}/22OCT/AYT\n.N/{tag}001\n.U/STAND104/20261022T{hhmmss}Z\nENDBUM";

    private static string Bpm(string flightNo, string tag, string point, string hhmmss) =>
        $"BPM\n.V/1LAYT\n.J/S/{point}/22OCT/{hhmmss}Z\n.F/{flightNo}/22OCT/AYT\n.N/{tag}001\nENDBPM";

    private static FlightContext Ctx(string flightNo, double chuteM = 50, double speed = 0.5) =>
        new($"{flightNo}-20261022", Now.AddHours(4), false, false, 120, 1000, chuteM, speed, 2, "HANDLER_B");

    private async Task<string> SetupAsync(string flightNo, bool context = true, params string[] extraMessages)
    {
        var tag = NewTag();
        var pnr = NewPnr();
        await Processor().ProcessAsync(Bsm(flightNo, tag, pnr), "test");
        foreach (var m in extraMessages)
        {
            await Processor().ProcessAsync(m.Replace("{tag}", tag), "test");
        }
        if (context)
        {
            await new FlightContextStore(fx.Db).UpsertAsync(Ctx(flightNo));
        }
        return $"{pnr}|{tag}";
    }

    [Fact]
    public async Task Bag_before_unloading_gets_flight_level_ml_window_when_context_exists()
    {
        var pnr = (await SetupAsync("ZZ0201")).Split('|')[0];
        var ml = new FakeMl(_ => Forecast);

        var result = await Queries(ml).LookupAsync(pnr, "YILMAZ");

        var eta = Assert.Single(result!.Bags).Eta;
        Assert.NotNull(eta);
        Assert.Equal("ML_FLIGHT", eta!.Basis);
        Assert.Equal(Now.AddHours(4).AddMinutes(10), eta.P10Utc);
        Assert.Equal(Now.AddHours(4).AddMinutes(28), eta.P90Utc);
        Assert.Equal(1, ml.FlightCalls);
    }

    [Fact]
    public async Task No_eta_when_ml_service_is_unavailable_before_unloading()
    {
        var pnr = (await SetupAsync("ZZ0202")).Split('|')[0];

        var result = await Queries(new FakeMl(_ => null)).LookupAsync(pnr, "YILMAZ");

        Assert.Null(Assert.Single(result!.Bags).Eta);
    }

    [Fact]
    public async Task No_eta_and_no_ml_call_without_flight_context()
    {
        var pnr = (await SetupAsync("ZZ0203", context: false)).Split('|')[0];
        var ml = new FakeMl(_ => Forecast, _ => BagForecast);

        var result = await Queries(ml).LookupAsync(pnr, "YILMAZ");

        Assert.Null(Assert.Single(result!.Bags).Eta);
        Assert.Equal(0, ml.FlightCalls + ml.BagCalls);
    }

    [Fact]
    public async Task Unloaded_bag_gets_per_bag_ml_window_counted_from_the_unload_time()
    {
        var pnr = (await SetupAsync("ZZ0204", true, Bum("ZZ0204", "{tag}"))).Split('|')[0];
        var ml = new FakeMl(_ => Forecast, _ => BagForecast);

        var result = await Queries(ml).LookupAsync(pnr, "YILMAZ");

        var eta = Assert.Single(result!.Bags).Eta;
        Assert.Equal("ML", eta!.Basis);
        // Mutlak zamanlar kontrol edilir ("şimdi" aynı veritabanını paylaşan diğer testlere bağlıdır). İndirme 12:10:00.
        var unload = new DateTimeOffset(2026, 10, 22, 12, 10, 0, TimeSpan.Zero);
        Assert.Equal(unload.AddSeconds(240), eta.P10Utc);
        Assert.Equal(unload.AddSeconds(330), eta.P50Utc);
        Assert.Equal(unload.AddSeconds(480), eta.P90Utc);
        Assert.Equal(0, ml.FlightCalls);
    }

    [Fact]
    public async Task Unloaded_bag_falls_back_to_the_empirical_estimate_when_ml_is_unavailable()
    {
        var pnr = (await SetupAsync("ZZ0205", true, Bum("ZZ0205", "{tag}"))).Split('|')[0];

        var result = await Queries(new FakeMl(_ => Forecast, _ => null)).LookupAsync(pnr, "YILMAZ");

        var eta = Assert.Single(result!.Bags).Eta;
        Assert.NotNull(eta);
        Assert.NotEqual("ML", eta!.Basis);
        Assert.NotEqual("PHYSICS", eta.Basis);
    }

    [Fact]
    public async Task Bag_in_the_chute_gets_a_physics_window_even_when_ml_is_down()
    {
        // 50 m / 0,5 m/sn = 100 sn; pencere ±15 sn
        var pnr = (await SetupAsync("ZZ0206", true, Bpm("ZZ0206", "{tag}", "CHUTE04", "121200"))).Split('|')[0];

        var result = await Queries(new FakeMl(_ => null, _ => null)).LookupAsync(pnr, "YILMAZ");

        var eta = Assert.Single(result!.Bags).Eta;
        Assert.Equal("PHYSICS", eta!.Basis);
        var chute = new DateTimeOffset(2026, 10, 22, 12, 12, 0, TimeSpan.Zero);
        Assert.Equal(chute.AddSeconds(85), eta.P10Utc);
        Assert.Equal(chute.AddSeconds(100), eta.P50Utc);
        Assert.Equal(chute.AddSeconds(115), eta.P90Utc);
    }

    [Fact]
    public async Task Pending_bag_outcomes_report_unload_to_carousel_seconds_once()
    {
        var parts = (await SetupAsync("ZZ0207", true,
            Bum("ZZ0207", "{tag}", "121000"), Bpm("ZZ0207", "{tag}", "CAROUSEL04", "121700"))).Split('|');
        var store = new FlightContextStore(fx.Db);

        var pending = await store.PendingBagOutcomesAsync(1000);
        var mine = pending.Single(p => p.Outcome.Context.FlightId == "ZZ0207-20261022");
        Assert.Equal(420, mine.Outcome.DurationSec, 1);

        await store.MarkBagOutcomesReportedAsync([mine.BagId]);
        Assert.DoesNotContain(await store.PendingBagOutcomesAsync(1000), p => p.BagId == mine.BagId);
        Assert.NotEmpty(parts);
    }
}
