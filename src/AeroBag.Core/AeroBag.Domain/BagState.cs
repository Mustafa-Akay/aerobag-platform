namespace AeroBag.Domain;

/// <summary>Bagaj yaşam döngüsü durumları (ARCHITECTURE.md bölüm 3.1).</summary>
/// <remarks>
/// Sayısal değerler sıra değildir; sıra <see cref="BagStateMachine"/>'dedir. Aktarma durumları (W10a) sona eklendi:
/// TransferSorted bağlantı uçuşundaki satırda aktarma hattından geçişi, Transferred gelen uçuştaki satırın aktarmaya
/// devredildiğini (banda gitmeyeceğini) bildirir.
/// </remarks>
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
    TransferSorted = 11,
    Transferred = 12,
    OffloadedNoShow = 99,
}

/// <summary>Bir bavul satırının bu havalimanındaki yolu (W10a).</summary>
public enum BagRoute
{
    /// <summary>Gelen uçuştaki satır (aktarma bavulunun gelen ayağı dahil).</summary>
    Arrival,

    /// <summary>Bağlantı uçuşundaki satır: aktarma hattından geçip bağlantı uçağına yüklenir. Güvenlik okuması isteğe bağlıdır.</summary>
    TransferOnward,
}
