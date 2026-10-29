import type { BagNotice } from "./api";

/** Bagaj yaşam döngüsünün yolcuya gösterilen adımları (docs/ARCHITECTURE.md bölüm 3.1). */
export const STEPS = [
  { state: "CHECKED_IN", short: "Teslim edildi", message: "Bagajınız teslim alındı." },
  { state: "SECURITY_SCREENED", short: "Güvenlik", message: "Bagajınız güvenlik kontrolünden geçti." },
  { state: "LOADED_ON_AIRCRAFT", short: "Uçakta", message: "Bagajınız uçağa yüklendi." },
  { state: "UNLOADED_AT_ARRIVAL", short: "İndirildi", message: "Bagajınız uçaktan indirildi." },
  { state: "INBOUND_CHUTE", short: "Şutta", message: "Bagajınız banda verilmek üzere." },
  { state: "ON_CAROUSEL", short: "Bantta", message: "Bagajınız bantta, alabilirsiniz." },
  { state: "CLAIMED_FROM_BELT", short: "Alındı", message: "Bagajınız banttan alındı." },
] as const;

/** Çıkış durumları adım çubuğunda "Alındı" olarak görünür. EXIT_REVIEW için yolcuya ayrı bir bilgi mesajı gösterilir. */
const AFTER_CLAIM = new Set(["EXIT_CLEARED", "EXIT_REVIEW", "EXIT_OVERDUE"]);

export function stepIndex(state: string): number {
  if (AFTER_CLAIM.has(state)) {
    return STEPS.length - 1;
  }
  return STEPS.findIndex((s) => s.state === state);
}

/**
 * Kayıp/geciken bavul bildirimleri (W4). Bir uyarı kesin hüküm değildir (okuyucu kaçırması da aynı izi bırakır);
 * bu yüzden metin "kayıt bulunamadı" der, alarm dili kullanmaz ve ne yapılacağını söyler.
 */
export const NOTICE_TEXT: Record<BagNotice, { title: string; body: string }> = {
  // Yolcuya yalnızca varışta, indirme bittikten sonra gösterilir (personel kalkıştan sonra görür).
  NOT_LOADED: {
    title: "Bavulunuzun bu uçağa yüklendiğine dair kayıt yok",
    body: "Uçaktan indirme tamamlandı ve bavulunuz okunmadı. Bant başında beklemek yerine bagaj hizmetlerine başvurun. Bavulunuz okunursa bu sayfa güncellenir.",
  },
  MISSING_AT_ARRIVAL: {
    title: "Uçaktan indirme tamamlandı, bavulunuz okunmadı",
    body: "Bant başında beklemek yerine bagaj hizmetlerine başvurun. Bavulunuz okunursa bu sayfa güncellenir.",
  },
  ARRIVAL_DELAYED: {
    title: "Bavul indirme beklenenden geç başladı",
    body: "Uçak park yerinde, bavullar henüz indirilmedi. İndirme başlayınca bu sayfa güncellenir.",
  },
};

/** Uyarı varsa bant numarası gösterilmez: yolcuyu bant başında bekletmemek için (iki uyarı da varışta gösterilir). */
export function hidesCarousel(notice: BagNotice | null): boolean {
  return notice === "MISSING_AT_ARRIVAL" || notice === "NOT_LOADED";
}

const CLAIMED_INDEX = STEPS.findIndex((s) => s.state === "CLAIMED_FROM_BELT");

/** Bant numarası, bavul banttan alınana kadar gösterilir (alındıktan sonra ya da uçağa alınmadıysa anlamı yok). */
export function showsCarousel(state: string): boolean {
  const i = stepIndex(state);
  return i >= 0 && i < CLAIMED_INDEX;
}

export function stateMessage(state: string, carousel: string | null = null): string {
  if (state === "ON_CAROUSEL" && carousel) {
    return `Bagajınız ${carousel} numaralı bantta, alabilirsiniz.`;
  }
  if (state === "OFFLOADED_NO_SHOW") {
    return "Bagajınız uçağa alınmadı. Havayolu ile iletişime geçin.";
  }
  if (state === "EXIT_REVIEW") {
    // ARCHITECTURE.md 6.3: sahibine bilgi verilir; müdahaleye teşvik eden alarm dili kullanılmaz.
    return "Bir bagajınızla ilgili beklenmedik bir çıkış kaydı algılandı. Lütfen bagaj hizmetleriyle iletişime geçin.";
  }
  const i = stepIndex(state);
  return i >= 0 ? STEPS[i].message : "Bagajınızın durumu güncelleniyor.";
}

/** Kısa durum adı (operasyon tablosu için). */
export function stateLabel(state: string): string {
  if (state === "OFFLOADED_NO_SHOW") return "Uçağa alınmadı";
  const step = STEPS.find((s) => s.state === state);
  return step ? step.short : state;
}
