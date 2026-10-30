using AeroBag.Application.TypeB;
using AeroBag.Simulator;

namespace AeroBag.UnitTests;

/// <summary>W7: yük testinin ürettiği mesajlar gerçek ayrıştırıcıdan geçer ve her bavul tam yaşam döngüsünü sırayla taşır.</summary>
public class LoadTestTests
{
    [Fact]
    public void Each_bag_gets_seven_valid_messages_in_lifecycle_order()
    {
        var date = new DateOnly(2026, 10, 30);
        var bag = LoadTest.Lifecycle(new Random(1), date, new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero));

        Assert.Equal(7, bag.Count);
        var states = bag.Select(text =>
        {
            var parsed = TypeBParser.Parse(text, date);
            Assert.True(parsed.IsSuccess, parsed.Detail);
            Assert.True(BaggageEventMapper.TryMap(parsed.Message!, DateTimeOffset.UtcNow, out var ev, out var error), error);
            return ev!.TargetState;
        }).ToList();
        Assert.Equal(states.OrderBy(s => (int)s), states);
        Assert.Equal(7, states.Distinct().Count());
    }

    [Fact]
    public void Bags_do_not_collide()
    {
        var rng = new Random(2);
        var date = new DateOnly(2026, 10, 30);
        var tags = Enumerable.Range(0, 2000)
            .Select(_ => LoadTest.Lifecycle(rng, date, new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero))[0].Split('\n')[3])
            .ToList();
        Assert.Equal(tags.Count, tags.Distinct().Count());
    }
}
