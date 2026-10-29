using System.Text;
using AeroBag.Infrastructure;

namespace AeroBag.IntegrationTests;

public class PassengerLookupTests(PostgresFixture fx) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly LookupKeyHasher Hasher = new(Encoding.UTF8.GetBytes("test-secret-0123456789abcdef"));

    private BaggageEventProcessor Processor() => new(fx.Db, new FixedClock(Now), Hasher);

    private PassengerQueries Queries() => new(fx.Db, Hasher, new EtaStatistics(fx.Db), new FixedClock(Now));

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    private static string NewPnr() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static string Bsm(string tag, string pnr, string surname) =>
        $"BSM\n.V/1LAYT\n.F/ZZ0124/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/{surname}/AYSE\n.L/{pnr}\nENDBSM";

    private static string Bum(string tag) =>
        $"BUM\n.V/1LAYT\n.F/ZZ0124/22OCT/AYT\n.N/{tag}001\n.U/STAND104/20261022T121000Z\nENDBUM";

    [Fact]
    public async Task Passenger_is_stored_only_as_hmac_key_and_linked_to_bag()
    {
        var tag = NewTag();
        await Processor().ProcessAsync(Bsm(tag, NewPnr(), "YILMAZ"), "test");

        await using var cmd = fx.Db.CreateCommand(
            """
            SELECT octet_length(p.lookup_key)
            FROM passengers p JOIN bags b ON b.passenger_id = p.passenger_id
            WHERE b.bag_tag_number = @t
            """);
        cmd.Parameters.AddWithValue("t", tag);
        Assert.Equal(32, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Lookup_finds_bag_with_case_and_spacing_differences_in_surname()
    {
        var tag = NewTag();
        var pnr = NewPnr();
        await Processor().ProcessAsync(Bsm(tag, pnr, "YILMAZ"), "test");

        var result = await Queries().LookupAsync(pnr.ToLowerInvariant(), "yil maz");

        Assert.NotNull(result);
        var bag = Assert.Single(result!.Bags);
        Assert.Equal(tag, bag.BagTag);
        Assert.Equal("CHECKED_IN", bag.State);
        Assert.Null(bag.Eta);
    }

    [Fact]
    public async Task Wrong_surname_or_wrong_pnr_gives_the_same_empty_result()
    {
        var tag = NewTag();
        var pnr = NewPnr();
        await Processor().ProcessAsync(Bsm(tag, pnr, "YILMAZ"), "test");

        Assert.Null(await Queries().LookupAsync(pnr, "KAYA"));
        Assert.Null(await Queries().LookupAsync(NewPnr(), "YILMAZ"));
    }

    [Fact]
    public async Task Reused_pnr_returns_only_the_latest_trip()
    {
        // PNR'lar zamanla yeniden kullanılır; simülatör de her gün aynı tohumla aynı PNR, soyad ve etiketi üretir.
        var tag = NewTag();
        var pnr = NewPnr();
        await Processor().ProcessAsync($"BSM\n.V/1LAYT\n.F/ZZ0130/20OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/{pnr}\nENDBSM", "test");
        await Processor().ProcessAsync($"BSM\n.V/1LAYT\n.F/ZZ0130/21OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/{pnr}\nENDBSM", "test");

        var bag = Assert.Single((await Queries().LookupAsync(pnr, "YILMAZ"))!.Bags);
        Assert.Equal("ZZ0130-20261021", bag.FlightId);
    }

    [Fact]
    public async Task Unloaded_bag_gets_an_ordered_eta_window()
    {
        var tag = NewTag();
        var pnr = NewPnr();
        await Processor().ProcessAsync(Bsm(tag, pnr, "YILMAZ"), "test");
        await Processor().ProcessAsync(Bum(tag), "test");

        var result = await Queries().LookupAsync(pnr, "YILMAZ");

        var bag = Assert.Single(result!.Bags);
        Assert.Equal("UNLOADED_AT_ARRIVAL", bag.State);
        Assert.NotNull(bag.Eta);
        Assert.True(bag.Eta!.P10Utc <= bag.Eta.P50Utc && bag.Eta.P50Utc <= bag.Eta.P90Utc);

        // BSM'in alınma zamanı (14:00) referans anı değildir; referans, son zaman taşıyan olaydır (BUM, 12:10).
        // Geçmiş veri yok, sabit varsayılan aralık: 300/540/900 saniye.
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 12, 10, 0, TimeSpan.Zero), result.AsOfUtc);
        Assert.Equal("FALLBACK", bag.Eta.Basis);
        Assert.Equal(300, bag.Eta.P10SecondsFromNow);
        Assert.Equal(540, bag.Eta.P50SecondsFromNow);
        Assert.Equal(900, bag.Eta.P90SecondsFromNow);
    }

    [Fact]
    public async Task Family_bags_under_one_pnr_are_returned_together()
    {
        var pnr = NewPnr();
        var tags = new[] { NewTag(), NewTag() };
        foreach (var tag in tags)
        {
            await Processor().ProcessAsync(Bsm(tag, pnr, "YILMAZ"), "test");
        }

        var result = await Queries().LookupAsync(pnr, "YILMAZ");

        Assert.Equal(tags.Order().ToArray(), result!.Bags.Select(b => b.BagTag).Order().ToArray());
    }

    [Fact]
    public async Task Flights_summary_counts_bags_per_flight()
    {
        var tag = NewTag();
        await Processor().ProcessAsync(Bsm(tag, NewPnr(), "YILMAZ"), "test");

        var flights = await new OpsQueries(fx.Db).FlightsAsync();

        var flight = Assert.Single(flights, f => f.FlightId == "ZZ0124-20261022");
        Assert.True(flight.TotalBags >= 1);
        Assert.True(flight.BeforeArrival >= 1);
    }
}
