using System.Text.Json;

namespace AeroBag.Simulator;

/// <summary>Bir kapı paketindeki etiketin gerçekte ne olduğu (simülatör bilir, API bilmez).</summary>
public static class GateTruth
{
    /// <summary>Bavulu sahibi çıkarıyor.</summary>
    public const string Owner = "OWNER";
    /// <summary>Aynı rezervasyondaki başka bir yolcu çıkarıyor (meşru).</summary>
    public const string Family = "FAMILY";
    /// <summary>Rezervasyonla ilgisi olmayan biri çıkarıyor (yakalanması gereken durum).</summary>
    public const string WrongPerson = "WRONG_PERSON";
    /// <summary>Yan koridordaki, çıkmayan bir bavul yanlışlıkla okundu (meşru; alarm yanlış alarmdır).</summary>
    public const string Neighbor = "NEIGHBOR";

    public static bool IsLegit(string truth) => truth != WrongPerson;
}

/// <summary>Kapı sensör paketi ve her etiketin gerçeği.</summary>
public sealed record GatePacket(
    DateTimeOffset TimeUtc, string GateId, IReadOnlyList<string> Tags, string? PresentedPnr, double? SignalQuality,
    IReadOnlyDictionary<string, string> Truth)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(new
    {
        gateId = GateId,
        timestampUtc = TimeUtc,
        detectedBagTags = Tags,
        passengerVerification = PresentedPnr is null
            ? null
            : new { method = "SIMULATED_BOARDING_PASS", presentedPnr = PresentedPnr, signalQuality = SignalQuality },
    }, Json);
}

/// <summary>
/// Çıkış kapısı sensörü modeli. Bütün oranlar DEMO VARSAYIMIDIR; sahada ölçülmedi (ARCHITECTURE.md 9.3, Faz 5).
/// Amaç, ExitGuard kuralının sensör hatalarına karşı nasıl davrandığını göstermektir: yanlış alarm ve kaçırma oranları
/// bu varsayımlara bağlıdır, gerçek bir kapının oranı değildir.
/// </summary>
public static class GateModel
{
    /// <summary>Bavulun bir önceki bavulla aynı rezervasyonda olma olasılığı (aile/grup).</summary>
    public const double SameBookingChance = 0.25;
    /// <summary>Gruptaki bir bavulu sahibi yerine gruptaki başka birinin çıkarma olasılığı.</summary>
    public const double FamilyCarryChance = 0.30;
    /// <summary>Bavulu rezervasyonla ilgisi olmayan birinin çıkarma olasılığı.</summary>
    public const double WrongPersonChance = 0.005;
    /// <summary>Bavulun çıkışta hiç okunmaması (eski BPM modeliyle aynı).</summary>
    public const double MissChance = 0.02;
    /// <summary>Yolcunun kimlik/biniş kartı okutmaması ya da okutamaması.</summary>
    public const double NoVerificationChance = 0.03;
    /// <summary>Okumanın zayıf sinyalli olma olasılığı; zayıf okumada kalite 0,20-0,75, güçlüde 0,75-1,00.</summary>
    public const double WeakSignalChance = 0.15;
    /// <summary>Zayıf okumada (kalite 0,75'in altında) PNR'ın bir karakterinin yanlış okunma olasılığı.</summary>
    public const double MisreadWhenWeakChance = 0.30;
    /// <summary>Yan koridordaki başka bir bavulun da pakete karışma olasılığı.</summary>
    public const double CrossReadChance = 0.01;
    /// <summary>Zayıf ve güçlü okuma arasındaki sınır.</summary>
    public const double WeakSignalUpper = 0.75;

    public const double MinExitSeconds = 30;
    public const double MeanExtraExitSeconds = 40;
    public const string GateId = "ARRIVAL_EXIT_GATE_B";

    public static List<GatePacket> Plan(IReadOnlyList<BagTruth> bags, SimRandom rng)
    {
        var groups = bags.GroupBy(b => b.Pnr).ToDictionary(g => g.Key, g => g.Count());
        var packets = new List<GatePacket>(bags.Count);
        for (var i = 0; i < bags.Count; i++)
        {
            var bag = bags[i];
            if (rng.Chance(MissChance))
            {
                continue;
            }

            var time = bag.ClaimUtc.AddSeconds(MinExitSeconds + rng.Exp(MeanExtraExitSeconds));
            string truth;
            string carrierPnr;
            if (rng.Chance(WrongPersonChance))
            {
                truth = GateTruth.WrongPerson;
                carrierPnr = OtherBooking(bags, bag.Pnr, rng);
            }
            else
            {
                truth = groups[bag.Pnr] > 1 && rng.Chance(FamilyCarryChance) ? GateTruth.Family : GateTruth.Owner;
                carrierPnr = bag.Pnr;
            }

            string? presented = carrierPnr;
            double? signal = null;
            if (rng.Chance(NoVerificationChance))
            {
                presented = null;
            }
            else
            {
                signal = Math.Round(rng.Chance(WeakSignalChance) ? rng.Uniform(0.20, 0.75) : rng.Uniform(0.75, 1.00), 2);
                if (signal < WeakSignalUpper && rng.Chance(MisreadWhenWeakChance))
                {
                    presented = Mutate(presented, rng);
                }
            }

            var tags = new List<string> { bag.BagTag };
            var truthByTag = new Dictionary<string, string>(StringComparer.Ordinal) { [bag.BagTag] = truth };
            if (bags.Count > 1 && rng.Chance(CrossReadChance))
            {
                var j = rng.Int(0, bags.Count - 1);
                if (j >= i) j++;
                tags.Add(bags[j].BagTag);
                truthByTag[bags[j].BagTag] = GateTruth.Neighbor;
            }

            packets.Add(new GatePacket(time, GateId, tags, presented, signal, truthByTag));
        }
        return packets;
    }

    private static string OtherBooking(IReadOnlyList<BagTruth> bags, string pnr, SimRandom rng)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var other = bags[rng.Int(0, bags.Count)].Pnr;
            if (other != pnr) return other;
        }
        return Mutate(pnr, rng);
    }

    /// <summary>Bir karakteri farklı bir karakterle değiştirir (OCR/BLE okuma hatası).</summary>
    public static string Mutate(string pnr, SimRandom rng)
    {
        var chars = pnr.ToCharArray();
        var pos = rng.Int(0, chars.Length);
        char c;
        do
        {
            c = FlightGenerator.PnrChars[rng.Int(0, FlightGenerator.PnrChars.Length)];
        } while (c == chars[pos]);
        chars[pos] = c;
        return new string(chars);
    }
}
