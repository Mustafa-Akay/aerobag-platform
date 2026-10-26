using System.Text.RegularExpressions;
using AeroBag.Application.Eta;

namespace AeroBag.Application.Ml;

/// <summary>Bir uçuşun ML modeline verilen, iniş öncesinde bilinen özellikleri (AODB bağlamı).</summary>
public sealed record FlightContext(
    string FlightId, DateTimeOffset OnBlockUtc, bool Wide, bool Rain, int Bags,
    double StandM, double ChuteM, double SpeedMps, int Arrivals30, string Handler)
{
    /// <summary>Python servisindeki sınırlarla aynıdır. Hata varsa açıklama, yoksa null döner.</summary>
    public string? Validate()
    {
        if (!Regex.IsMatch(FlightId ?? "", @"^[A-Z0-9\-]{1,40}$")) return "flightId geçersiz.";
        if (Bags is < 1 or > 800) return "bags 1-800 olmalı.";
        if (StandM is < 0 or > 10000) return "standM 0-10000 olmalı.";
        if (ChuteM is < 0 or > 500) return "chuteM 0-500 olmalı.";
        if (SpeedMps is <= 0 or > 3) return "speedMps 0-3 olmalı.";
        if (Arrivals30 is < 0 or > 30) return "arrivals30 0-30 olmalı.";
        if (!Regex.IsMatch(Handler ?? "", @"^HANDLER_[ABC]$")) return "handler geçersiz.";
        return null;
    }
}

public sealed record MinuteQuantiles(double P10, double P50, double P90);

/// <summary>Dakika cinsinden, on-block anından itibaren.</summary>
public sealed record MlFlightForecast(MinuteQuantiles First, MinuteQuantiles Span, MinuteQuantiles Last);

/// <summary>İndirilmiş bir bavulun, indirme anından banta gelişine kadar geçecek süre (saniye).</summary>
public sealed record BagDurationForecast(double P10Sec, double P50Sec, double P90Sec);

/// <summary>Banta gelmiş bir bavulun gerçek indirme→bant süresi; ML servisinin canlı düzeltmesi için.</summary>
public sealed record BagOutcome(FlightContext Context, double DurationSec);

public interface IMlPredictor
{
    /// <summary>Servis kapalıysa ya da hata verirse null döner; çağıran taban çizgisine düşer.</summary>
    Task<MlFlightForecast?> PredictAsync(FlightContext ctx, CancellationToken ct = default);

    /// <summary>İndirilmiş bavul için indirme anından itibaren banta gelme süresi. Hata ya da kapalıysa null.</summary>
    Task<BagDurationForecast?> PredictBagAsync(FlightContext ctx, CancellationToken ct = default);

    /// <summary>Banta gelmiş bavulların gerçek sürelerini bildirir; kaydedilen sayıyı döndürür (hata olursa -1).</summary>
    Task<int> ReportBagOutcomesAsync(IReadOnlyList<BagOutcome> items, CancellationToken ct = default);

    /// <summary>Biten uçuşun gerçek ilk/son bavul süresini bildirir (canlı düzeltme için).</summary>
    Task<bool> ReportOutcomeAsync(string flightId, double firstMin, double lastMin, CancellationToken ct = default);
}

public sealed class NullMlPredictor : IMlPredictor
{
    public Task<MlFlightForecast?> PredictAsync(FlightContext ctx, CancellationToken ct = default) =>
        Task.FromResult<MlFlightForecast?>(null);

    public Task<bool> ReportOutcomeAsync(string flightId, double firstMin, double lastMin, CancellationToken ct = default) =>
        Task.FromResult(false);

    public Task<BagDurationForecast?> PredictBagAsync(FlightContext ctx, CancellationToken ct = default) =>
        Task.FromResult<BagDurationForecast?>(null);

    public Task<int> ReportBagOutcomesAsync(IReadOnlyList<BagOutcome> items, CancellationToken ct = default) =>
        Task.FromResult(-1);
}

/// <summary>
/// İniş öncesi bir bavul için pencere: sırası bilinmediğinden ilk bavulun P10'undan son bavulun P90'ına kadar.
/// P50, ilk ve son bavul P50'sinin ortasıdır (bavulun uçuş içindeki konumu eşit olasılıklı varsayımı).
/// Bu bavul düzeyinde bir basitleştirmedir ve ayrıca ölçülmemiştir; bu pencere uçuş düzeyindeki modelin ham aralığıdır (basis ML_FLIGHT).
/// Pencere çoktan geçtiyse (asOf >= P90) null döner: yanlış ama kendinden emin bir aralık göstermemek için.
/// </summary>
public static class MlEtaWindow
{
    /// <summary>İndirilmiş bavul: son olay (indirme) anına modelin süre kuantilleri eklenir.</summary>
    public static EtaWindow BuildForBag(DateTimeOffset lastEventUtc, BagDurationForecast f)
    {
        var p10 = lastEventUtc.AddSeconds(f.P10Sec);
        var p50 = lastEventUtc.AddSeconds(Math.Max(f.P50Sec, f.P10Sec));
        var p90 = lastEventUtc.AddSeconds(Math.Max(f.P90Sec, Math.Max(f.P50Sec, f.P10Sec)));
        return new EtaWindow(p10, p50, p90, "ML");
    }

    public static EtaWindow? Build(FlightContext ctx, MlFlightForecast f, DateTimeOffset asOf)
    {
        var p10 = ctx.OnBlockUtc.AddMinutes(f.First.P10);
        var p50 = ctx.OnBlockUtc.AddMinutes((f.First.P50 + f.Last.P50) / 2);
        var p90 = ctx.OnBlockUtc.AddMinutes(f.Last.P90);
        if (asOf >= p90) return null;
        if (p50 < p10) p50 = p10;
        if (p90 < p50) p90 = p50;
        return new EtaWindow(p10, p50, p90, "ML_FLIGHT");
    }
}

/// <summary>
/// Şuttaki bavul için fizik tabanlı pencere (ARCHITECTURE.md 5.4): şut→bant süresi mesafe / bant hızı,
/// etrafında ±15 sn tolerans. Tolerans bir tasarım varsayımıdır; gerçek bantlarda ölçülmedi.
/// </summary>
public static class PhysicsEtaWindow
{
    public const double ToleranceSeconds = 15;

    public static EtaWindow Build(DateTimeOffset chuteEventUtc, FlightContext ctx)
    {
        var d = ctx.ChuteM / ctx.SpeedMps;
        return new EtaWindow(
            chuteEventUtc.AddSeconds(Math.Max(d - ToleranceSeconds, 0)),
            chuteEventUtc.AddSeconds(d),
            chuteEventUtc.AddSeconds(d + ToleranceSeconds),
            "PHYSICS");
    }
}
