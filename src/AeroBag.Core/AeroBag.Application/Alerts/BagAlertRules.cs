using System.Text.RegularExpressions;
using AeroBag.Domain;

namespace AeroBag.Application.Alerts;

/// <summary>Bavul uyarısı türleri (Faz 6, W4). Veritabanında UPPER_SNAKE_CASE saklanır.</summary>
public static class BagAlertKind
{
    /// <summary>Uçak kalktı, bavulun yükleme okuması yok.</summary>
    public const string NotLoaded = "NOT_LOADED";

    /// <summary>Varışta indirme bitti, bavul (yüklenmiş görünse de) indirilmedi.</summary>
    public const string MissingAtArrival = "MISSING_AT_ARRIVAL";
}

/// <summary>Uyarının kapanma gerekçesi.</summary>
public static class BagAlertCloseReason
{
    /// <summary>Bavul sonradan okundu (uyarının beklediği ya da daha ileri bir aşamada).</summary>
    public const string Found = "FOUND";

    /// <summary>Bavul uçaktan indirildi (yolcu uçmadı); uyarı konusu kalmadı.</summary>
    public const string Offloaded = "OFFLOADED";

    /// <summary>Kural artık geçerli değil (ör. kilometre taşı düzeltildi).</summary>
    public const string Cleared = "CLEARED";
}

/// <summary>Yolcuya gösterilen, saklanmayan bildirim: uçuşta indirme beklenenden geç başladı.</summary>
public static class BagNotice
{
    public const string ArrivalDelayed = "ARRIVAL_DELAYED";
}

/// <summary>
/// Tolerans süreleri: kilometre taşından sonra uyarı açılmadan önce beklenen süre. Okuma mesajları geç ya da
/// sırasız gelebildiği için sıfır olamaz. Varsayılanlar DEMO varsayımıdır (ölçüm değildir); W4 ölçümüyle gözden geçirilir.
/// </summary>
public sealed record AlertPolicy(TimeSpan NotLoadedGrace, TimeSpan MissingGrace)
{
    public static readonly AlertPolicy Default = new(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
}

/// <summary>AODB kilometre taşları. Alanlardan en az biri verilir; verilmeyen alan kayıtlı değeri korur.</summary>
public sealed record FlightMilestones(string FlightId, DateTimeOffset? DepartedUtc = null, DateTimeOffset? UnloadingCompletedUtc = null)
{
    public string? Validate()
    {
        if (!Regex.IsMatch(FlightId ?? "", @"^[A-Z0-9\-]{1,40}\z")) return "flightId geçersiz.";
        if (DepartedUtc is null && UnloadingCompletedUtc is null) return "departedUtc ya da unloadingCompletedUtc verilmeli.";
        if (DepartedUtc is { } d && UnloadingCompletedUtc is { } u && u <= d) return "unloadingCompletedUtc kalkıştan sonra olmalı.";
        return null;
    }
}

/// <summary>
/// Saf (yan etkisiz) uyarı kuralları. Bir bavulda en fazla bir uyarı geçerlidir:
/// yükleme okuması yoksa NOT_LOADED (varışta da sürer), yüklenmiş ama indirilmemişse MISSING_AT_ARRIVAL.
/// Uyarı bir tespittir, kesin hüküm değildir: okuyucu kaçırması da aynı izi bırakır. Bu yüzden yolcu metni
/// "kayıt bulunamadı" der ve bavul sonradan okunursa uyarı kapanır.
/// </summary>
public static class BagAlertRules
{
    private static bool PastArrival(BagState s) => s != BagState.OffloadedNoShow && (int)s >= (int)BagState.UnloadedAtArrival;

    /// <summary>Şu an geçerli olması gereken uyarı; yoksa null.</summary>
    public static string? Evaluate(BagState state, DateTimeOffset? departedUtc, DateTimeOffset? unloadingCompletedUtc,
        DateTimeOffset now, AlertPolicy policy)
    {
        if (state == BagState.OffloadedNoShow || PastArrival(state))
        {
            return null;
        }

        if ((int)state < (int)BagState.LoadedOnAircraft && departedUtc is { } d && now >= d + policy.NotLoadedGrace)
        {
            return BagAlertKind.NotLoaded;
        }

        if (unloadingCompletedUtc is { } u && now >= u + policy.MissingGrace)
        {
            return BagAlertKind.MissingAtArrival;
        }

        return null;
    }

    /// <summary>Açık bir uyarı artık geçerli değilse neden kapandığı.</summary>
    public static string CloseReason(string kind, BagState state)
    {
        if (state == BagState.OffloadedNoShow) return BagAlertCloseReason.Offloaded;
        var found = kind == BagAlertKind.NotLoaded
            ? (int)state >= (int)BagState.LoadedOnAircraft
            : PastArrival(state);
        return found ? BagAlertCloseReason.Found : BagAlertCloseReason.Cleared;
    }

    /// <summary>
    /// Açık uyarı bavulun şimdiki durumuyla hâlâ tutarlı mı. Uyarı işçisi birkaç saniyede bir çalıştığından
    /// bavul bu arada okunmuş olabilir; yolcuya eski uyarı gösterilmez.
    /// </summary>
    public static bool StillApplies(string kind, BagState state) =>
        state != BagState.OffloadedNoShow && kind switch
        {
            BagAlertKind.NotLoaded => (int)state < (int)BagState.LoadedOnAircraft,
            BagAlertKind.MissingAtArrival => !PastArrival(state),
            _ => false,
        };

    /// <summary>
    /// Uçuşta bavul indirme beklenenden geç başladı mı: on-block'tan sonra ilk bavul hedefi geçti ve uçuştan henüz
    /// hiç bavul indirilmedi. Yalnızca varış öncesindeki bavullar için anlamlıdır. Hedef DEMO varsayımıdır (SLA).
    /// </summary>
    public static bool IsArrivalDelayed(BagState state, DateTimeOffset? onBlockUtc, bool anyBagUnloaded,
        int firstBagTargetSec, DateTimeOffset now) =>
        state != BagState.OffloadedNoShow
        && (int)state < (int)BagState.UnloadedAtArrival
        && !anyBagUnloaded
        && onBlockUtc is { } ob
        && now >= ob.AddSeconds(firstBagTargetSec);
}
