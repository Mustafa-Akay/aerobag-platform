using AeroBag.Application.TypeB;
using AeroBag.Domain;

namespace AeroBag.UnitTests;

public class BaggageEventMapperTests
{
    private static readonly DateOnly Ref = new(2026, 10, 22);
    private static readonly DateTimeOffset T1 = new(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddSeconds(30);

    private static TypeBMessage Msg(string text) => TypeBParser.Parse(text, Ref).Message!;

    private const string Bsm = "BSM\n.V/1LAYT\n.F/ZZ0124/22OCT/AYT/Y\n.N/0891458921001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/AB3K7Q\nENDBSM";
    private const string Bum = "BUM\n.V/1LAYT\n.F/ZZ0124/22OCT/AYT\n.N/0891458921001\n.U/STAND104/20261022T211410Z\nENDBUM";

    private static string Bpm(string point) =>
        $"BPM\n.V/1LAYT\n.J/S/{point}/22OCT/212100Z\n.F/ZZ0124/22OCT/AYT\n.N/0891458921001\nENDBPM";

    private static BaggageEvent Map(string text, DateTimeOffset received)
    {
        Assert.True(BaggageEventMapper.TryMap(Msg(text), received, out var e, out var err), err);
        return e!;
    }

    [Theory]
    [InlineData("SEC01", BagState.SecurityScreened)]
    [InlineData("LOAD02", BagState.LoadedOnAircraft)]
    [InlineData("CHUTE04", BagState.InboundChute)]
    [InlineData("CAROUSEL04", BagState.OnCarousel)]
    [InlineData("CLAIM04", BagState.ClaimedFromBelt)]
    public void Bpm_scan_point_prefix_selects_state(string point, BagState expected)
    {
        Assert.Equal(expected, Map(Bpm(point), T1).TargetState);
    }

    [Fact]
    public void Unknown_scan_point_cannot_be_mapped()
    {
        Assert.False(BaggageEventMapper.TryMap(Msg(Bpm("XYZZY01")), T1, out var e, out var err));
        Assert.Null(e);
        Assert.NotNull(err);
    }

    [Fact]
    public void Bsm_maps_to_CheckedIn_and_duplicate_with_later_receive_time_has_same_key()
    {
        var a = Map(Bsm, T1);
        var b = Map(Bsm, T2);
        Assert.Equal(BagState.CheckedIn, a.TargetState);
        Assert.Equal(a.IdempotencyKey, b.IdempotencyKey);
    }

    [Fact]
    public void Bum_uses_message_time_and_ignores_receive_time_in_key()
    {
        var a = Map(Bum, T1);
        var b = Map(Bum, T2);
        Assert.Equal(BagState.UnloadedAtArrival, a.TargetState);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 21, 14, 10, TimeSpan.Zero), a.OccurredAtUtc);
        Assert.Equal(a.IdempotencyKey, b.IdempotencyKey);
    }

    [Fact]
    public void Different_states_for_same_bag_have_different_keys()
    {
        Assert.NotEqual(Map(Bpm("CHUTE04"), T1).IdempotencyKey, Map(Bpm("CAROUSEL04"), T1).IdempotencyKey);
    }
}
