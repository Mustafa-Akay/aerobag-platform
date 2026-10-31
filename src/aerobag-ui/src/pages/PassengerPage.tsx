import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { ApiError, lookup, type LookupResult } from "../api";
import { BagTagCard } from "../components/BagTagCard";
import { clockTime } from "../format";
import { useI18n } from "../i18n";
import { bagKey, useCoalescedTick, useLive } from "../live";

type Query = { pnr: string; surname: string };
const POLL_FAST_MS = 3000; // canlı bağlantı yokken
const POLL_SLOW_MS = 20000; // canlı bağlantı varken yalnızca güvenlik ağı
const LOOKUP_LIMIT_PER_MIN = 20; // sunucudaki hız sınırı; birleştirme aralığı buna göre seçildi

/** Hata metni değil türü saklanır: dil değişince açık hata mesajı da yeni dilde görünür. */
type ErrorKind = "notFound" | "tooMany" | "network" | "missingInput";

function errorKind(error: unknown): ErrorKind {
  if (error instanceof ApiError) {
    if (error.status === 404) return "notFound";
    if (error.status === 429) return "tooMany";
  }
  return "network";
}

const STORAGE_KEY = "aerobag.lastQuery";

/** Sayfalar arası geçişte aramayı korumak için; yalnızca bu sekmenin oturumunda tutulur. */
function loadSaved(): Query | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const value = JSON.parse(raw) as Partial<Query>;
    return typeof value.pnr === "string" && typeof value.surname === "string" ? { pnr: value.pnr, surname: value.surname } : null;
  } catch {
    return null;
  }
}

function save(query: Query): void {
  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(query));
  } catch {
    // Depolama kapalıysa arama yine çalışır, yalnızca hatırlanmaz.
  }
}

export function PassengerPage() {
  const { lang, m } = useI18n();
  const saved = loadSaved();
  const [pnr, setPnr] = useState(saved?.pnr ?? "");
  const [surname, setSurname] = useState(saved?.surname ?? "");
  const [query, setQuery] = useState<Query | null>(saved);
  const [result, setResult] = useState<LookupResult | null>(null);
  const [error, setError] = useState<ErrorKind | null>(null);
  const [loading, setLoading] = useState(false);
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null);

  const [tick, signal] = useCoalescedTick((60_000 / LOOKUP_LIMIT_PER_MIN) * 1.5);
  // Aktarma bavulunda iki satırın da (gelen ve bağlantı uçuşu) bildirimi dinlenir (W10a).
  const keys = useMemo(
    () => [...new Set(result?.bags.flatMap((b) => [bagKey(b.bagTag, b.flightId), ...(b.onwardFlightId ? [bagKey(b.bagTag, b.onwardFlightId)] : [])]) ?? [])],
    [result],
  );
  const live = useLive(keys.length > 0 ? { kind: "bags", keys } : null, result?.accessToken ?? null, "bagChanged", signal);
  const pollMs = useRef(POLL_FAST_MS);
  pollMs.current = live === "live" ? POLL_SLOW_MS : POLL_FAST_MS;

  useEffect(() => {
    if (!query) return;
    const controller = new AbortController();
    let timer: number | undefined;

    const run = async () => {
      try {
        const data = await lookup(query.pnr, query.surname, controller.signal);
        setResult(data);
        setError(null);
        setUpdatedAt(new Date());
        timer = window.setTimeout(run, pollMs.current);
      } catch (e) {
        if (controller.signal.aborted) return;
        setResult(null);
        setError(errorKind(e));
        // 404/429 sonrası otomatik yeniden denemeyi durdur; ağ hatasında sürdür.
        if (!(e instanceof ApiError)) timer = window.setTimeout(run, POLL_FAST_MS * 2);
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    };

    setLoading(true);
    void run();
    return () => {
      controller.abort();
      window.clearTimeout(timer);
    };
  }, [query, tick]);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const p = pnr.trim().toUpperCase();
    const s = surname.trim();
    if (!p || !s) {
      setError("missingInput");
      return;
    }
    setResult(null);
    setError(null);
    const next = { pnr: p, surname: s };
    save(next);
    setQuery(next);
  };

  return (
    <div className="grid gap-8 md:grid-cols-[minmax(0,20rem)_1fr]">
      <section aria-labelledby="form-title">
        <h1 id="form-title" className="font-display text-5xl font-bold leading-[0.95]">
          {m.passenger.title}
        </h1>
        <p className="mt-3 max-w-prose text-steel">{m.passenger.intro}</p>

        <form onSubmit={submit} className="mt-6 grid gap-4" noValidate>
          <label className="grid gap-1 text-sm font-semibold">
            {m.passenger.pnr}
            <input
              value={pnr}
              onChange={(e) => setPnr(e.target.value)}
              autoCapitalize="characters"
              autoComplete="off"
              maxLength={8}
              className="border-2 border-ink bg-tag px-3 py-2 font-display text-2xl uppercase tracking-widest"
            />
          </label>
          <label className="grid gap-1 text-sm font-semibold">
            {m.passenger.surname}
            <input
              value={surname}
              onChange={(e) => setSurname(e.target.value)}
              autoComplete="family-name"
              maxLength={40}
              className="border-2 border-ink bg-tag px-3 py-2 text-lg"
            />
          </label>
          <button type="submit" className="border-2 border-ink bg-signal px-4 py-3 text-lg font-semibold hover:bg-ink hover:text-signal">
            {m.passenger.submit}
          </button>
        </form>

        {error && (
          <p role="alert" className="mt-4 border-l-4 border-alert bg-tag p-3 text-sm text-alert">
            {m.passenger[error]}
          </p>
        )}
      </section>

      <section aria-live="polite" aria-label={m.passenger.resultsLabel} className="grid content-start gap-6">
        {loading && !result && <p className="text-steel">{m.passenger.searching}</p>}
        {!query && !loading && <p className="border-2 border-dashed border-line p-6 text-steel">{m.passenger.empty}</p>}
        {result?.bags.map((bag) => (
          <BagTagCard key={`${bag.bagTag}-${bag.flightId}`} bag={bag} />
        ))}
        {result && updatedAt && <p className="text-xs text-steel">{m.passenger.updated(clockTime(updatedAt, lang), live === "live")}</p>}
      </section>
    </div>
  );
}
