import { useEffect, useRef, useState } from "react";
import { ApiError, getReviews, resolveReview, type ExitReview } from "../api";
import { clockTime } from "../format";

const REASON: Record<string, string> = {
  PNR_MISMATCH: "Sunulan rezervasyon bavulla eşleşmedi",
  UNKNOWN_TAG: "Etiket sistemde yok",
  NOT_ON_CAROUSEL: "Bavul banda gelmeden çıkışta okundu",
};

const POLL_MS = 5000;

/**
 * Güvenlik ekranı: açık inceleme kayıtları. Kayıtlarda kişisel veri yoktur (etiket, uçuş, kapı, zaman, gerekçe).
 * Kapıda alarm yoktur; sonucu personel verir.
 */
export function SecurityPanel({ token, onExpired }: { token: string; onExpired: () => void }) {
  const [reviews, setReviews] = useState<ExitReview[] | null>(null);
  const [notes, setNotes] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [refresh, setRefresh] = useState(0);
  const expired = useRef(onExpired);
  expired.current = onExpired;

  useEffect(() => {
    const controller = new AbortController();
    let timer: number | undefined;
    const run = async () => {
      try {
        setReviews(await getReviews(token, controller.signal));
        setError(null);
      } catch (e) {
        if (controller.signal.aborted) return;
        if (e instanceof ApiError && (e.status === 401 || e.status === 403)) {
          expired.current();
          return;
        }
        setError("İnceleme kayıtları alınamadı.");
      }
      if (!controller.signal.aborted) timer = window.setTimeout(run, POLL_MS);
    };
    void run();
    return () => {
      controller.abort();
      window.clearTimeout(timer);
    };
  }, [token, refresh]);

  const resolve = async (id: string, resolution: "CONFIRMED" | "FALSE_ALARM") => {
    setBusy(id);
    try {
      await resolveReview(token, id, resolution, notes[id] ?? "");
      setRefresh((r) => r + 1);
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) setError("Bu kayıt başka biri tarafından sonuçlandırılmış.");
      else setError("Kayıt sonuçlandırılamadı.");
      setRefresh((r) => r + 1);
    } finally {
      setBusy(null);
    }
  };

  return (
    <section className="mt-10" aria-labelledby="reviews-title">
      <h2 id="reviews-title" className="font-display text-3xl font-bold">Çıkış inceleme kayıtları</h2>
      <p className="mt-1 max-w-3xl text-steel">
        Çıkış kapısında bavul ile sunulan rezervasyon eşleşmediğinde ya da sıra dışı bir okuma olduğunda burada bir kayıt açılır. Kapıda alarm çalmaz;
        kaydı inceleyip sonucunu siz verirsiniz. Kayıtlarda yolcu adı ya da rezervasyon kodu yoktur.
      </p>
      {error && (
        <p role="alert" className="mt-3 border-l-4 border-alert bg-tag p-3 text-sm text-alert">
          {error}
        </p>
      )}
      {reviews && reviews.length === 0 && <p className="mt-4 border-2 border-dashed border-line p-6 text-steel">Açık inceleme kaydı yok.</p>}
      {reviews && reviews.length > 0 && (
        <ul className="mt-4 grid gap-3">
          {reviews.map((r) => (
            <li key={r.id} className="grid gap-3 border-2 border-ink bg-tag p-4 sm:grid-cols-[1fr_auto]">
              <div>
                <p className="font-display text-xl font-bold tabular">{r.bagTag}</p>
                <p className="text-sm">{REASON[r.reason] ?? r.reason}</p>
                <p className="text-sm text-steel">
                  {r.flightId ?? "Uçuş bilinmiyor"}, {r.gateId}, {clockTime(new Date(r.occurredAtUtc))}
                </p>
                <label className="mt-2 grid gap-1 text-sm font-semibold">
                  Not (isteğe bağlı)
                  <input
                    value={notes[r.id] ?? ""}
                    onChange={(e) => setNotes((n) => ({ ...n, [r.id]: e.target.value }))}
                    maxLength={200}
                    className="border-2 border-ink bg-concrete px-2 py-1 font-normal"
                  />
                </label>
              </div>
              <div className="flex flex-wrap items-end gap-2 sm:flex-col sm:items-stretch">
                <button type="button" disabled={busy === r.id} onClick={() => void resolve(r.id, "FALSE_ALARM")}
                  className="border-2 border-ink px-3 py-2 text-sm font-semibold hover:bg-ink hover:text-tag disabled:opacity-60">
                  Yanlış alarm
                </button>
                <button type="button" disabled={busy === r.id} onClick={() => void resolve(r.id, "CONFIRMED")}
                  className="border-2 border-alert bg-alert px-3 py-2 text-sm font-semibold text-tag hover:opacity-90 disabled:opacity-60">
                  Doğrulandı
                </button>
              </div>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
