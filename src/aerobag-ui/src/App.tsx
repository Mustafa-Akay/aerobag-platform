import { useHashRoute } from "./hooks";
import { useI18n } from "./i18n";
import { LanguageSwitch } from "./components/LanguageSwitch";
import { PassengerPage } from "./pages/PassengerPage";
import { OpsPage } from "./pages/OpsPage";

export function App() {
  const route = useHashRoute();
  const isOps = route.startsWith("/ops");
  const { m } = useI18n();

  return (
    <div className="mx-auto flex min-h-screen max-w-5xl flex-col px-4 sm:px-6">
      <header className="flex flex-wrap items-end justify-between gap-x-5 gap-y-3 border-b-2 border-ink py-5">
        <a href="#/" className="font-display text-3xl font-bold leading-none tracking-tight">
          AeroBag
        </a>
        <div className="flex flex-wrap items-center gap-x-5 gap-y-2">
          <nav className="flex gap-5 text-sm font-semibold">
            <a href="#/" aria-current={!isOps ? "page" : undefined} className={!isOps ? "underline decoration-signal decoration-4 underline-offset-4" : "text-steel"}>
              {m.nav.passenger}
            </a>
            <a href="#/ops" aria-current={isOps ? "page" : undefined} className={isOps ? "underline decoration-signal decoration-4 underline-offset-4" : "text-steel"}>
              {m.nav.ops}
            </a>
          </nav>
          {/* Personel ekranları şimdilik yalnızca Türkçe; dil seçici yolcu ekranında. */}
          {!isOps && <LanguageSwitch />}
        </div>
      </header>

      <main className="flex-1 py-8">{isOps ? <OpsPage /> : <PassengerPage />}</main>

      <footer className="border-t border-line py-4 text-xs text-steel">{m.footer}</footer>
    </div>
  );
}
