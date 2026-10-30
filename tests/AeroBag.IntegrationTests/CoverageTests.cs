using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AeroBag.Application.Alerts;
using AeroBag.Application.Coverage;
using AeroBag.Application.Ml;
using AeroBag.Application.Time;
using AeroBag.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AeroBag.IntegrationTests;

/// <summary>
/// W5: okuma kapsamı. Sınıfın API'si tipik kapsamla (bant ağzı ve banttan alma okuması yok) açılır; doğrudan servis testleri
/// kapsamı açıkça verir.
/// </summary>
public sealed class CoverageTests(PostgresFixture fx) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Wall = new(2026, 10, 22, 23, 0, 0, TimeSpan.Zero);
    private static readonly LookupKeyHasher Hasher = new(Encoding.UTF8.GetBytes("test-secret-0123456789abcdef"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _typicalApi = TestApi.Create(fx, ("Coverage:Profile", "typical"));

    public void Dispose() => _typicalApi.Dispose();

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DateTimeOffset At(string hhmmss) => DateTimeOffset.ParseExact($"20261022{hhmmss}", "yyyyMMddHHmmss",
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(CultureInfo.InvariantCulture);

    private static string NewPnr() => "C" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();

    private static string Bsm(string flightNo, string tag, string pnr) =>
        $"BSM\n.V/1LAYT\n.F/{flightNo}/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/{pnr}\nENDBSM";

    private static string Bpm(string flightNo, string tag, string point, string time) =>
        $"BPM\n.V/1LAYT\n.J/S/{point}/22OCT/{time}Z\n.F/{flightNo}/22OCT/AYT\n.N/{tag}001\nENDBPM";

    private static string Bum(string flightNo, string tag, string time) =>
        $"BUM\n.V/1LAYT\n.F/{flightNo}/22OCT/AYT\n.N/{tag}001\n.U/STAND104/20261022T{time}Z\nENDBUM";

    private BaggageEventProcessor Processor(ReadCoverage coverage) => new(fx.Db, new FixedTime(Wall), Hasher, coverage);

    private async Task<string> OutcomeOfAsync(string tag, string toState)
    {
        await using var cmd = fx.Db.CreateCommand(
            """
            SELECT e.outcome FROM baggage_events e JOIN bags b ON b.bag_id = e.bag_id
            WHERE b.bag_tag_number = @t AND e.to_state = @s
            """);
        cmd.Parameters.AddWithValue("t", tag);
        cmd.Parameters.AddWithValue("s", toState);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Skipping_a_point_without_a_reader_is_applied_not_an_anomaly()
    {
        // En az kapsam: güvenlik okuyucusu yok; BSM'den yüklemeye geçmek beklenen akış.
        var tag = NewTag();
        await Processor(ReadCoverage.Minimal).ProcessAsync(Bsm("ZZ0711", tag, NewPnr()), "test");
        await Processor(ReadCoverage.Minimal).ProcessAsync(Bpm("ZZ0711", tag, "LOAD02", "074000"), "test");
        Assert.Equal("APPLIED", await OutcomeOfAsync(tag, "LOADED_ON_AIRCRAFT"));

        // Tipik kapsam: indirme okuyucusu var; şutta okunup indirmesi okunmayan bavul sıra dışıdır.
        var other = NewTag();
        await Processor(ReadCoverage.Typical).ProcessAsync(Bsm("ZZ0711", other, NewPnr()), "test");
        await Processor(ReadCoverage.Typical).ProcessAsync(Bpm("ZZ0711", other, "LOAD02", "074000"), "test");
        await Processor(ReadCoverage.Typical).ProcessAsync(Bpm("ZZ0711", other, "CHUTE01", "121000"), "test");
        Assert.Equal("ANOMALY", await OutcomeOfAsync(other, "INBOUND_CHUTE"));
    }

    [Fact]
    public async Task Exit_gate_clears_a_bag_last_read_at_the_chute_without_an_anomaly()
    {
        var client = _typicalApi.CreateClient();
        var tag = NewTag();
        var pnr = NewPnr();
        foreach (var text in new[]
                 {
                     Bsm("ZZ0712", tag, pnr), Bpm("ZZ0712", tag, "SEC01", "063000"), Bpm("ZZ0712", tag, "LOAD02", "074000"),
                     Bum("ZZ0712", tag, "120500"), Bpm("ZZ0712", tag, "CHUTE01", "120800"),
                 })
        {
            var r = await client.SendAsync(TestApi.Signed("/v1/events", text));
            Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        }

        var packet = JsonSerializer.Serialize(new
        {
            gateId = "ARRIVAL_EXIT_GATE_B",
            timestampUtc = "2026-10-22T12:14:00Z",
            detectedBagTags = new[] { tag },
            passengerVerification = new { method = "SIMULATED_BOARDING_PASS", presentedPnr = pnr, passengerSurname = "YILMAZ", signalQuality = 0.92 },
        }, Json);
        var resp = await client.SendAsync(TestApi.Signed("/v1/gate/scans", packet, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var result = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("results")[0];

        Assert.Equal("CLEARED_OWNER", result.GetProperty("decision").GetString());
        Assert.Equal("APPLIED", await OutcomeOfAsync(tag, "EXIT_CLEARED"));
    }

    [Fact]
    public async Task Sla_and_ops_fall_back_to_the_aodb_belt_times_without_carousel_reads()
    {
        const string id = "ZZ0713-20261022";
        var tag = NewTag();
        await Processor(ReadCoverage.Typical).ProcessAsync(Bsm("ZZ0713", tag, NewPnr()), "test");
        await Processor(ReadCoverage.Typical).ProcessAsync(Bum("ZZ0713", tag, "120500"), "test");
        await Processor(ReadCoverage.Typical).ProcessAsync(Bpm("ZZ0713", tag, "CHUTE01", "120800"), "test");
        await new FlightContextStore(fx.Db).UpsertAsync(new FlightContext(id, At("120000"), false, false, 1, 1000, 50, 0.5, 2, "HANDLER_B"));
        await new FlightMilestoneStore(fx.Db).UpsertAsync(new FlightMilestones(id, FirstBagOnBeltUtc: At("120900"), LastBagOnBeltUtc: At("121400")));

        var sla = (await new SlaQueries(fx.Db, new NullMlPredictor(), new FixedOperationalClock(At("123000"))).GetAsync()).Single(f => f.FlightId == id);
        Assert.Equal(SlaQueries.SourceAodb, sla.TimesSource);
        Assert.True(sla.AllBagsArrived);
        Assert.Equal(540, sla.FirstBag.ActualSec);
        Assert.Equal(840, sla.LastBag.ActualSec);

        var ops = (await new OpsQueries(fx.Db, new FixedOperationalClock(At("123000")), ReadCoverage.Typical).FlightsAsync()).Single(f => f.FlightId == id);
        Assert.Equal(At("120900"), ops.FirstCarouselUtc);
        Assert.Equal(4.0, ops.FirstBagMinutes);
        Assert.Null(ops.Stalled); // bant okuması yokken "takılı" sayılamaz
    }

    [Fact]
    public async Task Sla_without_reads_or_aodb_times_reports_no_source()
    {
        const string id = "ZZ0714-20261022";
        var tag = NewTag();
        await Processor(ReadCoverage.Typical).ProcessAsync(Bsm("ZZ0714", tag, NewPnr()), "test");
        await new FlightContextStore(fx.Db).UpsertAsync(new FlightContext(id, At("120000"), false, false, 1, 1000, 50, 0.5, 2, "HANDLER_B"));

        var sla = (await new SlaQueries(fx.Db, new NullMlPredictor(), new FixedOperationalClock(At("120500"))).GetAsync()).Single(f => f.FlightId == id);
        Assert.Equal(SlaQueries.SourceNone, sla.TimesSource);
        Assert.Null(sla.FirstBag.ActualSec);
    }

    [Fact]
    public async Task Passenger_sees_likely_on_belt_once_the_window_has_passed_without_a_carousel_reader()
    {
        const string id = "ZZ0715-20261022";
        var tag = NewTag();
        var pnr = NewPnr();
        await Processor(ReadCoverage.Typical).ProcessAsync(Bsm("ZZ0715", tag, pnr), "test");
        await Processor(ReadCoverage.Typical).ProcessAsync(Bpm("ZZ0715", tag, "CHUTE01", "121000"), "test");
        // Fizik penceresi: 50 m / 0,5 m/sn = 100 sn ± 15 sn, yani 12:11:55'e kadar.
        await new FlightContextStore(fx.Db).UpsertAsync(new FlightContext(id, At("120000"), false, false, 1, 1000, 50, 0.5, 2, "HANDLER_B"));

        PassengerQueries Q(DateTimeOffset now, ReadCoverage c) => new(fx.Db, Hasher, new EtaStatistics(fx.Db), new FixedTime(Wall),
            contexts: new FlightContextStore(fx.Db), operationalClock: new FixedOperationalClock(now), coverage: c);

        var during = Assert.Single((await Q(At("121100"), ReadCoverage.Typical).LookupAsync(pnr, "YILMAZ"))!.Bags);
        Assert.NotNull(during.Eta);
        Assert.Null(during.Notice);

        var after = Assert.Single((await Q(At("121200"), ReadCoverage.Typical).LookupAsync(pnr, "YILMAZ"))!.Bags);
        Assert.Equal(BagNotice.LikelyOnBelt, after.Notice);
        Assert.Null(after.Eta);

        // Tam kapsamda bant okuması gelecektir: aynı anda tahmin söylenmez.
        var full = Assert.Single((await Q(At("121200"), ReadCoverage.Full).LookupAsync(pnr, "YILMAZ"))!.Bags);
        Assert.Null(full.Notice);
    }

    [Fact]
    public async Task Belt_milestones_endpoint_accepts_and_rejects_inconsistent_times()
    {
        var client = _typicalApi.CreateClient();
        const string id = "ZZ0716-20261022";

        var first = await client.SendAsync(TestApi.Signed("/v1/flights/milestones",
            JsonSerializer.Serialize(new { flightId = id, firstBagOnBeltUtc = "2026-10-22T12:09:00Z" }), "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        // Ayrı istekte gelen son bavul, kayıtlı ilk bavuldan önce: veritabanı kısıtı 400'e çevrilir.
        var bad = await client.SendAsync(TestApi.Signed("/v1/flights/milestones",
            JsonSerializer.Serialize(new { flightId = id, lastBagOnBeltUtc = "2026-10-22T12:05:00Z" }), "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
