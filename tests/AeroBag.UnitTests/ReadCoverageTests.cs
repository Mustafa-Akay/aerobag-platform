using AeroBag.Application.Alerts;
using AeroBag.Application.Coverage;
using AeroBag.Application.ExitGuard;
using AeroBag.Domain;
using AeroBag.Simulator;

namespace AeroBag.UnitTests;

/// <summary>W5: okuma kapsamı profilleri ve sistemin onlara uyumu.</summary>
public class ReadCoverageTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("full", "full")]
    [InlineData(" Typical ", "typical")]
    [InlineData("MINIMAL", "minimal")]
    [InlineData(null, "full")]
    public void Profiles_parse_case_insensitively_and_default_to_full(string? input, string expected)
    {
        Assert.Equal(expected, ReadCoverage.Parse(input).Name);
    }

    [Fact]
    public void Unknown_profile_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => ReadCoverage.Parse("partial"));
    }

    [Fact]
    public void Skipping_a_point_without_a_reader_is_not_an_anomaly()
    {
        // Tipik kapsam: şuttan çıkışa (bant ağzı ve banttan alma atlanır) beklenen akıştır.
        Assert.False(ReadCoverage.Typical.IsAnomaly([BagState.OnCarousel, BagState.ClaimedFromBelt]));
        Assert.True(ReadCoverage.Full.IsAnomaly([BagState.OnCarousel, BagState.ClaimedFromBelt]));
        // Tipik kapsamda indirme okuyucusu var: atlanması okuma kaçırmasıdır.
        Assert.True(ReadCoverage.Typical.IsAnomaly([BagState.UnloadedAtArrival]));
        // En az kapsamda güvenlik okuyucusu yok.
        Assert.False(ReadCoverage.Minimal.IsAnomaly([BagState.SecurityScreened]));
    }

    [Fact]
    public void Last_arrival_state_follows_the_furthest_reader_before_exit()
    {
        Assert.Equal(BagState.OnCarousel, ReadCoverage.Full.LastArrivalState);
        Assert.Equal(BagState.InboundChute, ReadCoverage.Typical.LastArrivalState);
        Assert.Equal(BagState.UnloadedAtArrival, ReadCoverage.Minimal.LastArrivalState);
    }

    [Fact]
    public void Exit_guard_does_not_flag_a_bag_at_the_chute_when_the_carousel_has_no_reader()
    {
        byte[] owner = [1, 2, 3];
        var full = ExitGuardRules.Decide(BagState.InboundChute, owner, owner, 0.9, 0.6);
        var typical = ExitGuardRules.Decide(BagState.InboundChute, owner, owner, 0.9, 0.6, ReadCoverage.Typical.LastArrivalState);
        var early = ExitGuardRules.Decide(BagState.UnloadedAtArrival, owner, owner, 0.9, 0.6, ReadCoverage.Typical.LastArrivalState);

        Assert.Equal("NOT_ON_CAROUSEL", full.Reason);
        Assert.Equal(GateDecision.ClearedOwner, typical.Decision);
        Assert.Equal(GateDecision.OutOfSequence, early.Decision);
        Assert.Equal("NOT_ARRIVED", early.Reason);
    }

    private static IReadOnlyList<FlightResult> Run(ReadCoverage coverage) =>
        SimulationRunner.Run(new SimOptions(3, 40, 5, 'M', Start, Coverage: coverage));

    [Theory]
    [InlineData("typical", new[] { "BSM", "SEC", "LOAD", "BUM", "CHUTE" })]
    [InlineData("minimal", new[] { "BSM", "LOAD", "BUM" })]
    [InlineData("full", new[] { "BSM", "SEC", "LOAD", "BUM", "CHUTE", "CAROUSEL", "CLAIM" })]
    public void Simulator_emits_only_the_reads_of_the_profile(string profile, string[] kinds)
    {
        foreach (var f in Run(ReadCoverage.Parse(profile)))
        {
            foreach (var bag in f.Messages.GroupBy(m => m.BagTag))
            {
                Assert.Equal(kinds, bag.OrderBy(m => m.TimeUtc).Select(m => m.Kind).ToArray());
            }
        }
    }

    [Fact]
    public void Coverage_does_not_change_what_really_happened()
    {
        var full = Run(ReadCoverage.Full).SelectMany(f => f.Bags).ToList();
        var minimal = Run(ReadCoverage.Minimal).SelectMany(f => f.Bags).ToList();
        Assert.Equal(full, minimal);
    }

    [Fact]
    public void Belt_milestones_are_the_first_and_last_bag_on_the_belt()
    {
        foreach (var f in Run(ReadCoverage.Typical))
        {
            Assert.Equal(f.Bags.Single(b => b.IsFirst).CarouselUtc, f.Info.FirstBagOnBeltUtc);
            Assert.Equal(f.Bags.Single(b => b.IsLast).CarouselUtc, f.Info.LastBagOnBeltUtc);
            var ms = ContextSender.Milestones(f.Info).Select(m => m.Text).ToList();
            Assert.Contains(ms, t => t.Contains("firstBagOnBeltUtc"));
            Assert.Contains(ms, t => t.Contains("lastBagOnBeltUtc"));
        }
    }

    [Fact]
    public void Belt_milestones_are_validated()
    {
        var d = Start.AddHours(-3);
        Assert.Null(new FlightMilestones("ZZ0100-20261022", FirstBagOnBeltUtc: Start.AddMinutes(8)).Validate());
        Assert.Null(new FlightMilestones("ZZ0100-20261022", FirstBagOnBeltUtc: Start.AddMinutes(8), LastBagOnBeltUtc: Start.AddMinutes(20)).Validate());
        Assert.NotNull(new FlightMilestones("ZZ0100-20261022", FirstBagOnBeltUtc: Start.AddMinutes(20), LastBagOnBeltUtc: Start.AddMinutes(8)).Validate());
        Assert.NotNull(new FlightMilestones("ZZ0100-20261022", DepartedUtc: d, FirstBagOnBeltUtc: d.AddMinutes(-1)).Validate());
    }
}
