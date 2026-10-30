using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using AeroBag.Application.Ml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AeroBag.Infrastructure;

/// <summary>
/// ML servisine HTTP istemcisi. Hata ya da zaman aşımında null döner (çağıran taban çizgisine düşer).
/// Tahmin isteği başarısız olunca kısa bir süre servise hiç gitmez, böylece servis kapalıyken sorgular yavaşlamaz.
/// Başarılı tahminler kısa süre önbellekte tutulur.
/// İstek ve yanıt biçimi <c>contracts/ml</c> altındaki sözleşmedir (W6); iki taraf da o dosyalara karşı test edilir.
/// Hatalar <see cref="MlStatus"/>'a yazılır ve hata durumuna geçildiği an (her denemede değil) uyarı olarak loglanır.
/// </summary>
public sealed class MlHttpPredictor(HttpClient http, TimeProvider clock, MlStatus? status = null, ILogger? log = null) : IMlPredictor
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Servis geçici olarak yanıt vermezse uçuş düzeyi tahmin için en son başarılı sonuç bu süre boyunca kullanılır.
    /// Böylece kısa bir kesinti panoyu "Tahmin yok"a çevirmez. Bu süreden eskisi kullanılmaz.
    /// </summary>
    private static readonly TimeSpan StaleTtl = TimeSpan.FromMinutes(10);

    private readonly MlStatus _status = status ?? new MlStatus();
    private readonly ILogger _log = log ?? NullLogger.Instance;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, MlFlightForecast F)> _cache = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, BagDurationForecast F)> _bagCache = new();
    private long _downUntilTicks;

    private bool InBackoff => clock.GetUtcNow().UtcTicks < Interlocked.Read(ref _downUntilTicks);

    private static string Reason(Exception ex) => ex switch
    {
        TaskCanceledException => "zaman aşımı",
        HttpRequestException => "bağlantı hatası",
        JsonException or KeyNotFoundException or InvalidOperationException => "sözleşmeye uymayan yanıt",
        _ => ex.GetType().Name,
    };

    private void Ok()
    {
        if (_status.RecordSuccess(clock.GetUtcNow()))
        {
            _log.LogInformation("ML servisi yeniden yanıt veriyor.");
        }
    }

    /// <param name="backoff">Tahmin isteklerinde true: bir süre servise gidilmez. Geri bildirim isteklerinde false.</param>
    private void Fail(string reason, bool backoff)
    {
        var now = clock.GetUtcNow();
        DateTimeOffset? until = null;
        if (backoff)
        {
            until = now + FailureBackoff;
            Interlocked.Exchange(ref _downUntilTicks, until.Value.UtcTicks);
        }
        if (_status.RecordFailure(now, reason, until))
        {
            _log.LogWarning("ML servisi yanıt vermedi ({Reason}); tahminler taban çizgisine düşüyor{Backoff}.", reason,
                backoff ? $", {FailureBackoff.TotalSeconds:0} sn boyunca servise gidilmeyecek" : "");
        }
    }

    public async Task<MlFlightForecast?> PredictAsync(FlightContext c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        if (_cache.TryGetValue(c.FlightId, out var hit) && now - hit.At < CacheTtl)
        {
            return hit.F;
        }
        MlFlightForecast? Stale() => hit.F is not null && now - hit.At < StaleTtl ? hit.F : null;
        if (InBackoff) return Stale();

        try
        {
            var body = new
            {
                flight_id = c.FlightId, wide = c.Wide ? 1 : 0, rain = c.Rain ? 1 : 0, bags = c.Bags,
                stand_m = c.StandM, chute_m = c.ChuteM, speed = c.SpeedMps, arrivals_30 = c.Arrivals30, handler = c.Handler,
            };
            using var resp = await http.PostAsJsonAsync("/v1/predict", body, ct);
            if (!resp.IsSuccessStatusCode) { Fail($"HTTP {(int)resp.StatusCode}", backoff: true); return Stale(); }

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var m = doc.RootElement.GetProperty("minutes_from_on_block");
            MinuteQuantiles Q(string name)
            {
                var e = m.GetProperty(name);
                return new MinuteQuantiles(e.GetProperty("p10").GetDouble(), e.GetProperty("p50").GetDouble(), e.GetProperty("p90").GetDouble());
            }
            var forecast = new MlFlightForecast(Q("first_min"), Q("span_min"), Q("last_min"));
            _cache[c.FlightId] = (now, forecast);
            Ok();
            return forecast;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException
                                   && !ct.IsCancellationRequested)
        {
            Fail(Reason(ex), backoff: true);
            return Stale();
        }
    }

    private static object Features(FlightContext c) => new
    {
        wide = c.Wide ? 1 : 0, rain = c.Rain ? 1 : 0, bags = c.Bags, stand_m = c.StandM, chute_m = c.ChuteM,
        speed = c.SpeedMps, arrivals_30 = c.Arrivals30, handler = c.Handler,
    };

    public async Task<BagDurationForecast?> PredictBagAsync(FlightContext c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        if (_bagCache.TryGetValue(c.FlightId, out var hit) && now - hit.At < CacheTtl)
        {
            return hit.F;
        }
        if (InBackoff) return null;

        try
        {
            using var resp = await http.PostAsJsonAsync("/v1/predict-bag", Features(c), ct);
            if (!resp.IsSuccessStatusCode) { Fail($"HTTP {(int)resp.StatusCode}", backoff: true); return null; }

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var e = doc.RootElement.GetProperty("seconds_after_unload");
            var forecast = new BagDurationForecast(
                e.GetProperty("p10").GetDouble(), e.GetProperty("p50").GetDouble(), e.GetProperty("p90").GetDouble());
            _bagCache[c.FlightId] = (now, forecast);
            Ok();
            return forecast;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException
                                   && !ct.IsCancellationRequested)
        {
            Fail(Reason(ex), backoff: true);
            return null;
        }
    }

    public async Task<int> ReportBagOutcomesAsync(IReadOnlyList<BagOutcome> items, CancellationToken ct = default)
    {
        if (items.Count == 0) return 0;
        try
        {
            var body = new { items = items.Select(i => new { features = Features(i.Context), duration_s = i.DurationSec }).ToArray() };
            using var resp = await http.PostAsJsonAsync("/v1/outcome-bags", body, ct);
            if (!resp.IsSuccessStatusCode) { Fail($"HTTP {(int)resp.StatusCode}", backoff: false); return -1; }
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var recorded = doc.RootElement.GetProperty("recorded").GetInt32();
            Ok();
            return recorded;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException
                                   && !ct.IsCancellationRequested)
        {
            Fail(Reason(ex), backoff: false);
            return -1;
        }
    }

    public async Task<bool> ReportOutcomeAsync(string flightId, double firstMin, double lastMin, CancellationToken ct = default)
    {
        try
        {
            using var resp = await http.PostAsJsonAsync("/v1/outcome",
                new { flight_id = flightId, first_min = firstMin, last_min = lastMin }, ct);
            if (!resp.IsSuccessStatusCode) { Fail($"HTTP {(int)resp.StatusCode}", backoff: false); return false; }
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var recorded = doc.RootElement.GetProperty("recorded").GetBoolean();
            Ok();
            return recorded;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException
                                   && !ct.IsCancellationRequested)
        {
            Fail(Reason(ex), backoff: false);
            return false;
        }
    }
}
