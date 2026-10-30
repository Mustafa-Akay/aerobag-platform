using AeroBag.Application.Coverage;
using AeroBag.Domain;
using AeroBag.Simulator;

namespace AeroBag.UnitTests;

/// <summary>W5b: okuma kapsamına duyarlılık ölçümü.</summary>
public class CoverageEvaluationTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static CoverageEvaluation.Row Eval(ReadCoverage c, double drop = 0) =>
        CoverageEvaluation.Evaluate(SimulationRunner.Run(new SimOptions(15, 80, 3, 'M', Start, Coverage: c)), c, drop, 0, 1);

    [Fact]
    public void Full_coverage_without_loss_tells_the_passenger_exactly_when_the_bag_arrives()
    {
        var r = Eval(ReadCoverage.Full);
        Assert.All(r.ToldDelayMin, d => Assert.Equal(0, d, 6));
        Assert.Equal(0, r.ToldEarly + r.NeverTold);
        Assert.Equal(r.Flights, r.FirstFromReads);
        Assert.Equal(r.Flights, r.LastFromReads);
        Assert.Equal(0, r.NaiveAnomalies + r.AwareAnomalies);
    }

    [Fact]
    public void Typical_coverage_tells_shortly_after_arrival_from_the_chute_physics()
    {
        var r = Eval(ReadCoverage.Typical);
        Assert.Equal(0, r.NeverTold);
        // Fizik payı 15 sn; yol süresi gürültüsü (sd %3) onu çok nadiren aşar.
        Assert.True(r.ToldEarly * 100 <= r.Bags, $"erken söylenen {r.ToldEarly}/{r.Bags}");
        Assert.All(r.ToldDelayMin, d => Assert.InRange(d, -0.2, 1));
        Assert.Equal(r.Flights, r.FirstFromAodb);
        Assert.Equal(r.Flights, r.LastFromAodb);
        Assert.All(r.FirstErrorMin, e => Assert.Equal(0, e, 6));
    }

    [Fact]
    public void Knowing_the_coverage_removes_the_false_anomalies()
    {
        var typical = Eval(ReadCoverage.Typical);
        var minimal = Eval(ReadCoverage.Minimal);

        // Kapsamı bilmeyen kural: tipikte her bavul şuttan çıkışa, en azda BSM'den yüklemeye atlar.
        Assert.True(typical.NaiveAnomalies >= typical.Bags);
        Assert.True(minimal.NaiveAnomalies >= minimal.Bags);
        Assert.Equal(0, typical.AwareAnomalies);
        Assert.Equal(0, minimal.AwareAnomalies);
    }

    [Fact]
    public void Lost_reads_still_count_as_anomalies_for_points_that_have_readers()
    {
        var r = Eval(ReadCoverage.Typical, drop: 0.05);
        Assert.True(r.AwareAnomalies > 0);
        Assert.True(r.AwareAnomalies < r.NaiveAnomalies);
    }

    [Fact]
    public void Minimal_coverage_falls_back_to_the_default_window_after_unloading()
    {
        var flight = SimulationRunner.Run(new SimOptions(1, 30, 3, 'M', Start, Coverage: ReadCoverage.Minimal))[0];
        var bag = flight.Bags[0];
        var reads = new List<(DateTimeOffset, BagState)> { (bag.UnloadUtc, BagState.UnloadedAtArrival) };

        var told = CoverageEvaluation.ToldOnBelt(reads, flight.Info, ReadCoverage.Minimal);

        Assert.Equal(bag.UnloadUtc.AddSeconds(AeroBag.Application.Eta.EtaEstimator.FallbackUnloadToCarousel.P90Sec), told);
    }
}
