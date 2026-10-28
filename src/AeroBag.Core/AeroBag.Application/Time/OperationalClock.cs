namespace AeroBag.Application.Time;

/// <summary>
/// Operasyonun "şimdi"si: çıkış süresi, ETA, SLA, takılı bavul ve ML geri bildirimi bu saate göre çalışır.
/// Gerçek kurulumda gerçek saattir. Simülasyonda olaylar sanal zamanla (geçmiş ya da hızlandırılmış) geldiği için
/// "şimdi" en son olay zamanından türetilir. Hangisinin kullanılacağı yapılandırmayla seçilir (<c>Clock:Mode</c>).
/// Saklama süresi (KVKK) bu saati değil, her zaman gerçek saati kullanır.
/// </summary>
public interface IOperationalClock
{
    string Mode { get; }

    /// <summary>Şimdiki an. Olaydan türetilen saatte henüz hiç olay yoksa null.</summary>
    Task<DateTimeOffset?> NowAsync(CancellationToken ct = default);
}

public static class ClockModes
{
    public const string Real = "real";
    public const string Event = "event";
    public const string Fixed = "fixed";
}

/// <summary>Gerçek saat (üretim).</summary>
public sealed class SystemOperationalClock(TimeProvider time) : IOperationalClock
{
    public string Mode => ClockModes.Real;

    public Task<DateTimeOffset?> NowAsync(CancellationToken ct = default) => Task.FromResult<DateTimeOffset?>(time.GetUtcNow());
}

/// <summary>Sabit saat (testler ve geriye dönük hesaplar).</summary>
public sealed class FixedOperationalClock(DateTimeOffset? now) : IOperationalClock
{
    public string Mode => ClockModes.Fixed;

    public Task<DateTimeOffset?> NowAsync(CancellationToken ct = default) => Task.FromResult(now);
}
