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

    private sealed class FakeMl(Func<FlightContext, MlFlightForecast?> answer) : IMlPredictor
    {
        public int Calls { get; private set; }

        public Task<MlFlightForecast?> PredictAsync(FlightContext ctx, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(answer(ctx));
        }

        public Task<bool> ReportOutcomeAsync(string flightId, double firstMin, double lastMin, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private static readonly LookupKeyHasher Hasher = new(Encoding.UTF8.GetBytes("test-secret-0123456789abcdef"));

    private static readonly MlFlightForecast Forecast = new(
        new MinuteQuantiles(10, 13, 18), new MinuteQuantiles(4, 7, 12), new MinuteQuantiles(16, 20, 28));

    private BaggageEventProcessor Processor() => new(fx.Db, new FixedClock(Now), Hasher);

    private PassengerQueries Queries(IMlPredictor ml) =>
        new(fx.Db, Hasher, new EtaStatistics(fx.Db), new FixedClock(Now), ml, new FlightContextStore(fx.Db));

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    private static string NewPnr() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static string Bsm(string flightNo, string tag, string pnr) =>
        $"BSM\n.V/1LAYT\n.F/{flightNo}/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/{pnr}\nENDBSM";

    private static string Bum(string flightNo, string tag) =>
        $"BUM\n.V/1LAYT\n.F/{flightNo}/22OCT/AYT\n.N/{tag}001\n.U/STAND104/20261022T121000Z\nENDBUM";

    private static FlightContext Ctx(string flightNo) =>
        new($"{flightNo}-20261022", Now.AddHours(4), false, false, 120, 1000, 50, 0.55, 2, "HANDLER_B");

    [Fact]
    public async Task Bag_before_unloading_gets_ml_window_when_context_exists()
    {
        var tag = NewTag(); var pnr = NewPnr();
        await Processor().ProcessAsync(Bsm("ZZ0201", tag, pnr), "test");
        await new FlightContextStore(fx.Db).UpsertAsync(Ctx("ZZ0201"));
        var ml = new FakeMl(_ => Forecast);

        var result = await Queries(ml).LookupAsync(pnr, "YILMAZ");

        var eta = Assert.Single(result!.Bags).Eta;
        Assert.NotNull(eta);
        Assert.Equal("ML", eta!.Basis);
        Assert.Equal(4 * 3600 + 10 * 60, eta.P10SecondsFromNow);
        Assert.Equal(4 * 3600 + 28 * 60, eta.P90SecondsFromNow);
        Assert.Equal(1, ml.Calls);
    }

    [Fact]
    public async Task No_eta_when_ml_service_is_unavailable()
    {
        var tag = NewTag(); var pnr = NewPnr();
        await Processor().ProcessAsync(Bsm("ZZ0202", tag, pnr), "test");
        await new FlightContextStore(fx.Db).UpsertAsync(Ctx("ZZ0202"));

        var result = await Queries(new FakeMl(_ => null)).LookupAsync(pnr, "YILMAZ");

        Assert.Null(Assert.Single(result!.Bags).Eta);
    }

    [Fact]
    public async Task No_eta_and_no_ml_call_without_flight_context()
    {
        var tag = NewTag(); var pnr = NewPnr();
        await Processor().ProcessAsync(Bsm("ZZ0203", tag, pnr), "test");
        var ml = new FakeMl(_ => Forecast);

        var result = await Queries(ml).LookupAsync(pnr, "YILMAZ");

        Assert.Null(Assert.Single(result!.Bags).Eta);
        Assert.Equal(0, ml.Calls);
    }

    [Fact]
    public async Task Unloaded_bag_keeps_the_state_based_estimate_and_never_asks_ml()
    {
        var tag = NewTag(); var pnr = NewPnr();
        await Processor().ProcessAsync(Bsm("ZZ0204", tag, pnr), "test");
        await Processor().ProcessAsync(Bum("ZZ0204", tag), "test");
        await new FlightContextStore(fx.Db).UpsertAsync(Ctx("ZZ0204"));
        var ml = new FakeMl(_ => Forecast);

        var result = await Queries(ml).LookupAsync(pnr, "YILMAZ");

        var eta = Assert.Single(result!.Bags).Eta;
        Assert.NotNull(eta);
        Assert.NotEqual("ML", eta!.Basis);
        Assert.Equal(0, ml.Calls);
    }
}
