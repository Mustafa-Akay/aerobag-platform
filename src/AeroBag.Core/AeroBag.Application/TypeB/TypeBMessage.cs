namespace AeroBag.Application.TypeB;

public enum TypeBKind { Bsm, Bum, Bpm }

public enum ParseError
{
    Empty,
    TooLong,
    TooManyLines,
    LineTooLong,
    UnknownType,
    MissingEnd,
    EndMismatch,
    MalformedLine,
    DuplicateField,
    UnknownField,
    MissingField,
    InvalidField,
}

/// <summary>Mesajda adı geçen başka bir uçuş (W10a: aktarmada gelen ya da bağlantı uçuşu).</summary>
public sealed record FlightRef(string FlightNumber, DateOnly FlightDate, string Airport)
{
    /// <summary>Veritabanındaki flight_id biçimi (örn. ZZ0100-20261031).</summary>
    public string FlightId => $"{FlightNumber}-{FlightDate:yyyyMMdd}";
}

/// <summary>Basitleştirilmiş, Type B esinli mesajın ayrıştırılmış hali.</summary>
public sealed record TypeBMessage(
    TypeBKind Kind,
    string Airport,
    string FlightNumber,
    DateOnly FlightDate,
    string BagTag,
    string Sequence,
    string? DestinationAirport = null,
    string? CabinClass = null,
    int? WeightKg = null,
    string? Surname = null,
    string? GivenName = null,
    string? Pnr = null,
    string? Stand = null,
    string? ScanPoint = null,
    string? TargetCarousel = null,
    string? TargetPointType = null,
    DateTimeOffset? OccurredAtUtc = null,
    FlightRef? Inbound = null,
    FlightRef? Onward = null)
{
    /// <summary>Mesajın kendi uçuşu, veritabanındaki flight_id biçiminde.</summary>
    public string FlightId => $"{FlightNumber}-{FlightDate:yyyyMMdd}";
}

public sealed record ParseResult(TypeBMessage? Message, ParseError? Error, string? Detail)
{
    public bool IsSuccess => Message is not null;

    public static ParseResult Ok(TypeBMessage message) => new(message, null, null);

    public static ParseResult Fail(ParseError error, string detail) => new(null, error, detail);
}
