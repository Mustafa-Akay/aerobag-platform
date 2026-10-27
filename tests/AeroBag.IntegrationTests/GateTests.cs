using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AeroBag.IntegrationTests;

/// <summary>Faz 4.4: ExitGuard uçtan uca (imzalı kapı paketi, gerçek API, gerçek PostgreSQL, kısıtlı uygulama rolü).</summary>
public sealed class GateTests(PostgresFixture fx) : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = TestApi.Create(fx);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Dispose() => _factory.Dispose();

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    private static string NewPnr() => "G" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();

    private static async Task SendAsync(HttpClient client, string text)
    {
        var resp = await client.SendAsync(TestApi.Signed("/v1/events", text));
        Assert.True(resp.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.OK, $"olay reddedildi: {resp.StatusCode}");
    }

    /// <summary>BSM → indirme → bant → banttan alma. claimed=false ise yalnızca BSM gönderilir.</summary>
    private static async Task<string> BagAsync(HttpClient client, string flightNo, string pnr, string surname, bool claimed = true)
    {
        var tag = NewTag();
        await SendAsync(client, $"BSM\n.V/1LAYT\n.F/{flightNo}/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/{surname}/AYSE\n.L/{pnr}\nENDBSM");
        if (claimed)
        {
            await SendAsync(client, $"BUM\n.V/1LAYT\n.F/{flightNo}/22OCT/AYT\n.N/{tag}001\n.U/STAND104/20261022T120500Z\nENDBUM");
            await SendAsync(client, $"BPM\n.V/1LAYT\n.J/S/CAROUSEL04/22OCT/121000Z\n.F/{flightNo}/22OCT/AYT\n.N/{tag}001\nENDBPM");
            await SendAsync(client, $"BPM\n.V/1LAYT\n.J/S/CLAIM04/22OCT/121200Z\n.F/{flightNo}/22OCT/AYT\n.N/{tag}001\nENDBPM");
        }
        return tag;
    }

    private static object Packet(string[] tags, string? pnr, double? signal = 0.92, string time = "2026-10-22T12:14:00Z") => new
    {
        gateId = "ARRIVAL_EXIT_GATE_B",
        timestampUtc = time,
        detectedBagTags = tags,
        passengerVerification = new { method = "SIMULATED_BOARDING_PASS", presentedPnr = pnr, passengerSurname = "YILMAZ", signalQuality = signal },
    };

    private static async Task<(HttpStatusCode Status, Dictionary<string, JsonElement> ByTag)> GateAsync(HttpClient client, object packet,
        string sourceId = TestApi.SourceId, string secret = TestApi.SourceSecret)
    {
        var body = JsonSerializer.Serialize(packet, Json);
        var resp = await client.SendAsync(TestApi.Signed("/v1/gate/scans", body, "application/json", sourceId, secret));
        if (resp.StatusCode != HttpStatusCode.OK)
        {
            return (resp.StatusCode, new Dictionary<string, JsonElement>());
        }
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return (resp.StatusCode, json.GetProperty("results").EnumerateArray().ToDictionary(r => r.GetProperty("bagTag").GetString()!, r => r));
    }

    private static string Decision(Dictionary<string, JsonElement> byTag, string tag) => byTag[tag].GetProperty("decision").GetString()!;

    private async Task<string> StateAsync(string tag) => (string)(await ScalarAsync("SELECT current_state FROM bags WHERE bag_tag_number = @p", tag))!;

    private async Task<object?> ScalarAsync(string sql, string p)
    {
        await using var cmd = fx.Db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("p", p);
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<string> LoginAsync(HttpClient client, string user, string password)
    {
        var resp = await client.PostAsJsonAsync("/v1/auth/token", new { username = user, password });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string token, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }
        return req;
    }

    [Fact]
    public async Task Owner_is_cleared_and_the_presented_pnr_is_never_stored()
    {
        var client = _factory.CreateClient();
        var pnr = NewPnr();
        var tag = await BagAsync(client, "ZZ0701", pnr, "YILMAZ");

        var (status, byTag) = await GateAsync(client, Packet([tag], pnr));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("CLEARED_OWNER", Decision(byTag, tag));
        Assert.Equal("EXIT_CLEARED", await StateAsync(tag));

        foreach (var table in new[] { "gate_reads", "exit_reviews", "baggage_events", "dead_letter_messages", "passengers" })
        {
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM {table} t WHERE t::text LIKE '%' || @p || '%'", pnr));
        }
    }

    [Fact]
    public async Task Travellers_on_the_same_booking_may_take_each_others_bags()
    {
        var client = _factory.CreateClient();
        var pnr = NewPnr();
        await BagAsync(client, "ZZ0702", pnr, "YILMAZ");
        var spouseBag = await BagAsync(client, "ZZ0702", pnr, "KAYA");

        var (_, byTag) = await GateAsync(client, Packet([spouseBag], pnr));
        Assert.Equal("CLEARED_OWNER", Decision(byTag, spouseBag));
    }

    [Fact]
    public async Task Someone_else_triggers_a_silent_review_that_only_security_can_see_and_resolve()
    {
        var client = _factory.CreateClient();
        var tag = await BagAsync(client, "ZZ0703", NewPnr(), "YILMAZ");

        var (_, byTag) = await GateAsync(client, Packet([tag], NewPnr()));
        Assert.Equal("REVIEW_MISMATCH", Decision(byTag, tag));
        Assert.Equal("EXIT_REVIEW", await StateAsync(tag));

        var ops = await LoginAsync(client, "ops", "ops-demo-2026");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Authed(HttpMethod.Get, "/v1/security/reviews", ops))).StatusCode);

        var sec = await LoginAsync(client, "security", "security-demo-2026");
        var list = await (await client.SendAsync(Authed(HttpMethod.Get, "/v1/security/reviews", sec))).Content.ReadFromJsonAsync<JsonElement>();
        var review = list.EnumerateArray().Single(r => r.GetProperty("bagTag").GetString() == tag);
        Assert.Equal("PNR_MISMATCH", review.GetProperty("reason").GetString());
        Assert.Equal("OPEN", review.GetProperty("status").GetString());

        var id = review.GetProperty("id").GetGuid();
        var resolve = await client.SendAsync(Authed(HttpMethod.Post, $"/v1/security/reviews/{id}/resolve", sec, new { resolution = "FALSE_ALARM", note = "Aile üyesi, farklı rezervasyon." }));
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        var again = await client.SendAsync(Authed(HttpMethod.Post, $"/v1/security/reviews/{id}/resolve", sec, new { resolution = "CONFIRMED" }));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var opsResolve = await client.SendAsync(Authed(HttpMethod.Post, $"/v1/security/reviews/{id}/resolve", ops, new { resolution = "CONFIRMED" }));
        Assert.Equal(HttpStatusCode.Forbidden, opsResolve.StatusCode);

        var all = await (await client.SendAsync(Authed(HttpMethod.Get, "/v1/security/reviews?status=all", sec))).Content.ReadFromJsonAsync<JsonElement>();
        var resolved = all.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == id);
        Assert.Equal("RESOLVED", resolved.GetProperty("status").GetString());
        Assert.Equal("FALSE_ALARM", resolved.GetProperty("resolution").GetString());
        Assert.Equal("security", resolved.GetProperty("resolvedBy").GetString());
    }

    [Fact]
    public async Task Weak_signal_or_missing_verification_changes_nothing_and_raises_no_review()
    {
        var client = _factory.CreateClient();
        var pnr = NewPnr();
        var weak = await BagAsync(client, "ZZ0704", pnr, "YILMAZ");
        var unverified = await BagAsync(client, "ZZ0704", pnr, "YILMAZ");

        var (_, a) = await GateAsync(client, Packet([weak], NewPnr(), signal: 0.3));
        var (_, b) = await GateAsync(client, Packet([unverified], null));

        Assert.Equal("NO_DECISION", Decision(a, weak));
        Assert.Equal("NO_DECISION", Decision(b, unverified));
        foreach (var tag in new[] { weak, unverified })
        {
            Assert.Contains(await StateAsync(tag), new[] { "CLAIMED_FROM_BELT", "EXIT_OVERDUE" });
            Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM exit_reviews WHERE bag_tag = @p", tag));
            Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM gate_reads WHERE bag_tag = @p", tag));
        }
    }

    [Fact]
    public async Task Unknown_tag_and_bag_not_yet_on_the_carousel_open_reviews_without_state_change()
    {
        var client = _factory.CreateClient();
        var pnr = NewPnr();
        var unknown = NewTag();
        var early = await BagAsync(client, "ZZ0705", pnr, "YILMAZ", claimed: false);

        var (_, byTag) = await GateAsync(client, Packet([unknown, early], pnr));
        Assert.Equal("UNKNOWN_TAG", Decision(byTag, unknown));
        Assert.Equal("OUT_OF_SEQUENCE", Decision(byTag, early));
        Assert.Equal("CHECKED_IN", await StateAsync(early));
        Assert.Equal("UNKNOWN_TAG", await ScalarAsync("SELECT reason FROM exit_reviews WHERE bag_tag = @p", unknown));
        Assert.Equal("NOT_ON_CAROUSEL", await ScalarAsync("SELECT reason FROM exit_reviews WHERE bag_tag = @p", early));
    }

    [Fact]
    public async Task Same_packet_twice_is_recorded_once()
    {
        var client = _factory.CreateClient();
        var pnr = NewPnr();
        var tag = await BagAsync(client, "ZZ0706", pnr, "YILMAZ");

        var (_, first) = await GateAsync(client, Packet([tag], pnr, time: "2026-10-22T12:15:00Z"));
        var (_, second) = await GateAsync(client, Packet([tag], pnr, time: "2026-10-22T12:15:00Z"));

        Assert.Equal("CLEARED_OWNER", Decision(first, tag));
        Assert.Equal("DUPLICATE", Decision(second, tag));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM gate_reads WHERE bag_tag = @p", tag));
    }

    [Fact]
    public async Task Gate_endpoint_needs_a_signed_gate_source_and_a_valid_packet()
    {
        var client = _factory.CreateClient();
        var body = JsonSerializer.Serialize(Packet([NewTag()], NewPnr()), Json);

        var unsigned = await client.PostAsync("/v1/gate/scans", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);

        var (wrongScope, _) = await GateAsync(client, Packet([NewTag()], NewPnr()), TestApi.ContextOnlyId, TestApi.ContextOnlySecret);
        Assert.Equal(HttpStatusCode.Forbidden, wrongScope);

        var (badPnr, _) = await GateAsync(client, Packet([NewTag()], "x"));
        Assert.Equal(HttpStatusCode.BadRequest, badPnr);

        var (tooMany, _) = await GateAsync(client, Packet(Enumerable.Range(0, 11).Select(_ => NewTag()).ToArray(), NewPnr()));
        Assert.Equal(HttpStatusCode.BadRequest, tooMany);
    }

    [Fact]
    public async Task Feature_flag_off_removes_the_gate_endpoint()
    {
        using var off = TestApi.Create(fx, ("ExitGuard:Enabled", "false"));
        var client = off.CreateClient();
        var (status, _) = await GateAsync(client, Packet([NewTag()], NewPnr()));
        Assert.Equal(HttpStatusCode.NotFound, status);
    }
}
