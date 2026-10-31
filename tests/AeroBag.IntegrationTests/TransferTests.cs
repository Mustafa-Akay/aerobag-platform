using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AeroBag.Application.Alerts;
using AeroBag.Application.Time;
using AeroBag.Infrastructure;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Npgsql;

namespace AeroBag.IntegrationTests;

/// <summary>
/// W10a: aktarma bavulu. Gelen uçuştaki satır ve bağlantı uçuşundaki satır ayrıdır ve birbirini gösterir; bavul aktarma
/// hattına geçince gelen satır TRANSFERRED ile kapanır. İşlemci ve uyarı işçisi API'nin kısıtlı rolüyle çalıştırılır.
/// </summary>
public sealed class TransferTests(PostgresFixture fx) : IClassFixture<PostgresFixture>, IDisposable
{
    private static readonly DateTimeOffset Wall = new(2026, 10, 22, 23, 0, 0, TimeSpan.Zero);
    private static readonly LookupKeyHasher Hasher = new(Encoding.UTF8.GetBytes("test-secret-0123456789abcdef"));

    private readonly NpgsqlDataSource _app = NpgsqlDataSource.Create(fx.AppConnectionString);
    private readonly WebApplicationFactory<Program> _factory = TestApi.Create(fx);

    public void Dispose()
    {
        _app.Dispose();
        _factory.Dispose();
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DateTimeOffset At(string hhmmss) => DateTimeOffset.ParseExact($"20261022{hhmmss}", "yyyyMMddHHmmss",
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString(CultureInfo.InvariantCulture);

    private static string NewPnr() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static (string State, string? Inbound, string? Onward) R(string state, string? inbound, string? onward) => (state, inbound, onward);

    /// <summary>
    /// API mesajları gerçek saatle işler; mesajda yıl yoktur ve "22OCT" bugüne en yakın yıla yerleşir (TypeBParser).
    /// Bu yüzden API testlerinde uçuş kimliği sabit yazılmaz.
    /// </summary>
    private static string Fid(string flightNo)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var year = Enumerable.Range(today.Year - 1, 3).MinBy(y => Math.Abs(new DateOnly(y, 10, 22).DayNumber - today.DayNumber));
        return $"{flightNo}-{year:D4}1022";
    }

    private BaggageEventProcessor Processor() => new(_app, new FixedTime(Wall), Hasher);

    // Gelen uçuşun BSM'si (.O bağlantı uçuşu) ve aktarma BSM'si (.F bağlantı uçuşu, .I gelen uçuş).
    private static string InboundBsm(string inbound, string onward, string tag, string pnr) =>
        $"BSM\n.V/1LAYT\n.F/{inbound}/22OCT/AYT/Y\n.O/{onward}/22OCT/ADB\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/{pnr}\nENDBSM";

    private static string TransferBsm(string inbound, string onward, string tag, string pnr) =>
        $"BSM\n.V/1LAYT\n.F/{onward}/22OCT/ADB/Y\n.I/{inbound}/22OCT/FRA\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/{pnr}\nENDBSM";

    private static string LocalBsm(string flight, string tag, string pnr) =>
        $"BSM\n.V/1LAYT\n.F/{flight}/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/{pnr}\nENDBSM";

    private static string Bum(string flight, string tag, string time) =>
        $"BUM\n.V/1LAYT\n.F/{flight}/22OCT/AYT\n.N/{tag}001\n.U/STAND104/20261022T{time}Z\nENDBUM";

    private static string Bpm(string flight, string dest, string tag, string point, string time) =>
        $"BPM\n.V/1LAYT\n.J/S/{point}/22OCT/{time}Z\n.F/{flight}/22OCT/{dest}\n.N/{tag}001\nENDBPM";

    /// <summary>Gelen uçuş: güvenlik, yükleme, indirme. Bağlantı uçuşu: aktarma hattı ve (istenirse) yükleme.</summary>
    private static IEnumerable<string> Journey(string inbound, string onward, string tag, string pnr, bool sorted = true, bool loaded = true,
        bool transferBsm = true)
    {
        yield return InboundBsm(inbound, onward, tag, pnr);
        if (transferBsm) yield return TransferBsm(inbound, onward, tag, pnr);
        yield return Bpm(inbound, "AYT", tag, "SEC01", "063000");
        yield return Bpm(inbound, "AYT", tag, "LOAD02", "074000");
        yield return Bum(inbound, tag, "121000");
        if (sorted) yield return Bpm(onward, "ADB", tag, "TRANSFER01", "122500");
        if (loaded) yield return Bpm(onward, "ADB", tag, "LOAD01", "123500");
    }

    private async Task RunAsync(IEnumerable<string> messages)
    {
        var p = Processor();
        foreach (var m in messages)
        {
            var r = await p.ProcessAsync(m, "test");
            Assert.Equal(ProcessResult.Accepted, r.Status);
        }
    }

    private async Task<(string State, string? Inbound, string? Onward)> RowAsync(string tag, string flightId)
    {
        await using var cmd = fx.Db.CreateCommand(
            "SELECT current_state, inbound_flight_id, onward_flight_id FROM bags WHERE bag_tag_number = @t AND flight_id = @f");
        cmd.Parameters.AddWithValue("t", tag);
        cmd.Parameters.AddWithValue("f", flightId);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync(), $"{tag}@{flightId} satırı yok");
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
    }

