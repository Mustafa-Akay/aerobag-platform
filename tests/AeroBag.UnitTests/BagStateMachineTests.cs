using AeroBag.Domain;

namespace AeroBag.UnitTests;

public class BagStateMachineTests
{
    [Fact]
    public void Null_to_CheckedIn_is_applied()
    {
        var r = BagStateMachine.Apply(null, BagState.CheckedIn);
        Assert.Equal(BagState.CheckedIn, r.NewState);
        Assert.Equal(TransitionOutcome.Applied, r.Outcome);
    }

    [Fact]
    public void Transfer_bag_starts_at_UnloadedAtArrival_without_anomaly()
    {
        var r = BagStateMachine.Apply(null, BagState.UnloadedAtArrival);
        Assert.Equal(TransitionOutcome.Applied, r.Outcome);
        Assert.Empty(r.SkippedStates);
    }

    [Fact]
    public void Starting_mid_chain_is_anomaly_with_skipped_states()
    {
        var r = BagStateMachine.Apply(null, BagState.InboundChute);
        Assert.Equal(TransitionOutcome.AppliedWithAnomaly, r.Outcome);
        Assert.Equal(
            new[] { BagState.CheckedIn, BagState.SecurityScreened, BagState.LoadedOnAircraft, BagState.UnloadedAtArrival },
            r.SkippedStates.ToArray());
    }

    [Theory]
    [InlineData(BagState.CheckedIn, BagState.SecurityScreened)]
    [InlineData(BagState.SecurityScreened, BagState.LoadedOnAircraft)]
    [InlineData(BagState.LoadedOnAircraft, BagState.UnloadedAtArrival)]
    [InlineData(BagState.UnloadedAtArrival, BagState.InboundChute)]
    [InlineData(BagState.InboundChute, BagState.OnCarousel)]
    [InlineData(BagState.OnCarousel, BagState.ClaimedFromBelt)]
    [InlineData(BagState.ClaimedFromBelt, BagState.ExitCleared)]
    [InlineData(BagState.ClaimedFromBelt, BagState.ExitReview)]
    [InlineData(BagState.ClaimedFromBelt, BagState.ExitOverdue)]
    public void Sequential_step_is_applied(BagState from, BagState to)
    {
        var r = BagStateMachine.Apply(from, to);
        Assert.Equal(to, r.NewState);
        Assert.Equal(TransitionOutcome.Applied, r.Outcome);
    }

    [Fact]
    public void Skipping_one_step_is_anomaly()
    {
        var r = BagStateMachine.Apply(BagState.LoadedOnAircraft, BagState.InboundChute);
        Assert.Equal(BagState.InboundChute, r.NewState);
        Assert.Equal(TransitionOutcome.AppliedWithAnomaly, r.Outcome);
        Assert.Equal(new[] { BagState.UnloadedAtArrival }, r.SkippedStates.ToArray());
    }

    [Theory]
    [InlineData(BagState.OnCarousel, BagState.InboundChute)]
    [InlineData(BagState.OnCarousel, BagState.OnCarousel)]
    [InlineData(BagState.ClaimedFromBelt, BagState.CheckedIn)]
    public void Out_of_order_or_repeated_event_is_ignored_and_state_unchanged(BagState current, BagState target)
    {
        var r = BagStateMachine.Apply(current, target);
        Assert.Equal(current, r.NewState);
        Assert.Equal(TransitionOutcome.IgnoredStale, r.Outcome);
    }

    [Fact]
    public void Late_exit_read_after_overdue_is_applied()
    {
        var r = BagStateMachine.Apply(BagState.ExitOverdue, BagState.ExitCleared);
        Assert.Equal(BagState.ExitCleared, r.NewState);
        Assert.Equal(TransitionOutcome.Applied, r.Outcome);
    }

    [Theory]
    [InlineData(BagState.ExitCleared, BagState.ExitReview)]
    [InlineData(BagState.ExitReview, BagState.ExitCleared)]
    [InlineData(BagState.ExitCleared, BagState.OnCarousel)]
    public void Exit_states_are_terminal(BagState current, BagState target)
    {
        var r = BagStateMachine.Apply(current, target);
        Assert.Equal(current, r.NewState);
        Assert.Equal(TransitionOutcome.IgnoredStale, r.Outcome);
    }

    [Theory]
    [InlineData(BagState.CheckedIn)]
    [InlineData(BagState.SecurityScreened)]
    [InlineData(BagState.LoadedOnAircraft)]
    public void No_show_offload_is_allowed_before_arrival(BagState from)
    {
        var r = BagStateMachine.Apply(from, BagState.OffloadedNoShow);
        Assert.Equal(BagState.OffloadedNoShow, r.NewState);
        Assert.Equal(TransitionOutcome.Applied, r.Outcome);
    }

    [Fact]
    public void No_show_is_ignored_after_arrival_and_is_terminal()
    {
        Assert.Equal(TransitionOutcome.IgnoredStale,
            BagStateMachine.Apply(BagState.InboundChute, BagState.OffloadedNoShow).Outcome);
        Assert.Equal(TransitionOutcome.IgnoredStale,
            BagStateMachine.Apply(BagState.OffloadedNoShow, BagState.UnloadedAtArrival).Outcome);
    }
}
