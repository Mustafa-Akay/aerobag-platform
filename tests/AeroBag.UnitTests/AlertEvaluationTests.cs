using AeroBag.Application.Alerts;
using AeroBag.Simulator;

namespace AeroBag.UnitTests;

/// <summary>W4c: simülatörün kayıp bavul senaryoları ve uyarı ölçümü.</summary>
public class AlertEvaluationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<FlightResult> Run(double notLoaded, double missing, int flights = 20, int seed = 11) =>
        SimulationRunner.Run(new SimOptions(flights, 120, seed, 'M', Start, NotLoadedRate: notLoaded, MissingRate: missing));

    [Fact]
    public void Scenarios_do_not_change_the_data_of_normal_bags()
    {
        var plain = Run(0, 0);
        var withScenarios = Run(0.05, 0.05);

        Assert.All(plain.SelectMany(f => f.Bags), b => Assert.Null(b.Fate));
        var normal = withScenarios.SelectMany(f => f.Bags).Where(b => b.Fate is null).ToDictionary(b => b.BagTag);
        Assert.NotEmpty(normal);
        foreach (var b in plain.SelectMany(f => f.Bags).Where(b => normal.ContainsKey(b.BagTag)))
        {
            Assert.Equal(b with { IsFirst = false, IsLast = false }, normal[b.BagTag] with { IsFirst = false, IsLast = false });
        }
    }

    [Fact]
    public void Not_loaded_bags_stop_after_security_and_missing_bags_after_loading()
    {
        foreach (var f in Run(0.05, 0.05))
        {
            foreach (var b in f.Bags.Where(b => b.Fate is not null))
            {
                var kinds = f.Messages.Where(m => m.BagTag == b.BagTag).OrderBy(m => m.TimeUtc).Select(m => m.Kind).ToArray();
                var expected = b.Fate == BagFate.NotLoaded ? new[] { "BSM", "SEC" } : new[] { "BSM", "SEC", "LOAD" };
                Assert.Equal(expected, kinds);
            }
        }
    }

    [Fact]
    public void First_last_bag_and_unloading_completed_count_only_unloaded_bags()
    {
        foreach (var f in Run(0.05, 0.05))
        {
            var delivered = f.Bags.Where(b => b.Fate is null).ToList();
            Assert.Equal(delivered.Max(b => b.UnloadUtc), f.Info.UnloadingCompletedUtc);
            Assert.Null(f.Bags.Single(b => b.IsLast).Fate);
            Assert.Null(f.Bags.Single(b => b.IsFirst).Fate);
            Assert.Equal(delivered.Max(b => b.CarouselUtc), f.Bags.Single(b => b.IsLast).CarouselUtc);
        }
    }

    [Fact]
    public void Milestones_are_scheduled_at_departure_and_unloading_completed()
    {
        var info = Run(0, 0, flights: 1)[0].Info;
        var ms = ContextSender.Milestones(info).ToList();

        Assert.Equal(4, ms.Count); // kalkış, indirme bitti, ilk ve son bavul bantta (W5)
        Assert.All(ms, m => Assert.Equal("MILESTONE", m.Kind));
        Assert.Equal(info.DepartedUtc, ms[0].TimeUtc);
        Assert.Equal(info.UnloadingCompletedUtc, ms[1].TimeUtc);
        Assert.Contains("departedUtc", ms[0].Text);
        Assert.Contains("unloadingCompletedUtc", ms[1].Text);
    }

    [Fact]
    public void Without_read_loss_every_scenario_bag_is_caught_and_no_normal_bag_is_flagged()
    {
        var row = AlertEvaluation.Evaluate(Run(0.02, 0.02), AlertPolicy.Default, 0, 0, 1, "kayıpsız");

        Assert.True(row.NotLoadedTruth > 0 && row.MissingTruth > 0);
        Assert.Equal(row.NotLoadedTruth, row.NotLoadedDetected);
        Assert.Equal(row.MissingTruth, row.MissingDetected);
        Assert.All(row.NotLoadedLatencyMin, m => Assert.Equal(AlertPolicy.Default.NotLoadedGrace.TotalMinutes, m, 6));
        Assert.All(row.MissingLatencyMin, m => Assert.Equal(AlertPolicy.Default.MissingGrace.TotalMinutes, m, 6));
        Assert.Equal(0, row.FalseNotLoaded + row.FalseMissing);
        Assert.Equal(0, row.FalsePassenger);
    }

    [Fact]
    public void Passengers_see_far_fewer_false_alerts_than_staff()
    {
        // Kaçırılan yükleme okumasının yanlış NOT_LOADED'ı çoğunlukla indirme okumasıyla, yolcuya görünmeden kapanır.
        var row = AlertEvaluation.Evaluate(Run(0, 0, flights: 40), AlertPolicy.Default, 0.02, 0, 1, "kayıplı");

        Assert.True(row.FalseNotLoaded > 0);
        Assert.True(row.FalsePassenger * 3 < row.FalseNotLoaded + row.FalseMissing,
            $"yolcu {row.FalsePassenger}, personel {row.FalseNotLoaded + row.FalseMissing}");
    }

    [Fact]
    public void With_minimal_coverage_lost_unload_reads_do_not_reach_passengers_as_false_missing_alerts()
    {
        var flights = SimulationRunner.Run(new SimOptions(40, 120, 11, 'M', Start, Coverage: AeroBag.Application.Coverage.ReadCoverage.Minimal));
        var row = AlertEvaluation.Evaluate(flights, AlertPolicy.Default, 0.02, 0, 1, "kayıplı", AeroBag.Application.Coverage.ReadCoverage.Minimal);

        Assert.True(row.FalseMissing > 0, "kaybolan indirme okuması personel ekranında MISSING üretmeli");
        Assert.True(row.FalsePassenger * 500 <= row.Bags, $"yolcuya giden yanlış uyarı {row.FalsePassenger}/{row.Bags}");
    }

    [Fact]
    public void Not_loaded_becomes_visible_to_the_passenger_at_unloading_completed_plus_grace()
    {
        var info = Run(0, 0, flights: 1)[0].Info;
        var horizon = info.UnloadingCompletedUtc!.Value.AddHours(1);
        var opened = info.DepartedUtc!.Value.AddMinutes(10);

        var open = AlertEvaluation.PassengerWindow((opened, null, BagAlertKind.NotLoaded), info, AlertPolicy.Default, horizon);
        var closedEarly = AlertEvaluation.PassengerWindow((opened, info.UnloadingCompletedUtc.Value.AddMinutes(-20), BagAlertKind.NotLoaded), info, AlertPolicy.Default, horizon);
        var missing = AlertEvaluation.PassengerWindow((info.UnloadingCompletedUtc.Value.AddMinutes(5), null, BagAlertKind.MissingAtArrival), info, AlertPolicy.Default, horizon);

        Assert.Equal(info.UnloadingCompletedUtc.Value + AlertPolicy.Default.MissingGrace, open!.Value.From);
        Assert.Null(closedEarly);
        Assert.Equal(info.UnloadingCompletedUtc.Value.AddMinutes(5), missing!.Value.From);
    }

    [Fact]
    public void Lost_reads_cause_false_alerts_that_later_reads_close()
    {
        var row = AlertEvaluation.Evaluate(Run(0, 0, flights: 40), AlertPolicy.Default, 0.02, 0, 1, "kayıplı");

        Assert.True(row.FalseNotLoaded > 0, "kaybolan yükleme okuması NOT_LOADED üretmeli");
        Assert.Equal(0, row.NotLoadedTruth + row.MissingTruth);
        Assert.True(row.FalseStillOpen < row.FalseNotLoaded + row.FalseMissing, "çoğu yanlış uyarı sonraki okumayla kapanmalı");
    }

    [Fact]
    public void Same_seed_gives_the_same_measurement()
    {
        var flights = Run(0.01, 0.01);
        var a = AlertEvaluation.Report([AlertEvaluation.Evaluate(flights, AlertPolicy.Default, 0.01, 0.005, 3, "x")]);
        var b = AlertEvaluation.Report([AlertEvaluation.Evaluate(flights, AlertPolicy.Default, 0.01, 0.005, 3, "x")]);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Alert_history_opens_at_the_grace_boundary_and_closes_on_the_next_read()
    {
        var f = Run(0, 0, flights: 1)[0];
        var bag = f.Bags[0];
        // Yükleme okuması kayboldu; bavul varışta indirildi.
        var reads = new List<(DateTimeOffset, AeroBag.Domain.BagState)>
        {
            (bag.CheckInUtc, AeroBag.Domain.BagState.CheckedIn),
            (bag.SecurityUtc, AeroBag.Domain.BagState.SecurityScreened),
            (bag.UnloadUtc, AeroBag.Domain.BagState.UnloadedAtArrival),
        };

        var h = AlertEvaluation.Alerts(reads, f.Info, AlertPolicy.Default, bag.ClaimUtc.AddHours(1));

        var alert = Assert.Single(h);
        Assert.Equal(BagAlertKind.NotLoaded, alert.Kind);
        Assert.Equal(f.Info.DepartedUtc!.Value.AddMinutes(10), alert.Opened);
        Assert.Equal(bag.UnloadUtc, alert.Closed);
    }

    [Fact]
    public void Percentile_interpolates()
    {
        Assert.Equal(2.5, AlertEvaluation.Percentile([1, 2, 3, 4], 0.5));
        Assert.Equal(4, AlertEvaluation.Percentile([4], 0.9));
        Assert.True(double.IsNaN(AlertEvaluation.Percentile([], 0.5)));
    }
}
