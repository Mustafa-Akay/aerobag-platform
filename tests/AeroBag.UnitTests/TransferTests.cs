using AeroBag.Application.Coverage;
using AeroBag.Application.TypeB;
using AeroBag.Domain;
using AeroBag.Simulator;

namespace AeroBag.UnitTests;

/// <summary>W10a: aktarma bavulu (durum makinesi, mesaj alanları, simülatör).</summary>
public class TransferTests
{
    private static readonly DateOnly Ref = new(2026, 10, 22);
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    // ---- durum makinesi ----

    [Fact]
    public void Every_state_has_a_rank()
    {
        foreach (var s in Enum.GetValues<BagState>())
        {
            _ = BagStateMachine.Rank(s);
        }
    }

    [Fact]
    public void Onward_row_goes_from_checked_in_through_transfer_sorting_to_loading_without_anomaly()
    {
        var sorted = BagStateMachine.Apply(BagState.CheckedIn, BagState.TransferSorted, BagRoute.TransferOnward);
        Assert.Equal(TransitionOutcome.Applied, sorted.Outcome);

        var loaded = BagStateMachine.Apply(BagState.TransferSorted, BagState.LoadedOnAircraft, BagRoute.TransferOnward);
        Assert.Equal(TransitionOutcome.Applied, loaded.Outcome);
        Assert.Empty(loaded.SkippedStates);
    }

    [Fact]
    public void Onward_row_does_not_need_a_security_read_but_accepts_one()
    {
        Assert.Equal(TransitionOutcome.Applied, BagStateMachine.Apply(BagState.CheckedIn, BagState.SecurityScreened, BagRoute.TransferOnward).Outcome);
        Assert.Equal(TransitionOutcome.Applied, BagStateMachine.Apply(BagState.SecurityScreened, BagState.TransferSorted, BagRoute.TransferOnward).Outcome);
    }

    [Fact]
    public void Missed_transfer_read_is_skipped_but_not_a_reader_anomaly()
    {
        var r = BagStateMachine.Apply(BagState.CheckedIn, BagState.LoadedOnAircraft, BagRoute.TransferOnward);
        Assert.Equal(new[] { BagState.TransferSorted }, r.SkippedStates.ToArray());
        // Aktarma hattının okuması okuma kapsamı profillerinde yer almaz; atlanması "sıra dışı" sayılmaz.
        Assert.False(ReadCoverage.Full.IsAnomaly(r.SkippedStates));
    }

    [Fact]
    public void Arrival_chain_is_unchanged()
    {
        var r = BagStateMachine.Apply(BagState.CheckedIn, BagState.LoadedOnAircraft);
        Assert.Equal(new[] { BagState.SecurityScreened }, r.SkippedStates.ToArray());
    }

    [Fact]
    public void Inbound_row_is_handed_over_after_unloading_and_then_stays_closed()
    {
        var handed = BagStateMachine.Apply(BagState.UnloadedAtArrival, BagState.Transferred);
        Assert.Equal(TransitionOutcome.Applied, handed.Outcome);

        Assert.Equal(TransitionOutcome.IgnoredStale, BagStateMachine.Apply(BagState.Transferred, BagState.InboundChute).Outcome);
        Assert.Equal(TransitionOutcome.IgnoredStale, BagStateMachine.Apply(BagState.Transferred, BagState.OffloadedNoShow).Outcome);
    }

    [Fact]
    public void Hand_over_without_an_unload_read_records_the_skipped_unload()
    {
        var r = BagStateMachine.Apply(BagState.LoadedOnAircraft, BagState.Transferred);
        Assert.Equal(new[] { BagState.UnloadedAtArrival }, r.SkippedStates.ToArray());
    }

    [Fact]
    public void Bag_on_the_carousel_is_not_handed_over()
    {
        Assert.Equal(TransitionOutcome.IgnoredStale, BagStateMachine.Apply(BagState.OnCarousel, BagState.Transferred).Outcome);
    }

