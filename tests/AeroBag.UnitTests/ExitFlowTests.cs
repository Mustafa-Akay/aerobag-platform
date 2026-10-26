using AeroBag.Application.TypeB;
using AeroBag.Domain;
using AeroBag.Simulator;

namespace AeroBag.UnitTests;

public class ExitFlowTests
{
    private static readonly DateTimeOffset Received = new(2026, 10, 22, 22, 0, 0, TimeSpan.Zero);

    private static string ExitScan(string point) =>
        $"BPM\n.V/1LAYT\n.J/S/{point}/22OCT/212400Z\n.F/ZZ0124/22OCT/AYT\n.N/0891458921001\nENDBPM";

    [Fact]
    public void Exit_scan_point_maps_to_ExitCleared()
    {
        var parsed = TypeBParser.Parse(ExitScan("EXIT01"), new DateOnly(2026, 10, 22));
        Assert.True(parsed.IsSuccess, parsed.Detail);
        Assert.True(BaggageEventMapper.TryMap(parsed.Message!, Received, out var ev, out var error), error);
        Assert.Equal(BagState.ExitCleared, ev!.TargetState);
    }

    [Fact]
    public void Exit_read_after_overdue_still_clears_the_bag()
    {
        var r = BagStateMachine.Apply(BagState.ExitOverdue, BagState.ExitCleared);
        Assert.Equal(BagState.ExitCleared, r.NewState);
        Assert.Equal(TransitionOutcome.Applied, r.Outcome);
    }

    [Fact]
    public void Overdue_does_not_overwrite_a_cleared_bag()
    {
        var r = BagStateMachine.Apply(BagState.ExitCleared, BagState.ExitOverdue);
        Assert.Equal(BagState.ExitCleared, r.NewState);
        Assert.Equal(TransitionOutcome.IgnoredStale, r.Outcome);
    }

    private static IReadOnlyList<FlightResult> Run(bool exit) =>
        SimulationRunner.Run(new SimOptions(6, 60, 3, 'A', new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero), exit));

    [Fact]
    public void Exit_scans_are_off_by_default_and_do_not_change_other_messages()
    {
        var without = Run(false).SelectMany(f => f.Messages).Select(m => m.Text).ToList();
        var with = Run(true).SelectMany(f => f.Messages).Where(m => m.Kind != "EXIT").Select(m => m.Text).ToList();
        Assert.DoesNotContain(Run(false).SelectMany(f => f.Messages), m => m.Kind == "EXIT");
        Assert.Equal(without, with);
    }

    [Fact]
    public void Generated_exit_scans_parse_and_follow_the_claim_for_most_bags()
    {
        var reference = new DateOnly(2026, 10, 22);
        var exits = 0;
        var bags = 0;
        foreach (var flight in Run(true))
        {
            foreach (var g in flight.Messages.GroupBy(m => m.BagTag))
            {
                bags++;
                var claim = g.Single(m => m.Kind == "CLAIM").TimeUtc;
                foreach (var e in g.Where(m => m.Kind == "EXIT"))
                {
                    exits++;
                    var parsed = TypeBParser.Parse(e.Text, reference);
                    Assert.True(parsed.IsSuccess, parsed.Detail);
                    Assert.True(e.TimeUtc >= claim.AddSeconds(30));
                }
            }
        }
        Assert.InRange(exits / (double)bags, 0.9, 1.0);
        Assert.True(exits < bags, "Bazı bavullar çıkışta okunmamalı.");
    }
}
