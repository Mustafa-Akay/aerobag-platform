using System.Globalization;

namespace AeroBag.Simulator;

/// <summary>Simülatörün ürettiği basitleştirilmiş Type B mesajları (docs/ARCHITECTURE.md bölüm 4.1).</summary>
public static class MessageFormatter
{
    private static string DayMonth(DateTimeOffset t) =>
        t.UtcDateTime.ToString("ddMMM", CultureInfo.InvariantCulture).ToUpperInvariant();

    private static string DayMonth(DateOnly d) =>
        d.ToString("ddMMM", CultureInfo.InvariantCulture).ToUpperInvariant();

    public static string Bsm(string flightNumber, DateOnly date, string tag, int weightKg,
        string surname, string given, string pnr) =>
        $"BSM\n.V/1LAYT\n.F/{flightNumber}/{DayMonth(date)}/AYT/Y\n.N/{tag}001\n.W/K/1/{weightKg}\n.P/{surname}/{given}\n.L/{pnr}\nENDBSM";

    public static string Bum(string flightNumber, DateOnly date, string tag, string stand, DateTimeOffset t) =>
        $"BUM\n.V/1LAYT\n.F/{flightNumber}/{DayMonth(date)}/AYT\n.N/{tag}001\n.U/{stand}/{t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}\nENDBUM";

    public static string Bpm(string flightNumber, DateOnly date, string tag, string point, DateTimeOffset t, string? target = null)
    {
        var q = target is null ? string.Empty : $"\n.Q/{target}/BELT_ENTRY";
        var time = t.UtcDateTime.ToString("HHmmss", CultureInfo.InvariantCulture);
        return $"BPM\n.V/1LAYT\n.J/S/{point}/{DayMonth(t)}/{time}Z\n.F/{flightNumber}/{DayMonth(date)}/AYT\n.N/{tag}001{q}\nENDBPM";
    }
}
