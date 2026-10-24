using AeroBag.Infrastructure;

namespace AeroBag.IntegrationTests;

public class OpsStalledTests(PostgresFixture fx) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private BaggageEventProcessor NewProcessor() =>
        new(fx.Db, new FixedClock(Now), new LookupKeyHasher(System.Text.Encoding.UTF8.GetBytes("test-secret-0123456789abcdef")));

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    private static string Bsm(string tag) =>
        $"BSM\n.V/1LAYT\n.F/ZZ0124/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/AB3K7Q\nENDBSM";

    private static string Bpm(string tag, string point, string time) =>
        $"BPM\n.V/1LAYT\n.J/S/{point}/22OCT/{time}Z\n.F/ZZ0124/22OCT/AYT\n.N/{tag}001\nENDBPM";

    private async Task SendAsync(string text)
    {
        var result = await NewProcessor().ProcessAsync(text, "test");
        Assert.Equal(ProcessResult.Accepted, result.Status);
    }

    [Fact]
    public async Task Bag_waiting_on_carousel_longer_than_threshold_is_counted_as_stalled()
    {
        var stuck = NewTag();
        var recent = NewTag();
        var claimed = NewTag();

        await SendAsync(Bsm(stuck));
        await SendAsync(Bpm(stuck, "CAROUSEL01", "210000"));   // 35 dk önce, hâlâ bantta
        await SendAsync(Bsm(recent));
        await SendAsync(Bpm(recent, "CAROUSEL01", "212000"));  // 15 dk önce: eşiğin altında
        await SendAsync(Bsm(claimed));
        await SendAsync(Bpm(claimed, "CLAIM01", "213500"));    // sanal "şimdi" = 21:35

        var flight = Assert.Single(await new OpsQueries(fx.Db).FlightsAsync());

        Assert.Equal(2, flight.OnCarousel);
        Assert.Equal(1, flight.Claimed);
        Assert.Equal(1, flight.Stalled);
    }
}
