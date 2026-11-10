namespace AeroBag.Infrastructure;

/// <summary>
/// Olay zamanının alınma anından en çok ne kadar sapabileceği. <c>null</c>: sınır yok (simülasyon; olay zamanı sanaldır).
/// Gerçek saat modunda (Clock:Mode=real) varsayılan 24 saattir; <c>Ingest:MaxEventTimeSkewHours</c> ile değiştirilir.
/// </summary>
public sealed record EventTimePolicy(TimeSpan? MaxSkew)
{
    public static readonly EventTimePolicy Unlimited = new((TimeSpan?)null);
}
