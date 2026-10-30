using System.Globalization;
using System.Text;
using AeroBag.Application.Coverage;
using AeroBag.Application.Eta;
using AeroBag.Application.Ml;
using AeroBag.Domain;

namespace AeroBag.Simulator;

/// <summary>
/// Okuma kapsamına duyarlılık (W5), API'siz. Aynı tohumdan üretilen gerçek zaman çizelgesi her profilde "görülür":
/// okuyucusu olmayan noktanın okuması hiç yoktur, olanınki iletimde kaybolabilir (<see cref="AlertEvaluation.Deliver"/>).
/// Kurallar API'dekilerle aynıdır: durum makinesi, kapsamaya göre sıra dışı, fizik penceresi ve varsayılan ETA aralığı.
/// ML servisi bu ölçümde yoktur; indirme sonrası tahmin varsayılan aralıktır (bant okuması olmayan kurulumda ampirik istatistik
/// de oluşmaz). Sonuçlar senaryo varsayımlarına bağlıdır.
/// </summary>
public static class CoverageEvaluation
{
    public sealed record Row(
        string Profile, int Bags,
        IReadOnlyList<double> ToldDelayMin, int ToldEarly, int NeverTold,
        int Flights, int FirstFromReads, int LastFromReads, int FirstFromAodb, int LastFromAodb,
        IReadOnlyList<double> FirstErrorMin, IReadOnlyList<double> LastErrorMin,
        int NaiveAnomalies, int AwareAnomalies);

    /// <summary>
    /// Yolcuya "bavulunuz bantta" ya da "büyük olasılıkla bantta" denildiği an; hiç denilmiyorsa null.
    /// Bant ağzı okuyucusu varsa bant ya da banttan alma okumasıdır (tam kapsamda tahmine dayalı bildirim yoktur).
    /// Yoksa son varış okumasından sonra tahmin aralığının üst sınırıdır (şutta fizik, indirmede varsayılan aralık).
    /// </summary>
    public static DateTimeOffset? ToldOnBelt(IReadOnlyList<(DateTimeOffset At, BagState State)> reads, FlightInfo info, ReadCoverage coverage)
    {
        if (coverage.Has(ReadPoint.Carousel))
        {
            var r = reads.Where(x => x.State is BagState.OnCarousel or BagState.ClaimedFromBelt).Select(x => (DateTimeOffset?)x.At).FirstOrDefault();
            return r;
        }

        var chute = reads.Where(x => x.State == BagState.InboundChute).Select(x => (DateTimeOffset?)x.At).FirstOrDefault();
        if (chute is { } c)
        {
            var ctx = new FlightContext(info.FlightId, info.OnBlockUtc, info.WideBody, info.Weather == "RAIN", info.TotalBags,
                info.StandDistanceM, info.ChuteToCarouselM, info.ConveyorSpeedMps, info.ArrivalsLast30Min, info.GroundHandler);
            return PhysicsEtaWindow.Build(c, ctx).P90Utc;
        }

        var unload = reads.Where(x => x.State == BagState.UnloadedAtArrival).Select(x => (DateTimeOffset?)x.At).FirstOrDefault();
        return unload is { } u ? EtaEstimator.Estimate(BagState.UnloadedAtArrival, u, null, null)!.P90Utc : null;
    }

    /// <summary>Okumaları sırayla durum makinesinden geçirir; sıra dışı sayısını kapsamı bilmeyen ve bilen kurala göre döndürür.</summary>
    public static (int Naive, int Aware) Anomalies(IReadOnlyList<(DateTimeOffset At, BagState State)> reads, ReadCoverage coverage)
    {
        BagState? current = null;
        int naive = 0, aware = 0;
        foreach (var (_, state) in reads)
        {
            var t = BagStateMachine.Apply(current, state);
            if (t.Outcome == TransitionOutcome.AppliedWithAnomaly)
            {
                naive++;
                if (coverage.IsAnomaly(t.SkippedStates)) aware++;
            }
            if (t.Outcome != TransitionOutcome.IgnoredStale) current = t.NewState;
        }
        return (naive, aware);
    }

