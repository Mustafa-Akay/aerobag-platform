import type { BagNotice, PassengerBag } from "./api";
import { MESSAGES, type Messages, type StepState, type TransferStep } from "./i18n";

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

/** Aktarma bavulunun adımları (W10a): ilk uçuş, aktarma havalimanında indirme, aktarma hattı, bağlantı uçağı. */
export const TRANSFER_STEPS: readonly TransferStep[] = [
  "CHECKED_IN",
  "SECURITY_SCREENED",
  "LOADED_ON_AIRCRAFT",
  "UNLOADED_AT_ARRIVAL",
  "TRANSFER_SORTED",
  "ONWARD_LOADED",
];

/** Bağlantı uçuşundaki satırın "yüklendi" ya da daha ileri durumları (varış tarafı bu havalimanında izlenmez). */
const ONWARD_LOADED = new Set(["LOADED_ON_AIRCRAFT", "UNLOADED_AT_ARRIVAL", "INBOUND_CHUTE", "ON_CAROUSEL", "CLAIMED_FROM_BELT"]);

export function isTransfer(bag: PassengerBag): boolean {
  return bag.onwardFlightId !== null;
}

/**
 * Aktarma bavulunun adım çubuğundaki yeri. Önce bağlantı uçuşundaki satıra bakılır (yüklendi, aktarma hattında); o henüz
 * ilerlemediyse gelen uçuştaki satırın durumu gösterilir. TRANSFERRED (gelen satır aktarmaya devredildi) "Aktarmada" sayılır.
 */
export function transferStepIndex(bag: PassengerBag): number {
  if (bag.onwardState !== null && ONWARD_LOADED.has(bag.onwardState)) return TRANSFER_STEPS.indexOf("ONWARD_LOADED");
  if (bag.onwardState === "TRANSFER_SORTED" || bag.state === "TRANSFERRED") return TRANSFER_STEPS.indexOf("TRANSFER_SORTED");
  return TRANSFER_STEPS.findIndex((s) => s === bag.state);
}

export function transferMessage(bag: PassengerBag, m: Messages): string {
  if (bag.onwardState === "OFFLOADED_NO_SHOW") return m.transfer.onwardOffloaded;
  if (bag.state === "OFFLOADED_NO_SHOW") return m.state.offloaded;
  const i = transferStepIndex(bag);
  if (i < 0) return m.state.updating;
  const message = m.transfer.steps[TRANSFER_STEPS[i]].message;
  // Takip bağlantı uçağına yüklemede biter; yolcuya bavulunu nerede alacağı söylenir.
  return TRANSFER_STEPS[i] === "ONWARD_LOADED" && bag.onwardDestination ? `${message} ${m.transfer.collectAt(bag.onwardDestination)}` : message;
}

/** Kısa durum adı (operasyon tablosu için; personel ekranları şimdilik yalnızca Türkçe). */
export function stateLabel(state: string): string {
  if (state === "OFFLOADED_NO_SHOW") return "Uçağa alınmadı";
  if (state === "TRANSFER_SORTED") return MESSAGES.tr.transfer.steps.TRANSFER_SORTED.short;
  if (state === "TRANSFERRED") return "Aktarmaya geçti";
  const step = STEPS.find((s) => s === state);
  return step ? MESSAGES.tr.steps[step].short : state;
}
