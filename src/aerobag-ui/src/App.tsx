import { useHashRoute } from "./hooks";
import { PassengerPage } from "./pages/PassengerPage";
import { OpsPage } from "./pages/OpsPage";

export function App() {
  const route = useHashRoute();
  const isOps = route.startsWith("/ops");

  return (
    <div className="mx-auto flex min-h-screen max-w-5xl flex-col px-4 sm:px-6">
      <header className="flex items-end justify-between border-b-2 border-ink py-5">
        <a href="#/" className="font-display text-3xl font-bold leading-none tracking-tight">
          AeroBag
        </a>
        <nav className="flex gap-5 text-sm font-semibold">
          <a href="#/" aria-current={!isOps ? "page" : undefined} className={!isOps ? "underline decoration-signal decoration-4 underline-offset-4" : "text-steel"}>
            Bagajım nerede
          </a>
          <a href="#/ops" aria-current={isOps ? "page" : undefined} className={isOps ? "underline decoration-signal decoration-4 underline-offset-4" : "text-steel"}>
            Operasyon panosu
          </a>
        </nav>
      </header>

      <main className="flex-1 py-8">{isOps ? <OpsPage /> : <PassengerPage />}</main>

      <footer className="border-t border-line py-4 text-xs text-steel">
        Simülasyon verisiyle çalışan bir mühendislik projesidir; gerçek bir havalimanı veya havayolu verisi içermez.
      </footer>
    </div>
  );
}
