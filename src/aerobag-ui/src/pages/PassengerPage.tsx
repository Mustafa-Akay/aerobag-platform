import { useEffect, useState, type FormEvent } from "react";
import { ApiError, lookup, type LookupResult } from "../api";
import { BagTagCard } from "../components/BagTagCard";
import { clockTime } from "../format";

type Query = { pnr: string; surname: string };
const POLL_MS = 3000;

function errorText(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 404) return "Bu rezervasyon kodu ve soyad ile eşleşen bir bagaj bulamadık. Bilgileri kontrol edip tekrar deneyin.";
    if (error.status === 429) return "Çok fazla deneme yaptınız. Bir dakika bekleyip tekrar deneyin.";
  }
  return "Sunucuya ulaşamadık. Bağlantınızı kontrol edip tekrar deneyin.";
}

export function PassengerPage() {
  const [pnr, setPnr] = useState("");
  const [surname, setSurname] = useState("");
  const [query, setQuery] = useState<Query | null>(null);
  const [result, setResult] = useState<LookupResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null);

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
        timer = window.setTimeout(run, POLL_MS);
      } catch (e) {
        if (controller.signal.aborted) return;
        setResult(null);
        setError(errorText(e));
        // 404/429 sonrası otomatik yeniden denemeyi durdur; ağ hatasında sürdür.
        if (!(e instanceof ApiError)) timer = window.setTimeout(run, POLL_MS * 2);
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
  }, [query]);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const p = pnr.trim().toUpperCase();
    const s = surname.trim();
    if (!p || !s) {
      setError("Rezervasyon kodunu ve soyadı girin.");
      return;
    }
    setResult(null);
    setError(null);
    setQuery({ pnr: p, surname: s });
  };

  return (
    <div className="grid gap-8 md:grid-cols-[minmax(0,20rem)_1fr]">
      <section aria-labelledby="form-title">
        <h1 id="form-title" className="font-display text-5xl font-bold leading-[0.95]">
          Bagajım ne zaman gelir?
        </h1>
        <p className="mt-3 max-w-prose text-steel">Rezervasyon kodunuzu ve soyadınızı girin; bagajınızın nerede olduğunu ve banda ne zaman çıkacağını görün.</p>

        <form onSubmit={submit} className="mt-6 grid gap-4" noValidate>
          <label className="grid gap-1 text-sm font-semibold">
            Rezervasyon kodu (PNR)
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
            Soyad
            <input
              value={surname}
              onChange={(e) => setSurname(e.target.value)}
              autoComplete="family-name"
              maxLength={40}
              className="border-2 border-ink bg-tag px-3 py-2 text-lg"
            />
          </label>
          <button type="submit" className="border-2 border-ink bg-signal px-4 py-3 text-lg font-semibold hover:bg-ink hover:text-signal">
            Bagajımı bul
          </button>
        </form>

        {error && (
          <p role="alert" className="mt-4 border-l-4 border-alert bg-tag p-3 text-sm text-alert">
            {error}
          </p>
        )}
      </section>

      <section aria-live="polite" aria-label="Bagaj durumu" className="grid content-start gap-6">
        {loading && !result && <p className="text-steel">Bagajınız aranıyor…</p>}
        {!query && !loading && <p className="border-2 border-dashed border-line p-6 text-steel">Sonuçlar burada görünecek.</p>}
        {result?.bags.map((bag) => (
          <BagTagCard key={`${bag.bagTag}-${bag.flightId}`} bag={bag} />
        ))}
        {result && updatedAt && <p className="text-xs text-steel">Son güncelleme {clockTime(updatedAt)}. Sayfa kendiliğinden yenilenir.</p>}
      </section>
    </div>
  );
}
