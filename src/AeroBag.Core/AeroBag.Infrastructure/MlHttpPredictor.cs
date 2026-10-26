using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using AeroBag.Application.Ml;

namespace AeroBag.Infrastructure;

/// <summary>
/// ML servisine HTTP istemcisi. Hata ya da zaman aşımında null döner (çağıran taban çizgisine düşer).
/// Hata sonrası kısa bir süre servise hiç gitmez, böylece servis kapalıyken sorgular yavaşlamaz.
/// Başarılı tahminler kısa süre önbellekte tutulur.
/// </summary>
public sealed class MlHttpPredictor(HttpClient http, TimeProvider clock) : IMlPredictor
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, MlFlightForecast F)> _cache = new();
    private long _downUntilTicks;

    private bool InBackoff => clock.GetUtcNow().UtcTicks < Interlocked.Read(ref _downUntilTicks);

    private void Fail() => Interlocked.Exchange(ref _downUntilTicks, (clock.GetUtcNow() + FailureBackoff).UtcTicks);

    public async Task<MlFlightForecast?> PredictAsync(FlightContext c, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        if (_cache.TryGetValue(c.FlightId, out var hit) && now - hit.At < CacheTtl)
        {
            return hit.F;
        }
        if (InBackoff) return null;

        try
        {
            var body = new
            {
                flight_id = c.FlightId, wide = c.Wide ? 1 : 0, rain = c.Rain ? 1 : 0, bags = c.Bags,
                stand_m = c.StandM, chute_m = c.ChuteM, speed = c.SpeedMps, arrivals_30 = c.Arrivals30, handler = c.Handler,
            };
            using var resp = await http.PostAsJsonAsync("/v1/predict", body, ct);
            if (!resp.IsSuccessStatusCode) { Fail(); return null; }

            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var m = doc.RootElement.GetProperty("minutes_from_on_block");
            MinuteQuantiles Q(string name)
            {
                var e = m.GetProperty(name);
                return new MinuteQuantiles(e.GetProperty("p10").GetDouble(), e.GetProperty("p50").GetDouble(), e.GetProperty("p90").GetDouble());
            }
            var forecast = new MlFlightForecast(Q("first_min"), Q("span_min"), Q("last_min"));
            _cache[c.FlightId] = (now, forecast);
            return forecast;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Fail();
            return null;
        }
    }

    public async Task<bool> ReportOutcomeAsync(string flightId, double firstMin, double lastMin, CancellationToken ct = default)
    {
        try
        {
            using var resp = await http.PostAsJsonAsync("/v1/outcome",
                new { flight_id = flightId, first_min = firstMin, last_min = lastMin }, ct);
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.GetProperty("recorded").GetBoolean();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }
}
