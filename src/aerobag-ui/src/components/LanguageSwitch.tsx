import { useI18n, type Lang } from "../i18n";

const OPTIONS: { lang: Lang; label: string; name: string }[] = [
  { lang: "tr", label: "TR", name: "Türkçe" },
  { lang: "en", label: "EN", name: "English" },
];

/** Yolcu ekranının dil seçici. Dil adları her zaman kendi dilinde okunur (ekran okuyucu için lang özniteliğiyle). */
export function LanguageSwitch() {
  const { lang, setLang, m } = useI18n();
  return (
    <div role="group" aria-label={m.language.label} className="flex outline outline-2 outline-ink">
      {OPTIONS.map((o) => {
        const active = o.lang === lang;
        return (
          <button
            key={o.lang}
            type="button"
            lang={o.lang}
            aria-label={o.name}
            aria-pressed={active}
            onClick={() => setLang(o.lang)}
            className={["px-2 py-0.5 text-xs font-semibold", active ? "bg-ink text-tag" : "bg-tag text-ink hover:bg-signal"].join(" ")}
          >
            {o.label}
          </button>
        );
      })}
    </div>
  );
}
