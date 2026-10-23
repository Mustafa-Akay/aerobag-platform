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
    DateTimeOffset? OccurredAtUtc = null);

public sealed record ParseResult(TypeBMessage? Message, ParseError? Error, string? Detail)
{
    public bool IsSuccess => Message is not null;

    public static ParseResult Ok(TypeBMessage message) => new(message, null, null);

    public static ParseResult Fail(ParseError error, string detail) => new(null, error, detail);
}
