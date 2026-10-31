import type { TransferFlight } from "../api";

function utcTime(iso: string): string {
  return new Date(iso).toLocaleTimeString("tr-TR", { hour: "2-digit", minute: "2-digit", timeZone: "UTC" });
}

function cutoffText(f: TransferFlight): string {
  if (f.departedUtc) return `Kalktı ${utcTime(f.departedUtc)}`;
  if (f.cutoffUtc === null) return "–";
  if (f.minutesToCutoff === null) return utcTime(f.cutoffUtc);
  if (f.minutesToCutoff <= 0) return `${utcTime(f.cutoffUtc)} (kapandı)`;
  return `${utcTime(f.cutoffUtc)} (${Math.floor(f.minutesToCutoff)} dk)`;
}

/**
 * Buradan kalkan bağlantı uçuşlarına aktarılan bavullar (W10a). Kabul kapanışı geçtiği halde yüklenmemiş bavul bu
 * uçuşu kaçırmış demektir; satır vurgulanır.
 */
export function TransferPanel({ rows }: { rows: TransferFlight[] }) {
  return (
    <section className="mt-10" aria-labelledby="transfer-title">
      <h2 id="transfer-title" className="font-display text-3xl font-bold">Aktarma bavulları</h2>
      <p className="mt-1 max-w-3xl text-steel">
        Gelen uçuşlardan indirilip bağlantı uçuşuna aktarılan bavullar, bağlantı uçuşuna göre. Kabul kapanışı planlanan kalkıştan önceki son
        yükleme anıdır (demo varsayımı). "Ayrıştırma bekliyor": indirildi, aktarma hattından henüz geçmedi. Saatler UTC.
      </p>
      {rows.length === 0 ? (
        <p className="mt-4 border-2 border-dashed border-line p-4 text-steel">Aktarma bavulu yok.</p>
      ) : (
        <div className="mt-4 overflow-x-auto border-2 border-ink bg-tag">
          <table className="w-full min-w-[44rem] border-collapse text-left">
            <caption className="sr-only">Bağlantı uçuşlarına göre aktarma bavulları</caption>
            <thead>
              <tr className="border-b-2 border-ink text-sm">
                <th scope="col" className="px-3 py-2 font-semibold">Bağlantı uçuşu</th>
                <th scope="col" className="px-3 py-2 font-semibold">Planlanan kalkış</th>
                <th scope="col" className="px-3 py-2 font-semibold">Kabul kapanışı</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Toplam</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Gelmedi</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Ayrıştırma bekliyor</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Aktarmada</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Yüklendi</th>
              </tr>
            </thead>
            <tbody className="tabular">
              {rows.map((f) => {
                const open = f.total - f.loaded - f.offloaded;
                const closed = f.departedUtc !== null || (f.minutesToCutoff !== null && f.minutesToCutoff <= 0);
                const missed = closed && open > 0;
                return (
                  <tr key={f.flightId} className={["border-t border-line", missed ? "bg-concrete" : ""].join(" ")}>
                    <th scope="row" className="px-3 py-2 text-left font-bold">{f.flightId}</th>
                    <td className="px-3 py-2">{f.scheduledDepartureUtc ? utcTime(f.scheduledDepartureUtc) : "–"}</td>
                    <td className="px-3 py-2">{cutoffText(f)}</td>
                    <td className="px-3 py-2 text-right">{f.total}</td>
                    <td className="px-3 py-2 text-right">{f.notArrived}</td>
                    <td className="px-3 py-2 text-right">{f.awaitingSort}</td>
                    <td className="px-3 py-2 text-right">{f.sorted}</td>
                    <td className="px-3 py-2 text-right">
                      {f.loaded}
                      {missed && <span className="ml-2 font-semibold text-alert">{open} yetişmedi</span>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
