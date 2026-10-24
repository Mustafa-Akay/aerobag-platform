using AeroBag.Infrastructure;

namespace AeroBag.IntegrationTests;

public class OutboxRelayTests(PostgresFixture fx) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingPublisher(Func<OutboxMessage, bool>? failWhen = null) : IOutboxPublisher
    {
        public List<OutboxMessage> Sent { get; } = [];

        public Task PublishAsync(OutboxMessage message, CancellationToken ct)
        {
            if (failWhen?.Invoke(message) == true)
            {
                throw new InvalidOperationException("yayın hatası");
            }
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private BaggageEventProcessor NewProcessor() =>
        new(fx.Db, new FixedClock(Now), new LookupKeyHasher(System.Text.Encoding.UTF8.GetBytes("test-secret-0123456789abcdef")));

    private OutboxRelay NewRelay() => new(fx.Db, new FixedClock(Now));

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    private static string Bsm(string tag) =>
        $"BSM\n.V/1LAYT\n.F/ZZ0124/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/YILMAZ/AYSE\n.L/AB3K7Q\nENDBSM";

    private static string Bpm(string tag, string point, string time) =>
        $"BPM\n.V/1LAYT\n.J/S/{point}/22OCT/{time}Z\n.F/ZZ0124/22OCT/AYT\n.N/{tag}001\nENDBPM";

    private async Task<(long Attempts, bool Processed)> StateAsync(string tag)
    {
        await using var cmd = fx.Db.CreateCommand(
            "SELECT attempts, processed_at_utc IS NOT NULL FROM outbox_messages WHERE partition_key = @t");
        cmd.Parameters.AddWithValue("t", tag);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt32(0), reader.GetBoolean(1));
    }

    [Fact]
    public async Task Dispatch_publishes_once_and_marks_processed()
    {
        var tag = NewTag();
        await NewProcessor().ProcessAsync(Bsm(tag), "test");
        var publisher = new RecordingPublisher();

        await NewRelay().DispatchOnceAsync(publisher);
        var second = new RecordingPublisher();
        await NewRelay().DispatchOnceAsync(second);

        Assert.Single(publisher.Sent, m => m.PartitionKey == tag);
        Assert.DoesNotContain(second.Sent, m => m.PartitionKey == tag);
        Assert.Equal((1L, true), await StateAsync(tag));
    }

    [Fact]
    public async Task Dispatch_keeps_order_within_a_batch()
    {
        var tag = NewTag();
        var processor = NewProcessor();
        foreach (var text in new[] { Bsm(tag), Bpm(tag, "SEC01", "205500"), Bpm(tag, "LOAD01", "205800") })
        {
            // Önkoşul: üç mesajın da kabul edildiğinden emin ol; yoksa asıl sınanan şey sessizce boşa çıkar.
            Assert.Equal(ProcessResult.Accepted, (await processor.ProcessAsync(text, "test")).Status);
        }
        var publisher = new RecordingPublisher();

        await NewRelay().DispatchOnceAsync(publisher);

        var states = publisher.Sent.Where(m => m.PartitionKey == tag)
            .Select(m => System.Text.Json.JsonDocument.Parse(m.PayloadJson).RootElement.GetProperty("toState").GetString())
            .ToList();
        Assert.Equal(["CHECKED_IN", "SECURITY_SCREENED", "LOADED_ON_AIRCRAFT"], states);
    }

    [Fact]
    public async Task Failed_publish_is_retried_and_counted_then_parked_after_max_attempts()
    {
        var tag = NewTag();
        await NewProcessor().ProcessAsync(Bsm(tag), "test");
        var failing = new RecordingPublisher(m => m.PartitionKey == tag);

        var first = await NewRelay().DispatchOnceAsync(failing);
        Assert.True(first.Failed >= 1);
        Assert.Equal((1L, false), await StateAsync(tag));

        for (var i = 1; i < OutboxRelay.MaxAttempts; i++)
        {
            await NewRelay().DispatchOnceAsync(failing);
        }
        Assert.Equal((5L, false), await StateAsync(tag));

        // Sınıra ulaşan mesaj artık denenmez; sağlıklı yayıncı bile onu görmez.
        var healthy = new RecordingPublisher();
        await NewRelay().DispatchOnceAsync(healthy);
        Assert.DoesNotContain(healthy.Sent, m => m.PartitionKey == tag);
        Assert.Equal((5L, false), await StateAsync(tag));
    }

    [Fact]
    public async Task Failed_publish_succeeds_on_later_attempt()
    {
        var tag = NewTag();
        await NewProcessor().ProcessAsync(Bsm(tag), "test");

        await NewRelay().DispatchOnceAsync(new RecordingPublisher(m => m.PartitionKey == tag));
        var healthy = new RecordingPublisher();
        await NewRelay().DispatchOnceAsync(healthy);

        Assert.Single(healthy.Sent, m => m.PartitionKey == tag);
        Assert.Equal((2L, true), await StateAsync(tag));
    }
}
