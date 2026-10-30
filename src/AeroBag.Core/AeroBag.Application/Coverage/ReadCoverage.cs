using AeroBag.Domain;

namespace AeroBag.Application.Coverage;

/// <summary>Bavulun okunabildiği noktalar. BSM (check-in) her zaman vardır; çıkış kapısı (ExitGuard) ayrıca yapılandırılır.</summary>
public enum ReadPoint
{
    Security,
    Load,
    Unload,
    Chute,
    Carousel,
    Claim,
}

/// <summary>
/// Havalimanının okuma kapsamı (Faz 6, W5): hangi noktalarda okuyucu var. Okuyucu yerleşimi altyapının bir özelliğidir;
/// uçuştan uçuşa tahmin edilmez, yapılandırmayla verilir (<c>Coverage:Profile</c>). Sistem kapsamı bilmezse okuyucusu
/// olmayan bir noktanın atlanmasını "sıra dışı" sayar, bant okuması beklediği için ExitGuard her bavulda yanlış alarm verir.
/// </summary>
public sealed class ReadCoverage
{
    /// <summary>Her noktada okuyucu var (simülatörün varsayılanı).</summary>
    public static readonly ReadCoverage Full = new("full",
        [ReadPoint.Security, ReadPoint.Load, ReadPoint.Unload, ReadPoint.Chute, ReadPoint.Carousel, ReadPoint.Claim]);

    /// <summary>Yaygın kurulum: şuta kadar okuma var; bant ağzında ve banttan almada okuyucu yok.</summary>
    public static readonly ReadCoverage Typical = new("typical",
        [ReadPoint.Security, ReadPoint.Load, ReadPoint.Unload, ReadPoint.Chute]);

    /// <summary>En az kurulum: yalnızca yükleme ve indirme okuması (BSM her zaman var).</summary>
    public static readonly ReadCoverage Minimal = new("minimal", [ReadPoint.Load, ReadPoint.Unload]);

    private readonly HashSet<ReadPoint> _points;

    private ReadCoverage(string name, IEnumerable<ReadPoint> points)
    {
        Name = name;
        _points = [.. points];
    }

    public string Name { get; }

    public IReadOnlyCollection<ReadPoint> Points => _points;

    public bool Has(ReadPoint point) => _points.Contains(point);

    public static ReadCoverage Parse(string? profile) => (profile ?? "full").Trim().ToLowerInvariant() switch
    {
        "full" => Full,
        "typical" => Typical,
        "minimal" => Minimal,
        var other => throw new ArgumentException($"Coverage:Profile '{other}' geçersiz; full, typical ya da minimal olmalı."),
    };

    /// <summary>Bir durumu doğrulayan okuma noktası; check-in (BSM) ve sistemin ürettiği çıkış durumları için null.</summary>
    public static ReadPoint? PointOf(BagState state) => state switch
    {
        BagState.SecurityScreened => ReadPoint.Security,
        BagState.LoadedOnAircraft => ReadPoint.Load,
        BagState.UnloadedAtArrival => ReadPoint.Unload,
        BagState.InboundChute => ReadPoint.Chute,
        BagState.OnCarousel => ReadPoint.Carousel,
        BagState.ClaimedFromBelt => ReadPoint.Claim,
        _ => null,
    };

    /// <summary>
    /// Atlanan durumlar bir okuma kaçırması mı (sıra dışı)? Yalnızca okuyucusu olan bir noktanın atlanması sayılır;
    /// okuyucusu olmayan noktanın atlanması bu kurulumda beklenen akıştır.
    /// </summary>
    public bool IsAnomaly(IEnumerable<BagState> skipped) =>
        skipped.Any(s => PointOf(s) is { } p && Has(p));

    /// <summary>
    /// Çıkıştan önce okunabilen en ileri varış durumu. ExitGuard bundan önceki bir bavulun çıkışta okunmasını sıra dışı sayar:
    /// tam kapsamda bant ağzı, tipik kapsamda şut, en az kapsamda indirme.
    /// </summary>
    public BagState LastArrivalState =>
        Has(ReadPoint.Carousel) ? BagState.OnCarousel
        : Has(ReadPoint.Chute) ? BagState.InboundChute
        : Has(ReadPoint.Unload) ? BagState.UnloadedAtArrival
        : BagState.LoadedOnAircraft;

    /// <summary>
    /// İndirmeden sonra bavulu okuyan ikinci bir nokta (şut, bant ağzı ya da banttan alma) var mı. Yoksa kaybolan bir indirme
    /// okuması bir daha düzeltilemez ve "varışta okunmadı" uyarısı kalıcı yanlış uyarı olur (W5 ölçümü).
    /// </summary>
    public bool HasReadAfterUnload => Has(ReadPoint.Chute) || Has(ReadPoint.Carousel) || Has(ReadPoint.Claim);

    public override string ToString() => Name;
}
