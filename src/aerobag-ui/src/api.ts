export type EtaView = {
  p10Utc: string;
  p50Utc: string;
  p90Utc: string;
  p10SecondsFromNow: number;
  p50SecondsFromNow: number;
  p90SecondsFromNow: number;
  basis: "HISTORY" | "FALLBACK" | "ML" | "ML_FLIGHT" | "PHYSICS";
};

export type PassengerBag = {
  bagTag: string;
  flightId: string;
  state: string;
  eta: EtaView | null;
  /** Uçuşa atanan bagaj bandı; henüz atanmadıysa null. */
  carousel: string | null;
};

export type LookupResult = {
  asOfUtc: string;
  bags: PassengerBag[];
  /** Yalnızca bu sorgudaki bagajlar için canlı kanala erişim veren kısa ömürlü yolcu jetonu. */
  accessToken: string;
  expiresInSec: number;
};

export type LoginResult = { accessToken: string; expiresInSec: number; role: "ops" | "security" };

export type FlightSummary = {
  flightId: string;
  totalBags: number;
  beforeArrival: number;
  inbound: number;
  onCarousel: number;
  claimed: number;
  exitRead: number;
  exitOverdue: number;
  exitReview: number;
  anomalies: number;
  firstUnloadUtc: string | null;
  firstCarouselUtc: string | null;
  lastCarouselUtc: string | null;
  firstBagMinutes: number | null;
  carousel: string | null;
};

export type SlaStatus = "MET" | "BREACHED" | "ON_TRACK" | "AT_RISK" | "LIKELY_BREACH" | "UNKNOWN";

export type SlaLeg = {
  targetSec: number;
  actualSec: number | null;
  breachProbability: number | null;
  status: SlaStatus;
  projectedP50Sec: number | null;
  projectedP90Sec: number | null;
  projectedDelayP50Sec: number | null;
  projectedDelayP90Sec: number | null;
};

export type FlightSla = {
  flightId: string;
  assumption: string;
  onBlockUtc: string | null;
  elapsedSec: number | null;
  allBagsArrived: boolean;
  firstBag: SlaLeg;
  lastBag: SlaLeg;
};

export class ApiError extends Error {
  readonly status: number;

  constructor(status: number) {
    super(`API ${status}`);
    this.status = status;
  }
}

async function getJson<T>(url: string, signal?: AbortSignal, token?: string): Promise<T> {
  const response = await fetch(url, { signal, headers: token ? { Authorization: `Bearer ${token}` } : undefined });
  if (!response.ok) {
    throw new ApiError(response.status);
  }
  return (await response.json()) as T;
}

/** PNR ve soyad adreste değil gövdede gider; böylece sunucu ve vekil sunucu kayıtlarına düşmez. */
export async function lookup(pnr: string, surname: string, signal?: AbortSignal): Promise<LookupResult> {
  const response = await fetch("/v1/lookup", {
    method: "POST",
    signal,
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ pnr, surname }),
  });
  if (!response.ok) {
    throw new ApiError(response.status);
  }
  return (await response.json()) as LookupResult;
}

export async function login(username: string, password: string): Promise<LoginResult> {
  const response = await fetch("/v1/auth/token", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ username, password }),
  });
  if (!response.ok) {
    throw new ApiError(response.status);
  }
  return (await response.json()) as LoginResult;
}

export function getFlights(token: string, signal?: AbortSignal): Promise<FlightSummary[]> {
  return getJson<FlightSummary[]>("/v1/flights", signal, token);
}

export function getFlightSla(token: string, signal?: AbortSignal): Promise<FlightSla[]> {
  return getJson<FlightSla[]>("/v1/flights/sla", signal, token);
}

export type ExitReview = {
  id: string;
  bagTag: string;
  flightId: string | null;
  gateId: string;
  reason: "PNR_MISMATCH" | "UNKNOWN_TAG" | "NOT_ON_CAROUSEL" | string;
  occurredAtUtc: string;
  status: "OPEN" | "RESOLVED";
  resolution: "CONFIRMED" | "FALSE_ALARM" | null;
  resolvedBy: string | null;
  resolvedAtUtc: string | null;
  note: string | null;
};

export function getReviews(token: string, signal?: AbortSignal): Promise<ExitReview[]> {
  return getJson<ExitReview[]>("/v1/security/reviews", signal, token);
}

export async function resolveReview(token: string, id: string, resolution: "CONFIRMED" | "FALSE_ALARM", note: string): Promise<void> {
  const response = await fetch(`/v1/security/reviews/${encodeURIComponent(id)}/resolve`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${token}` },
    body: JSON.stringify({ resolution, note: note.trim() === "" ? null : note.trim() }),
  });
  if (!response.ok) {
    throw new ApiError(response.status);
  }
}
