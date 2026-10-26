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
};

export type LookupResult = {
  asOfUtc: string;
  bags: PassengerBag[];
};

export type FlightSummary = {
  flightId: string;
  totalBags: number;
  beforeArrival: number;
  inbound: number;
  onCarousel: number;
  claimed: number;
  exitRead: number;
  exitOverdue: number;
  anomalies: number;
  firstUnloadUtc: string | null;
  firstCarouselUtc: string | null;
  lastCarouselUtc: string | null;
  firstBagMinutes: number | null;
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

async function getJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const response = await fetch(url, { signal });
  if (!response.ok) {
    throw new ApiError(response.status);
  }
  return (await response.json()) as T;
}

export function lookup(pnr: string, surname: string, signal?: AbortSignal): Promise<LookupResult> {
  const query = `pnr=${encodeURIComponent(pnr)}&surname=${encodeURIComponent(surname)}`;
  return getJson<LookupResult>(`/v1/lookup?${query}`, signal);
}

export function getFlights(signal?: AbortSignal): Promise<FlightSummary[]> {
  return getJson<FlightSummary[]>("/v1/flights", signal);
}

export function getFlightSla(signal?: AbortSignal): Promise<FlightSla[]> {
  return getJson<FlightSla[]>("/v1/flights/sla", signal);
}
