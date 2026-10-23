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
public static class BagStateMachine
{
    private static readonly BagState[] Chain =
    [
        BagState.CheckedIn,
        BagState.SecurityScreened,
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
        BagState.LoadedOnAircraft,
    ];

    // ExitCleared ve ExitReview aynı sırada: biri gerçekleşince diğeri otomatik değiştirmez.
    private static int Rank(BagState s) => s switch
    {
        BagState.ExitOverdue => 8,
        BagState.ExitCleared or BagState.ExitReview => 9,
        BagState.OffloadedNoShow => 99,
        _ => (int)s,
    };

    private static bool IsTerminal(BagState s) =>
        s is BagState.ExitCleared or BagState.ExitReview or BagState.OffloadedNoShow;

    public static TransitionResult Apply(BagState? current, BagState target)
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

        if (current is null)
        {
            // Bu havalimanında yaşam döngüsü CheckedIn ile başlar; aktarma bavulu UnloadedAtArrival ile.
            if (target is BagState.CheckedIn or BagState.UnloadedAtArrival)
            {
                return new TransitionResult(target, TransitionOutcome.Applied, []);
            }
            return Advance(target, SkippedBetween(0, Rank(target)));
        }

        var rankCurrent = Rank(current.Value);
        var rankTarget = Rank(target);
        if (rankTarget <= rankCurrent)
        {
            return Ignored(current.Value);
        }

        return Advance(target, SkippedBetween(rankCurrent, rankTarget));
    }

    private static List<BagState> SkippedBetween(int rankFromExclusive, int rankToExclusive) =>
        Chain.Where(s => Rank(s) > rankFromExclusive && Rank(s) < rankToExclusive).ToList();

    private static TransitionResult Advance(BagState target, List<BagState> skipped) =>
        new(target,
            skipped.Count == 0 ? TransitionOutcome.Applied : TransitionOutcome.AppliedWithAnomaly,
            skipped);

    private static TransitionResult Ignored(BagState current) =>
        new(current, TransitionOutcome.IgnoredStale, []);
}
