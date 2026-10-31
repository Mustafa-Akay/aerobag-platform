namespace AeroBag.Domain;

public enum TransitionOutcome
{
    /// <summary>Durum ilerledi, atlanan adım yok.</summary>
    Applied,
    /// <summary>Durum ilerledi ama ara adım(lar) atlandı (okuyucu kaçırması olabilir).</summary>
    AppliedWithAnomaly,
    /// <summary>Olay mevcut durumdan geride ya da aynı; durum değişmedi.</summary>
    IgnoredStale,
}

public sealed record TransitionResult(
    BagState NewState,
    TransitionOutcome Outcome,
    IReadOnlyList<BagState> SkippedStates);

/// <summary>
/// Saf (yan etkisiz) durum makinesi. Kurallar: durum sırası monotondur, ara adım atlanabilir
/// (ANOMALY), geride kalan olay yok sayılır (IGNORED_STALE). Çift mesaj tekilleştirmesi
/// idempotency_key ile bu katmanın dışında yapılır.
/// </summary>
/// <remarks>
/// Aktarma (W10a): bağlantı uçuşundaki satırın zinciri güvenlik yerine aktarma hattını içerir (aktarma bavulu çoğu
/// zaman yeniden taranmaz; taranırsa güvenlik okuması yine kabul edilir ama atlanması sıra dışı sayılmaz). Gelen uçuştaki
/// satır indirmeden sonra Transferred ile kapanır.
/// </remarks>
public static class BagStateMachine
{
    private static readonly BagState[] ArrivalChain =
    [
        BagState.CheckedIn,
        BagState.SecurityScreened,
        BagState.LoadedOnAircraft,
        BagState.UnloadedAtArrival,
        BagState.InboundChute,
        BagState.OnCarousel,
        BagState.ClaimedFromBelt,
    ];

    private static readonly BagState[] TransferOnwardChain =
    [
        BagState.CheckedIn,
        BagState.TransferSorted,
        BagState.LoadedOnAircraft,
        BagState.UnloadedAtArrival,
        BagState.InboundChute,
        BagState.OnCarousel,
        BagState.ClaimedFromBelt,
    ];

    private static readonly BagState[] NoShowAllowedFrom =
    [
        BagState.CheckedIn,
        BagState.SecurityScreened,
        BagState.TransferSorted,
        BagState.LoadedOnAircraft,
    ];

    /// <summary>Sıra. Aktarma durumları araya girebilsin diye onar onar artar.</summary>
    // ExitCleared ve ExitReview aynı sırada: biri gerçekleşince diğeri otomatik değiştirmez.
    public static int Rank(BagState s) => s switch
    {
        BagState.CheckedIn => 10,
        BagState.SecurityScreened => 20,
        BagState.TransferSorted => 25,
        BagState.LoadedOnAircraft => 30,
        BagState.UnloadedAtArrival => 40,
        BagState.Transferred => 45,
        BagState.InboundChute => 50,
        BagState.OnCarousel => 60,
        BagState.ClaimedFromBelt => 70,
        BagState.ExitOverdue => 80,
        BagState.ExitCleared or BagState.ExitReview => 90,
        BagState.OffloadedNoShow => 990,
        _ => throw new ArgumentOutOfRangeException(nameof(s), s, null),
    };

    private static bool IsTerminal(BagState s) =>
        s is BagState.ExitCleared or BagState.ExitReview or BagState.OffloadedNoShow or BagState.Transferred;

    public static TransitionResult Apply(BagState? current, BagState target, BagRoute route = BagRoute.Arrival)
    {
        if (current is { } cur && IsTerminal(cur))
        {
            return Ignored(cur);
        }

        if (target == BagState.OffloadedNoShow)
        {
            if (current is null || NoShowAllowedFrom.Contains(current.Value))
            {
                return new TransitionResult(target, TransitionOutcome.Applied, []);
            }
            return Ignored(current.Value);
        }

        var chain = route == BagRoute.TransferOnward ? TransferOnwardChain : ArrivalChain;

        if (current is null)
        {
            // Bu havalimanında yaşam döngüsü CheckedIn ile başlar; BSM'si gelmemiş bir bavul UnloadedAtArrival ile de başlayabilir.
            if (target is BagState.CheckedIn or BagState.UnloadedAtArrival)
            {
                return new TransitionResult(target, TransitionOutcome.Applied, []);
            }
            return Advance(target, SkippedBetween(chain, 0, Rank(target)));
        }

        var rankCurrent = Rank(current.Value);
        var rankTarget = Rank(target);
        if (rankTarget <= rankCurrent)
        {
            return Ignored(current.Value);
        }

        return Advance(target, SkippedBetween(chain, rankCurrent, rankTarget));
    }

    private static List<BagState> SkippedBetween(BagState[] chain, int rankFromExclusive, int rankToExclusive) =>
        chain.Where(s => Rank(s) > rankFromExclusive && Rank(s) < rankToExclusive).ToList();

    private static TransitionResult Advance(BagState target, List<BagState> skipped) =>
        new(target,
            skipped.Count == 0 ? TransitionOutcome.Applied : TransitionOutcome.AppliedWithAnomaly,
            skipped);

    private static TransitionResult Ignored(BagState current) =>
        new(current, TransitionOutcome.IgnoredStale, []);
}
