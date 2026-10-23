namespace AeroBag.Domain;

/// <summary>Bagaj yaşam döngüsü durumları (ARCHITECTURE.md bölüm 3.1).</summary>
public enum BagState
{
    CheckedIn = 1,
    SecurityScreened = 2,
    LoadedOnAircraft = 3,
    UnloadedAtArrival = 4,
    InboundChute = 5,
    OnCarousel = 6,
    ClaimedFromBelt = 7,
    ExitOverdue = 8,
    ExitCleared = 9,
    ExitReview = 10,
    OffloadedNoShow = 99,
}
