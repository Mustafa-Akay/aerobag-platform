import type { BagNotice } from "./api";
import { MESSAGES, type Messages, type StepState } from "./i18n";

/**
 * Bagaj yaşam döngüsünün yolcuya gösterilen adımları (docs/ARCHITECTURE.md bölüm 3.1).
 * Metinler i18n.tsx'teki sözlükte; burada yalnızca sıra ve kurallar var.
 */
export const STEPS: readonly StepState[] = [
  "CHECKED_IN",
  "SECURITY_SCREENED",
  "LOADED_ON_AIRCRAFT",
  "UNLOADED_AT_ARRIVAL",
  "INBOUND_CHUTE",
  "ON_CAROUSEL",
  "CLAIMED_FROM_BELT",
];

/** Çıkış durumları adım çubuğunda "Alındı" olarak görünür. EXIT_REVIEW için yolcuya ayrı bir bilgi mesajı gösterilir. */
const AFTER_CLAIM = new Set(["EXIT_CLEARED", "EXIT_REVIEW", "EXIT_OVERDUE"]);

export function stepIndex(state: string): number {
  if (AFTER_CLAIM.has(state)) {
    return STEPS.length - 1;
  }
  return STEPS.findIndex((s) => s === state);
}

/** Uyarı varsa bant numarası gösterilmez: yolcuyu bant başında bekletmemek için (iki uyarı da varışta gösterilir). */
export function hidesCarousel(notice: BagNotice | null): boolean {
  return notice === "MISSING_AT_ARRIVAL" || notice === "NOT_LOADED";
}

const CLAIMED_INDEX = STEPS.indexOf("CLAIMED_FROM_BELT");

/** Bant numarası, bavul banttan alınana kadar gösterilir (alındıktan sonra ya da uçağa alınmadıysa anlamı yok). */
export function showsCarousel(state: string): boolean {
  const i = stepIndex(state);
  return i >= 0 && i < CLAIMED_INDEX;
}

export function stateMessage(state: string, carousel: string | null, m: Messages): string {
  if (state === "ON_CAROUSEL" && carousel) {
    return m.state.onCarousel(carousel);
  }
  if (state === "OFFLOADED_NO_SHOW") {
    return m.state.offloaded;
  }
  if (state === "EXIT_REVIEW") {
    return m.state.exitReview;
  }
  const i = stepIndex(state);
  return i >= 0 ? m.steps[STEPS[i]].message : m.state.updating;
}

/** Kısa durum adı (operasyon tablosu için; personel ekranları şimdilik yalnızca Türkçe). */
export function stateLabel(state: string): string {
  if (state === "OFFLOADED_NO_SHOW") return "Uçağa alınmadı";
  const step = STEPS.find((s) => s === state);
  return step ? MESSAGES.tr.steps[step].short : state;
}
