using System.Globalization;

namespace AeroBag.Simulator;

/// <summary>Simülatörün ürettiği basitleştirilmiş Type B mesajları (docs/ARCHITECTURE.md bölüm 4.1).</summary>
public static class MessageFormatter
{
    private static string DayMonth(DateTimeOffset t) =>
        t.UtcDateTime.ToString("ddMMM", CultureInfo.InvariantCulture).ToUpperInvariant();

    private static string DayMonth(DateOnly d) =>
        d.ToString("ddMMM", CultureInfo.InvariantCulture).ToUpperInvariant();

    /// <param name="destination">.F satırındaki varış havalimanı: gelen uçuşlarda AYT, bağlantı uçuşunda gidilen yer.</param>
    /// <param name="inbound">Aktarma BSM'sinde gelen uçuş (.I satırı, W10a).</param>
    /// <param name="onward">Gelen uçuşun BSM'sinde bağlantı uçuşu (.O satırı, W10a).</param>
    public static string Bsm(string flightNumber, DateOnly date, string tag, int weightKg,
        string surname, string given, string pnr, string destination = "AYT",
        (string Number, DateOnly Date, string Airport)? inbound = null,
        (string Number, DateOnly Date, string Airport)? onward = null)
    {
        var i = inbound is { } x ? $"\n.I/{x.Number}/{DayMonth(x.Date)}/{x.Airport}" : string.Empty;
        var o = onward is { } y ? $"\n.O/{y.Number}/{DayMonth(y.Date)}/{y.Airport}" : string.Empty;
        return $"BSM\n.V/1LAYT\n.F/{flightNumber}/{DayMonth(date)}/{destination}/Y{i}{o}\n.N/{tag}001\n.W/K/1/{weightKg}\n.P/{surname}/{given}\n.L/{pnr}\nENDBSM";
    }

    public static string Bum(string flightNumber, DateOnly date, string tag, string stand, DateTimeOffset t) =>
        $"BUM\n.V/1LAYT\n.F/{flightNumber}/{DayMonth(date)}/AYT\n.N/{tag}001\n.U/{stand}/{t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}\nENDBUM";

    public static string Bpm(string flightNumber, DateOnly date, string tag, string point, DateTimeOffset t, string? target = null,
        string destination = "AYT")
    {
        var q = target is null ? string.Empty : $"\n.Q/{target}/BELT_ENTRY";
        var time = t.UtcDateTime.ToString("HHmmss", CultureInfo.InvariantCulture);
        return $"BPM\n.V/1LAYT\n.J/S/{point}/{DayMonth(t)}/{time}Z\n.F/{flightNumber}/{DayMonth(date)}/{destination}\n.N/{tag}001{q}\nENDBPM";
    }
}
