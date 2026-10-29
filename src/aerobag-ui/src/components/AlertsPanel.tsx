import type { OpenAlert } from "../api";
import { stateLabel } from "../states";

const KIND_TEXT: Record<OpenAlert["kind"], string> = {
  NOT_LOADED: "Yükleme kaydı yok",
  MISSING_AT_ARRIVAL: "Varışta okunmadı",
};

function utcTime(iso: string): string {
  return new Date(iso).toLocaleTimeString("tr-TR", { hour: "2-digit", minute: "2-digit", timeZone: "UTC" });
}

/** Açık kayıp bavul uyarıları (W4). Kayıp bavul bir durum değil, uyarıdır: bavul okunursa listeden kendiliğinden düşer. */
export function AlertsPanel({ rows }: { rows: OpenAlert[] }) {
  return (
    <section className="mt-10" aria-labelledby="alerts-title">
      <h2 id="alerts-title" className="font-display text-3xl font-bold">Eksik bavullar</h2>
      <p className="mt-1 max-w-3xl text-steel">
        Kalkıştan sonra yükleme okuması olmayan ya da varışta indirme bittiği halde okunmayan bavullar. Okuyucu kaçırması da aynı izi
        bırakır; bavul sonradan okunursa uyarı kapanır. Saatler operasyon saatine göredir (UTC).
      </p>
      {rows.length === 0 ? (
        <p className="mt-4 border-2 border-dashed border-line p-4 text-steel">Açık uyarı yok.</p>
      ) : (
        <div className="mt-4 overflow-x-auto border-2 border-ink bg-tag">
          <table className="w-full min-w-[36rem] border-collapse text-left">
            <caption className="sr-only">Açık kayıp bavul uyarıları</caption>
            <thead>
              <tr className="border-b-2 border-ink text-sm">
                <th scope="col" className="px-3 py-2 font-semibold">Bagaj etiketi</th>
                <th scope="col" className="px-3 py-2 font-semibold">Uçuş</th>
                <th scope="col" className="px-3 py-2 font-semibold">Uyarı</th>
                <th scope="col" className="px-3 py-2 font-semibold">Son okuma</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Açıldı</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((a) => (
                <tr key={`${a.bagTag}-${a.flightId}-${a.kind}`} className="border-t border-line">
                  <th scope="row" className="px-3 py-2 text-left font-bold tabular">{a.bagTag}</th>
                  <td className="px-3 py-2">{a.flightId}</td>
                  <td className="px-3 py-2 font-semibold">{KIND_TEXT[a.kind]}</td>
                  <td className="px-3 py-2">{stateLabel(a.state)}</td>
                  <td className="px-3 py-2 text-right tabular">{utcTime(a.openedAtUtc)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}
