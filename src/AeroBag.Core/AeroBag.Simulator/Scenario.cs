namespace AeroBag.Simulator;

/// <param name="Regime">'A' normal operasyon, 'B' yağmurlu ve yavaş operasyon, 'M' karışık.</param>
/// <param name="ExitScans">Banttan alınan bavullar için çıkış kapısı taramaları da üretilsin mi (canlı gösterim için).</param>
public sealed record SimOptions(int Flights, int BaseBags, int Seed, char Regime, DateTimeOffset StartUtc, bool ExitScans = false);

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
    string GroundHandler);

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
    string Surname);

public sealed record ScheduledMessage(DateTimeOffset TimeUtc, string Text, string BagTag, string Kind);

public sealed record FlightResult(FlightInfo Info, IReadOnlyList<BagTruth> Bags, IReadOnlyList<ScheduledMessage> Messages);
