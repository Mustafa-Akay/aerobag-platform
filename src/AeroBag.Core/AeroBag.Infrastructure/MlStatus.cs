namespace AeroBag.Infrastructure;

/// <summary>ML servisinin son durumu (operasyon panosu ve GET /v1/ml/status için). Kişisel veri içermez.</summary>
public sealed record MlStatusView(
    bool Enabled,
    bool Healthy,
    long Requests,
    long Failures,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    string? LastFailureReason,
    DateTimeOffset? BackoffUntilUtc);

/// <summary>
/// ML çağrılarının sayacı (W6). Daha önce tahmin sessizce taban çizgisine (HISTORY/FALLBACK) düşüyor, nedeni hiçbir yerde
/// görünmüyordu (28 Ekim canlı denemesi). İş parçacığı güvenlidir.
/// </summary>
public sealed class MlStatus(bool enabled = true)
{
    private readonly object _gate = new();
    private long _requests;
    private long _failures;
    private DateTimeOffset? _lastSuccess;
    private DateTimeOffset? _lastFailure;
    private string? _lastReason;
    private DateTimeOffset? _backoffUntil;
    private bool _failing;

    /// <summary>Başarılı çağrı. Önceden hata durumundaysa true döner (servis yeniden yanıt veriyor).</summary>
    public bool RecordSuccess(DateTimeOffset now)
    {
        lock (_gate)
        {
            _requests++;
            _lastSuccess = now;
            var recovered = _failing;
            _failing = false;
            return recovered;
        }
    }

    /// <summary>Başarısız çağrı. Hata durumuna yeni geçildiyse true döner (log yalnızca o an yazılır, her denemede değil).</summary>
    public bool RecordFailure(DateTimeOffset now, string reason, DateTimeOffset? backoffUntil)
    {
        lock (_gate)
        {
            _requests++;
            _failures++;
            _lastFailure = now;
            _lastReason = reason;
            if (backoffUntil is not null) _backoffUntil = backoffUntil;
            var started = !_failing;
            _failing = true;
            return started;
        }
    }

    public MlStatusView Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            var backoff = _backoffUntil is { } b && b > now ? _backoffUntil : null;
            return new MlStatusView(enabled, enabled && !_failing, _requests, _failures, _lastSuccess, _lastFailure, _lastReason, backoff);
        }
    }
}
