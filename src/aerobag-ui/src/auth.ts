/**
 * Personel jetonu. Yalnızca bu sekmenin oturumunda tutulur (sessionStorage) ve kısa ömürlüdür.
 * Not: sessionStorage'daki jeton, sayfada bir XSS açığı olursa okunabilir; bu risk THREAT_MODEL.md'de yazılıdır.
 */
export type StaffSession = { token: string; role: "ops" | "security"; expiresAt: number };

const KEY = "aerobag.staff";

export function loadStaff(): StaffSession | null {
  try {
    const raw = sessionStorage.getItem(KEY);
    if (!raw) return null;
    const s = JSON.parse(raw) as Partial<StaffSession>;
    if (typeof s.token !== "string" || typeof s.expiresAt !== "number" || (s.role !== "ops" && s.role !== "security")) return null;
    if (s.expiresAt <= Date.now()) {
      clearStaff();
      return null;
    }
    return { token: s.token, role: s.role, expiresAt: s.expiresAt };
  } catch {
    return null;
  }
}

export function saveStaff(session: StaffSession): void {
  try {
    sessionStorage.setItem(KEY, JSON.stringify(session));
  } catch {
    // Depolama kapalıysa oturum yalnızca sayfa açıkken sürer.
  }
}

export function clearStaff(): void {
  try {
    sessionStorage.removeItem(KEY);
  } catch {
    // yok say
  }
}
