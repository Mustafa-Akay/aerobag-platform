using System.Net;
using System.Text;
using AeroBag.Infrastructure;
using AeroBag.Infrastructure.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AeroBag.IntegrationTests;

/// <summary>
/// 10 Kasım 2026 güvenlik incelemesinin düzeltmeleri: geliştirme ortamı dışında demo varsayılanlarıyla açılmama ve
/// olay zamanı sınırı.
/// </summary>
public class HardeningTests(PostgresFixture fx) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static WebApplicationFactory<Program> InProduction(WebApplicationFactory<Program> factory) =>
        factory.WithWebHostBuilder(b => b.UseEnvironment("Production"));

    private static (string Key, string Value)[] RealSettings() =>
    [
        ("Security:LookupKeySecret", "hardening-test-lookup-secret-0123456789"),
        ("Auth:Users:0:Username", "ops-real"),
        ("Auth:Users:0:PasswordHash", PasswordHasher.Hash("hardening-test-password", iterations: 10_000)),
        ("Auth:Users:0:Role", "ops"),
    ];

    [Fact]
    public void Outside_development_the_api_refuses_to_start_with_demo_defaults()
    {
        // TestApi bağlantı dizesini ve mesaj kaynaklarını verir; kullanıcılar ve arama anahtarı demo varsayılanında kalır.
        using var factory = InProduction(TestApi.Create(fx));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var message = ex.ToString();
        Assert.Contains("Auth:Users", message);
        Assert.Contains("Security:LookupKeySecret", message);
        Assert.DoesNotContain("Ingest:Sources", message);
        Assert.DoesNotContain("ConnectionStrings:Default", message);
    }

    [Fact]
    public async Task Outside_development_the_api_starts_when_every_secret_is_configured()
    {
        using var factory = InProduction(TestApi.Create(fx, RealSettings()));
        using var client = factory.CreateClient();

        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        // Demo kullanıcısı artık yok; yapılandırılan kullanıcı girebiliyor.
        var demo = await client.PostAsync("/v1/auth/token",
            new StringContent("""{"username":"ops","password":"ops-demo-2026"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, demo.StatusCode);

        var real = await client.PostAsync("/v1/auth/token",
            new StringContent("""{"username":"ops-real","password":"hardening-test-password"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, real.StatusCode);
    }

    private BaggageEventProcessor NewProcessor(EventTimePolicy? policy) =>
        new(fx.Db, new FixedClock(Now), new LookupKeyHasher(Encoding.UTF8.GetBytes("test-secret-0123456789abcdef")), timePolicy: policy);

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    private static string Bsm(string tag) =>
        $"BSM\n.V/1LAYT\n.F/ZZ0124/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/AB3K7Q\nENDBSM";

    private static string Bpm(string tag, string day) =>
        $"BPM\n.V/1LAYT\n.J/S/SEC01/{day}/150000Z\n.F/ZZ0124/22OCT/AYT\n.N/{tag}001\nENDBPM";

    private async Task<long> CountAsync(string sql, string tag)
    {
        await using var cmd = fx.Db.CreateCommand(sql);
        cmd.Parameters.AddWithValue("t", tag);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private const string EventCountSql =
        "SELECT count(*) FROM baggage_events e JOIN bags b ON b.bag_id = e.bag_id WHERE b.bag_tag_number = @t";

    private const string DeadLetterSql =
        "SELECT count(*) FROM dead_letter_messages WHERE error LIKE 'EventTimeOutOfRange%' AND raw_text LIKE '%' || @t || '%'";

    [Fact]
    public async Task With_a_time_limit_an_event_dated_days_away_is_dead_lettered_and_changes_nothing()
    {
        var tag = NewTag();
        var processor = NewProcessor(new EventTimePolicy(TimeSpan.FromHours(24)));
        Assert.Equal(ProcessResult.Accepted, (await processor.ProcessAsync(Bsm(tag), "test")).Status);

        // Alınma anı 22 Ekim 14:00; okuma 25 Ekim 15:00 diyor (3 gün ileri).
        var result = await processor.ProcessAsync(Bpm(tag, "25OCT"), "test");

        Assert.Equal(ProcessResult.Rejected, result.Status);
        Assert.StartsWith("EventTimeOutOfRange", result.Error);
        Assert.Equal(1, await CountAsync(EventCountSql, tag));
        Assert.Equal(1, await CountAsync(DeadLetterSql, tag));
    }

    [Fact]
    public async Task With_a_time_limit_an_event_inside_the_window_is_accepted()
    {
        var tag = NewTag();
        var processor = NewProcessor(new EventTimePolicy(TimeSpan.FromHours(24)));
        await processor.ProcessAsync(Bsm(tag), "test");

        // Aynı gün 15:00: alınma anından 1 saat ileri.
        var result = await processor.ProcessAsync(Bpm(tag, "22OCT"), "test");

        Assert.Equal(ProcessResult.Accepted, result.Status);
        Assert.Equal(2, await CountAsync(EventCountSql, tag));
        Assert.Equal(0, await CountAsync(DeadLetterSql, tag));
    }

    [Fact]
    public async Task Without_a_time_limit_simulated_event_times_are_accepted_as_before()
    {
        var tag = NewTag();
        var processor = NewProcessor(policy: null);
        await processor.ProcessAsync(Bsm(tag), "test");

        var result = await processor.ProcessAsync(Bpm(tag, "25OCT"), "test");

        Assert.Equal(ProcessResult.Accepted, result.Status);
        Assert.Equal(2, await CountAsync(EventCountSql, tag));
    }
}
