using AeroBag.Application.Ml;

namespace AeroBag.Application.Sla;

/// <summary>
/// İlk/son bavul hedefi için durum. Hedefler DEMO varsayımıdır (sözleşme değil).
/// MET: gerçekleşti ve hedefte. BREACHED: hedef aşıldı (gerçekleşti ya da süre doldu).
/// ON_TRACK / AT_RISK / LIKELY_BREACH: modelin ihlal olasılığına göre. UNKNOWN: tahmin yok.
/// </summary>
public static class SlaStatus
{
    public const string Met = "MET";
    public const string Breached = "BREACHED";
    public const string OnTrack = "ON_TRACK";
    public const string AtRisk = "AT_RISK";
    public const string LikelyBreach = "LIKELY_BREACH";
    public const string Unknown = "UNKNOWN";

    public static string FromProbability(double p) => p switch
    {
        < 0.2 => OnTrack,
        < 0.6 => AtRisk,
        _ => LikelyBreach,
    };
}

public sealed record SlaLeg(
    int TargetSec,
    double? ActualSec,
    double? BreachProbability,
    string Status,
    double? ProjectedP50Sec,
    double? ProjectedP90Sec,
    double? ProjectedDelayP50Sec,
    double? ProjectedDelayP90Sec);

/// <summary>
/// Üç kuantilden (P10, P50, P90) parça parça doğrusal bir dağılım fonksiyonu kurar.
/// Uç kuyruklar komşu parçanın eğimiyle, 0,01 ile 0,99 arasında kesilerek uzatılır.
/// Bu bir yaklaşıklıktır: model yalnızca üç kuantil verir, gerçek dağılım şeklini bilmeyiz.
/// </summary>
public static class QuantileCdf
{
    public const double Floor = 0.01;
    private const double Ceil = 0.99;

    public static double Cdf(MinuteQuantiles q, double x)
    {
        var p10 = q.P10;
        var p50 = Math.Max(q.P50, p10);
        var p90 = Math.Max(q.P90, p50);

        double lo = SlopeOrDefault(p10, p50, 0.4, p90 - p10);
        double hi = SlopeOrDefault(p50, p90, 0.4, p50 - p10);

        double result;
        if (x <= p10)
        {
            result = 0.1 - (p10 - x) * lo;
        }
        else if (x <= p50)
        {
            result = p50 == p10 ? 0.5 : 0.1 + 0.4 * (x - p10) / (p50 - p10);
        }
        else if (x <= p90)
        {
            result = p90 == p50 ? 0.9 : 0.5 + 0.4 * (x - p50) / (p90 - p50);
        }
        else
        {
            result = 0.9 + (x - p90) * hi;
        }

        return Math.Clamp(result, Floor, Ceil);
    }

    /// <summary>Kuyruk eğimi (olasılık / dakika). Parça dejenereyse (genişlik 0) bütün aralıktan türetilir.</summary>
    private static double SlopeOrDefault(double a, double b, double dp, double fallbackWidth)
    {
        var width = b - a;
        if (width > 1e-6) return dp / width;
        return fallbackWidth > 1e-6 ? 0.8 / fallbackWidth : 0.4;
    }
}

public static class SlaRisk
{
    /// <summary>Hedefi (saniye, on-block'tan) aşma olasılığı; zaman geçtiyse "şu ana kadar aşılmadı" koşuluyla.</summary>
    public static double ExceedProbability(MinuteQuantiles q, double targetSec, double elapsedSec)
    {
        var survivalAtTarget = 1 - QuantileCdf.Cdf(q, targetSec / 60.0);
        if (targetSec <= elapsedSec)
        {
            return 1.0;
        }

        // Alt kuyrukta kesilmiş (taban) değer gerçek olasılık değildir; o durumda koşullandırma yapılmaz.
        var cdfNow = QuantileCdf.Cdf(q, elapsedSec / 60.0);
        var survivalNow = cdfNow <= QuantileCdf.Floor + 1e-9 ? 1.0 : 1 - cdfNow;
        return Math.Clamp(survivalAtTarget / Math.Max(survivalNow, 1e-6), 0, 1);
    }

    /// <summary>
    /// Bir bacak (ilk ya da son bavul) için durum.
    /// <paramref name="actualSec"/>: gerçekleştiyse on-block'tan geçen süre. Değilse null.
    /// <paramref name="elapsedSec"/>: on-block'tan bu yana geçen (sanal) süre.
    /// </summary>
    public static SlaLeg Evaluate(int targetSec, double? actualSec, double elapsedSec, MinuteQuantiles? forecast)
    {
        if (actualSec is { } a)
        {
            var breached = a > targetSec;
            return new SlaLeg(targetSec, a, breached ? 1 : 0, breached ? SlaStatus.Breached : SlaStatus.Met,
                null, null, breached ? a - targetSec : 0, breached ? a - targetSec : 0);
        }

        var overdue = elapsedSec > targetSec;
        if (forecast is null)
        {
            return new SlaLeg(targetSec, null, overdue ? 1 : null, overdue ? SlaStatus.Breached : SlaStatus.Unknown,
                null, null, overdue ? elapsedSec - targetSec : null, null);
        }

        var p50 = Math.Max(forecast.P50 * 60, elapsedSec);
        var p90 = Math.Max(forecast.P90 * 60, p50);
        var prob = ExceedProbability(forecast, targetSec, elapsedSec);
        return new SlaLeg(targetSec, null, prob, overdue ? SlaStatus.Breached : SlaStatus.FromProbability(prob),
            p50, p90, Math.Max(p50 - targetSec, 0), Math.Max(p90 - targetSec, 0));
    }
}
