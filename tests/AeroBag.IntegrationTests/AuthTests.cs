using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Http.Connections;

namespace AeroBag.IntegrationTests;

/// <summary>Faz 4 kimlik doğrulama ve yetki testleri: gerçek API, gerçek PostgreSQL (Testcontainers).</summary>
public sealed class AuthTests(PostgresFixture fx) : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = TestApi.Create(fx);

    public void Dispose() => _factory.Dispose();

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    private static string NewPnr() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private async Task<(string Pnr, string Tag)> CreateBagAsync(HttpClient client, string flightNo, string? tag = null, string date = "22OCT")
    {
        tag ??= NewTag();
        var pnr = NewPnr();
        var bsm = $"BSM\n.V/1LAYT\n.F/{flightNo}/{date}/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/{pnr}\nENDBSM";
        var resp = await client.SendAsync(TestApi.Signed("/v1/events", bsm));
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        return (pnr, tag);
    }

    private static async Task<string> LoginAsync(HttpClient client, string user, string password)
    {
        var resp = await client.PostAsJsonAsync("/v1/auth/token", new { username = user, password });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("accessToken").GetString()!;
    }

    private static async Task<string> PassengerTokenAsync(HttpClient client, string pnr) =>
        (await PassengerLookupAsync(client, pnr)).Token;

    /// <summary>Yolcu sorgusu: jeton ve sorgudaki ilk bavulun uçuşu.</summary>
    private static async Task<(string Token, string FlightId)> PassengerLookupAsync(HttpClient client, string pnr)
    {
        var resp = await client.PostAsJsonAsync("/v1/lookup", new { pnr, surname = "YILMAZ" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("expiresInSec").GetInt32() > 0);
        var flightId = json.GetProperty("bags")[0].GetProperty("flightId").GetString()!;
        return (json.GetProperty("accessToken").GetString()!, flightId);
    }

    private static HttpRequestMessage Get(string url, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return req;
    }

    [Fact]
    public async Task Ops_endpoints_reject_requests_without_a_token()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/flights")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/flights/sla")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/alerts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/ml/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/bags/1234567890")).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_get_the_same_answer()
    {
        var client = _factory.CreateClient();
        var wrong = await client.PostAsJsonAsync("/v1/auth/token", new { username = "ops", password = "yanlis" });
        var unknown = await client.PostAsJsonAsync("/v1/auth/token", new { username = "yok-boyle-biri", password = "yanlis" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ops_and_security_tokens_can_read_the_dashboard()
    {
        var client = _factory.CreateClient();
        foreach (var (user, pass) in new[] { ("ops", "ops-demo-2026"), ("security", "security-demo-2026") })
        {
            var token = await LoginAsync(client, user, pass);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/v1/flights", token))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/v1/flights/sla", token))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/v1/alerts", token))).StatusCode);
            var ml = await client.SendAsync(Get("/v1/ml/status", token));
            Assert.Equal(HttpStatusCode.OK, ml.StatusCode);
            // Testlerde ML kapalıdır (Ml:BaseUrl boş): durum bunu söylemeli.
            Assert.False((await ml.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean());
        }
    }

    [Fact]
    public async Task Passenger_token_cannot_reach_ops_endpoints()
    {
        var client = _factory.CreateClient();
        var (pnr, _) = await CreateBagAsync(client, "ZZ0401");
        var token = await PassengerTokenAsync(client, pnr);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Get("/v1/flights", token))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Get("/v1/flights/sla", token))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Get("/v1/alerts", token))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Get("/v1/ml/status", token))).StatusCode);
    }

    [Fact]
    public async Task Passenger_token_sees_only_its_own_bag()
    {
        var client = _factory.CreateClient();
        var (pnr, tag) = await CreateBagAsync(client, "ZZ0402");
        var (_, otherTag) = await CreateBagAsync(client, "ZZ0402");
        var token = await PassengerTokenAsync(client, pnr);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get($"/v1/bags/{tag}", token))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Get($"/v1/bags/{otherTag}", token))).StatusCode);

        var ops = await LoginAsync(client, "ops", "ops-demo-2026");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get($"/v1/bags/{otherTag}", ops))).StatusCode);
    }

    [Fact]
    public async Task Passenger_token_is_bound_to_the_flight_not_only_the_tag()
    {
        // Etiket numaraları yeniden kullanılır: aynı etiket önce 22 Ekim'de A yolcusunun, sonra 23 Ekim'de B yolcusunun bavulu.
        var client = _factory.CreateClient();
        var tag = NewTag();
        var (pnrA, _) = await CreateBagAsync(client, "ZZ0404", tag, "22OCT");
        var (pnrB, _) = await CreateBagAsync(client, "ZZ0404", tag, "23OCT");
        var (tokenA, flightA) = await PassengerLookupAsync(client, pnrA);
        var (_, flightB) = await PassengerLookupAsync(client, pnrB);
        Assert.NotEqual(flightA, flightB);

        // B'nin bavulu daha yeni olduğu hâlde A uçuşu belirtmeden kendi bavulunu görür, B'ninkini değil.
        var own = await client.SendAsync(Get($"/v1/bags/{tag}", tokenA));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(flightA, (await own.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("flightId").GetString());
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get($"/v1/bags/{tag}?flight={flightA}", tokenA))).StatusCode);

        // Uçuşu açıkça istese de B'nin bavulu A'ya kapalıdır; varlığı da sızdırılmaz.
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Get($"/v1/bags/{tag}?flight={flightB}", tokenA))).StatusCode);

        // Canlı kanalda da yalnızca kendi (etiket, uçuş) grubuna katılabilir.
        await using var hub = Hub(tokenA);
        await hub.StartAsync();
        await hub.InvokeAsync("JoinBags", new[] { $"{tag}@{flightA}" });
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("JoinBags", new[] { $"{tag}@{flightB}" }));

        // Personel her ikisini de görür.
        var ops = await LoginAsync(client, "ops", "ops-demo-2026");
        var other = await client.SendAsync(Get($"/v1/bags/{tag}?flight={flightB}", ops));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(flightB, (await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("flightId").GetString());
    }

    [Fact]
    public async Task Tampered_or_foreign_tokens_are_rejected()
    {
        var client = _factory.CreateClient();
        var token = await LoginAsync(client, "ops", "ops-demo-2026");

        // İmzanın son karakterini değiştir.
        var tampered = token[..^2] + (token[^2] == 'A' ? 'B' : 'A') + token[^1];
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/v1/flights", tampered))).StatusCode);

        // Rolü "ops" yapılmış ama başka bir anahtarla imzalanmış jeton.
        var foreign = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(
            new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
            {
                Issuer = "aerobag-api",
                Audience = "aerobag",
                Claims = new Dictionary<string, object> { ["role"] = "ops", ["sub"] = "attacker" },
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                    new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Encoding.UTF8.GetBytes("some-other-key-that-is-long-enough-0123456789")),
                    Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256),
            });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Get("/v1/flights", foreign))).StatusCode);
    }

    private HubConnection Hub(string? token) => new HubConnectionBuilder()
        .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/live"), o =>
        {
            o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
            if (token is not null)
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            }
        })
        .Build();

    [Fact]
    public async Task Live_channel_requires_a_token_and_checks_groups()
    {
        var client = _factory.CreateClient();
        var (pnr, tag) = await CreateBagAsync(client, "ZZ0403");
        var (_, otherTag) = await CreateBagAsync(client, "ZZ0403");
        var (passenger, flight) = await PassengerLookupAsync(client, pnr);

        await using (var anonymous = Hub(null))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());
        }

        await using var hub = Hub(passenger);
        await hub.StartAsync();
        await hub.InvokeAsync("JoinBags", new[] { $"{tag}@{flight}" });
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("JoinBags", new[] { $"{otherTag}@{flight}" }));
        // Eski biçim (yalnız etiket) ve bozuk anahtarlar reddedilir.
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("JoinBags", new[] { tag }));
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("JoinBags", new[] { $"{tag}@{flight}\n" }));
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("JoinOps"));

        var ops = await LoginAsync(client, "ops", "ops-demo-2026");
        await using var opsHub = Hub(ops);
        await opsHub.StartAsync();
        await opsHub.InvokeAsync("JoinOps");
    }
}
