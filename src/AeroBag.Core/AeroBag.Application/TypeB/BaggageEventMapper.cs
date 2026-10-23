using System.Security.Cryptography;
using System.Text;
using AeroBag.Domain;

namespace AeroBag.Application.TypeB;

/// <summary>Durum makinesine verilecek, kaynak mesajdan bağımsız normalleştirilmiş olay.</summary>
public sealed record BaggageEvent(
    string BagTag,
    string Sequence,
    string FlightNumber,
    DateOnly FlightDate,
    string Airport,
    BagState TargetState,
    DateTimeOffset OccurredAtUtc,
    string IdempotencyKey,
    string? Pnr,
    string? ScanPoint,
    string MessageType);

public static class BaggageEventMapper
{
    /// <summary>
    /// BPM okuma noktası önekinden durum çıkarılır:
    /// SEC=güvenlik, LOAD=uçağa yükleme, CHUTE=şut girişi, CAROUSEL=bant ağzı, CLAIM=banttan alma.
    /// </summary>
    public static bool TryMap(TypeBMessage m, DateTimeOffset receivedAtUtc, out BaggageEvent? result, out string? error)
    {
        result = null;
        error = null;

        BagState state;
        DateTimeOffset occurred;
        switch (m.Kind)
        {
            case TypeBKind.Bsm:
                state = BagState.CheckedIn;
                occurred = receivedAtUtc;
                break;
            case TypeBKind.Bum:
                state = BagState.UnloadedAtArrival;
                occurred = m.OccurredAtUtc ?? receivedAtUtc;
                break;
            case TypeBKind.Bpm:
                var point = m.ScanPoint ?? string.Empty;
                if (point.StartsWith("SEC", StringComparison.Ordinal)) state = BagState.SecurityScreened;
                else if (point.StartsWith("LOAD", StringComparison.Ordinal)) state = BagState.LoadedOnAircraft;
                else if (point.StartsWith("CHUTE", StringComparison.Ordinal)) state = BagState.InboundChute;
                else if (point.StartsWith("CAROUSEL", StringComparison.Ordinal)) state = BagState.OnCarousel;
                else if (point.StartsWith("CLAIM", StringComparison.Ordinal)) state = BagState.ClaimedFromBelt;
                else
                {
                    error = $"Bilinmeyen okuma noktası: {point}";
                    return false;
                }
                occurred = m.OccurredAtUtc ?? receivedAtUtc;
                break;
            default:
                error = "Desteklenmeyen mesaj tipi.";
                return false;
        }

        // BSM'de olay zamanı mesajdan gelmez (alınma zamanı kullanılır); kopya mesajın anahtarı
        // değişmesin diye BSM anahtarına zaman girmez.
        var timePart = m.Kind == TypeBKind.Bsm ? string.Empty : occurred.UtcDateTime.ToString("O");
        var raw = $"{m.Kind}|{m.BagTag}{m.Sequence}|{m.FlightNumber}|{m.FlightDate:yyyyMMdd}|{state}|{timePart}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

        result = new BaggageEvent(m.BagTag, m.Sequence, m.FlightNumber, m.FlightDate, m.Airport,
            state, occurred, key, m.Pnr, m.ScanPoint, m.Kind.ToString().ToUpperInvariant());
        return true;
    }
}
