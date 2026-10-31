namespace AeroBag.Simulator;

/// <param name="Regime">'A' normal operasyon, 'B' yağmurlu ve yavaş operasyon, 'M' karışık.</param>
/// <param name="ExitScans">Banttan alınan bavullar için çıkış kapısı taramaları da üretilsin mi (canlı gösterim için).</param>
/// <param name="GatePackets">Çıkış taramaları BPM EXIT yerine ExitGuard kapı paketi (PNR'lı) olarak mı üretilsin.
/// Açıkken bazı bavullar aynı rezervasyonu paylaşır (aile); bu yalnızca PNR'ı değiştirir, CSV/ML verisini değiştirmez.</param>
/// <param name="NotLoadedRate">Uçağa yüklenmeyen bavul oranı (W4 senaryosu, demo varsayımı). Bavulun BSM ve güvenlik okuması olur, sonrası olmaz.</param>
/// <param name="MissingRate">Yüklenip varışta indirilmeyen bavul oranı (W4 senaryosu, demo varsayımı). Yükleme okumasından sonrası olmaz.</param>
/// <param name="Coverage">Okuma kapsamı profili (W5): okuyucusu olmayan noktaların mesajları üretilmez. Null: tam kapsam.</param>
/// <param name="TransferRate">Aktarma bavulu oranı (W10a, demo varsayımı): bavul bu havalimanında banda değil bağlantı uçuşuna gider.</param>
public sealed record SimOptions(int Flights, int BaseBags, int Seed, char Regime, DateTimeOffset StartUtc, bool ExitScans = false, bool GatePackets = false,
    double NotLoadedRate = 0, double MissingRate = 0, AeroBag.Application.Coverage.ReadCoverage? Coverage = null, double TransferRate = 0);

/// <summary>Bavulun simülasyondaki akıbeti (W4). Null: normal akış.</summary>
public static class BagFate
{
    public const string NotLoaded = "NOT_LOADED";
    public const string MissingAtArrival = "MISSING_AT_ARRIVAL";

    /// <summary>Aktarma bavulu (W10a): indirildikten sonra aktarma hattına ve bağlantı uçuşuna gider; banda gelmez.</summary>
    public const string Transfer = "TRANSFER";
}

/// <summary>
/// Buradan kalkan bir bağlantı uçuşu (W10a). Bağlantı uçuşları 20 dakikalık bir tarife ızgarasındadır; aynı uçuşa birçok
/// gelen uçuştan bavul gelir. <paramref name="AnnouncedUtc"/>: AODB'nin planlanan kalkışı bildirdiği an (ilk aktarma BSM'si).
/// </summary>
public sealed record OnwardFlight(string FlightId, string FlightNumber, DateOnly Date, string Destination,
    DateTimeOffset ScheduledDepartureUtc, DateTimeOffset DepartedUtc, DateTimeOffset AnnouncedUtc);

public sealed record FlightInfo(
    string FlightId,
    string FlightNumber,
    DateOnly Date,
    string Regime,
    string AircraftType,
    bool WideBody,
    string Weather,
    int StandDistanceM,
    int ChuteToCarouselM,
    double ConveyorSpeedMps,
    int Carousel,
    int TotalBags,
    DateTimeOffset OnBlockUtc,
    int ArrivalsLast30Min,
    string GroundHandler,
    DateTimeOffset? DepartedUtc = null,
    DateTimeOffset? UnloadingCompletedUtc = null,
    DateTimeOffset? FirstBagOnBeltUtc = null,
    DateTimeOffset? LastBagOnBeltUtc = null);

/// <summary>Simülatörün bildiği gerçek zamanlar (mesajlar kayıp olsa bile bunlar eksiksizdir).</summary>
public sealed record BagTruth(
    string FlightId,
    string BagTag,
    int UnloadIndex,
    DateTimeOffset CheckInUtc,
    DateTimeOffset SecurityUtc,
    DateTimeOffset LoadUtc,
    DateTimeOffset UnloadUtc,
    DateTimeOffset ChuteUtc,
    DateTimeOffset CarouselUtc,
    DateTimeOffset ClaimUtc,
    bool IsFirst,
    bool IsLast,
    string Pnr,
    string Surname,
    string? Fate = null,
    TransferTruth? Transfer = null);

/// <summary>
/// Aktarma bavulunun gerçek zamanları (W10a). <paramref name="OnwardLoadUtc"/> null: bavul kabul kapanışına yetişmedi
/// (bağlantı uçağına yüklenmedi).
/// </summary>
public sealed record TransferTruth(string OnwardFlightId, DateTimeOffset ScheduledDepartureUtc, DateTimeOffset CutoffUtc,
    DateTimeOffset SortUtc, DateTimeOffset? OnwardLoadUtc)
{
    public bool MissedConnection => OnwardLoadUtc is null;
}

/// <param name="Gate">Kind GATE ise kapı paketi (gerçek etiketleriyle); Text paketin JSON'udur.</param>
public sealed record ScheduledMessage(DateTimeOffset TimeUtc, string Text, string BagTag, string Kind, GatePacket? Gate = null);

/// <param name="Onward">Bu uçuşun aktarma bavullarının gittiği bağlantı uçuşları (W10a); aynı uçuş başka sonuçlarda da görünebilir.</param>
public sealed record FlightResult(FlightInfo Info, IReadOnlyList<BagTruth> Bags, IReadOnlyList<ScheduledMessage> Messages,
    IReadOnlyList<OnwardFlight>? Onward = null)
{
    public IReadOnlyList<OnwardFlight> OnwardFlights => Onward ?? [];
}
