import { STEPS, stepIndex } from "../states";

export function Stepper({ state }: { state: string }) {
  const current = stepIndex(state);
  return (
    <ol className="grid grid-cols-7 gap-1" aria-label="Bagaj yolculuğu">
      {STEPS.map((step, i) => {
        const done = i < current;
        const active = i === current;
        return (
          <li key={step.state} className="flex flex-col items-center gap-2 text-center" aria-current={active ? "step" : undefined}>
            <span
              className={[
                "h-3 w-full",
                done ? "bg-ink" : active ? "bg-signal outline outline-2 outline-ink" : "bg-line",
              ].join(" ")}
            />
            <span className={["text-[11px] leading-tight sm:text-xs", active ? "font-semibold text-ink" : done ? "text-ink" : "text-steel"].join(" ")}>
              {step.short}
            </span>
          </li>
        );
      })}
    </ol>
  );
}
