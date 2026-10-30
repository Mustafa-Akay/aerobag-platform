import { MESSAGES, type Lang } from "./i18n";

/** Süre metni; dil verilmezse Türkçe (personel ekranları). */
export function minutesText(seconds: number, lang: Lang = "tr"): string {
  const m = MESSAGES[lang].minutes;
  const minutes = Math.round(seconds / 60);
  if (minutes < 1) return m.underOne;
  if (minutes < 120) return m.min(minutes);
  return m.hours(Math.floor(minutes / 60), minutes % 60);
}

export function decimalMinutes(value: number): string {
  return value.toLocaleString(MESSAGES.tr.locale, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
}

export function clockTime(date: Date, lang: Lang = "tr"): string {
  return date.toLocaleTimeString(MESSAGES[lang].locale, { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}
