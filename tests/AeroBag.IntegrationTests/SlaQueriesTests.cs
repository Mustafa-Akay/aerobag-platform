using System.Text;
using AeroBag.Application.Ml;
using AeroBag.Application.Sla;
using AeroBag.Infrastructure;

namespace AeroBag.IntegrationTests;

/// <summary>
/// "Şimdi" sanal saattir (olayların en son zamanı). Bu sınıftaki tüm olaylar 12:20'den önce olduğundan
/// on-block 12:00 için geçen süre 5 ile 20 dakika arasındadır; testler yalnızca bu aralıkta kesin olan sonuçları doğrular.
/// </summary>
public class SlaQueriesTests(PostgresFixture fx) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset OnBlock = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeMl(MlFlightForecast? forecast) : IMlPredictor
    {
        public Task<MlFlightForecast?> PredictAsync(FlightContext ctx, CancellationToken ct = default) => Task.FromResult(forecast);
        public Task<BagDurationForecast?> PredictBagAsync(FlightContext ctx, CancellationToken ct = default) => Task.FromResult<BagDurationForecast?>(null);
        public Task<int> ReportBagOutcomesAsync(IReadOnlyList<BagOutcome> items, CancellationToken ct = default) => Task.FromResult(items.Count);
        public Task<bool> ReportOutcomeAsync(string flightId, double firstMin, double lastMin, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly LookupKeyHasher Hasher = new(Encoding.UTF8.GetBytes("test-secret-0123456789abcdef"));
    private static readonly MlFlightForecast Forecast = new(
        new MinuteQuantiles(10, 13, 18), new MinuteQuantiles(4, 7, 12), new MinuteQuantiles(16, 20, 28));

    private BaggageEventProcessor Processor() => new(fx.Db, new FixedClock(OnBlock), Hasher);

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    private async Task<string> FlightAsync(string flightNo, int firstTarget, int lastTarget, bool context, params string[] hhmmss)
    {
        var tag = NewTag();
        var pnr = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        await Processor().ProcessAsync(
            $"BSM\n.V/1LAYT\n.F/{flightNo}/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/{pnr}\nENDBSM", "test");

        if (hhmmss.Length > 0)
        {
            await Processor().ProcessAsync(
                $"BUM\n.V/1LAYT\n.F/{flightNo}/22OCT/AYT\n.N/{tag}001\n.U/STAND104/20261022T{hhmmss[0]}Z\nENDBUM", "test");
        }
        if (hhmmss.Length > 1)
        {
            await Processor().ProcessAsync(
                $"BPM\n.V/1LAYT\n.J/S/CAROUSEL04/22OCT/{hhmmss[1]}Z\n.F/{flightNo}/22OCT/AYT\n.N/{tag}001\nENDBPM", "test");
        }

        var id = $"{flightNo}-20261022";
        await using (var cmd = fx.Db.CreateCommand("UPDATE flights SET sla_first_bag_sec = @f, sla_last_bag_sec = @l WHERE flight_id = @id"))
        {
            cmd.Parameters.AddWithValue("f", firstTarget);
            cmd.Parameters.AddWithValue("l", lastTarget);
            cmd.Parameters.AddWithValue("id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        if (context)
        {
            await new FlightContextStore(fx.Db).UpsertAsync(
                new FlightContext(id, OnBlock, false, false, 1, 1000, 50, 0.5, 2, "HANDLER_B"));
        }
        return id;
    }

    private async Task<FlightSla> GetAsync(string id, IMlPredictor ml) =>
        (await new SlaQueries(fx.Db, ml).GetAsync()).Single(f => f.FlightId == id);

    [Fact]
    public async Task Finished_flight_within_targets_is_met_with_actual_seconds()
    {
        var id = await FlightAsync("ZZ0301", 900, 1800, true, "120500", "121400");
        var sla = await GetAsync(id, new FakeMl(Forecast));

        Assert.Equal("DEMO_ASSUMPTION", sla.Assumption);
        Assert.True(sla.AllBagsArrived);
        Assert.Equal(SlaStatus.Met, sla.FirstBag.Status);
        Assert.Equal(840, sla.FirstBag.ActualSec);
        Assert.Equal(SlaStatus.Met, sla.LastBag.Status);
        Assert.Equal(840, sla.LastBag.ActualSec);
    }

    [Fact]
    public async Task First_bag_over_target_is_breached_with_real_delay()
    {
        var id = await FlightAsync("ZZ0302", 900, 1800, true, "120500", "121600");
        var sla = await GetAsync(id, new FakeMl(Forecast));

        Assert.Equal(SlaStatus.Breached, sla.FirstBag.Status);
        Assert.Equal(60, sla.FirstBag.ProjectedDelayP50Sec);
        Assert.Equal(SlaStatus.Met, sla.LastBag.Status);
    }

    [Fact]
    public async Task Unfinished_flight_uses_the_forecast_and_is_on_track_with_generous_targets()
    {
        var id = await FlightAsync("ZZ0303", 3600, 7200, true, "120500");
        var sla = await GetAsync(id, new FakeMl(Forecast));

        Assert.False(sla.AllBagsArrived);
        Assert.Equal(SlaStatus.OnTrack, sla.FirstBag.Status);
        Assert.Equal(SlaStatus.OnTrack, sla.LastBag.Status);
        Assert.Equal(0, sla.LastBag.ProjectedDelayP90Sec);
        Assert.InRange(sla.LastBag.BreachProbability!.Value, 0, 0.2);
    }

    [Fact]
    public async Task Unfinished_flight_past_its_target_is_breached_and_shows_projected_delay()
    {
        var id = await FlightAsync("ZZ0304", 60, 60, true, "120500");
        var sla = await GetAsync(id, new FakeMl(Forecast));

        Assert.Equal(SlaStatus.Breached, sla.FirstBag.Status);
        Assert.Equal(SlaStatus.Breached, sla.LastBag.Status);
        Assert.True(sla.LastBag.ProjectedDelayP50Sec > 0);
        Assert.True(sla.LastBag.ProjectedDelayP90Sec >= sla.LastBag.ProjectedDelayP50Sec);
    }

    [Fact]
    public async Task Without_ml_unfinished_flight_is_unknown_but_actuals_still_count()
    {
        var open = await FlightAsync("ZZ0305", 3600, 7200, true, "120500");
        var done = await FlightAsync("ZZ0306", 900, 1800, true, "120500", "121400");
        var ml = new NullMlPredictor();

        var openSla = await GetAsync(open, ml);
        Assert.Equal(SlaStatus.Unknown, openSla.FirstBag.Status);
        Assert.Null(openSla.LastBag.BreachProbability);

        var doneSla = await GetAsync(done, ml);
        Assert.Equal(SlaStatus.Met, doneSla.FirstBag.Status);
    }

    [Fact]
    public async Task Flight_without_context_is_unknown()
    {
        var id = await FlightAsync("ZZ0307", 900, 1800, false, "120500");
        var sla = await GetAsync(id, new FakeMl(Forecast));

        Assert.Null(sla.OnBlockUtc);
        Assert.Equal(SlaStatus.Unknown, sla.FirstBag.Status);
        Assert.Equal(SlaStatus.Unknown, sla.LastBag.Status);
        Assert.Equal(1800, sla.LastBag.TargetSec);
    }
}
