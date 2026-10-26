using System.Globalization;

namespace AeroBag.Simulator;

/// <summary>ETA modelinin eğitileceği veri seti: bavul başına bir satır, uçuş özellikleri ve gerçek zamanlar.</summary>
public static class CsvExporter
{
    private static string Iso(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static void Write(string path, IReadOnlyList<FlightResult> flights)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var w = new StreamWriter(path);
        w.WriteLine(string.Join(',',
            "flight_id", "bag_tag", "regime", "aircraft_type", "wide_body", "weather", "total_bags",
            "stand_distance_m", "chute_to_carousel_m", "conveyor_speed_mps", "arrivals_last_30min", "ground_handler",
            "on_block_utc", "unload_utc", "chute_utc", "carousel_utc", "claim_utc",
            "unload_index", "sec_onblock_to_unload", "sec_onblock_to_carousel", "is_first_bag", "is_last_bag"));

        foreach (var f in flights)
        {
            var i = f.Info;
            foreach (var b in f.Bags)
            {
                w.WriteLine(string.Join(',',
                    i.FlightId, b.BagTag, i.Regime, i.AircraftType, i.WideBody ? 1 : 0, i.Weather, i.TotalBags,
                    i.StandDistanceM, i.ChuteToCarouselM, i.ConveyorSpeedMps.ToString(CultureInfo.InvariantCulture),
                    i.ArrivalsLast30Min, i.GroundHandler,
                    Iso(i.OnBlockUtc), Iso(b.UnloadUtc), Iso(b.ChuteUtc), Iso(b.CarouselUtc), Iso(b.ClaimUtc),
                    b.UnloadIndex,
                    ((int)(b.UnloadUtc - i.OnBlockUtc).TotalSeconds).ToString(CultureInfo.InvariantCulture),
                    ((int)(b.CarouselUtc - i.OnBlockUtc).TotalSeconds).ToString(CultureInfo.InvariantCulture),
                    b.IsFirst ? 1 : 0, b.IsLast ? 1 : 0));
            }
        }
    }
}