    [Fact]
    public void Sorted_onward_bag_can_still_be_offloaded()
    {
        Assert.Equal(BagState.OffloadedNoShow, BagStateMachine.Apply(BagState.TransferSorted, BagState.OffloadedNoShow, BagRoute.TransferOnward).NewState);
    }

    // ---- mesaj alanları ----

    private static ParseResult Parse(string body) => TypeBParser.Parse(body, Ref);

    [Fact]
    public void Bsm_carries_inbound_and_onward_flights()
    {
        var inbound = Parse("BSM\n.V/1LAYT\n.F/ZZ0100/22OCT/AYT/Y\n.O/ZZ5005/22OCT/ADB\n.N/0891000001001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/AB3K7Q\nENDBSM");
        Assert.True(inbound.IsSuccess, inbound.Detail);
        Assert.Equal("ZZ5005-20261022", inbound.Message!.Onward!.FlightId);
        Assert.Equal("ADB", inbound.Message.Onward.Airport);
        Assert.Null(inbound.Message.Inbound);

        var transfer = Parse("BSM\n.V/1LAYT\n.F/ZZ5005/22OCT/ADB/Y\n.I/ZZ0100/22OCT/FRA\n.N/0891000001001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/AB3K7Q\nENDBSM");
        Assert.True(transfer.IsSuccess, transfer.Detail);
        Assert.Equal("ZZ0100-20261022", transfer.Message!.Inbound!.FlightId);

        Assert.True(BaggageEventMapper.TryMap(transfer.Message, Start, out var e, out var err), err);
        Assert.Equal("ZZ0100-20261022", e!.InboundFlightId);
        Assert.Null(e.OnwardFlightId);
    }

