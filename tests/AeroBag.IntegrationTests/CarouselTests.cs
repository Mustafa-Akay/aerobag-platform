using System.Net;
using System.Text;
using System.Text.Json;
using AeroBag.Application.Ml;
using AeroBag.Application.Time;
using AeroBag.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace AeroBag.IntegrationTests;

/// <summary>
/// W3: bant numarası. AODB bağlamıyla gelir, yolcu sorgusunda ve operasyon tablosunda görünür;
/// atanınca ya da değişince o uçuşun bavulları için canlı bildirim (outbox) yazılır.
/// </summary>
public sealed class CarouselTests(PostgresFixture fx) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Wall = new(2026, 10, 22, 23, 0, 0, TimeSpan.Zero);
    private static readonly LookupKeyHasher Hasher = new(Encoding.UTF8.GetBytes("test-secret-0123456789abcdef"));

    private readonly WebApplicationFactory<Program> _factory = TestApi.Create(fx);

    public void Dispose() => _factory.Dispose();

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    private static string NewPnr() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static FlightContext Ctx(string flightId, string? carousel) =>
        new(flightId, new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero), false, false, 10, 500, 50, 0.5, 2, "HANDLER_A", carousel);

    private PassengerQueries Queries() =>
        new(fx.Db, Hasher, new EtaStatistics(fx.Db), new FixedTime(Wall), operationalClock: new FixedOperationalClock(Wall));

    /// <summary>Bir yolcu ve bavulu oluşturur, isteğe bağlı olarak bavulu bant ağzına getirir.</summary>
    private async Task<(string Tag, string Pnr)> BagAsync(string flightNo, bool onCarousel = false)
    {
        var tag = NewTag();
        var pnr = NewPnr();
        var p = new BaggageEventProcessor(fx.Db, new FixedTime(Wall), Hasher);
        await p.ProcessAsync($"BSM\n.V/1LAYT\n.F/{flightNo}/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/{pnr}\nENDBSM", "test");
        if (onCarousel)
        {
            await p.ProcessAsync($"BPM\n.V/1LAYT\n.J/S/CAROUSEL04/22OCT/121500Z\n.F/{flightNo}/22OCT/AYT\n.N/{tag}001\nENDBPM", "test");
        }
        return (tag, pnr);
    }

    private async Task<List<JsonElement>> CarouselNoticesAsync(string flightId)
    {
        await using var cmd = fx.Db.CreateCommand(
            "SELECT payload::text FROM outbox_messages WHERE topic = 'flight.context' AND partition_key = @id ORDER BY seq");
        cmd.Parameters.AddWithValue("id", flightId);
        var list = new List<JsonElement>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(JsonDocument.Parse(r.GetString(0)).RootElement.Clone());
        }
        return list;
    }

    [Fact]
    public async Task Lookup_shows_no_carousel_until_one_is_assigned()
    {
        var (_, pnr) = await BagAsync("ZZ0701");
        var before = await Queries().LookupAsync(pnr, "YILMAZ");
        Assert.Null(Assert.Single(before!.Bags).Carousel);

        await new FlightContextStore(fx.Db).UpsertAsync(Ctx("ZZ0701-20261022", "4"));

        var after = await Queries().LookupAsync(pnr, "YILMAZ");
        Assert.Equal("4", Assert.Single(after!.Bags).Carousel);
    }

    [Fact]
    public async Task Carousel_is_returned_for_a_bag_already_on_the_belt()
    {
        // Bant bilgisinin en çok işe yaradığı an: bavul bantta (ML/fizik bağlamına artık gerek yokken de gelmeli).
        var (_, pnr) = await BagAsync("ZZ0702", onCarousel: true);
        await new FlightContextStore(fx.Db).UpsertAsync(Ctx("ZZ0702-20261022", "4"));

        var bag = Assert.Single((await Queries().LookupAsync(pnr, "YILMAZ"))!.Bags);
        Assert.Equal("ON_CAROUSEL", bag.State);
        Assert.Equal("4", bag.Carousel);
    }

    [Fact]
    public async Task Changed_carousel_is_returned_and_announced_for_every_bag_of_the_flight()
    {
        var id = "ZZ0703-20261022";
        var (tag1, pnr) = await BagAsync("ZZ0703");
        var (tag2, _) = await BagAsync("ZZ0703");
        var store = new FlightContextStore(fx.Db);

        Assert.True(await store.UpsertAsync(Ctx(id, "4")));
        Assert.True(await store.UpsertAsync(Ctx(id, "7")));

        Assert.Equal("7", Assert.Single((await Queries().LookupAsync(pnr, "YILMAZ"))!.Bags).Carousel);

        var notices = await CarouselNoticesAsync(id);
        Assert.Equal(2, notices.Count);
        var last = notices[^1];
        Assert.Equal("7", last.GetProperty("carousel").GetString());
        Assert.Equal("4", last.GetProperty("previousCarousel").GetString());
        var tags = last.GetProperty("bagTags").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains(tag1, tags);
        Assert.Contains(tag2, tags);
    }

    [Fact]
    public async Task Resending_the_same_carousel_or_none_keeps_it_and_announces_nothing()
    {
        var id = "ZZ0704-20261022";
        await BagAsync("ZZ0704");
        var store = new FlightContextStore(fx.Db);
        await store.UpsertAsync(Ctx(id, "4"));

        Assert.False(await store.UpsertAsync(Ctx(id, "4")));
        Assert.False(await store.UpsertAsync(Ctx(id, null)));

        Assert.Equal("4", (await store.GetAsync([id]))[id].Carousel);
        Assert.Single(await CarouselNoticesAsync(id));
    }

    [Fact]
    public async Task Ops_table_shows_the_carousel()
    {
        var id = "ZZ0705-20261022";
        await BagAsync("ZZ0705");
        await BagAsync("ZZ0706");
        await new FlightContextStore(fx.Db).UpsertAsync(Ctx(id, "B12"));

        var flights = await new OpsQueries(fx.Db, new FixedOperationalClock(Wall)).FlightsAsync();

        Assert.Equal("B12", flights.Single(f => f.FlightId == id).Carousel);
        Assert.Null(flights.Single(f => f.FlightId == "ZZ0706-20261022").Carousel);
    }

    private static string ContextJson(string flightId, string? carousel) => JsonSerializer.Serialize(new
    {
        flightId, onBlockUtc = "2026-10-22T12:00:00Z", wide = false, rain = false, bags = 100,
        standM = 500.0, chuteM = 50.0, speedMps = 0.5, arrivals30 = 2, handler = "HANDLER_A", carousel,
    });

    [Fact]
    public async Task Context_endpoint_stores_the_carousel_and_rejects_a_malformed_one()
    {
        var id = "ZZ0707-20261022";
        var client = _factory.CreateClient();

        var ok = await client.SendAsync(TestApi.Signed("/v1/flights/context", ContextJson(id, "5"), "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);

        foreach (var bad in new[] { "bant 4", "4;DROP", "", "123456789", "4\n" })
        {
            var resp = await client.SendAsync(TestApi.Signed("/v1/flights/context", ContextJson(id, bad), "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }
        Assert.Equal("5", (await new FlightContextStore(fx.Db).GetAsync([id]))[id].Carousel);
    }

    [Fact]
    public async Task Database_rejects_a_malformed_carousel_even_if_the_api_check_is_bypassed()
    {
        await using var cmd = fx.Db.CreateCommand(
            """
            INSERT INTO flight_context (flight_id, on_block_utc, wide, rain, bags, stand_m, chute_m, speed_mps, arrivals_30, handler, carousel)
            VALUES ('ZZ0708-20261022', '2026-10-22T12:00:00Z', false, false, 10, 500, 50, 0.5, 2, 'HANDLER_A', 'bant 4')
            """);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }
}
