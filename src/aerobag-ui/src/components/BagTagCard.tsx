import type { PassengerBag } from "../api";
import { useI18n } from "../i18n";
import { hidesCarousel, showsCarousel, stateMessage } from "../states";
import { Stepper } from "./Stepper";
import { EtaBand } from "./EtaBand";

export function BagTagCard({ bag }: { bag: PassengerBag }) {
  const { m } = useI18n();
  const waiting = bag.eta !== null;
  const carousel = bag.carousel && showsCarousel(bag.state) && !hidesCarousel(bag.notice) ? bag.carousel : null;
  const notice = bag.notice ? m.notices[bag.notice] : null;
  return (
    <article className="relative bg-tag p-5 shadow-[6px_6px_0_0_var(--color-ink)] outline outline-2 outline-ink sm:p-7">
      <div className="absolute left-1/2 top-3 h-4 w-4 -translate-x-1/2 rounded-full bg-concrete outline outline-2 outline-ink" aria-hidden="true" />
      <header className="mt-4 flex flex-wrap items-end justify-between gap-2">
        <div>
          <p className="text-sm text-steel">{m.card.tag}</p>
          <p className="font-display text-4xl font-bold leading-none tabular">{bag.bagTag}</p>
        </div>
        <div className="flex items-end gap-4">
          <p className="text-sm">
            {m.card.flight} <strong className="tabular">{bag.flightId}</strong>
          </p>
          {carousel && (
            <p className="bg-signal px-3 py-1 text-ink outline outline-2 outline-ink" aria-live="polite">
              <span className="block text-xs">{m.card.carousel}</span>
              <span className="block font-display text-3xl font-bold leading-none tabular">{carousel}</span>
            </p>
          )}
        </div>
      </header>

      <p className="mt-5 text-lg font-semibold">{stateMessage(bag.state, carousel, m)}</p>

      {notice && (
        <div role="status" className="mt-4 border-l-4 border-signal bg-concrete p-3">
          <p className="font-semibold">{notice.title}</p>
          <p className="mt-1 text-sm">{notice.body}</p>
        </div>
      )}

      <div className="mt-5">
        <Stepper state={bag.state} />
      </div>

      {waiting && bag.eta && (
        <div className="mt-6">
          <EtaBand eta={bag.eta} />
        </div>
      )}
    </article>
  );
}
