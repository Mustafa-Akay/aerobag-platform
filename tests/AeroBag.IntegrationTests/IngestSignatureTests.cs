using System.Net;
using System.Text;
using AeroBag.Application.Security;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AeroBag.IntegrationTests;

/// <summary>Faz 4.2: mesaj alma uçlarında kaynak imzası (sahte kaynak, değiştirilmiş gövde, eski istek, tekrar oynatma, kapsam).</summary>
public sealed class IngestSignatureTests(PostgresFixture fx) : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = TestApi.Create(fx);

    public void Dispose() => _factory.Dispose();

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    private static string Bsm(string tag) =>
        $"BSM\n.V/1LAYT\n.F/ZZ0501/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/ABC123\nENDBSM";

    private const string ContextJson =
        """{"flightId":"ZZ0501-20261022","onBlockUtc":"2026-10-22T12:00:00Z","wide":false,"rain":false,"bags":100,"standM":500,"chuteM":50,"speedMps":0.5,"arrivals30":2,"handler":"HANDLER_A"}""";

    [Fact]
    public async Task Unsigned_requests_are_rejected()
    {
        var client = _factory.CreateClient();
        var events = await client.PostAsync("/v1/events", new StringContent(Bsm(NewTag()), Encoding.UTF8, "text/plain"));
        var context = await client.PostAsync("/v1/flights/context", new StringContent(ContextJson, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, events.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, context.StatusCode);
    }

    [Fact]
    public async Task Signed_event_is_accepted_and_its_source_is_recorded()
    {
        var client = _factory.CreateClient();
        var tag = NewTag();
        var resp = await client.SendAsync(TestApi.Signed("/v1/events", Bsm(tag)));
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);

        await using var cmd = fx.Db.CreateCommand(
            "SELECT e.source FROM baggage_events e JOIN bags b ON b.bag_id = e.bag_id WHERE b.bag_tag_number = @t");
        cmd.Parameters.AddWithValue("t", tag);
        Assert.Equal(TestApi.SourceId, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Wrong_secret_or_unknown_source_is_rejected()
    {
        var client = _factory.CreateClient();
        var wrongSecret = await client.SendAsync(TestApi.Signed("/v1/events", Bsm(NewTag()), secret: "attacker-guess-attacker-guess-attacker-guess"));
        var unknown = await client.SendAsync(TestApi.Signed("/v1/events", Bsm(NewTag()), sourceId: "nobody"));

        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    [Fact]
    public async Task Body_changed_after_signing_is_rejected()
    {
        var client = _factory.CreateClient();
        var tag = NewTag();
        var resp = await client.SendAsync(TestApi.Signed("/v1/events", Bsm(tag), sendBody: Bsm(tag).Replace("YILMAZ", "KAYA")));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Theory]
    [InlineData(-600)]
    [InlineData(600)]
    public async Task Request_outside_the_time_window_is_rejected(int offsetSeconds)
    {
        var client = _factory.CreateClient();
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + offsetSeconds;
        var resp = await client.SendAsync(TestApi.Signed("/v1/events", Bsm(NewTag()), unixSeconds: ts));
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Replayed_request_is_rejected()
    {
        var client = _factory.CreateClient();
        var body = Bsm(NewTag());
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = RequestSignature.NewNonce();

        var first = await client.SendAsync(TestApi.Signed("/v1/events", body, unixSeconds: ts, nonce: nonce));
        var replay = await client.SendAsync(TestApi.Signed("/v1/events", body, unixSeconds: ts, nonce: nonce));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Same_message_with_a_new_nonce_reaches_idempotency()
    {
        var client = _factory.CreateClient();
        var body = Bsm(NewTag());
        var first = await client.SendAsync(TestApi.Signed("/v1/events", body));
        var second = await client.SendAsync(TestApi.Signed("/v1/events", body));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode); // kopya: tekilleştirildi, durum değişmedi
    }

    [Fact]
    public async Task Source_can_write_only_to_its_scopes()
    {
        var client = _factory.CreateClient();
        var events = await client.SendAsync(TestApi.Signed("/v1/events", Bsm(NewTag()),
            sourceId: TestApi.ContextOnlyId, secret: TestApi.ContextOnlySecret));
        var context = await client.SendAsync(TestApi.Signed("/v1/flights/context", ContextJson, "application/json",
            sourceId: TestApi.ContextOnlyId, secret: TestApi.ContextOnlySecret));

        Assert.Equal(HttpStatusCode.Forbidden, events.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, context.StatusCode);
    }
}
