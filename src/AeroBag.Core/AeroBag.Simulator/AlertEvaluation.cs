using System.Globalization;
using System.Text;
using AeroBag.Application.Alerts;
using AeroBag.Domain;

namespace AeroBag.Simulator;

/// <summary>
/// Kayıp bavul uyarısını (W4) API'siz, çok sayıda uçuşta değerlendirir. API'deki kuralın aynısı (<see cref="BagAlertRules"/>)
/// kullanılır. Bavulun durumu, kendisine ulaşan (kaybolmayan) okumaların en ilerisidir; bu, API'deki durum makinesinin
/// davranışıdır (ileri atlama uygulanır, geride kalan yok sayılır, BSM'siz gelen okuma bavulu oluşturur).
/// Kayıp okuma = düşen ya da bozulan mesaj (bozuk mesaj ayrıştırılamaz). Uyarı işçisinin gecikmesi (≤5 sn) sıfır kabul edilir.
/// Sonuçlar senaryo oranlarına ve gürültü varsayımlarına bağlıdır; sahadaki oranı göstermez.
/// </summary>
public static class AlertEvaluation
{
    public sealed record Row(
        string Label, double GraceMinutes, int Bags,
        int NotLoadedTruth, int NotLoadedDetected, IReadOnlyList<double> NotLoadedLatencyMin,
        int MissingTruth, int MissingDetected, int MissingBeforeBeltEmpty, IReadOnlyList<double> MissingLatencyMin,
        int FalseNotLoaded, int FalseMissing, IReadOnlyList<double> FalseOpenMin, int FalseStillOpen,
        int TruthPassengerBeforeBeltEmpty, int FalsePassenger, IReadOnlyList<double> FalsePassengerOpenMin);

