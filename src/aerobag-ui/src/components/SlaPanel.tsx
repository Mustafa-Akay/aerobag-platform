import type { FlightSla, SlaLeg, SlaStatus } from "../api";
import { minutesText } from "../format";

const STATUS: Record<SlaStatus, { text: string; tone: string }> = {
  MET: { text: "Hedefte", tone: "text-ok" },
  ON_TRACK: { text: "Yolunda", tone: "text-ok" },
  AT_RISK: { text: "Risk altında", tone: "text-ink" },
  LIKELY_BREACH: { text: "Büyük olasılıkla aşılacak", tone: "text-alert" },
  BREACHED: { text: "Aşıldı", tone: "text-alert" },
  UNKNOWN: { text: "Tahmin yok", tone: "text-steel" },
};

function percent(p: number | null): string {
  return p === null ? "–" : `%${Math.round(p * 100)}`;
}

function Leg({ leg }: { leg: SlaLeg }) {
  const s = STATUS[leg.status];
  const measured = leg.actualSec !== null;
  return (
    <td className="px-3 py-3 align-top">
      <div className={["font-bold", s.tone].join(" ")}>{s.text}</div>
      <div className="text-sm text-steel">Hedef {minutesText(leg.targetSec)}</div>
      {measured ? (
        <div className="text-sm">Gerçekleşen {minutesText(leg.actualSec!)}</div>
      ) : (
        <div className="text-sm">
          {leg.projectedP50Sec !== null ? `Tahmin ${minutesText(leg.projectedP50Sec)} (en geç ${minutesText(leg.projectedP90Sec ?? leg.projectedP50Sec)})` : "Tahmin yok"}
        </div>
      )}
      {!measured && <div className="text-sm">Aşma olasılığı {percent(leg.breachProbability)}</div>}
      {leg.projectedDelayP50Sec !== null && leg.projectedDelayP50Sec > 0 && (
        <div className="text-sm font-semibold">
          {measured ? "Gecikme" : "Beklenen gecikme"} {minutesText(leg.projectedDelayP50Sec)}
          {!measured && leg.projectedDelayP90Sec !== null && leg.projectedDelayP90Sec > leg.projectedDelayP50Sec
            ? ` (en kötü ${minutesText(leg.projectedDelayP90Sec)})`
            : ""}
        </div>
      )}
    </td>
  );
}

export function SlaPanel({ rows }: { rows: FlightSla[] }) {
  return (
    <section className="mt-10" aria-labelledby="sla-title">
      <h2 id="sla-title" className="font-display text-3xl font-bold">Teslim hedefi ve gecikme riski</h2>
      <p className="mt-1 max-w-3xl text-steel">
        Süreler uçağın park yerine girdiği andan (on-block) ölçülür. Hedefler (ilk bagaj 15 dk, son bagaj 30 dk) bu demo için varsayılmıştır, gerçek bir sözleşme değildir.
        Tahminler modelin aralığından gelir; son bagaj tahmini uçuştaki tüm bagajlar banda gelince gerçek değere dönüşür.
      </p>
      <div className="mt-4 overflow-x-auto border-2 border-ink bg-tag">
        <table className="w-full min-w-[44rem] border-collapse text-left">
          <caption className="sr-only">Uçuş başına ilk ve son bagaj hedefi</caption>
          <thead>
            <tr className="border-b-2 border-ink text-sm">
              <th scope="col" className="px-3 py-2 font-semibold">Uçuş</th>
              <th scope="col" className="px-3 py-2 font-semibold">İlk bagaj</th>
              <th scope="col" className="px-3 py-2 font-semibold">Son bagaj</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((r) => (
              <tr key={r.flightId} className="border-t border-line">
                <th scope="row" className="px-3 py-3 text-left align-top font-display text-xl font-bold">{r.flightId}</th>
                <Leg leg={r.firstBag} />
                <Leg leg={r.lastBag} />
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}
