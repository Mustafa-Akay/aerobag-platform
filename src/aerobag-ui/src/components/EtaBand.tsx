import type { EtaView } from "../api";
import { minutesText } from "../format";
import { useI18n } from "../i18n";

export function EtaBand({ eta }: { eta: EtaView }) {
  const { lang, m } = useI18n();
  const early = minutesText(eta.p10SecondsFromNow, lang);
  const late = minutesText(eta.p90SecondsFromNow, lang);
  const mid = minutesText(eta.p50SecondsFromNow, lang);
  const total = Math.max(eta.p90SecondsFromNow, 1);
  const left = Math.min(100, Math.max(0, (eta.p10SecondsFromNow / total) * 100));
  const mark = Math.min(100, Math.max(0, (eta.p50SecondsFromNow / total) * 100));

  return (
    <section aria-labelledby="eta-title" className="border-t-2 border-dashed border-ink pt-5">
      <h3 id="eta-title" className="text-sm font-semibold text-steel">
        {m.eta.title}
      </h3>
      <p className="mt-1 font-display text-5xl font-bold leading-none tabular">
        {early} – {late}
      </p>
      <p className="mt-2 text-sm">
        {m.eta.likely} <strong className="tabular">{mid}</strong>. {m.eta.caveat}
      </p>

      <div className="relative mt-4 h-4 bg-line" role="img" aria-label={m.eta.range(early, late, mid)}>
        <div className="absolute inset-y-0 bg-signal outline outline-2 outline-ink" style={{ left: `${left}%`, right: 0 }} />
        <div className="absolute -inset-y-1 w-1 bg-ink" style={{ left: `calc(${mark}% - 2px)` }} />
      </div>

      <p className="mt-3 text-xs text-steel">
        {m.eta.basis[eta.basis]}
      </p>
    </section>
  );
}