    /// <summary>
    /// Uyarının yolcuya göründüğü aralık (<see cref="BagAlertRules.VisibleToPassenger"/>): NOT_LOADED ancak "indirme bitti"
    /// toleransından sonra görünür. Aralık boşsa null.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To)? PassengerWindow(
        (DateTimeOffset Opened, DateTimeOffset? Closed, string Kind) alert, FlightInfo info, AlertPolicy policy, DateTimeOffset horizon,
        bool canBeCorrected = true)
    {
        // İndirmeden sonra okuyucu yoksa MISSING yolcuya gösterilmez (BagAlertRules.VisibleToPassenger).
        if (alert.Kind == BagAlertKind.MissingAtArrival && !canBeCorrected) return null;
        var from = alert.Opened;
        if (alert.Kind == BagAlertKind.NotLoaded)
        {
            if (info.UnloadingCompletedUtc is not { } u) return null;
            var visibleFrom = u + policy.MissingGrace;
            if (visibleFrom > from) from = visibleFrom;
        }
        var to = alert.Closed ?? horizon;
        return from < to ? (from, to) : null;
    }

    private static BagState? StateOf(string kind) => kind switch
    {
        "BSM" => BagState.CheckedIn,
        "SEC" => BagState.SecurityScreened,
        "LOAD" => BagState.LoadedOnAircraft,
        "BUM" => BagState.UnloadedAtArrival,
        "CHUTE" => BagState.InboundChute,
        "CAROUSEL" => BagState.OnCarousel,
        "CLAIM" => BagState.ClaimedFromBelt,
        _ => null, // çıkış okumaları uyarıyı etkilemez (bavul zaten varıştan sonradır)
    };

    /// <summary>Her bavula ulaşan okumalar (zaman sıralı). Aynı tohum aynı kaybı üretir.</summary>
    public static Dictionary<string, List<(DateTimeOffset At, BagState State)>> Deliver(FlightResult f, double drop, double corrupt, int seed)
    {
        var rng = new SimRandom(seed);
        var byBag = f.Bags.ToDictionary(b => b.BagTag, _ => new List<(DateTimeOffset, BagState)>(), StringComparer.Ordinal);
        foreach (var m in f.Messages)
        {
            if (StateOf(m.Kind) is not { } state) continue;
            var lost = rng.Chance(drop) || rng.Chance(corrupt);
            if (!lost) byBag[m.BagTag].Add((m.TimeUtc, state));
        }
        return byBag;
    }

    /// <summary>Bir bavulun uyarı geçmişi: (açılış, kapanış ya da null, tür). Kural her değişim anında uygulanır.</summary>
    public static List<(DateTimeOffset Opened, DateTimeOffset? Closed, string Kind)> Alerts(
        IReadOnlyList<(DateTimeOffset At, BagState State)> reads, FlightInfo info, AlertPolicy policy, DateTimeOffset horizon)
    {
        var points = reads.Select(r => r.At).ToList();
        if (info.DepartedUtc is { } d) points.Add(d + policy.NotLoadedGrace);
        if (info.UnloadingCompletedUtc is { } u) points.Add(u + policy.MissingGrace);
        points = points.Where(p => p <= horizon).Distinct().Order().ToList();

        var history = new List<(DateTimeOffset, DateTimeOffset?, string)>();
        string? open = null;
        DateTimeOffset openedAt = default;
        foreach (var t in points)
        {
            BagState? state = null;
            foreach (var r in reads)
            {
                if (r.At > t) break;
                if (state is null || Rank(r.State) > Rank(state.Value)) state = r.State;
            }

            var wanted = state is { } s ? BagAlertRules.Evaluate(s, info.DepartedUtc, info.UnloadingCompletedUtc, t, policy) : null;
            if (wanted == open) continue;
            if (open is not null) history.Add((openedAt, t, open));
            open = wanted;
            openedAt = t;
        }
        if (open is not null) history.Add((openedAt, null, open));
        return history;
    }

    private static int Rank(BagState s) => BagStateMachine.Rank(s);

    public static Row Evaluate(IReadOnlyList<FlightResult> flights, AlertPolicy policy, double drop, double corrupt, int seed, string label,
        AeroBag.Application.Coverage.ReadCoverage? coverage = null)
    {
        var canBeCorrected = (coverage ?? AeroBag.Application.Coverage.ReadCoverage.Full).HasReadAfterUnload;
        int bags = 0, nlTruth = 0, nlHit = 0, miTruth = 0, miHit = 0, miEarly = 0, fNl = 0, fMi = 0, fOpen = 0, tPax = 0, fPax = 0;
        var fPaxDur = new List<double>();
        var nlLat = new List<double>();
        var miLat = new List<double>();
        var fDur = new List<double>();

        for (var fi = 0; fi < flights.Count; fi++)
        {
            var f = flights[fi];
            var delivered = Deliver(f, drop, corrupt, unchecked(seed + fi * 7919));
            var beltEmpty = f.Bags.Single(b => b.IsLast).CarouselUtc;          // son bavul banda geldi
            var horizon = f.Bags.Where(b => b.Fate is null).Max(b => b.ClaimUtc).AddMinutes(30);

            foreach (var b in f.Bags)
            {
                bags++;
                var history = Alerts(delivered[b.BagTag], f.Info, policy, horizon);
                var first = history.Count > 0 ? history[0] : default((DateTimeOffset Opened, DateTimeOffset? Closed, string Kind)?);
                var paxWindows = history.Select(h => PassengerWindow(h, f.Info, policy, horizon, canBeCorrected)).OfType<(DateTimeOffset From, DateTimeOffset To)>().ToList();
                if (b.Fate is not null && paxWindows.Count > 0 && paxWindows[0].From <= beltEmpty) tPax++;
                if (b.Fate is null && paxWindows.Count > 0)
                {
                    fPax++;
                    fPaxDur.Add(paxWindows.Sum(w => (w.To - w.From).TotalMinutes));
                }

                switch (b.Fate)
                {
                    case BagFate.NotLoaded:
                        nlTruth++;
                        if (first is { } a && a.Opened <= f.Info.OnBlockUtc)
                        {
                            nlHit++;
                            nlLat.Add((a.Opened - f.Info.DepartedUtc!.Value).TotalMinutes);
                        }
                        break;
                    case BagFate.MissingAtArrival:
                        miTruth++;
                        // Yakalandı: ölçüm ufku içinde uyarı açıldı. Yükleme okuması da kaybolduysa uyarı NOT_LOADED olarak
                        // inişten önce açılır; bu da yakalamadır (gecikme o durumda negatiftir).
                        if (first is { } m)
                        {
                            miHit++;
                            if (m.Opened <= beltEmpty) miEarly++;
                            miLat.Add((m.Opened - f.Info.UnloadingCompletedUtc!.Value).TotalMinutes);
                        }
                        break;
                    default:
                        if (first is { } x)
                        {
                            if (x.Kind == BagAlertKind.NotLoaded) fNl++; else fMi++;
                            fDur.Add(((x.Closed ?? horizon) - x.Opened).TotalMinutes);
                            if (x.Closed is null) fOpen++;
                        }
                        break;
                }
            }
        }

        return new Row(label, policy.MissingGrace.TotalMinutes, bags, nlTruth, nlHit, nlLat, miTruth, miHit, miEarly, miLat, fNl, fMi, fDur, fOpen,
            tPax, fPax, fPaxDur);
    }

    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.Order().ToList();
        var rank = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    public static string Report(IReadOnlyList<Row> rows)
    {
        var inv = CultureInfo.InvariantCulture;
        static string Rate(int hit, int all) => all == 0 ? "–" : string.Format(CultureInfo.InvariantCulture, "{0}/{1} ({2:P1})", hit, all, (double)hit / all);
        string Min(IReadOnlyList<double> v, double p) => v.Count == 0 ? "–" : Percentile(v, p).ToString("0.0", inv);

        var sb = new StringBuilder();
        sb.AppendLine("Personel ekranı (uyarının açıldığı an):");
        sb.AppendLine("Koşul | varış toleransı (dk) | bavul | yüklenmeyen: yakalanan (inişten önce) | gecikme medyan (dk, kalkıştan) | varışta yok: yakalanan | bant boşalmadan yakalanan | gecikme medyan (dk, indirme bittiden) | yanlış uyarı (1000 bavulda) | yanlış NOT_LOADED / MISSING | yanlış uyarının açık kaldığı süre medyan / p90 (dk) | sonuna kadar açık kalan yanlış uyarı");
        foreach (var r in rows)
        {
            var falseTotal = r.FalseNotLoaded + r.FalseMissing;
            sb.AppendLine(string.Format(inv, "{0} | {1:0} | {2} | {3} | {4} | {5} | {6} | {7} | {8:0.0} | {9} / {10} | {11} / {12} | {13}",
                r.Label, r.GraceMinutes, r.Bags,
                Rate(r.NotLoadedDetected, r.NotLoadedTruth), Min(r.NotLoadedLatencyMin, 0.5),
                Rate(r.MissingDetected, r.MissingTruth), Rate(r.MissingBeforeBeltEmpty, r.MissingTruth), Min(r.MissingLatencyMin, 0.5),
                r.Bags == 0 ? 0 : 1000.0 * falseTotal / r.Bags, r.FalseNotLoaded, r.FalseMissing,
                Min(r.FalseOpenMin, 0.5), Min(r.FalseOpenMin, 0.9), r.FalseStillOpen));
        }

        sb.AppendLine();
        sb.AppendLine("Yolcu ekranı (NOT_LOADED yalnızca indirme bitti + varış toleransından sonra görünür):");
        sb.AppendLine("Koşul | varış toleransı (dk) | gerçek vaka: yolcu bant boşalmadan öğrendi | yanlış uyarı gören yolcu (1000 bavulda) | yanlış uyarının yolcuya göründüğü süre medyan / p90 (dk)");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Format(inv, "{0} | {1:0} | {2} | {3:0.0} | {4} / {5}",
                r.Label, r.GraceMinutes, Rate(r.TruthPassengerBeforeBeltEmpty, r.NotLoadedTruth + r.MissingTruth),
                r.Bags == 0 ? 0 : 1000.0 * r.FalsePassenger / r.Bags,
                Min(r.FalsePassengerOpenMin, 0.5), Min(r.FalsePassengerOpenMin, 0.9)));
        }
        return sb.ToString();
    }
}
