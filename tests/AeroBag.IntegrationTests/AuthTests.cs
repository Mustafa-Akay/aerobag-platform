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

    private async Task<(string Pnr, string Tag)> CreateBagAsync(HttpClient client, string flightNo)
    {
        var tag = NewTag();
        var pnr = NewPnr();
        var bsm = $"BSM\n.V/1LAYT\n.F/{flightNo}/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/{pnr}\nENDBSM";
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

    private static async Task<string> PassengerTokenAsync(HttpClient client, string pnr)
    {
        var resp = await client.GetAsync($"/v1/lookup?pnr={pnr}&surname=YILMAZ");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetProperty("expiresInSec").GetInt32() > 0);
        return json.GetProperty("accessToken").GetString()!;
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
        var passenger = await PassengerTokenAsync(client, pnr);

        await using (var anonymous = Hub(null))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());
        }

        await using var hub = Hub(passenger);
        await hub.StartAsync();
        await hub.InvokeAsync("JoinBags", new[] { tag });
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("JoinBags", new[] { otherTag }));
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("JoinOps"));

        var ops = await LoginAsync(client, "ops", "ops-demo-2026");
        await using var opsHub = Hub(ops);
        await opsHub.StartAsync();
        await opsHub.InvokeAsync("JoinOps");
    }
}
