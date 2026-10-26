import type { EtaView } from "../api";
import { minutesText } from "../format";

const BASIS_NOTE: Record<EtaView["basis"], string> = {
  ML: "Bu tahmin, uçuşun özelliklerine (gövde tipi, hava, mesafe, bavul sayısı) bakan ve son bavulların gerçek sürelerine göre kendini güncelleyen bir makine öğrenmesi modelinden geliyor.",
  ML_FLIGHT:
    "Bu tahmin, uçuşun özelliklerine bakan bir makine öğrenmesi modelinden geliyor. Uçuştaki ilk bavuldan sonuncuya kadar olan aralığı kapsar; bavulunuzun sırası bilinmediği için geniştir.",
  PHYSICS: "Bu tahmin, şuttan banta olan mesafe ve bant hızından hesaplandı; yaklaşık 15 saniyelik bir pay içerir.",
  HISTORY: "Bu tahmin, simülasyondaki geçmiş uçuşların gerçekleşen sürelerinden hesaplandı.",
  FALLBACK: "Yeterli geçmiş veri yok; tahmin varsayılan bir aralığa dayanıyor ve geniş olabilir.",
};

export function EtaBand({ eta }: { eta: EtaView }) {
  const early = minutesText(eta.p10SecondsFromNow);
  const late = minutesText(eta.p90SecondsFromNow);
  const mid = minutesText(eta.p50SecondsFromNow);
  const total = Math.max(eta.p90SecondsFromNow, 1);
  const left = Math.min(100, Math.max(0, (eta.p10SecondsFromNow / total) * 100));
  const mark = Math.min(100, Math.max(0, (eta.p50SecondsFromNow / total) * 100));

  return (
    <section aria-labelledby="eta-title" className="border-t-2 border-dashed border-ink pt-5">
      <h3 id="eta-title" className="text-sm font-semibold text-steel">
        Bantta görünme tahmini
      </h3>
      <p className="mt-1 font-display text-5xl font-bold leading-none tabular">
        {early} – {late}
      </p>
      <p className="mt-2 text-sm">
        En olası süre <strong className="tabular">{mid}</strong>. Aralığın dışına çıkma ihtimali düşüktür ama sıfır değildir.
      </p>

      <div className="relative mt-4 h-4 bg-line" role="img" aria-label={`Tahmin aralığı ${early} ile ${late} arası, en olası ${mid}`}>
        <div className="absolute inset-y-0 bg-signal outline outline-2 outline-ink" style={{ left: `${left}%`, right: 0 }} />
        <div className="absolute -inset-y-1 w-1 bg-ink" style={{ left: `calc(${mark}% - 2px)` }} />
      </div>

      <p className="mt-3 text-xs text-steel">
        {BASIS_NOTE[eta.basis]}
      </p>
    </section>
  );
}
