const tr = "tr-TR";

export function minutesText(seconds: number): string {
  const minutes = Math.round(seconds / 60);
  if (minutes < 1) return "1 dakikadan az";
  if (minutes < 120) return `${minutes} dk`;
  return `${Math.floor(minutes / 60)} sa ${minutes % 60} dk`;
}

export function decimalMinutes(value: number): string {
  return value.toLocaleString(tr, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
}

export function clockTime(date: Date): string {
  return date.toLocaleTimeString(tr, { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}
