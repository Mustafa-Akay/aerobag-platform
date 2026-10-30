using System.Text;
using AeroBag.Infrastructure;
using Npgsql;

namespace AeroBag.IntegrationTests;

/// <summary>KVKK notu: saklama süresi dolan yolcunun arama özetleri değiştirilir; uygulama rolü bunun dışında yolcu satırını değiştiremez.</summary>
public class RetentionTests(PostgresFixture fx) : IClassFixture<PostgresFixture>
{
    private static readonly LookupKeyHasher Hasher = new(Encoding.UTF8.GetBytes("test-secret-0123456789abcdef"));
    private static readonly DateTimeOffset T0 = new(2026, 10, 22, 14, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string NewTag() => Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

    private static string NewPnr() => "R" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();

    private async Task<string> BagAsync(string pnr, bool delivered)
    {
        var tag = NewTag();
        var p = new BaggageEventProcessor(fx.Db, new FixedClock(T0), Hasher);
        await p.ProcessAsync($"BSM\n.V/1LAYT\n.F/ZZ0801/22OCT/AYT/Y\n.N/{tag}001\n.W/K/1/18\n.P/TUNA/ECE\n.L/{pnr}\nENDBSM", "test");
        if (delivered)
        {
            await p.ProcessAsync($"BPM\n.V/1LAYT\n.J/S/CLAIM04/22OCT/121200Z\n.F/ZZ0801/22OCT/AYT\n.N/{tag}001\nENDBPM", "test");
        }
        return tag;
    }

    private PassengerQueries Lookup() => new(fx.Db, Hasher, new EtaStatistics(fx.Db), new FixedClock(T0));

    private RetentionService Retention(TimeSpan after) =>
        new(NpgsqlDataSource.Create(fx.AppConnectionString), new FixedClock(T0 + after), new RetentionOptions(30));

    [Fact]
    public async Task Delivered_passenger_becomes_unfindable_after_the_retention_period()
    {
        var pnr = NewPnr();
        await BagAsync(pnr, delivered: true);
        Assert.NotNull(await Lookup().LookupAsync(pnr, "TUNA"));

        Assert.Equal(0, await Retention(TimeSpan.FromDays(29)).AnonymizeDueAsync());
        Assert.NotNull(await Lookup().LookupAsync(pnr, "TUNA"));

        Assert.True(await Retention(TimeSpan.FromDays(31)).AnonymizeDueAsync() >= 1);
        Assert.Null(await Lookup().LookupAsync(pnr, "TUNA"));

        // İkinci çalıştırma aynı yolcuya dokunmaz.
        await using var cmd = fx.Db.CreateCommand("SELECT count(*) FROM passengers WHERE anonymized_at_utc IS NOT NULL");
        var before = (long)(await cmd.ExecuteScalarAsync())!;
        await Retention(TimeSpan.FromDays(32)).AnonymizeDueAsync();
        Assert.Equal(before, (long)(await cmd.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Passenger_with_an_undelivered_bag_is_kept_until_the_undelivered_limit()
    {
        // Bant ağzında okuyucu olmayan kurulumda bavul "şutta" kalabilir; kayıp bavul da teslim edilmez (W5).
        var pnr = NewPnr();
        await BagAsync(pnr, delivered: false);

        await Retention(TimeSpan.FromDays(89)).AnonymizeDueAsync();
        Assert.NotNull(await Lookup().LookupAsync(pnr, "TUNA"));

        await Retention(TimeSpan.FromDays(91)).AnonymizeDueAsync();
        Assert.Null(await Lookup().LookupAsync(pnr, "TUNA"));
    }

    [Fact]
    public async Task App_role_can_change_only_the_key_columns_of_passengers()
    {
        await using var app = NpgsqlDataSource.Create(fx.AppConnectionString);
        await using var cmd = app.CreateCommand("UPDATE passengers SET cabin_class = 'C'");
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }
}
