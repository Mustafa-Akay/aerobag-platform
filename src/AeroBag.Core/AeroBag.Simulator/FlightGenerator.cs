namespace AeroBag.Simulator;

/// <summary>
/// Bir uçuşun bavul yaşam döngüsünü üretir. Model (hepsi demo varsayımıdır, ölçüm değildir):
/// ilk bavul gecikmesi log-normal; indirme aralıkları üstel; bavullar 8-16'lık arabalarla şuta taşınır;
/// şut→bant süresi mesafe/bant hızıdır; yolcu bekleme süresi üstel. Rejim B (yağmur, yavaş yer hizmeti)
/// tüm gecikmeleri büyütür; bu, dağılım kayması testi için kullanılacak.
/// </summary>
public static class FlightGenerator
{
    private static readonly string[] Narrow = ["B738", "A320", "A321"];
    private static readonly string[] Wide = ["A333", "B788"];
    private static readonly string[] Surnames = ["YILMAZ", "KAYA", "DEMIR", "CELIK", "SAHIN", "OZTURK", "AYDIN", "ARSLAN", "DOGAN", "KILIC"];
    private static readonly string[] Givens = ["AYSE", "MEHMET", "FATMA", "ALI", "ZEYNEP", "MUSTAFA", "ELIF", "HASAN", "EMINE", "AHMET"];
    private const string PnrChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static FlightResult Generate(int index, SimOptions o, SimRandom rng)
    {
        var wide = rng.Chance(0.2);
        var regime = o.Regime switch
        {
            'A' => "A",
            'B' => "B",
            _ => rng.Chance(0.7) ? "A" : "B",
        };
        var rain = regime == "B" ? rng.Chance(0.8) : rng.Chance(0.1);
        var load = (regime == "B" ? 1.35 : 1.0) * (rain ? 1.15 : 1.0);

        var bagCount = Math.Max(20, (int)(o.BaseBags * (wide ? 1.8 : 1.0) * rng.Uniform(0.7, 1.3)));
        var standDist = rng.Int(300, 2200);
        var chuteDist = rng.Int(30, 70);
        var speed = Math.Round(rng.Uniform(0.50, 0.60), 2);
        var carousel = rng.Int(1, 7);
        var aircraft = wide ? rng.Pick(Wide) : rng.Pick(Narrow);

        var onBlock = o.StartUtc.AddMinutes(index * 15);
        var date = DateOnly.FromDateTime(onBlock.UtcDateTime);
        var flightNumber = $"ZZ{100 + index:D4}";
        var flightId = $"{flightNumber}-{date:yyyyMMdd}";
        var stand = $"STAND{100 + index % 30}";

        // Uçaktan indirme: ilk bavul gecikmesi + bavul aralıkları.
        var unload = new DateTimeOffset[bagCount];
        var t = onBlock.AddSeconds((240 + rng.LogNormal(120, 0.4)) * load * (wide ? 1.25 : 1.0));
        for (var i = 0; i < bagCount; i++)
        {
            if (i > 0)
            {
                t = t.AddSeconds(1.5 + rng.Exp(1.5 * load));
            }
            unload[i] = t;
        }

        // Arabalar dolunca şuta gider; araba içindeki bavullar birkaç saniye arayla girer.
        var chute = new DateTimeOffset[bagCount];
        var start = 0;
        while (start < bagCount)
        {
            var end = Math.Min(bagCount, start + rng.Int(8, 17));
            var depart = unload[end - 1];
            var transport = standDist / rng.Uniform(5, 7) * load + rng.Exp(30);
            var arrive = depart.AddSeconds(transport);
            for (var k = start; k < end; k++)
            {
                chute[k] = arrive.AddSeconds((k - start) * rng.Uniform(2, 4));
            }
            start = end;
        }

        var carouselT = new DateTimeOffset[bagCount];
        var claim = new DateTimeOffset[bagCount];
        for (var i = 0; i < bagCount; i++)
        {
            var travel = chuteDist / speed * (1 + rng.Normal(0, 0.03));
            carouselT[i] = chute[i].AddSeconds(travel);
            claim[i] = carouselT[i].AddSeconds(20 + rng.Exp(120));
        }

        var firstIdx = Array.IndexOf(carouselT, carouselT.Min());
        var lastIdx = Array.IndexOf(carouselT, carouselT.Max());

        var departure = onBlock.AddMinutes(-(210 + rng.Uniform(-10, 10)));
        var bags = new List<BagTruth>(bagCount);
        var messages = new List<ScheduledMessage>(bagCount * 7);
        var cc = $"CAROUSEL{carousel:D2}";

        for (var i = 0; i < bagCount; i++)
        {
            var tag = $"0891{index * 1000 + i:D6}";
            var checkIn = departure.AddMinutes(-rng.Uniform(90, 150));
            var security = checkIn.AddMinutes(rng.Uniform(5, 15));
            var loaded = departure.AddMinutes(-rng.Uniform(15, 35));

            var pnr = new string(Enumerable.Range(0, 6).Select(_ => PnrChars[rng.Int(0, PnrChars.Length)]).ToArray());
            var surname = rng.Pick(Surnames);
            var given = rng.Pick(Givens);

            bags.Add(new BagTruth(flightId, tag, i, checkIn, security, loaded, unload[i], chute[i],
                carouselT[i], claim[i], i == firstIdx, i == lastIdx, pnr, surname));

            messages.Add(new ScheduledMessage(checkIn,
                MessageFormatter.Bsm(flightNumber, date, tag, rng.Int(8, 31), surname, given, pnr),
                tag, "BSM"));
            messages.Add(new ScheduledMessage(security,
                MessageFormatter.Bpm(flightNumber, date, tag, "SEC01", security), tag, "SEC"));
            messages.Add(new ScheduledMessage(loaded,
                MessageFormatter.Bpm(flightNumber, date, tag, "LOAD02", loaded), tag, "LOAD"));
            messages.Add(new ScheduledMessage(unload[i],
                MessageFormatter.Bum(flightNumber, date, tag, stand, unload[i]), tag, "BUM"));
            messages.Add(new ScheduledMessage(chute[i],
                MessageFormatter.Bpm(flightNumber, date, tag, $"CHUTE{carousel:D2}", chute[i], cc), tag, "CHUTE"));
            messages.Add(new ScheduledMessage(carouselT[i],
                MessageFormatter.Bpm(flightNumber, date, tag, cc, carouselT[i]), tag, "CAROUSEL"));
            messages.Add(new ScheduledMessage(claim[i],
                MessageFormatter.Bpm(flightNumber, date, tag, $"CLAIM{carousel:D2}", claim[i]), tag, "CLAIM"));
        }

        var info = new FlightInfo(flightId, flightNumber, date, regime, aircraft, wide, rain ? "RAIN" : "CLEAR",
            standDist, chuteDist, speed, carousel, bagCount, onBlock, 0);

        return new FlightResult(info, bags, messages.OrderBy(m => m.TimeUtc).ToList());
    }
}
