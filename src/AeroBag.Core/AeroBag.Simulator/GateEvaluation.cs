using System.Globalization;
using System.Text;
using AeroBag.Application.ExitGuard;
using AeroBag.Domain;

namespace AeroBag.Simulator;

/// <summary>
/// ExitGuard kuralını, simülatörün ürettiği kapı paketleri üzerinde API'siz değerlendirir.
/// API'deki kuralın aynısı (<see cref="ExitGuardRules"/>) kullanılır. Sonuçlar <see cref="GateModel"/>'deki
/// varsayılan sensör hatalarına bağlıdır; sahadaki oranı göstermez.
/// </summary>
public static class GateEvaluation
{
    public sealed record Row(double MinSignal, IReadOnlyDictionary<(string Truth, string Decision), int> Counts)
    {
        public int Count(Func<string, bool> truth, string? decision = null) =>
            Counts.Where(kv => truth(kv.Key.Truth) && (decision is null || kv.Key.Decision == decision)).Sum(kv => kv.Value);
    }

    public static Row Evaluate(IReadOnlyList<FlightResult> flights, double minSignal)
    {
        var counts = new Dictionary<(string, string), int>();
        foreach (var f in flights)
        {
            var byTag = f.Bags.ToDictionary(b => b.BagTag, StringComparer.Ordinal);
            var gates = f.Messages.Where(m => m.Gate is not null).Select(m => m.Gate!).ToList();
            var ownExit = gates.ToDictionary(g => g.Tags[0], g => g.TimeUtc, StringComparer.Ordinal);

            foreach (var g in gates)
            {
                foreach (var tag in g.Tags)
                {
                    var bag = byTag[tag];
                    var state = StateAt(bag, g.TimeUtc, ownExit.TryGetValue(tag, out var t) && tag != g.Tags[0] ? t : null);
                    var verdict = ExitGuardRules.Decide(state, Key(bag.Pnr), g.PresentedPnr is null ? null : Key(g.PresentedPnr),
                        g.SignalQuality, minSignal);
                    var k = (g.Truth[tag], verdict.Code);
                    counts[k] = counts.GetValueOrDefault(k) + 1;
                }
            }
        }
        return new Row(minSignal, counts);
    }

    /// <summary>Yan koridordan okunan bavulun o andaki durumu (kendi çıkışı daha önce olduysa kapanmıştır).</summary>
    private static BagState StateAt(BagTruth bag, DateTimeOffset at, DateTimeOffset? ownExitAt)
    {
        if (ownExitAt is { } e && e < at) return BagState.ExitCleared;
        if (bag.ClaimUtc <= at) return BagState.ClaimedFromBelt;
        if (bag.CarouselUtc <= at) return BagState.OnCarousel;
        return BagState.InboundChute;
    }

    private static byte[] Key(string pnr) => Encoding.UTF8.GetBytes(pnr);

    public static string Report(IReadOnlyList<Row> rows)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine("Eşik | meşru okuma | yanlış alarm | yanlış alarm oranı | kararsız (meşru) | başkası | yakalanan | kararsız (başkası) | yanlışlıkla geçen");
        foreach (var r in rows)
        {
            var legit = r.Count(GateTruth.IsLegit);
            var falseAlarm = r.Count(GateTruth.IsLegit, "REVIEW_MISMATCH");
            var legitUndecided = r.Count(GateTruth.IsLegit, "NO_DECISION");
            var wrong = r.Count(t => t == GateTruth.WrongPerson);
            var caught = r.Count(t => t == GateTruth.WrongPerson, "REVIEW_MISMATCH");
            var wrongUndecided = r.Count(t => t == GateTruth.WrongPerson, "NO_DECISION");
            var wrongCleared = r.Count(t => t == GateTruth.WrongPerson, "CLEARED_OWNER");
            sb.AppendLine(string.Format(inv, "{0:0.00} | {1} | {2} | {3:P2} | {4} ({5:P1}) | {6} | {7} | {8} | {9}",
                r.MinSignal, legit, falseAlarm, legit == 0 ? 0 : (double)falseAlarm / legit, legitUndecided,
                legit == 0 ? 0 : (double)legitUndecided / legit, wrong, caught, wrongUndecided, wrongCleared));
        }

        var main = rows.FirstOrDefault(r => Math.Abs(r.MinSignal - ExitGuardRules.DefaultMinSignalQuality) < 1e-9) ?? rows[0];
        sb.AppendLine();
        sb.AppendLine(string.Create(inv, $"Ayrıntı (eşik {main.MinSignal:0.00}), gerçek -> karar:"));
        foreach (var ((truth, decision), n) in main.Counts.OrderBy(kv => kv.Key.Truth).ThenBy(kv => kv.Key.Decision))
        {
            sb.AppendLine($"  {truth,-13} -> {decision,-16} {n}");
        }
        return sb.ToString();
    }
}
