using AeroBag.Domain;

namespace AeroBag.Application.Eta;

public sealed record DurationQuantiles(int Samples, double P10Sec, double P50Sec, double P90Sec);

public sealed record EtaWindow(DateTimeOffset P10Utc, DateTimeOffset P50Utc, DateTimeOffset P90Utc, string Basis);

/// <summary>
/// Taban çizgisi ETA: bavulun son durum olayından sonra bandta olma süresinin geçmiş
/// olaylardan hesaplanmış P10/P50/P90 aralığı. Yeterli geçmiş (MinSamples) yoksa sabit varsayılan aralık
/// kullanılır ve Basis "FALLBACK" olur. Makine öğrenmesi modeli (Faz 2) bunun karşısında ölçülecek.
/// </summary>
public static class EtaEstimator
{
    public const int MinSamples = 20;

    public static readonly DurationQuantiles FallbackUnloadToCarousel = new(0, 300, 540, 900);
    public static readonly DurationQuantiles FallbackChuteToCarousel = new(0, 60, 90, 150);

    public static EtaWindow? Estimate(
        BagState state,
        DateTimeOffset lastEventUtc,
        DurationQuantiles? unloadToCarousel,
        DurationQuantiles? chuteToCarousel)
    {
        var (measured, fallback) = state switch
        {
            BagState.UnloadedAtArrival => (unloadToCarousel, FallbackUnloadToCarousel),
            BagState.InboundChute => (chuteToCarousel, FallbackChuteToCarousel),
            _ => (null, null),
        };
        if (fallback is null)
        {
            return null;
        }

        var useHistory = measured is { Samples: >= MinSamples };
        var q = useHistory ? measured! : fallback;
        return new EtaWindow(
            lastEventUtc.AddSeconds(q.P10Sec),
            lastEventUtc.AddSeconds(q.P50Sec),
            lastEventUtc.AddSeconds(q.P90Sec),
            useHistory ? "HISTORY" : "FALLBACK");
    }
}
