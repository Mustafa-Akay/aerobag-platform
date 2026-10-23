using AeroBag.Application.TypeB;
using AeroBag.Domain;
using AeroBag.Simulator;

namespace AeroBag.UnitTests;

public class SimulatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] LifecycleOrder = ["BSM", "SEC", "LOAD", "BUM", "CHUTE", "CAROUSEL", "CLAIM"];

    private static IReadOnlyList<FlightResult> Run(int seed = 7, char regime = 'M', int flights = 3, int bags = 30) =>
        SimulationRunner.Run(new SimOptions(flights, bags, seed, regime, Start));

    [Fact]
    public void Same_seed_produces_identical_messages_and_different_seed_does_not()
    {
        var a = Run(seed: 1).SelectMany(f => f.Messages).Select(m => m.Text).ToList();
        var b = Run(seed: 1).SelectMany(f => f.Messages).Select(m => m.Text).ToList();
        var c = Run(seed: 2).SelectMany(f => f.Messages).Select(m => m.Text).ToList();

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Every_generated_message_parses_and_maps()
    {
        var reference = new DateOnly(2026, 10, 22);
        foreach (var message in Run().SelectMany(f => f.Messages))
        {
            var parsed = TypeBParser.Parse(message.Text, reference);
            Assert.True(parsed.IsSuccess, $"{message.Kind}: {parsed.Detail}\n{message.Text}");
            Assert.True(BaggageEventMapper.TryMap(parsed.Message!, message.TimeUtc, out _, out var error), error);
        }
    }

    [Fact]
    public void Each_bag_has_seven_messages_in_lifecycle_order()
    {
        foreach (var flight in Run())
        {
            foreach (var group in flight.Messages.GroupBy(m => m.BagTag))
            {
                var kinds = group.OrderBy(m => m.TimeUtc).Select(m => m.Kind).ToArray();
                Assert.Equal(LifecycleOrder, kinds);
            }
            Assert.Equal(flight.Info.TotalBags * 7, flight.Messages.Count);
        }
    }

    [Fact]
    public void Bag_tags_are_unique_ten_digit_numbers_across_flights()
    {
        var tags = Run().SelectMany(f => f.Bags).Select(b => b.BagTag).ToList();
        Assert.Equal(tags.Count, tags.Distinct().Count());
        Assert.All(tags, t => Assert.Matches(@"^\d{10}$", t));
    }

    [Fact]
    public void Exactly_one_first_and_one_last_bag_per_flight()
    {
        foreach (var f in Run())
        {
            var first = Assert.Single(f.Bags, b => b.IsFirst);
            var last = Assert.Single(f.Bags, b => b.IsLast);
            Assert.Equal(f.Bags.Min(b => b.CarouselUtc), first.CarouselUtc);
            Assert.Equal(f.Bags.Max(b => b.CarouselUtc), last.CarouselUtc);
        }
    }

    [Fact]
    public void Chute_to_carousel_time_follows_distance_over_speed()
    {
        foreach (var f in Run())
        {
            var expected = f.Info.ChuteToCarouselM / f.Info.ConveyorSpeedMps;
            foreach (var b in f.Bags)
            {
                var actual = (b.CarouselUtc - b.ChuteUtc).TotalSeconds;
                Assert.InRange(actual, expected * 0.85, expected * 1.15);
            }
        }
    }

    [Fact]
    public void Slow_regime_delays_first_bag_more_than_normal_regime()
    {
        double MeanFirstBag(char regime) => Run(seed: 11, regime: regime, flights: 40, bags: 20)
            .Average(f => (f.Bags.Single(b => b.IsFirst).CarouselUtc - f.Info.OnBlockUtc).TotalSeconds);

        Assert.True(MeanFirstBag('B') > MeanFirstBag('A') * 1.2);
    }

    [Fact]
    public void Replaying_messages_through_the_state_machine_reaches_ClaimedFromBelt_without_anomalies()
    {
        var reference = new DateOnly(2026, 10, 22);
        var states = new Dictionary<string, BagState?>();

        foreach (var message in Run().SelectMany(f => f.Messages).OrderBy(m => m.TimeUtc))
        {
            var parsed = TypeBParser.Parse(message.Text, reference).Message!;
            Assert.True(BaggageEventMapper.TryMap(parsed, message.TimeUtc, out var ev, out _));

            states.TryGetValue(ev!.BagTag, out var current);
            var result = BagStateMachine.Apply(current, ev.TargetState);

            Assert.Equal(TransitionOutcome.Applied, result.Outcome);
            states[ev.BagTag] = result.NewState;
        }

        Assert.All(states.Values, s => Assert.Equal(BagState.ClaimedFromBelt, s));
    }
}
