namespace AeroBag.Infrastructure;

public sealed record ProcessResult(
    string Status,
    string? Outcome,
    string? FromState,
    string? ToState,
    string? BagTag,
    string? FlightId,
    string? Error)
{
    public const string Accepted = "ACCEPTED";
    public const string Duplicate = "DUPLICATE";
    public const string Rejected = "REJECTED";

    public static ProcessResult Reject(string error) =>
        new(Rejected, null, null, null, null, null, error);

    public static ProcessResult Dup(string bagTag, string flightId) =>
        new(Duplicate, null, null, null, bagTag, flightId, null);
}