    [Theory]
    [InlineData(".I/ZZ0100/22OCT/AYT")]            // kendi uçuşu
    [InlineData(".I/ZZ0200/22OCT/FRA\n.O/ZZ0200/22OCT/ADB")] // gelen ve bağlantı aynı uçuş
    [InlineData(".O/ZZ5005/22OCT")]                 // havalimanı eksik
    [InlineData(".O/ZZ5005/32OCT/ADB")]             // geçersiz gün
    [InlineData(".I/zz0100/22OCT/FRA")]             // küçük harf
    [InlineData(".O/ZZ5005/22OCT/ADB\n.O/ZZ5006/22OCT/ADB")] // tekrar eden alan
    public void Invalid_inbound_or_onward_lines_are_rejected(string lines)
    {
        var r = Parse($"BSM\n.V/1LAYT\n.F/ZZ0100/22OCT/AYT/Y\n{lines}\n.N/0891000001001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/AB3K7Q\nENDBSM");
        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void Transfer_sorter_read_maps_to_transfer_sorted()
    {
        var m = Parse("BPM\n.V/1LAYT\n.J/S/TRANSFER01/22OCT/122500Z\n.F/ZZ5005/22OCT/ADB\n.N/0891000001001\nENDBPM").Message!;
        Assert.True(BaggageEventMapper.TryMap(m, Start, out var e, out var err), err);
        Assert.Equal(BagState.TransferSorted, e!.TargetState);
    }

    // ---- simülatör ----

    private static IReadOnlyList<FlightResult> Run(double transferRate, int flights = 8, int seed = 5) =>
        SimulationRunner.Run(new SimOptions(flights, 60, seed, 'M', Start, TransferRate: transferRate));

    [Fact]
    public void Transfer_rate_zero_changes_nothing_and_other_bags_keep_their_messages()
    {
        var none = Run(0);
        var some = Run(0.15);
        Assert.All(none, f => Assert.Empty(f.OnwardFlights));

        var transferTags = some.SelectMany(f => f.Bags).Where(b => b.Transfer is not null).Select(b => b.BagTag).ToHashSet();
        Assert.NotEmpty(transferTags);
        // Aktarma seçimi ayrı rastgele akıştadır: aktarma olmayan bavulların mesajları aynen kalır.
        var before = none.SelectMany(f => f.Messages).Where(m => !transferTags.Contains(m.BagTag) && m.Kind != "GATE").Select(m => m.Text);
        var after = some.SelectMany(f => f.Messages).Where(m => !transferTags.Contains(m.BagTag) && m.Kind != "GATE").Select(m => m.Text);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Transfer_bags_go_to_a_connecting_flight_instead_of_the_carousel()
    {
        var flights = Run(0.2);
        var onward = flights.SelectMany(f => f.OnwardFlights).GroupBy(o => o.FlightId).Select(g => g.First()).ToList();
        Assert.NotEmpty(onward);
        // Bağlantı uçuşları 20 dk'lık tarife ızgarasında; aynı uçuş her gelen uçuşta aynı görünür.
        Assert.All(onward, o => Assert.Equal(0, (o.ScheduledDepartureUtc - Start).TotalMinutes % 20, 6));
        Assert.All(flights.SelectMany(f => f.OnwardFlights).GroupBy(o => o.FlightId),
            g => Assert.Single(g.Select(o => (o.ScheduledDepartureUtc, o.DepartedUtc)).Distinct()));

        foreach (var f in flights)
        {
            foreach (var b in f.Bags.Where(b => b.Transfer is not null))
            {
                var t = b.Transfer!;
                var kinds = f.Messages.Where(m => m.BagTag == b.BagTag).Select(m => m.Kind).ToList();
                Assert.DoesNotContain("CHUTE", kinds);
                Assert.DoesNotContain("CAROUSEL", kinds);
                Assert.DoesNotContain("CLAIM", kinds);
                Assert.Contains("TBSM", kinds);
                Assert.Contains("TRANSFER", kinds);
                Assert.Equal(!t.MissedConnection, kinds.Contains("TLOAD"));
                Assert.True(t.SortUtc > b.UnloadUtc);
                Assert.Equal(t.ScheduledDepartureUtc - TimeSpan.FromMinutes(15), t.CutoffUtc);
                if (t.OnwardLoadUtc is { } lo)
                {
                    Assert.True(lo <= t.CutoffUtc);
                }
                // Bağlantı süresi gerçekçi aralıkta: inişten en az 40 dk sonra.
                Assert.True(t.ScheduledDepartureUtc >= f.Info.OnBlockUtc.AddMinutes(40));
            }
        }
    }

    [Fact]
    public void Some_tight_connections_are_missed_and_most_are_made()
    {
        var bags = Run(0.2, flights: 30).SelectMany(f => f.Bags).Where(b => b.Transfer is not null).ToList();
        var missed = bags.Count(b => b.Transfer!.MissedConnection);
        Assert.InRange(missed, 1, bags.Count / 2);
    }

    [Fact]
    public void Transfer_messages_parse_and_carry_the_links()
    {
        foreach (var f in Run(0.2))
        {
            foreach (var m in f.Messages.Where(m => m.Kind is "BSM" or "TBSM" or "TRANSFER" or "TLOAD"))
            {
                var parsed = TypeBParser.Parse(m.Text, Ref);
                Assert.True(parsed.IsSuccess, $"{m.Kind}: {parsed.Detail}\n{m.Text}");
                if (m.Kind == "TBSM")
                {
                    Assert.Equal(f.Info.FlightId, parsed.Message!.Inbound!.FlightId);
                }
            }
        }
    }

    [Fact]
    public void Transfer_bags_count_as_unloaded_but_not_as_first_or_last_on_the_belt()
    {
        foreach (var f in Run(0.3))
        {
            Assert.DoesNotContain(f.Bags, b => b.Transfer is not null && (b.IsFirst || b.IsLast));
            var unloaded = f.Bags.Where(b => b.Fate is null or BagFate.Transfer).Max(b => b.UnloadUtc);
            Assert.Equal(unloaded, f.Info.UnloadingCompletedUtc);
        }
    }
}
