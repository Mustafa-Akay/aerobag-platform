using System.Text.Json;
using AeroBag.Simulator;

namespace AeroBag.UnitTests;

public class SimulatorGateTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<FlightResult> Run(bool gate, int flights = 20) =>
        SimulationRunner.Run(new SimOptions(flights, 120, 42, 'M', Start, ExitScans: true, GatePackets: gate));

    [Fact]
    public void Gate_mode_changes_only_pnrs_and_exit_messages_not_the_timeline()
    {
        var bpm = Run(gate: false);
        var gate = Run(gate: true);

        var a = bpm.SelectMany(f => f.Bags).Select(b => (b.BagTag, b.UnloadUtc, b.CarouselUtc, b.ClaimUtc)).ToList();
        var b = gate.SelectMany(f => f.Bags).Select(b => (b.BagTag, b.UnloadUtc, b.CarouselUtc, b.ClaimUtc)).ToList();
        Assert.Equal(a, b);

        Assert.DoesNotContain(gate.SelectMany(f => f.Messages), m => m.Kind == "EXIT");
        Assert.DoesNotContain(bpm.SelectMany(f => f.Messages), m => m.Kind == "GATE");
    }

    [Fact]
    public void Some_bags_share_a_booking_in_gate_mode()
    {
        var bags = Run(gate: true).SelectMany(f => f.Bags).ToList();
        var shared = bags.GroupBy(b => (b.FlightId, b.Pnr)).Count(g => g.Count() > 1);
        Assert.True(shared > 0);
    }

    [Fact]
    public void Gate_packets_are_valid_json_and_every_tag_has_a_truth()
    {
        var packets = Run(gate: true).SelectMany(f => f.Messages).Where(m => m.Kind == "GATE").ToList();
        Assert.NotEmpty(packets);
        foreach (var m in packets.Take(200))
        {
            using var doc = JsonDocument.Parse(m.Text);
            var tags = doc.RootElement.GetProperty("detectedBagTags").EnumerateArray().Select(t => t.GetString()!).ToList();
            Assert.Equal(tags, m.Gate!.Tags);
            Assert.All(tags, t => Assert.True(m.Gate.Truth.ContainsKey(t)));
            Assert.InRange(tags.Count, 1, 2);
        }
        var truths = packets.SelectMany(p => p.Gate!.Truth.Values).ToHashSet();
        Assert.Contains(GateTruth.Owner, truths);
        Assert.Contains(GateTruth.Family, truths);
    }

    [Fact]
    public void Evaluation_never_clears_someone_from_another_booking()
    {
        var flights = Run(gate: true, flights: 60);
        var row = GateEvaluation.Evaluate(flights, 0.6);

        Assert.Equal(0, row.Count(t => t == GateTruth.WrongPerson, "CLEARED_OWNER"));
        var legit = row.Count(GateTruth.IsLegit);
        // Doğrulamasız (%3) ve eşik altı zayıf okumalar (~%11) kararsız kalır; meşru okumaların büyük çoğunluğu geçmelidir.
        Assert.True(row.Count(GateTruth.IsLegit, "CLEARED_OWNER") > legit * 0.75);
    }

    [Fact]
    public void Lower_threshold_trades_fewer_undecided_reads_for_more_false_alarms()
    {
        var flights = Run(gate: true, flights: 60);
        var low = GateEvaluation.Evaluate(flights, 0.3);
        var high = GateEvaluation.Evaluate(flights, 0.8);

        Assert.True(low.Count(GateTruth.IsLegit, "REVIEW_MISMATCH") >= high.Count(GateTruth.IsLegit, "REVIEW_MISMATCH"));
        Assert.True(low.Count(GateTruth.IsLegit, "NO_DECISION") <= high.Count(GateTruth.IsLegit, "NO_DECISION"));
        Assert.Contains("Eşik", GateEvaluation.Report([low, high]));
    }
}
