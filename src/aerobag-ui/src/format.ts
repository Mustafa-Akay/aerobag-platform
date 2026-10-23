const tr = "tr-TR";

export function minutesText(seconds: number): string {
  const minutes = Math.round(seconds / 60);
  return minutes < 1 ? "1 dakikadan az" : `${minutes} dk`;
}

export function decimalMinutes(value: number): string {
  return value.toLocaleString(tr, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
}

export function clockTime(date: Date): string {
  return date.toLocaleTimeString(tr, { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}
