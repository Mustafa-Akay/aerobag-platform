using AeroBag.Application.Alerts;
using AeroBag.Domain;

namespace AeroBag.UnitTests;

/// <summary>W4: kayıp ve geciken bavul kuralları (saf). Tolerans 10 dk (varsayılan).</summary>
public class BagAlertRulesTests
{
    private static readonly DateTimeOffset Departed = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Unloaded = new(2026, 10, 22, 12, 20, 0, TimeSpan.Zero);
    private static readonly AlertPolicy P = AlertPolicy.Default;

    [Theory]
    [InlineData(BagState.CheckedIn)]
    [InlineData(BagState.SecurityScreened)]
    public void Unloaded_scan_missing_after_departure_grace_opens_not_loaded(BagState state)
    {
        Assert.Null(BagAlertRules.Evaluate(state, Departed, null, Departed.AddMinutes(9).AddSeconds(59), P));
        Assert.Equal(BagAlertKind.NotLoaded, BagAlertRules.Evaluate(state, Departed, null, Departed.AddMinutes(10), P));
    }

    [Fact]
    public void Loaded_bag_gets_no_alert_after_departure()
    {
        Assert.Null(BagAlertRules.Evaluate(BagState.LoadedOnAircraft, Departed, null, Departed.AddHours(1), P));
    }

    [Fact]
    public void Loaded_but_not_unloaded_bag_is_missing_after_unloading_grace()
    {
        Assert.Null(BagAlertRules.Evaluate(BagState.LoadedOnAircraft, Departed, Unloaded, Unloaded.AddMinutes(9), P));
        Assert.Equal(BagAlertKind.MissingAtArrival, BagAlertRules.Evaluate(BagState.LoadedOnAircraft, Departed, Unloaded, Unloaded.AddMinutes(10), P));
    }

    [Fact]
    public void Never_loaded_bag_stays_not_loaded_at_arrival_so_only_one_alert_is_open()
    {
        Assert.Equal(BagAlertKind.NotLoaded, BagAlertRules.Evaluate(BagState.SecurityScreened, Departed, Unloaded, Unloaded.AddHours(1), P));
    }

    [Fact]
    public void Without_a_departure_time_a_bag_without_load_scan_is_judged_at_arrival()
    {
        Assert.Null(BagAlertRules.Evaluate(BagState.CheckedIn, null, Unloaded, Unloaded.AddMinutes(5), P));
        Assert.Equal(BagAlertKind.MissingAtArrival, BagAlertRules.Evaluate(BagState.CheckedIn, null, Unloaded, Unloaded.AddMinutes(10), P));
    }

    [Theory]
    [InlineData(BagState.UnloadedAtArrival)]
    [InlineData(BagState.InboundChute)]
    [InlineData(BagState.OnCarousel)]
    [InlineData(BagState.ClaimedFromBelt)]
    [InlineData(BagState.ExitOverdue)]
    [InlineData(BagState.OffloadedNoShow)]
    public void Bags_past_arrival_or_offloaded_get_no_alert(BagState state)
    {
        Assert.Null(BagAlertRules.Evaluate(state, Departed, Unloaded, Unloaded.AddHours(2), P));
    }

    [Fact]
    public void Without_milestones_nothing_is_opened()
    {
        Assert.Null(BagAlertRules.Evaluate(BagState.CheckedIn, null, null, Unloaded.AddDays(1), P));
    }

    [Fact]
    public void Grace_period_is_configurable()
    {
        var strict = new AlertPolicy(TimeSpan.Zero, TimeSpan.FromMinutes(2));
        Assert.Equal(BagAlertKind.NotLoaded, BagAlertRules.Evaluate(BagState.CheckedIn, Departed, null, Departed, strict));
        Assert.Equal(BagAlertKind.MissingAtArrival, BagAlertRules.Evaluate(BagState.LoadedOnAircraft, null, Unloaded, Unloaded.AddMinutes(2), strict));
    }

    [Theory]
    [InlineData(BagAlertKind.NotLoaded, BagState.LoadedOnAircraft, BagAlertCloseReason.Found)]
    [InlineData(BagAlertKind.NotLoaded, BagState.OnCarousel, BagAlertCloseReason.Found)]
    [InlineData(BagAlertKind.MissingAtArrival, BagState.InboundChute, BagAlertCloseReason.Found)]
    [InlineData(BagAlertKind.MissingAtArrival, BagState.ExitCleared, BagAlertCloseReason.Found)]
    [InlineData(BagAlertKind.NotLoaded, BagState.OffloadedNoShow, BagAlertCloseReason.Offloaded)]
    [InlineData(BagAlertKind.MissingAtArrival, BagState.LoadedOnAircraft, BagAlertCloseReason.Cleared)]
    public void Close_reason_says_why_the_alert_ended(string kind, BagState state, string reason)
    {
        Assert.Equal(reason, BagAlertRules.CloseReason(kind, state));
    }

    [Theory]
    [InlineData(BagAlertKind.NotLoaded, BagState.SecurityScreened, true)]
    [InlineData(BagAlertKind.NotLoaded, BagState.LoadedOnAircraft, false)]
    [InlineData(BagAlertKind.MissingAtArrival, BagState.LoadedOnAircraft, true)]
    [InlineData(BagAlertKind.MissingAtArrival, BagState.UnloadedAtArrival, false)]
    [InlineData(BagAlertKind.MissingAtArrival, BagState.OffloadedNoShow, false)]
    public void Stale_open_alert_is_not_shown_once_the_bag_is_read(string kind, BagState state, bool applies)
    {
        Assert.Equal(applies, BagAlertRules.StillApplies(kind, state));
    }

    [Fact]
    public void Arrival_is_delayed_when_no_bag_is_unloaded_after_the_first_bag_target()
    {
        var onBlock = new DateTimeOffset(2026, 10, 22, 12, 25, 0, TimeSpan.Zero);
        Assert.False(BagAlertRules.IsArrivalDelayed(BagState.LoadedOnAircraft, onBlock, false, 900, onBlock.AddSeconds(899)));
        Assert.True(BagAlertRules.IsArrivalDelayed(BagState.LoadedOnAircraft, onBlock, false, 900, onBlock.AddSeconds(900)));
        Assert.False(BagAlertRules.IsArrivalDelayed(BagState.LoadedOnAircraft, onBlock, true, 900, onBlock.AddMinutes(40)));
        Assert.False(BagAlertRules.IsArrivalDelayed(BagState.UnloadedAtArrival, onBlock, false, 900, onBlock.AddMinutes(40)));
        Assert.False(BagAlertRules.IsArrivalDelayed(BagState.LoadedOnAircraft, null, false, 900, onBlock.AddMinutes(40)));
    }

    [Fact]
    public void Milestones_need_a_valid_flight_and_at_least_one_time_in_order()
    {
        Assert.Null(new FlightMilestones("ZZ0100-20261022", Departed).Validate());
        Assert.Null(new FlightMilestones("ZZ0100-20261022", null, Unloaded).Validate());
        Assert.Null(new FlightMilestones("ZZ0100-20261022", Departed, Unloaded).Validate());
        Assert.NotNull(new FlightMilestones("ZZ0100-20261022").Validate());
        Assert.NotNull(new FlightMilestones("zz 0100;", Departed).Validate());
        Assert.NotNull(new FlightMilestones("ZZ0100-20261022", Unloaded, Departed).Validate());
    }
}