    public static Row Evaluate(IReadOnlyList<FlightResult> flights, ReadCoverage coverage, double drop, double corrupt, int seed)
    {
        var delay = new List<double>();
        var firstErr = new List<double>();
        var lastErr = new List<double>();
        int bags = 0, early = 0, never = 0, firstReads = 0, lastReads = 0, firstAodb = 0, lastAodb = 0, naive = 0, aware = 0;

        for (var fi = 0; fi < flights.Count; fi++)
        {
            var f = flights[fi];
            var delivered = AlertEvaluation.Deliver(f, drop, corrupt, unchecked(seed + fi * 7919));
            var arrived = new List<DateTimeOffset>();
            var allArrived = true;

            foreach (var b in f.Bags.Where(b => b.Fate is null))
            {
                bags++;
                var reads = delivered[b.BagTag];
                // Sıra dışı sayımına çıkış kapısı okuması da girer (canlı gösterimdeki ExitGuard): bavul son okumasından
                // doğrudan çıkışa geçer. Kapı okuması kapsamdan bağımsızdır ve iletim kaybına uğramaz (GateModel ayrıca ölçülür).
                var (n, a) = Anomalies([.. reads, (b.ClaimUtc.AddMinutes(1), BagState.ExitCleared)], coverage);
                naive += n;
                aware += a;

                var told = ToldOnBelt(reads, f.Info, coverage);
                if (told is { } t)
                {
                    var d = (t - b.CarouselUtc).TotalMinutes;
                    delay.Add(d);
                    if (d < 0) early++;
                }
                else
                {
                    never++;
                }

                // SLA, okumalardan: bant ağzı okuması (ilk bant olayı); son bavul ancak bütün bavullar bant ya da sonrasındaysa.
                var carouselRead = reads.Where(x => x.State == BagState.OnCarousel).Select(x => (DateTimeOffset?)x.At).FirstOrDefault();
                if (carouselRead is { } cr) arrived.Add(cr);
                var furthest = reads.Count == 0 ? (BagState?)null : reads.Max(x => x.State);
                if (furthest is null || furthest < BagState.OnCarousel) allArrived = false;
            }

            var truthFirst = f.Bags.Single(b => b.IsFirst).CarouselUtc;
            var truthLast = f.Bags.Single(b => b.IsLast).CarouselUtc;
            // API'deki kural (SlaQueries): okuma varsa okuma, yoksa AODB. AODB simülasyonda gerçek zamanı bildirir.
            DateTimeOffset? first = arrived.Count > 0 ? arrived.Min() : f.Info.FirstBagOnBeltUtc;
            var firstFromReads = arrived.Count > 0;
            DateTimeOffset? last = allArrived && arrived.Count > 0 ? arrived.Max() : f.Info.LastBagOnBeltUtc;
            var lastFromReads = allArrived && arrived.Count > 0;
            if (firstFromReads) firstReads++; else if (first is not null) firstAodb++;
            if (lastFromReads) lastReads++; else if (last is not null) lastAodb++;
            if (first is { } fv) firstErr.Add(Math.Abs((fv - truthFirst).TotalMinutes));
            if (last is { } lv) lastErr.Add(Math.Abs((lv - truthLast).TotalMinutes));
        }

        return new Row(coverage.Name, bags, delay, early, never, flights.Count, firstReads, lastReads, firstAodb, lastAodb,
            firstErr, lastErr, naive, aware);
    }

    public static string Report(IReadOnlyList<Row> rows)
    {
        var inv = CultureInfo.InvariantCulture;
        string P(IReadOnlyList<double> v, double p) => v.Count == 0 ? "–" : AlertEvaluation.Percentile(v, p).ToString("0.0", inv);
        static string Pct(int n, int all) => all == 0 ? "–" : string.Format(CultureInfo.InvariantCulture, "{0} ({1:P1})", n, (double)n / all);

        var sb = new StringBuilder();
        sb.AppendLine("Yolcu: bavul banda geldikten sonra \"bantta / büyük olasılıkla bantta\" bilgisine kadar geçen süre (eksi: bavul gelmeden söylendi)");
        sb.AppendLine("Profil | bavul | gecikme medyan / p90 (dk) | erken söylenen (yanlış bilgi) | erken söylenenlerde medyan (dk) | hiç söylenmeyen");
        foreach (var r in rows)
        {
            var earlyOnly = r.ToldDelayMin.Where(d => d < 0).ToList();
            sb.AppendLine(string.Format(inv, "{0} | {1} | {2} / {3} | {4} | {5} | {6}",
                r.Profile, r.Bags, P(r.ToldDelayMin, 0.5), P(r.ToldDelayMin, 0.9), Pct(r.ToldEarly, r.Bags),
                P(earlyOnly, 0.5), Pct(r.NeverTold, r.Bags)));
        }

        sb.AppendLine();
        sb.AppendLine("SLA: ilk ve son bavulun kaynağı ve gerçek zamandan sapması (AODB simülasyonda gerçek zamanı bildirir)");
        sb.AppendLine("Profil | uçuş | ilk bavul: okuma / AODB | son bavul: okuma / AODB | ilk bavul sapması medyan / p90 (dk) | son bavul sapması medyan / p90 (dk)");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Format(inv, "{0} | {1} | {2} / {3} | {4} / {5} | {6} / {7} | {8} / {9}",
                r.Profile, r.Flights, r.FirstFromReads, r.FirstFromAodb, r.LastFromReads, r.LastFromAodb,
                P(r.FirstErrorMin, 0.5), P(r.FirstErrorMin, 0.9), P(r.LastErrorMin, 0.5), P(r.LastErrorMin, 0.9)));
        }

        sb.AppendLine();
        sb.AppendLine("\"Sıra dışı\" sayısı (1000 bavulda): kapsamı bilmeyen kural / kapsamı bilen kural (API)");
        sb.AppendLine("Profil | bilmeyen | bilen");
        foreach (var r in rows)
        {
            sb.AppendLine(string.Format(inv, "{0} | {1:0.0} | {2:0.0}",
                r.Profile, r.Bags == 0 ? 0 : 1000.0 * r.NaiveAnomalies / r.Bags, r.Bags == 0 ? 0 : 1000.0 * r.AwareAnomalies / r.Bags));
        }
        return sb.ToString();
    }
}