    private async Task<long> CountAsync(string sql, string tag)
    {
        await using var cmd = fx.Db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("t", tag);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Transfer_flow_links_the_two_rows_and_hands_the_inbound_row_over()
    {
        var tag = NewTag();
        await RunAsync(Journey("ZZ0911", "ZZ5911", tag, NewPnr()));

        Assert.Equal(R("TRANSFERRED", null, "ZZ5911-20261022"), await RowAsync(tag, "ZZ0911-20261022"));
        Assert.Equal(R("LOADED_ON_AIRCRAFT", "ZZ0911-20261022", null), await RowAsync(tag, "ZZ5911-20261022"));

        // Bağlantı satırında güvenlik okumasının olmaması sıra dışı değildir (aktarma zinciri).
        Assert.Equal(0, await CountAsync(
            "SELECT count(*) FROM baggage_events e JOIN bags b ON b.bag_id = e.bag_id WHERE b.bag_tag_number = @t AND e.outcome = 'ANOMALY'", tag));
        // Devir bir kez yazılır: yükleme okuması tekrar gelse de ikinci bir TRANSFERRED olayı olmaz.
        await RunAsync([Bpm("ZZ5911", "ADB", tag, "LOAD01", "123600")]);
        Assert.Equal(1, await CountAsync(
            "SELECT count(*) FROM baggage_events e JOIN bags b ON b.bag_id = e.bag_id WHERE b.bag_tag_number = @t AND e.to_state = 'TRANSFERRED' AND e.message_type = 'SYSTEM'", tag));
        // Gelen satır kapandıktan sonra gelen bir şut okuması onu banda geri çevirmez.
        await RunAsync([Bpm("ZZ0911", "AYT", tag, "CHUTE04", "123000")]);
        Assert.Equal("TRANSFERRED", (await RowAsync(tag, "ZZ0911-20261022")).State);
    }

    [Fact]
    public async Task Lost_transfer_bsm_is_recovered_from_the_inbound_onward_line()
    {
        var tag = NewTag();
        await RunAsync(Journey("ZZ0912", "ZZ5912", tag, NewPnr(), loaded: false, transferBsm: false));

        Assert.Equal(R("TRANSFER_SORTED", "ZZ0912-20261022", null), await RowAsync(tag, "ZZ5912-20261022"));
        Assert.Equal("TRANSFERRED", (await RowAsync(tag, "ZZ0912-20261022")).State);
    }

    [Fact]
    public async Task Bag_that_is_not_yet_sorted_keeps_the_inbound_row_open()
    {
        var tag = NewTag();
        await RunAsync(Journey("ZZ0914", "ZZ5914", tag, NewPnr(), sorted: false, loaded: false));

        Assert.Equal(R("UNLOADED_AT_ARRIVAL", null, "ZZ5914-20261022"), await RowAsync(tag, "ZZ0914-20261022"));
        Assert.Equal(R("CHECKED_IN", "ZZ0914-20261022", null), await RowAsync(tag, "ZZ5914-20261022"));
    }

    [Fact]
    public async Task Missed_connection_and_transferred_rows_do_not_raise_arrival_alerts()
    {
        var sorted = NewTag();   // aktarma hattından geçti, yüklenmedi (gelen satır TRANSFERRED)
        var waiting = NewTag();  // indirildi, aktarma hattına hiç gelmedi (bağlantı satırı CHECKED_IN)
        await RunAsync(Journey("ZZ0915", "ZZ5915", sorted, NewPnr(), loaded: false));
        await RunAsync(Journey("ZZ0915", "ZZ5915", waiting, NewPnr(), sorted: false, loaded: false));
        var milestones = new FlightMilestoneStore(_app);
        await milestones.UpsertAsync(new FlightMilestones("ZZ0915-20261022", At("080000"), At("121500")));
        await milestones.UpsertAsync(new FlightMilestones("ZZ5915-20261022", DepartedUtc: At("130000"), ScheduledDepartureUtc: At("130000")));

        await new BagAlertService(_app, AlertPolicy.Default).ScanAsync(At("140000"));

        // Bağlantı uçağı kalktı, bavullar yüklenmedi: bu W10b'nin "bağlantı kaçırıldı" kuralıdır; bağlantı satırında NOT_LOADED
        // açılmaz. Gelen satırlar indirildi (biri aktarmaya devredildi): MISSING_AT_ARRIVAL açılmaz.
        foreach (var tag in new[] { sorted, waiting })
        {
            Assert.Equal(0, await CountAsync(
                "SELECT count(*) FROM bag_alerts a JOIN bags b ON b.bag_id = a.bag_id WHERE b.bag_tag_number = @t", tag));
        }
        Assert.Equal("CHECKED_IN", (await RowAsync(waiting, "ZZ5915-20261022")).State);
    }

    [Fact]
    public async Task Passenger_is_told_about_a_missed_connection_only_after_the_connecting_flight_departs()
    {
        var made = NewTag();
        var missed = NewTag();
        var pnrMade = NewPnr();
        var pnrMissed = NewPnr();
        await RunAsync(Journey("ZZ0918", "ZZ5918", made, pnrMade));
        await RunAsync(Journey("ZZ0918", "ZZ5918", missed, pnrMissed, loaded: false));
        await new FlightMilestoneStore(_app).UpsertAsync(
            new FlightMilestones("ZZ5918-20261022", DepartedUtc: At("130500"), ScheduledDepartureUtc: At("130000")));

        PassengerQueries Lookup(string hhmmss) =>
            new(fx.Db, Hasher, new EtaStatistics(fx.Db), new FixedTime(Wall), operationalClock: new FixedOperationalClock(At(hhmmss)));

        // Kalkıştan önce (kabul kapanışı geçmiş olsa da) yolcuya "yetişmedi" denmez; bu yalnızca personel panosundadır.
        Assert.False((await Lookup("130400").LookupAsync(pnrMissed, "YILMAZ"))!.Bags.Single().ConnectionMissed);

        var after = (await Lookup("130500").LookupAsync(pnrMissed, "YILMAZ"))!.Bags.Single();
        Assert.True(after.ConnectionMissed);
        Assert.Equal("TRANSFER_SORTED", after.OnwardState);

        // Yüklenen bavul kalkıştan sonra da "yetişmedi" sayılmaz.
        Assert.False((await Lookup("130500").LookupAsync(pnrMade, "YILMAZ"))!.Bags.Single().ConnectionMissed);
    }

    private static HttpRequestMessage Get(string url, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private async Task PostAllAsync(HttpClient client, IEnumerable<string> messages)
    {
        foreach (var m in messages)
        {
            var resp = await client.SendAsync(TestApi.Signed("/v1/events", m));
            Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        }
    }

    private static async Task<string> OpsTokenAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/v1/auth/token", new { username = "ops", password = "ops-demo-2026" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    [Fact]
    public async Task Passenger_sees_one_transfer_card_and_the_token_covers_both_legs()
    {
        var client = _factory.CreateClient();
        var tag = NewTag();
        var pnr = NewPnr();
        await PostAllAsync(client, Journey("ZZ0916", "ZZ5916", tag, pnr, loaded: false));

        var resp = await client.PostAsJsonAsync("/v1/lookup", new { pnr, surname = "YILMAZ" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var bags = json.GetProperty("bags");
        Assert.Equal(1, bags.GetArrayLength());
        var bag = bags[0];
        Assert.Equal(tag, bag.GetProperty("bagTag").GetString());
        Assert.Equal(Fid("ZZ0916"), bag.GetProperty("flightId").GetString());
        Assert.Equal("TRANSFERRED", bag.GetProperty("state").GetString());
        Assert.Equal(Fid("ZZ5916"), bag.GetProperty("onwardFlightId").GetString());
        Assert.Equal("TRANSFER_SORTED", bag.GetProperty("onwardState").GetString());
        Assert.Equal("ADB", bag.GetProperty("onwardDestination").GetString()); // bağlantı uçuşunun .F satırından
        Assert.Equal(JsonValueKind.Null, bag.GetProperty("eta").ValueKind);
        Assert.Equal(JsonValueKind.Null, bag.GetProperty("carousel").ValueKind);
        Assert.Equal(JsonValueKind.Null, bag.GetProperty("notice").ValueKind);

        // Canlı kanalda iki satırın grubuna da katılabilir (bağlantı uçuşundaki okumalar da karta gelir).
        var token = json.GetProperty("accessToken").GetString()!;
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/live"), o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        await hub.StartAsync();
        await hub.InvokeAsync("JoinBags", new[] { $"{tag}@{Fid("ZZ0916")}", $"{tag}@{Fid("ZZ5916")}" });
    }

    [Fact]
    public async Task Ops_dashboard_counts_transfer_bags_separately_and_lists_connecting_flights()
    {
        var client = _factory.CreateClient();
        var local = NewTag();
        var transfer = NewTag();
        await PostAllAsync(client, [LocalBsm("ZZ0917", local, NewPnr()), Bum("ZZ0917", local, "121000")]);
        await PostAllAsync(client, Journey("ZZ0917", "ZZ5917", transfer, NewPnr(), loaded: false));
        var milestone = JsonSerializer.Serialize(new { flightId = Fid("ZZ5917"), scheduledDepartureUtc = At("130000") });
        Assert.Equal(HttpStatusCode.Accepted,
            (await client.SendAsync(TestApi.Signed("/v1/flights/milestones", milestone, "application/json"))).StatusCode);

        var ops = await OpsTokenAsync(client);
        var flights = await (await client.SendAsync(Get("/v1/flights", ops))).Content.ReadFromJsonAsync<JsonElement>();
        var inbound = flights.EnumerateArray().Single(f => f.GetProperty("flightId").GetString() == Fid("ZZ0917"));
        Assert.Equal(2, inbound.GetProperty("totalBags").GetInt32());
        Assert.Equal(1, inbound.GetProperty("transfer").GetInt32());
        Assert.Equal(1, inbound.GetProperty("inbound").GetInt32()); // yalnızca yerel bavul "yolda"
        Assert.DoesNotContain(flights.EnumerateArray(), f => f.GetProperty("flightId").GetString() == Fid("ZZ5917"));

        var sla = await (await client.SendAsync(Get("/v1/flights/sla", ops))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(sla.EnumerateArray(), f => f.GetProperty("flightId").GetString() == Fid("ZZ5917"));

        var transfers = await client.SendAsync(Get("/v1/transfers", ops));
        Assert.Equal(HttpStatusCode.OK, transfers.StatusCode);
        var onward = (await transfers.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray()
            .Single(f => f.GetProperty("flightId").GetString() == Fid("ZZ5917"));
        Assert.Equal(1, onward.GetProperty("total").GetInt32());
        Assert.Equal(1, onward.GetProperty("sorted").GetInt32());
        Assert.Equal(0, onward.GetProperty("loaded").GetInt32());
        Assert.Equal(At("124500"), onward.GetProperty("cutoffUtc").GetDateTimeOffset()); // planlanan kalkış - 15 dk (varsayılan)

        // Jetonsuz istek reddedilir (yolcu jetonunun 403 aldığı AuthTests'te).
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/transfers")).StatusCode);
    }
}
