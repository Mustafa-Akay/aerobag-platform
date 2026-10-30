using System.Security.Cryptography;
using AeroBag.Domain;

namespace AeroBag.Application.ExitGuard;

public enum GateDecision
{
    /// <summary>Bavul ile sunulan PNR eşleşti (aynı rezervasyondaki yolcular dahil). Bavul EXIT_CLEARED olur.</summary>
    ClearedOwner,
    /// <summary>Sunulan PNR bavulun PNR'ıyla uyuşmadı. Kapıda alarm yok; bavul EXIT_REVIEW olur, inceleme kaydı açılır.</summary>
    ReviewMismatch,
    /// <summary>Karar verilecek kadar bilgi yok (doğrulama yok, sinyal zayıf, sahip bilgisi yok). Durum değişmez.</summary>
    NoDecision,
    /// <summary>Bavulun yaşam döngüsü zaten kapanmış. Durum değişmez.</summary>
    AlreadyClosed,
    /// <summary>Bavul daha banda gelmemişken çıkışta okundu. Durum değişmez, inceleme kaydı açılır.</summary>
    OutOfSequence,
}

public sealed record GateVerdict(GateDecision Decision, string Reason)
{
    public string Code => Decision switch
    {
        GateDecision.ClearedOwner => "CLEARED_OWNER",
        GateDecision.ReviewMismatch => "REVIEW_MISMATCH",
        GateDecision.NoDecision => "NO_DECISION",
        GateDecision.AlreadyClosed => "ALREADY_CLOSED",
        _ => "OUT_OF_SEQUENCE",
    };
}

/// <summary>
/// ExitGuard karar kuralı (ARCHITECTURE.md 6.3). Saf fonksiyondur; veritabanı ya da saat kullanmaz.
/// Eşleştirme PNR düzeyindedir: aynı rezervasyondaki yolcular birbirlerinin bavulunu çıkarabilir (aile kuralı),
/// soyad karşılaştırılmaz. PNR'lar düz metin değil, HMAC özeti olarak karşılaştırılır.
/// Belirsiz durumda (doğrulama yok, sinyal zayıf) karar verilmez: yanlış alarm üretmek yerine çıkış süresine bırakılır.
/// </summary>
public static class ExitGuardRules
{
    public const double DefaultMinSignalQuality = 0.6;

    /// <param name="lastArrivalState">
    /// Çıkıştan önce okunabilen en ileri varış durumu (okuma kapsamı, W5). Bavul bundan geride okunursa sıra dışıdır.
    /// Varsayılan bant ağzıdır; bant ağzında okuyucu yoksa şut ya da indirme verilir, yoksa her bavul yanlış alarm olur.
    /// </param>
    public static GateVerdict Decide(BagState current, byte[]? ownerPnrKey, byte[]? presentedPnrKey, double? signalQuality, double minSignalQuality,
        BagState lastArrivalState = BagState.OnCarousel)
    {
        if (current is BagState.ExitCleared or BagState.ExitReview or BagState.OffloadedNoShow)
        {
            return new GateVerdict(GateDecision.AlreadyClosed, "CLOSED");
        }
        if (current < lastArrivalState)
        {
            return new GateVerdict(GateDecision.OutOfSequence, lastArrivalState == BagState.OnCarousel ? "NOT_ON_CAROUSEL" : "NOT_ARRIVED");
        }
        if (presentedPnrKey is null)
        {
            return new GateVerdict(GateDecision.NoDecision, "NO_VERIFICATION");
        }
        if (signalQuality is not { } q || q < minSignalQuality)
        {
            return new GateVerdict(GateDecision.NoDecision, "LOW_SIGNAL");
        }
        if (ownerPnrKey is null)
        {
            return new GateVerdict(GateDecision.NoDecision, "NO_OWNER_DATA");
        }
        return CryptographicOperations.FixedTimeEquals(ownerPnrKey, presentedPnrKey)
            ? new GateVerdict(GateDecision.ClearedOwner, "PNR_MATCH")
            : new GateVerdict(GateDecision.ReviewMismatch, "PNR_MISMATCH");
    }
}
