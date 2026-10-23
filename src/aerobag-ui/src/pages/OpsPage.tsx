import { useEffect, useState } from "react";
import { getFlights, type FlightSummary } from "../api";
import { clockTime, decimalMinutes } from "../format";

const POLL_MS = 3000;

export function OpsPage() {
  const [flights, setFlights] = useState<FlightSummary[] | null>(null);
  const [error, setError] = useState(false);
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    let timer: number | undefined;
    const run = async () => {
      try {
        setFlights(await getFlights(controller.signal));
        setError(false);
        setUpdatedAt(new Date());
      } catch {
        if (!controller.signal.aborted) setError(true);
      }
      if (!controller.signal.aborted) timer = window.setTimeout(run, POLL_MS);
    };
    void run();
    return () => {
      controller.abort();
      window.clearTimeout(timer);
    };
  }, []);

  return (
    <div>
      <h1 className="font-display text-5xl font-bold leading-[0.95]">Operasyon panosu</h1>
      <p className="mt-2 text-steel">Uçuş başına bagaj sayıları, ilk bagaj süresi ve sıra dışı olaylar.</p>

      {error && (
        <p role="alert" className="mt-4 border-l-4 border-alert bg-tag p-3 text-sm text-alert">
          API&apos;ye ulaşılamıyor. docker compose ve API&apos;nin çalıştığından emin olun.
        </p>
      )}

      {flights && flights.length === 0 && <p className="mt-6 border-2 border-dashed border-line p-6 text-steel">Henüz uçuş yok. Simülatörü çalıştırın.</p>}

      {flights && flights.length > 0 && (
        <div className="mt-6 overflow-x-auto bg-ink p-1">
          <table className="w-full min-w-[40rem] border-collapse text-left text-tag">
            <caption className="sr-only">Uçuş özetleri</caption>
            <thead>
              <tr className="text-sm text-signal">
                <th scope="col" className="px-3 py-2 font-semibold">Uçuş</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Toplam</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Varış öncesi</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Yolda</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Bantta</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Alındı</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Sıra dışı</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">İlk bagaj (dk)</th>
              </tr>
            </thead>
            <tbody className="font-display text-2xl tabular">
              {flights.map((f) => (
                <tr key={f.flightId} className="border-t border-steel">
                  <th scope="row" className="px-3 py-2 text-left font-bold">{f.flightId}</th>
                  <td className="px-3 py-2 text-right">{f.totalBags}</td>
                  <td className="px-3 py-2 text-right">{f.beforeArrival}</td>
                  <td className="px-3 py-2 text-right">{f.inbound}</td>
                  <td className="px-3 py-2 text-right">{f.onCarousel}</td>
                  <td className="px-3 py-2 text-right">{f.claimed}</td>
                  <td className={["px-3 py-2 text-right", f.anomalies > 0 ? "font-bold text-signal" : ""].join(" ")}>{f.anomalies}</td>
                  <td className="px-3 py-2 text-right">{f.firstBagMinutes === null ? "–" : decimalMinutes(f.firstBagMinutes)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {updatedAt && <p className="mt-3 text-xs text-steel">Son güncelleme {clockTime(updatedAt)}.</p>}
    </div>
  );
}
