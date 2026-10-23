namespace AeroBag.Simulator;

public static class SimulationRunner
{
    public static IReadOnlyList<FlightResult> Run(SimOptions options)
    {
        var rng = new SimRandom(options.Seed);
        var flights = Enumerable.Range(0, options.Flights)
            .Select(i => FlightGenerator.Generate(i, options, rng))
            .ToList();

        // Son 30 dakikada aynı bant grubuna kaç uçuş indi (yoğunluk özelliği).
        for (var i = 0; i < flights.Count; i++)
        {
            var onBlock = flights[i].Info.OnBlockUtc;
            var arrivals = flights.Count(f =>
                f.Info.OnBlockUtc < onBlock && onBlock - f.Info.OnBlockUtc <= TimeSpan.FromMinutes(30));
            flights[i] = flights[i] with { Info = flights[i].Info with { ArrivalsLast30Min = arrivals } };
        }

        return flights;
    }
}
