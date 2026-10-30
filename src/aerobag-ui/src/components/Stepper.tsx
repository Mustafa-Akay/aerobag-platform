import { useI18n } from "../i18n";
import { STEPS, stepIndex } from "../states";

export function Stepper({ state }: { state: string }) {
  const { m } = useI18n();
  const current = stepIndex(state);
  return (
    <ol className="grid grid-cols-7 gap-1" aria-label={m.stepperLabel}>
      {STEPS.map((step, i) => {
        const done = i < current;
        const active = i === current;
        return (
          <li key={step} className="flex flex-col items-center gap-2 text-center" aria-current={active ? "step" : undefined}>
            <span
              className={[
                "h-3 w-full",
                done ? "bg-ink" : active ? "bg-signal outline outline-2 outline-ink" : "bg-line",
              ].join(" ")}
            />
            <span className={["text-[11px] leading-tight sm:text-xs", active ? "font-semibold text-ink" : done ? "text-ink" : "text-steel"].join(" ")}>
              {m.steps[step].short}
            </span>
          </li>
        );
      })}
    </ol>
  );
}
