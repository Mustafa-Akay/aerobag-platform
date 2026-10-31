using System.Globalization;
using System.Text.RegularExpressions;

namespace AeroBag.Application.TypeB;

/// <summary>
/// ARCHITECTURE.md bölüm 4.1'deki basitleştirilmiş BSM/BUM/BPM biçimini ayrıştırır.
/// Katıdır: bilinmeyen alan, çift alan, bozuk tarih ya da aşırı uzun girdi reddedilir ve
/// çağıran taraf mesajı ölü mektup kuyruğuna yazar. Gerçek IATA Type B uyumluluğu iddia edilmez.
/// </summary>
public static class TypeBParser
{
    public const int MaxTextLength = 2000;
    public const int MaxLines = 20;
    public const int MaxLineLength = 120;

    private static readonly Regex Version = new(@"^1L([A-Z]{3})$", RegexOptions.Compiled);
    private static readonly Regex Flight = new(@"^([A-Z0-9]{2}\d{3,4}[A-Z]?)/(\d{2}[A-Z]{3})/([A-Z]{3})(?:/([A-Z]))?$", RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"^(\d{10})(\d{3})$", RegexOptions.Compiled);
    private static readonly Regex Weight = new(@"^K/(\d{1,2})/(\d{1,2})$", RegexOptions.Compiled);
    private static readonly Regex Passenger = new(@"^([A-Z][A-Z' -]{0,63})/([A-Z][A-Z' -]{0,63})$", RegexOptions.Compiled);
    private static readonly Regex PnrRx = new(@"^[A-Z0-9]{6}$", RegexOptions.Compiled);
    private static readonly Regex Unload = new(@"^([A-Z0-9]{2,12})/(\d{8}T\d{6}Z)$", RegexOptions.Compiled);
    private static readonly Regex Scan = new(@"^S/([A-Z]{3,10}\d{2})/(\d{2}[A-Z]{3})/(\d{6})Z$", RegexOptions.Compiled);
    private static readonly Regex Target = new(@"^([A-Z]{3,10}\d{2})/([A-Z_]{3,20})$", RegexOptions.Compiled);
    // W10a: aktarma. .I gelen uçuş, .O bağlantı uçuşu (uçuş/gün-ay/havalimanı).
    private static readonly Regex OtherFlight = new(@"^([A-Z0-9]{2}\d{3,4}[A-Z]?)/(\d{2}[A-Z]{3})/([A-Z]{3})$", RegexOptions.Compiled);

    private static readonly string[] Months =
        ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private static readonly Dictionary<TypeBKind, string[]> Required = new()
    {
        [TypeBKind.Bsm] = ["V", "F", "N", "W", "P", "L"],
        [TypeBKind.Bum] = ["V", "F", "N", "U"],
        [TypeBKind.Bpm] = ["V", "J", "F", "N"],
    };

    private static readonly HashSet<string> Known = ["V", "F", "N", "W", "P", "L", "U", "J", "Q", "I", "O"];

    /// <param name="text">Ham mesaj metni.</param>
    /// <param name="referenceDate">
    /// Mesajlarda yıl yoktur (22OCT); yıl, bu tarihe en yakın olacak şekilde seçilir.
    /// </param>
    public static ParseResult Parse(string? text, DateOnly referenceDate)
    {
        try
        {
            return ParseCore(text, referenceDate);
        }
        catch (TypeBException ex)
        {
            return ParseResult.Fail(ex.Error, ex.Message);
        }
    }

