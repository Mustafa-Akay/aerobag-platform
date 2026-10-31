namespace AeroBag.Application.Transfer;

/// <summary>
/// Aktarma bavulu ayarları (Faz 6, W10a). Bavul kabul kapanışı: bağlantı uçağına bavul yüklemenin bittiği an, planlanan
/// kalkıştan bu kadar önce. Varsayılan 15 dk DEMO varsayımıdır; gerçek değer havayoluna, uçak tipine ve kapıya göre değişir
/// ve yapılandırmayla verilir (<c>Transfer:CutoffMinutes</c>).
/// </summary>
public sealed record TransferPolicy(TimeSpan Cutoff)
{
    public static readonly TransferPolicy Default = new(TimeSpan.FromMinutes(15));

    /// <summary>Planlanan kalkıştan kabul kapanışı; kalkış bilinmiyorsa null.</summary>
    public DateTimeOffset? CutoffFor(DateTimeOffset? scheduledDepartureUtc) => scheduledDepartureUtc - Cutoff;
}
