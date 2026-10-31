/** Yatay adım çubuğu. Etiketler ve şimdiki adım dışarıdan gelir (normal ve aktarma bavulu farklı adımlar gösterir). */
export function Stepper({ labels, current, ariaLabel }: { labels: readonly string[]; current: number; ariaLabel: string }) {
  return (
    <ol className="grid gap-1" style={{ gridTemplateColumns: `repeat(${labels.length}, minmax(0, 1fr))` }} aria-label={ariaLabel}>
      {labels.map((label, i) => {
        const done = i < current;
        const active = i === current;
        return (
          <li key={label} className="flex flex-col items-center gap-2 text-center" aria-current={active ? "step" : undefined}>
            <span
              className={[
                "h-3 w-full",
                done ? "bg-ink" : active ? "bg-signal outline outline-2 outline-ink" : "bg-line",
              ].join(" ")}
            />
            <span className={["text-[11px] leading-tight sm:text-xs", active ? "font-semibold text-ink" : done ? "text-ink" : "text-steel"].join(" ")}>
              {label}
            </span>
          </li>
        );
      })}
    </ol>
  );
}
