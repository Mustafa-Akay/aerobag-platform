using AeroBag.Application.ExitGuard;
using AeroBag.Domain;

namespace AeroBag.UnitTests;

public class ExitGuardRulesTests
{
    private static readonly byte[] Owner = [1, 2, 3, 4];
    private static readonly byte[] Other = [9, 9, 9, 9];
    private const double Min = ExitGuardRules.DefaultMinSignalQuality;

    [Theory]
    [InlineData(BagState.ClaimedFromBelt)]
    [InlineData(BagState.ExitOverdue)]
    [InlineData(BagState.OnCarousel)]
    public void Matching_pnr_clears_the_owner(BagState state)
    {
        var v = ExitGuardRules.Decide(state, Owner, [1, 2, 3, 4], 0.9, Min);
        Assert.Equal(GateDecision.ClearedOwner, v.Decision);
        Assert.Equal("CLEARED_OWNER", v.Code);
    }

    [Fact]
    public void Different_pnr_goes_to_silent_review()
    {
        var v = ExitGuardRules.Decide(BagState.ClaimedFromBelt, Owner, Other, 0.9, Min);
        Assert.Equal(GateDecision.ReviewMismatch, v.Decision);
        Assert.Equal("PNR_MISMATCH", v.Reason);
    }

    [Theory]
    [InlineData(null, "NO_VERIFICATION")]
    [InlineData(0.59, "LOW_SIGNAL")]
    public void Weak_evidence_gives_no_decision_instead_of_an_alarm(double? signal, string reason)
    {
        var presented = reason == "NO_VERIFICATION" ? null : Other;
        var v = ExitGuardRules.Decide(BagState.ClaimedFromBelt, Owner, presented, signal ?? 0.9, Min);
        Assert.Equal(GateDecision.NoDecision, v.Decision);
        Assert.Equal(reason, v.Reason);
    }

    [Fact]
    public void Missing_signal_quality_is_treated_as_low()
    {
        Assert.Equal("LOW_SIGNAL", ExitGuardRules.Decide(BagState.ClaimedFromBelt, Owner, Owner, null, Min).Reason);
    }

    [Fact]
    public void Bag_without_owner_data_gets_no_decision()
    {
        var v = ExitGuardRules.Decide(BagState.ClaimedFromBelt, null, Other, 0.9, Min);
        Assert.Equal(GateDecision.NoDecision, v.Decision);
        Assert.Equal("NO_OWNER_DATA", v.Reason);
    }

    [Theory]
    [InlineData(BagState.ExitCleared)]
    [InlineData(BagState.ExitReview)]
    [InlineData(BagState.OffloadedNoShow)]
    public void Closed_bags_are_not_changed(BagState state)
    {
        Assert.Equal(GateDecision.AlreadyClosed, ExitGuardRules.Decide(state, Owner, Other, 0.9, Min).Decision);
    }

    [Theory]
    [InlineData(BagState.CheckedIn)]
    [InlineData(BagState.LoadedOnAircraft)]
    [InlineData(BagState.InboundChute)]
    public void Bag_seen_at_exit_before_the_carousel_is_out_of_sequence(BagState state)
    {
        var v = ExitGuardRules.Decide(state, Owner, Owner, 0.9, Min);
        Assert.Equal(GateDecision.OutOfSequence, v.Decision);
        Assert.Equal("NOT_ON_CAROUSEL", v.Reason);
    }
}
