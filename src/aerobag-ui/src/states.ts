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

export function stateMessage(state: string): string {
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