    private static ParseResult ParseCore(string? text, DateOnly reference)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new TypeBException(ParseError.Empty, "Mesaj boş.");
        }
        if (text.Length > MaxTextLength)
        {
            throw new TypeBException(ParseError.TooLong, $"Mesaj {MaxTextLength} karakterden uzun.");
        }

        var lines = text.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
        if (lines.Count > MaxLines)
        {
            throw new TypeBException(ParseError.TooManyLines, $"Satır sayısı {MaxLines} üstünde.");
        }
        if (lines.Any(l => l.Length > MaxLineLength))
        {
            throw new TypeBException(ParseError.LineTooLong, $"Bir satır {MaxLineLength} karakterden uzun.");
        }

        var kind = lines[0] switch
        {
            "BSM" => TypeBKind.Bsm,
            "BUM" => TypeBKind.Bum,
            "BPM" => TypeBKind.Bpm,
            _ => throw new TypeBException(ParseError.UnknownType, "Mesaj tipi BSM, BUM ya da BPM değil."),
        };

        var endToken = "END" + lines[0];
        var last = lines[^1];
        if (lines.Count < 2 || !last.StartsWith("END", StringComparison.Ordinal))
        {
            throw new TypeBException(ParseError.MissingEnd, "Bitiş satırı yok.");
        }
        if (last != endToken)
        {
            throw new TypeBException(ParseError.EndMismatch, "Bitiş satırı mesaj tipiyle uyuşmuyor.");
        }

        var fields = new Dictionary<string, string>();
        foreach (var line in lines.Skip(1).Take(lines.Count - 2))
        {
            if (line.Length < 4 || line[0] != '.' || !char.IsAsciiLetterUpper(line[1]) || line[2] != '/')
            {
                throw new TypeBException(ParseError.MalformedLine, $"Bozuk satır: '{Truncate(line)}'.");
            }
            var key = line[1].ToString();
            if (!Known.Contains(key))
            {
                throw new TypeBException(ParseError.UnknownField, $"Bilinmeyen alan: .{key}");
            }
            if (!fields.TryAdd(key, line[3..]))
            {
                throw new TypeBException(ParseError.DuplicateField, $"Alan tekrar ediyor: .{key}");
            }
        }

        foreach (var req in Required[kind])
        {
            if (!fields.ContainsKey(req))
            {
                throw new TypeBException(ParseError.MissingField, $"Zorunlu alan eksik: .{req}");
            }
        }

        var airport = Require(Version, fields["V"], "V").Groups[1].Value;

        var f = Require(Flight, fields["F"], "F");
        var flightDate = ParseDayMonth(f.Groups[2].Value, reference, "F");

        var n = Require(Tag, fields["N"], "N");

        int? weight = null;
        if (fields.TryGetValue("W", out var w))
        {
            var wm = Require(Weight, w, "W");
            var pieces = int.Parse(wm.Groups[1].Value, CultureInfo.InvariantCulture);
            weight = int.Parse(wm.Groups[2].Value, CultureInfo.InvariantCulture);
            if (pieces < 1 || weight < 1)
            {
                throw new TypeBException(ParseError.InvalidField, "Alan .W: parça ve ağırlık en az 1 olmalı.");
            }
        }

        string? surname = null, given = null;
        if (fields.TryGetValue("P", out var p))
        {
            var pm = Require(Passenger, p, "P");
            surname = pm.Groups[1].Value;
            given = pm.Groups[2].Value;
        }

        string? pnr = null;
        if (fields.TryGetValue("L", out var l))
        {
            Require(PnrRx, l, "L");
            pnr = l;
        }

        string? stand = null;
        DateTimeOffset? occurred = null;
        if (fields.TryGetValue("U", out var u))
        {
            var um = Require(Unload, u, "U");
            stand = um.Groups[1].Value;
            if (!DateTimeOffset.TryParseExact(um.Groups[2].Value, "yyyyMMdd'T'HHmmss'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                throw new TypeBException(ParseError.InvalidField, "Alan .U: geçersiz zaman damgası.");
            }
            occurred = parsed;
        }

        string? scanPoint = null;
        if (fields.TryGetValue("J", out var j))
        {
            var jm = Require(Scan, j, "J");
            scanPoint = jm.Groups[1].Value;
            var day = ParseDayMonth(jm.Groups[2].Value, reference, "J");
            var t = jm.Groups[3].Value;
            int hh = int.Parse(t[..2], CultureInfo.InvariantCulture);
            int mm = int.Parse(t[2..4], CultureInfo.InvariantCulture);
            int ss = int.Parse(t[4..], CultureInfo.InvariantCulture);
            if (hh > 23 || mm > 59 || ss > 59)
            {
                throw new TypeBException(ParseError.InvalidField, "Alan .J: geçersiz saat.");
            }
            occurred = new DateTimeOffset(day.Year, day.Month, day.Day, hh, mm, ss, TimeSpan.Zero);
        }

        string? carousel = null, pointType = null;
        if (fields.TryGetValue("Q", out var q))
        {
            var qm = Require(Target, q, "Q");
            carousel = qm.Groups[1].Value;
            pointType = qm.Groups[2].Value;
        }

        var ownFlightId = $"{f.Groups[1].Value}-{flightDate:yyyyMMdd}";
        var inbound = OptionalFlight(fields, "I", reference, ownFlightId);
        var onward = OptionalFlight(fields, "O", reference, ownFlightId);
        if (inbound is not null && onward is not null && inbound.FlightId == onward.FlightId)
        {
            throw new TypeBException(ParseError.InvalidField, "Alan .I ve .O aynı uçuşu gösteremez.");
        }

        var message = new TypeBMessage(
            kind, airport, f.Groups[1].Value, flightDate, n.Groups[1].Value, n.Groups[2].Value,
            DestinationAirport: f.Groups[3].Value,
            CabinClass: f.Groups[4].Success ? f.Groups[4].Value : null,
            WeightKg: weight, Surname: surname, GivenName: given, Pnr: pnr, Stand: stand,
            ScanPoint: scanPoint, TargetCarousel: carousel, TargetPointType: pointType,
            OccurredAtUtc: occurred, Inbound: inbound, Onward: onward);
        return ParseResult.Ok(message);
    }

    /// <summary>.I/.O alanı: mesajın kendi uçuşundan farklı bir uçuş olmalı.</summary>
    private static FlightRef? OptionalFlight(Dictionary<string, string> fields, string key, DateOnly reference, string ownFlightId)
    {
        if (!fields.TryGetValue(key, out var value))
        {
            return null;
        }
        var m = Require(OtherFlight, value, key);
        var flight = new FlightRef(m.Groups[1].Value, ParseDayMonth(m.Groups[2].Value, reference, key), m.Groups[3].Value);
        if (flight.FlightId == ownFlightId)
        {
            throw new TypeBException(ParseError.InvalidField, $"Alan .{key} mesajın kendi uçuşunu gösteremez.");
        }
        return flight;
    }

    private static Match Require(Regex rx, string value, string field)
    {
        var m = rx.Match(value);
        if (!m.Success)
        {
            throw new TypeBException(ParseError.InvalidField, $"Alan .{field} biçimi geçersiz: '{Truncate(value)}'.");
        }
        return m;
    }

    private static DateOnly ParseDayMonth(string ddMmm, DateOnly reference, string field)
    {
        var day = int.Parse(ddMmm[..2], CultureInfo.InvariantCulture);
        var month = Array.IndexOf(Months, ddMmm[2..]) + 1;
        if (month == 0)
        {
            throw new TypeBException(ParseError.InvalidField, $"Alan .{field}: geçersiz ay '{ddMmm[2..]}'.");
        }

        DateOnly? best = null;
        var bestDistance = int.MaxValue;
        for (var year = reference.Year - 1; year <= reference.Year + 1; year++)
        {
            if (day < 1 || day > DateTime.DaysInMonth(year, month))
            {
                continue;
            }
            var candidate = new DateOnly(year, month, day);
            var distance = Math.Abs(candidate.DayNumber - reference.DayNumber);
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }
        return best ?? throw new TypeBException(ParseError.InvalidField, $"Alan .{field}: geçersiz tarih '{ddMmm}'.");
    }

    private static string Truncate(string s) => s.Length <= 40 ? s : s[..40] + "...";

    private sealed class TypeBException(ParseError error, string message) : Exception(message)
    {
        public ParseError Error { get; } = error;
    }
}
