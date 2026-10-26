using AeroBag.Application.Ml;
using AeroBag.Application.Sla;

namespace AeroBag.UnitTests;

public class SlaRiskTests
{
    private static readonly MinuteQuantiles Q = new(10, 13, 18);

    [Fact]
    public void Cdf_hits_the_given_quantiles()
    {
        Assert.Equal(0.1, QuantileCdf.Cdf(Q, 10), 6);
        Assert.Equal(0.5, QuantileCdf.Cdf(Q, 13), 6);
        Assert.Equal(0.9, QuantileCdf.Cdf(Q, 18), 6);
    }

    [Fact]
    public void Cdf_is_monotonic_and_bounded()
    {
        var prev = 0.0;
        for (var x = -5.0; x <= 60; x += 0.5)
        {
            var c = QuantileCdf.Cdf(Q, x);
            Assert.InRange(c, 0.01, 0.99);
            Assert.True(c >= prev - 1e-12);
            prev = c;
        }
    }

    [Fact]
    public void Degenerate_quantiles_do_not_produce_nan()
    {
        var c = QuantileCdf.Cdf(new MinuteQuantiles(10, 10, 10), 10);
        Assert.False(double.IsNaN(c));
        Assert.InRange(c, 0.01, 0.99);
    }

    [Fact]
    public void Exceed_probability_matches_the_forecast_at_p50_and_p90()
    {
        Assert.Equal(0.5, SlaRisk.ExceedProbability(Q, 13 * 60, 0), 6);
        Assert.Equal(0.1, SlaRisk.ExceedProbability(Q, 18 * 60, 0), 6);
    }

    [Fact]
    public void Elapsed_time_without_the_event_raises_the_probability()
    {
        var fresh = SlaRisk.ExceedProbability(Q, 18 * 60, 0);
        var late = SlaRisk.ExceedProbability(Q, 18 * 60, 15 * 60);
        Assert.True(late > fresh);
    }

    [Fact]
    public void Target_already_passed_without_the_event_is_certain()
    {
        Assert.Equal(1.0, SlaRisk.ExceedProbability(Q, 900, 901));
    }

    [Fact]
    public void Actual_within_target_is_met()
    {
        var leg = SlaRisk.Evaluate(900, 800, 1000, Q);
        Assert.Equal(SlaStatus.Met, leg.Status);
        Assert.Equal(0, leg.BreachProbability);
        Assert.Equal(0, leg.ProjectedDelayP90Sec);
    }

    [Fact]
    public void Actual_over_target_is_breached_with_the_real_delay()
    {
        var leg = SlaRisk.Evaluate(900, 1000, 1200, Q);
        Assert.Equal(SlaStatus.Breached, leg.Status);
        Assert.Equal(1, leg.BreachProbability);
        Assert.Equal(100, leg.ProjectedDelayP50Sec);
    }

    [Fact]
    public void Forecast_below_target_is_on_track_and_above_is_likely_breach()
    {
        var easy = SlaRisk.Evaluate(1800, null, 300, new MinuteQuantiles(10, 13, 18));
        Assert.Equal(SlaStatus.OnTrack, easy.Status);
        Assert.Equal(0, easy.ProjectedDelayP50Sec);

        var hard = SlaRisk.Evaluate(900, null, 300, new MinuteQuantiles(14, 20, 28));
        Assert.Equal(SlaStatus.LikelyBreach, hard.Status);
        Assert.Equal(300.0, hard.ProjectedDelayP50Sec!.Value, 6);
        Assert.Equal(780.0, hard.ProjectedDelayP90Sec!.Value, 6);
    }

    [Fact]
    public void Without_forecast_status_is_unknown_until_the_target_passes()
    {
        Assert.Equal(SlaStatus.Unknown, SlaRisk.Evaluate(900, null, 300, null).Status);
        var breached = SlaRisk.Evaluate(900, null, 1000, null);
        Assert.Equal(SlaStatus.Breached, breached.Status);
        Assert.Equal(100, breached.ProjectedDelayP50Sec);
    }

    [Fact]
    public void Projection_never_lies_in_the_past()
    {
        var leg = SlaRisk.Evaluate(1800, null, 1500, new MinuteQuantiles(10, 13, 18));
        Assert.True(leg.ProjectedP50Sec >= 1500);
        Assert.True(leg.ProjectedP90Sec >= leg.ProjectedP50Sec);
    }
}
