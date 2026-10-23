using AeroBag.Application.Eta;
using AeroBag.Domain;

namespace AeroBag.UnitTests;

public class EtaEstimatorTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(BagState.CheckedIn)]
    [InlineData(BagState.SecurityScreened)]
    [InlineData(BagState.LoadedOnAircraft)]
    [InlineData(BagState.OnCarousel)]
    [InlineData(BagState.ClaimedFromBelt)]
    [InlineData(BagState.ExitCleared)]
    public void No_eta_outside_the_arrival_phase(BagState state)
    {
        Assert.Null(EtaEstimator.Estimate(state, T, null, null));
    }

    [Fact]
    public void Falls_back_to_default_window_when_history_is_missing()
    {
        var w = EtaEstimator.Estimate(BagState.UnloadedAtArrival, T, null, null)!;
        Assert.Equal("FALLBACK", w.Basis);
        Assert.Equal(T.AddSeconds(300), w.P10Utc);
        Assert.Equal(T.AddSeconds(540), w.P50Utc);
        Assert.Equal(T.AddSeconds(900), w.P90Utc);
    }

    [Fact]
    public void Falls_back_when_history_has_too_few_samples()
    {
        var thin = new DurationQuantiles(EtaEstimator.MinSamples - 1, 1, 2, 3);
        Assert.Equal("FALLBACK", EtaEstimator.Estimate(BagState.InboundChute, T, null, thin)!.Basis);
    }

    [Fact]
    public void Uses_history_when_enough_samples_exist()
    {
        var history = new DurationQuantiles(EtaEstimator.MinSamples, 400, 500, 650);
        var w = EtaEstimator.Estimate(BagState.UnloadedAtArrival, T, history, null)!;
        Assert.Equal("HISTORY", w.Basis);
        Assert.Equal(T.AddSeconds(400), w.P10Utc);
        Assert.Equal(T.AddSeconds(650), w.P90Utc);
    }

    [Fact]
    public void Chute_state_uses_chute_statistics_not_unload_statistics()
    {
        var unload = new DurationQuantiles(100, 400, 500, 650);
        var chute = new DurationQuantiles(100, 70, 85, 100);
        var w = EtaEstimator.Estimate(BagState.InboundChute, T, unload, chute)!;
        Assert.Equal(T.AddSeconds(85), w.P50Utc);
    }

    [Fact]
    public void Window_is_ordered()
    {
        var w = EtaEstimator.Estimate(BagState.UnloadedAtArrival, T, null, null)!;
        Assert.True(w.P10Utc <= w.P50Utc && w.P50Utc <= w.P90Utc);
    }
}
