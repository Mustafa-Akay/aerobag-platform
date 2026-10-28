using AeroBag.Application.Ml;

namespace AeroBag.UnitTests;

public class MlEtaWindowTests
{
    private static readonly DateTimeOffset OnBlock = new(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);

    private static FlightContext Ctx(string id = "ZZ0100-20261022", int bags = 120, string handler = "HANDLER_B") =>
        new(id, OnBlock, false, false, bags, 1000, 50, 0.55, 2, handler);

    private static readonly MlFlightForecast Forecast = new(
        new MinuteQuantiles(10, 13, 18), new MinuteQuantiles(4, 7, 12), new MinuteQuantiles(16, 20, 28));

    [Fact]
    public void Window_spans_first_bag_p10_to_last_bag_p90_with_midpoint_median()
    {
        var w = MlEtaWindow.Build(Ctx(), Forecast, OnBlock.AddMinutes(-30))!;

        Assert.Equal("ML_FLIGHT", w.Basis);
        Assert.Equal(OnBlock.AddMinutes(10), w.P10Utc);
        Assert.Equal(OnBlock.AddMinutes(16.5), w.P50Utc);
        Assert.Equal(OnBlock.AddMinutes(28), w.P90Utc);
        Assert.True(w.P10Utc <= w.P50Utc && w.P50Utc <= w.P90Utc);
    }

    [Fact]
    public void Window_is_dropped_once_the_whole_range_is_in_the_past()
    {
        Assert.Null(MlEtaWindow.Build(Ctx(), Forecast, OnBlock.AddMinutes(28)));
        Assert.NotNull(MlEtaWindow.Build(Ctx(), Forecast, OnBlock.AddMinutes(27)));
    }

    [Fact]
    public void Window_stays_ordered_even_for_degenerate_forecasts()
    {
        var odd = new MlFlightForecast(new MinuteQuantiles(12, 12, 12), new MinuteQuantiles(0, 0, 0), new MinuteQuantiles(10, 10, 10));
        var w = MlEtaWindow.Build(Ctx(), odd, OnBlock)!;
        Assert.True(w.P10Utc <= w.P50Utc && w.P50Utc <= w.P90Utc);
    }

    [Fact]
    public void Valid_context_passes_validation()
    {
        Assert.Null(Ctx().Validate());
    }

    [Theory]
    [InlineData("zz bad;id", 120, "HANDLER_B")]
    [InlineData("ZZ0100-20261022", 0, "HANDLER_B")]
    [InlineData("ZZ0100-20261022", 900, "HANDLER_B")]
    [InlineData("ZZ0100-20261022", 120, "HANDLER_Z")]
    public void Invalid_context_is_rejected(string id, int bags, string handler)
    {
        Assert.NotNull(Ctx(id, bags, handler).Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("4")]
    [InlineData("07")]
    [InlineData("B12")]
    [InlineData("12345678")]
    public void Carousel_is_optional_and_accepts_short_uppercase_codes(string? carousel)
    {
        Assert.Null((Ctx() with { Carousel = carousel }).Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("b12")]
    [InlineData("BANT 4")]
    [InlineData("123456789")]
    [InlineData("4\n")]
    public void Malformed_carousel_is_rejected(string carousel)
    {
        Assert.NotNull((Ctx() with { Carousel = carousel }).Validate());
    }

    [Fact]
    public void Bag_window_counts_from_the_unload_event_and_stays_ordered()
    {
        var w = MlEtaWindow.BuildForBag(OnBlock, new BagDurationForecast(240, 330, 480));
        Assert.Equal("ML", w.Basis);
        Assert.Equal(OnBlock.AddSeconds(240), w.P10Utc);
        Assert.Equal(OnBlock.AddSeconds(330), w.P50Utc);
        Assert.Equal(OnBlock.AddSeconds(480), w.P90Utc);

        var odd = MlEtaWindow.BuildForBag(OnBlock, new BagDurationForecast(300, 200, 100));
        Assert.True(odd.P10Utc <= odd.P50Utc && odd.P50Utc <= odd.P90Utc);
    }

    [Fact]
    public void Physics_window_is_distance_over_speed_plus_minus_tolerance()
    {
        var ctx = Ctx() with { ChuteM = 48, SpeedMps = 0.55 };   // ≈ 87,3 sn
        var w = PhysicsEtaWindow.Build(OnBlock, ctx);
        Assert.Equal("PHYSICS", w.Basis);
        Assert.Equal(48 / 0.55, (w.P50Utc - OnBlock).TotalSeconds, 3);
        Assert.Equal(PhysicsEtaWindow.ToleranceSeconds, (w.P90Utc - w.P50Utc).TotalSeconds, 3);
        Assert.Equal(PhysicsEtaWindow.ToleranceSeconds, (w.P50Utc - w.P10Utc).TotalSeconds, 3);
    }

    [Fact]
    public void Physics_window_never_starts_before_the_chute_scan()
    {
        var w = PhysicsEtaWindow.Build(OnBlock, Ctx() with { ChuteM = 5, SpeedMps = 1 });   // 5 sn < tolerans
        Assert.True(w.P10Utc >= OnBlock);
    }
}
