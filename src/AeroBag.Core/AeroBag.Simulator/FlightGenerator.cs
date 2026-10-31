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
    private static readonly string[] HandlerNames = ["HANDLER_A", "HANDLER_B", "HANDLER_C"];
    private static readonly double[] HandlerPace = [0.85, 1.0, 1.2];

    // Gürültü parametreleri: ml/ kalibrasyon deneyiyle seçildi (docs/EVALUATION.md). Gerçek ölçüm değil, demo varsayımıdır.
    private const double CrewSigma = 0.25;
    private const double DisruptionChance = 0.20;
    private const double DisruptionMeanSeconds = 480;
    private const double CartPauseChance = 0.20;
    private const double CartPauseMeanSeconds = 90;

    // Çıkış taramaları (yalnızca ExitScans açıkken): banttan alındıktan sonra 30 sn + üstel gecikme (ort. 40 sn).
    private const double ExitMissChance = 0.02;
    private const double ExitMinSeconds = 30;
    private const double ExitMeanExtraSeconds = 40;

    internal const string PnrChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    // Aktarma (W10a), hepsi demo varsayımıdır: gelen uçuşun kalktığı yer, bağlantı uçuşlarının gittiği yerler ve 20 dk'lık
    // kalkış ızgarası. Bağlantı süresi (iniş → planlanan kalkış) 40-150 dk arası tekdüze; bir kısmı sıkışıktır.
    // Aktarma hattı süresi indirmeden sonra 5 dk + log-normal (ortalama 10 dk), rejim B'de daha uzun; yükleme 3-8 dk sonra.
    // Kabul kapanışı planlanan kalkıştan 15 dk önce (API'deki Transfer:CutoffMinutes varsayılanıyla aynı).
    private static readonly string[] Origins = ["FRA", "DUS", "LHR", "AMS", "CDG"];
    private static readonly string[] Destinations = ["ADB", "ESB", "IST", "SAW", "TZX", "GZT"];
    internal static readonly TimeSpan OnwardSlot = TimeSpan.FromMinutes(20);
    internal const double MinConnectionMinutes = 40;
    internal const double MaxConnectionMinutes = 150;
    internal static readonly TimeSpan TransferCutoff = TimeSpan.FromMinutes(15);
    private const double TransferMinSeconds = 300;
    private const double TransferMeanExtraSeconds = 600;
    private const double TransferSigma = 0.5;

    /// <summary>Tarife ızgarasında planlanan kalkışı <paramref name="desired"/>'dan sonraki ilk dilim olan bağlantı uçuşu.</summary>
    internal static OnwardFlight OnwardFor(DateTimeOffset desired, SimOptions o, DateTimeOffset announced)
    {
        var slot = Math.Max(0, (int)Math.Ceiling((desired - o.StartUtc).TotalMinutes / OnwardSlot.TotalMinutes));
        var std = o.StartUtc + slot * OnwardSlot;
        var date = DateOnly.FromDateTime(std.UtcDateTime);
        var number = $"ZZ{5000 + slot:D4}";
        // Kalkış gecikmesi uçuşa özgüdür (aynı uçuş her gelen uçuşta aynı görünsün diye dilime bağlı ayrı akış).
        var departed = std.AddMinutes(new SimRandom(unchecked(o.Seed * 7 + slot)).Uniform(0, 8));
        return new OnwardFlight($"{number}-{date:yyyyMMdd}", number, date, Destinations[slot % Destinations.Length], std, departed, announced);
    }

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

        // Yer hizmeti firması gözlenebilir bir özelliktir (AODB'den bilinir) ve hızı gerçekten etkiler.
        var handlerIdx = rng.Int(0, 3);
        var handler = HandlerNames[handlerIdx];
        // Gözlenemeyen ekip hızı: aynı firma içinde bile uçuştan uçuşa değişir (çarpan, log-normal).
        var crew = Math.Exp(rng.Normal(0, CrewSigma));
        var pace = crew * HandlerPace[handlerIdx];
        var aircraft = wide ? rng.Pick(Wide) : rng.Pick(Narrow);

        // Gerçek tarifede inişler eşit aralıklı değildir; sapma yoğunluk özelliğinin (arrivals_last_30min) değişmesini sağlar.
        var onBlock = o.StartUtc.AddMinutes(index * 15 + rng.Uniform(-7, 7));
        var date = DateOnly.FromDateTime(onBlock.UtcDateTime);
        var flightNumber = $"ZZ{100 + index:D4}";
        var flightId = $"{flightNumber}-{date:yyyyMMdd}";
        var stand = $"STAND{100 + index % 30}";

        // Uçaktan indirme: ilk bavul gecikmesi + bavul aralıkları.
        var unload = new DateTimeOffset[bagCount];
        var firstDelay = (240 + rng.LogNormal(120, 0.4)) * load * (wide ? 1.25 : 1.0) * pace;
        if (rng.Chance(DisruptionChance))
        {
            firstDelay += rng.Exp(DisruptionMeanSeconds); // geç gelen ekipman/personel: ağır kuyruk
        }

        var t = onBlock.AddSeconds(firstDelay);
        for (var i = 0; i < bagCount; i++)
        {
            if (i > 0)
            {
                t = t.AddSeconds(1.5 + rng.Exp(1.5 * load * pace));
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
            if (rng.Chance(CartPauseChance))
            {
                transport += rng.Exp(CartPauseMeanSeconds); // araba beklemesi (başka uçuşa kaydırma, trafik)
            }

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

        // W4 senaryoları: ayrı rastgele akış, böylece oranlar sıfırken üretilen veri (CSV, ML eğitimi) hiç değişmez.
        var fates = new string?[bagCount];
        if (o.NotLoadedRate > 0 || o.MissingRate > 0)
        {
            var fateRng = new SimRandom(unchecked(o.Seed * 31337 + index));
            for (var i = 0; i < bagCount; i++)
            {
                var u = fateRng.Uniform(0, 1);
                fates[i] = u < o.NotLoadedRate ? BagFate.NotLoaded
                    : u < o.NotLoadedRate + o.MissingRate ? BagFate.MissingAtArrival
                    : null;
            }
        }
        // W10a aktarma bavulları: yine ayrı akış; yalnızca akıbeti normal olan bavullar arasından seçilir.
        var transferRng = o.TransferRate > 0 ? new SimRandom(unchecked(o.Seed * 50021 + index)) : null;
        if (transferRng is not null)
        {
            for (var i = 0; i < bagCount; i++)
            {
                if (fates[i] is null && transferRng.Chance(o.TransferRate))
                {
                    fates[i] = BagFate.Transfer;
                }
            }
        }
        var delivered = Enumerable.Range(0, bagCount).Where(i => fates[i] is null).ToList();
        if (delivered.Count == 0)
        {
            fates[0] = null; // en az bir bavul banda gelsin (ilk/son bavul ve indirme bitti zamanı tanımlı kalsın)
            delivered.Add(0);
        }

        // İlk/son bavul, indirme bitti: yalnızca gerçekten indirilen bavullar arasında.
        var firstIdx = delivered.MinBy(i => carouselT[i]);
        var lastIdx = delivered.MaxBy(i => carouselT[i]);
        // Aktarma bavulları da indirilir (banda gitmeseler de); indirme bitti zamanına girerler.
        var unloadingCompleted = Enumerable.Range(0, bagCount).Where(i => fates[i] is null or BagFate.Transfer).Max(i => unload[i]);

        var departure = onBlock.AddMinutes(-(210 + rng.Uniform(-10, 10)));
        var bags = new List<BagTruth>(bagCount);
        var messages = new List<ScheduledMessage>(bagCount * 7);
        var cc = $"CAROUSEL{carousel:D2}";

        // Kapı paketleri açıkken bazı bavullar bir öncekiyle aynı rezervasyonu paylaşır (aile). Ayrı rastgele akış:
        // ana akış aynı kalır, yalnızca PNR değişir.
        var bookingRng = o.GatePackets ? new SimRandom(unchecked(o.Seed * 7919 + index)) : null;
        string? previousPnr = null;
        var origin = Origins[index % Origins.Length];
        var onwardFlights = new Dictionary<string, OnwardFlight>();

        for (var i = 0; i < bagCount; i++)
        {
            var tag = $"0891{index * 1000 + i:D6}";
            var checkIn = departure.AddMinutes(-rng.Uniform(90, 150));
            var security = checkIn.AddMinutes(rng.Uniform(5, 15));
            var loaded = departure.AddMinutes(-rng.Uniform(15, 35));

            var pnr = new string(Enumerable.Range(0, 6).Select(_ => PnrChars[rng.Int(0, PnrChars.Length)]).ToArray());
            if (bookingRng is not null && previousPnr is not null && bookingRng.Chance(GateModel.SameBookingChance))
            {
                pnr = previousPnr;
            }
            previousPnr = pnr;
            var surname = rng.Pick(Surnames);
            var given = rng.Pick(Givens);

            var weight = rng.Int(8, 31);
            if (fates[i] == BagFate.Transfer)
            {
                var truth = AddTransfer(i, tag, weight, checkIn, security, loaded, unload[i], pnr, surname, given);
                bags.Add(new BagTruth(flightId, tag, i, checkIn, security, loaded, unload[i], chute[i],
                    carouselT[i], claim[i], false, false, pnr, surname, fates[i], truth));
                continue;
            }

            bags.Add(new BagTruth(flightId, tag, i, checkIn, security, loaded, unload[i], chute[i],
                carouselT[i], claim[i], i == firstIdx, i == lastIdx, pnr, surname, fates[i]));

            messages.Add(new ScheduledMessage(checkIn,
                MessageFormatter.Bsm(flightNumber, date, tag, weight, surname, given, pnr),
                tag, "BSM"));
            messages.Add(new ScheduledMessage(security,
                MessageFormatter.Bpm(flightNumber, date, tag, "SEC01", security), tag, "SEC"));
            if (fates[i] == BagFate.NotLoaded)
            {
                continue; // güvenlikten geçti, uçağa yüklenmedi
            }
            messages.Add(new ScheduledMessage(loaded,
                MessageFormatter.Bpm(flightNumber, date, tag, "LOAD02", loaded), tag, "LOAD"));
            if (fates[i] == BagFate.MissingAtArrival)
            {
                continue; // yüklendi, varışta indirilmedi (uçakta kaldı ya da yanlış yere gitti)
            }
            messages.Add(new ScheduledMessage(unload[i],
                MessageFormatter.Bum(flightNumber, date, tag, stand, unload[i]), tag, "BUM"));
            messages.Add(new ScheduledMessage(chute[i],
                MessageFormatter.Bpm(flightNumber, date, tag, $"CHUTE{carousel:D2}", chute[i], cc), tag, "CHUTE"));
            messages.Add(new ScheduledMessage(carouselT[i],
                MessageFormatter.Bpm(flightNumber, date, tag, cc, carouselT[i]), tag, "CAROUSEL"));
            messages.Add(new ScheduledMessage(claim[i],
                MessageFormatter.Bpm(flightNumber, date, tag, $"CLAIM{carousel:D2}", claim[i]), tag, "CLAIM"));
        }

        // Aktarma bavulu (W10a): gelen uçuşta BSM (.O ile), güvenlik, yükleme ve indirme; aynı anda bağlantı uçuşu için aktarma
        // BSM'si (.I ile); indirmeden sonra aktarma hattı okuması ve kabul kapanışına yetişirse bağlantı uçağına yükleme.
        TransferTruth AddTransfer(int i, string tag, int weight, DateTimeOffset checkIn, DateTimeOffset security, DateTimeOffset loaded,
            DateTimeOffset unloaded, string pnr, string surname, string given)
        {
            var trng = transferRng!; // aktarma akıbeti yalnızca transferRng varken seçilir
            var desired = onBlock.AddMinutes(trng.Uniform(MinConnectionMinutes, MaxConnectionMinutes));
            var onward = OnwardFor(desired, o, checkIn);
            if (!onwardFlights.TryGetValue(onward.FlightId, out var known) || onward.AnnouncedUtc < known.AnnouncedUtc)
            {
                onwardFlights[onward.FlightId] = onward;
            }

            var sort = unloaded.AddSeconds((TransferMinSeconds + trng.LogNormal(TransferMeanExtraSeconds, TransferSigma)) * load);
            var cutoff = onward.ScheduledDepartureUtc - TransferCutoff;
            var onwardLoad = sort.AddSeconds(trng.Uniform(180, 480));
            DateTimeOffset? loadedOnward = onwardLoad <= cutoff ? onwardLoad : null;

            var onwardRef = (onward.FlightNumber, onward.Date, onward.Destination);
            messages.Add(new ScheduledMessage(checkIn,
                MessageFormatter.Bsm(flightNumber, date, tag, weight, surname, given, pnr, onward: onwardRef), tag, "BSM"));
            messages.Add(new ScheduledMessage(checkIn.AddSeconds(1),
                MessageFormatter.Bsm(onward.FlightNumber, onward.Date, tag, weight, surname, given, pnr, onward.Destination,
                    inbound: (flightNumber, date, origin)), tag, "TBSM"));
            messages.Add(new ScheduledMessage(security,
                MessageFormatter.Bpm(flightNumber, date, tag, "SEC01", security), tag, "SEC"));
            messages.Add(new ScheduledMessage(loaded,
                MessageFormatter.Bpm(flightNumber, date, tag, "LOAD02", loaded), tag, "LOAD"));
            messages.Add(new ScheduledMessage(unloaded,
                MessageFormatter.Bum(flightNumber, date, tag, stand, unloaded), tag, "BUM"));
            messages.Add(new ScheduledMessage(sort,
                MessageFormatter.Bpm(onward.FlightNumber, onward.Date, tag, "TRANSFER01", sort, destination: onward.Destination), tag, "TRANSFER"));
            if (loadedOnward is { } lo)
            {
                messages.Add(new ScheduledMessage(lo,
                    MessageFormatter.Bpm(onward.FlightNumber, onward.Date, tag, "LOAD01", lo, destination: onward.Destination), tag, "TLOAD"));
            }
            return new TransferTruth(onward.FlightId, onward.ScheduledDepartureUtc, cutoff, sort, loadedOnward);
        }

        if (o.ExitScans && o.GatePackets)
        {
            // ExitGuard kapı paketleri: sahip, aile, başkası, zayıf sinyal, yanlış okuma, yan koridor (GateModel).
            var gateRng = new SimRandom(unchecked(o.Seed * 100003 + index));
            foreach (var g in GateModel.Plan(bags.Where(b => b.Fate is null).ToList(), gateRng))
            {
                messages.Add(new ScheduledMessage(g.TimeUtc, g.ToJson(), g.Tags[0], "GATE", g));
            }
        }
        else if (o.ExitScans)
        {
            // Ayrı rastgele akış: çıkış taramaları açılıp kapanınca diğer verinin (CSV, ML eğitimi) değişmemesi için.
            var exitRng = new SimRandom(unchecked(o.Seed * 100003 + index));
            foreach (var i in delivered)
            {
                // %2 bavul çıkış kapısında hiç okunmaz (etiket sorunu, başka çıkış, okuyucu kaçırması).
                if (exitRng.Chance(ExitMissChance))
                {
                    continue;
                }
                var exit = claim[i].AddSeconds(ExitMinSeconds + exitRng.Exp(ExitMeanExtraSeconds));
                messages.Add(new ScheduledMessage(exit,
                    MessageFormatter.Bpm(flightNumber, date, bags[i].BagTag, "EXIT01", exit), bags[i].BagTag, "EXIT"));
            }
        }

        var info = new FlightInfo(flightId, flightNumber, date, regime, aircraft, wide, rain ? "RAIN" : "CLEAR",
            standDist, chuteDist, speed, carousel, bagCount, onBlock, 0, handler,
            DepartedUtc: departure, UnloadingCompletedUtc: unloadingCompleted,
            FirstBagOnBeltUtc: carouselT[firstIdx], LastBagOnBeltUtc: carouselT[lastIdx]);

        // Okuma kapsamı (W5): okuyucusu olmayan noktanın mesajı hiç oluşmaz. Gerçek zamanlar (BagTruth) değişmez.
        var coverage = o.Coverage ?? AeroBag.Application.Coverage.ReadCoverage.Full;
        var visible = messages.Where(m => IsCovered(m.Kind, coverage));
        return new FlightResult(info, bags, visible.OrderBy(m => m.TimeUtc).ToList(),
            onwardFlights.Values.OrderBy(x => x.ScheduledDepartureUtc).ToList());
    }

    private static bool IsCovered(string kind, AeroBag.Application.Coverage.ReadCoverage coverage) => kind switch
    {
        "SEC" => coverage.Has(AeroBag.Application.Coverage.ReadPoint.Security),
        "LOAD" or "TLOAD" => coverage.Has(AeroBag.Application.Coverage.ReadPoint.Load),
        "BUM" => coverage.Has(AeroBag.Application.Coverage.ReadPoint.Unload),
        "CHUTE" => coverage.Has(AeroBag.Application.Coverage.ReadPoint.Chute),
        "CAROUSEL" => coverage.Has(AeroBag.Application.Coverage.ReadPoint.Carousel),
        "CLAIM" => coverage.Has(AeroBag.Application.Coverage.ReadPoint.Claim),
        _ => true, // BSM, aktarma BSM'si, aktarma hattı (W10a), çıkış kapısı (ExitGuard) ve BPM çıkış okuması kapsamdan bağımsızdır
    };
}
