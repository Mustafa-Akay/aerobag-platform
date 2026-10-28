using AeroBag.Application.Time;

namespace AeroBag.UnitTests;

/// <summary>W2'nin bitti ölçütü: kodda "en son olay zamanı" ile şimdi hesaplayan tek yer EventDerivedClock'tur.</summary>
public class OperationalClockGuardTests
{
    private const string VirtualNowSql = "max(occurred_at_utc) FROM baggage_events WHERE message_type <> 'BSM'";

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AeroBag.slnx")))
        {
            dir = dir.Parent;
        }
        return dir ?? throw new DirectoryNotFoundException("AeroBag.slnx bulunamadı.");
    }

    [Fact]
    public void Only_the_event_clock_derives_now_from_the_event_log()
    {
        var src = Path.Combine(RepoRoot().FullName, "src");
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(VirtualNowSql, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(new[] { "EventDerivedClock.cs" }, offenders);
    }

    [Fact]
    public async Task System_clock_returns_the_time_provider_time()
    {
        var t = new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
        var clock = new SystemOperationalClock(new Fixed(t));
        Assert.Equal(t, await clock.NowAsync());
        Assert.Equal(ClockModes.Real, clock.Mode);
    }

    [Fact]
    public async Task Fixed_clock_can_be_empty()
    {
        Assert.Null(await new FixedOperationalClock(null).NowAsync());
    }

    private sealed class Fixed(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
