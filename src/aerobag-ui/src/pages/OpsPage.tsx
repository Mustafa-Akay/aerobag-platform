import { useEffect, useRef, useState } from "react";
import {
  ApiError,
  getAlerts,
  getFlights,
  getFlightSla,
  getMlStatus,
  getTransfers,
  type FlightSla,
  type FlightSummary,
  type MlStatus,
  type OpenAlert,
  type TransferFlight,
} from "../api";
import { AlertsPanel } from "../components/AlertsPanel";
import { clearStaff, loadStaff, type StaffSession } from "../auth";
import { StaffLogin } from "../components/StaffLogin";
import { SecurityPanel } from "../components/SecurityPanel";
import { SlaPanel } from "../components/SlaPanel";
import { TransferPanel } from "../components/TransferPanel";
import { clockTime, decimalMinutes } from "../format";
import { useCoalescedTick, useLive } from "../live";

const POLL_FAST_MS = 3000;
const POLL_SLOW_MS = 20000;

const ROLE_TEXT: Record<StaffSession["role"], string> = { ops: "Operasyon", security: "Güvenlik" };

/** ML servisi erişilemezse tahminler sessizce geçmiş verilere düşer; bu satır nedenini görünür kılar (W6). */
function MlStatusLine({ status }: { status: MlStatus }) {
  if (!status.enabled) {
    return <p className="mt-3 text-sm text-steel">ML servisi yapılandırmada kapalı; tahminler geçmiş verilere dayanıyor.</p>;
  }
  if (status.healthy) {
    return <p className="mt-3 text-sm text-steel">ML servisi yanıt veriyor.</p>;
  }
  const when = status.lastFailureUtc ? clockTime(new Date(status.lastFailureUtc)) : "–";
  return (
    <p role="status" className="mt-3 border-l-4 border-signal bg-tag p-3 text-sm">
      <strong>ML servisi yanıt vermiyor</strong> ({status.lastFailureReason ?? "bilinmeyen neden"}, son hata {when}). Tahminler geçmiş
      verilere ya da varsayılan aralığa dayanıyor.
    </p>
  );
}

export function OpsPage() {
  const [session, setSession] = useState<StaffSession | null>(() => loadStaff());
  const [notice, setNotice] = useState<string | null>(null);

  if (!session) {
    return <StaffLogin notice={notice} onLogin={(s) => { setNotice(null); setSession(s); }} />;
  }

  const end = (message: string | null) => {
    clearStaff();
    setNotice(message);
    setSession(null);
  };

  return <OpsDashboard session={session} onExpired={() => end("Oturumunuzun süresi doldu. Tekrar giriş yapın.")} onLogout={() => end(null)} />;
}

function OpsDashboard({ session, onExpired, onLogout }: { session: StaffSession; onExpired: () => void; onLogout: () => void }) {
  const [flights, setFlights] = useState<FlightSummary[] | null>(null);
  const [sla, setSla] = useState<FlightSla[] | null>(null);
  const [alerts, setAlerts] = useState<OpenAlert[] | null>(null);
  const [ml, setMl] = useState<MlStatus | null>(null);
  const [transfers, setTransfers] = useState<TransferFlight[] | null>(null);
  const [error, setError] = useState(false);
  const [updatedAt, setUpdatedAt] = useState<Date | null>(null);

  const [tick, signal] = useCoalescedTick(1000);
  const live = useLive({ kind: "ops" }, session.token, "flightsChanged", signal);
  const expired = useRef(onExpired);
  expired.current = onExpired;
  const pollMs = useRef(POLL_FAST_MS);
  pollMs.current = live === "live" ? POLL_SLOW_MS : POLL_FAST_MS;

  useEffect(() => {
    const controller = new AbortController();
    let timer: number | undefined;
    const run = async () => {
      try {
        setFlights(await getFlights(session.token, controller.signal));
        try {
          setSla(await getFlightSla(session.token, controller.signal));
        } catch {
          if (!controller.signal.aborted) setSla(null);
        }
        try {
          setAlerts(await getAlerts(session.token, controller.signal));
        } catch {
          if (!controller.signal.aborted) setAlerts(null);
        }
        try {
          setMl(await getMlStatus(session.token, controller.signal));
        } catch {
          if (!controller.signal.aborted) setMl(null);
        }
        try {
          setTransfers(await getTransfers(session.token, controller.signal));
        } catch {
          if (!controller.signal.aborted) setTransfers(null);
        }
        setError(false);
        setUpdatedAt(new Date());
      } catch (e) {
        if (controller.signal.aborted) return;
        if (e instanceof ApiError && (e.status === 401 || e.status === 403)) {
          expired.current();
          return;
        }
        setError(true);
      }
      if (!controller.signal.aborted) timer = window.setTimeout(run, pollMs.current);
    };
    void run();
    return () => {
      controller.abort();
      window.clearTimeout(timer);
    };
  }, [tick, session.token]);

  return (
    <div>
      <div className="flex flex-wrap items-baseline justify-between gap-3">
        <h1 className="font-display text-5xl font-bold leading-[0.95]">Operasyon panosu</h1>
        <p className="text-sm text-steel">
          {ROLE_TEXT[session.role]} rolüyle giriş yapıldı.{" "}
          <button type="button" onClick={onLogout} className="font-semibold text-ink underline underline-offset-4">
            Oturumu kapat
          </button>
        </p>
      </div>
      <p className="mt-2 text-steel">Uçuş başına bagaj sayıları, ilk bagaj süresi ve sıra dışı olaylar. "Alındı" banttan alınan tüm bagajları sayar; çıkış sütunları bunların alt kümesidir. "Çıkış okunmadı" bir sonuç değil, inceleme gerektiren durumdur. "İncelemede": çıkışta bavul ile rezervasyon eşleşmedi. "Eksik": açık kayıp bavul uyarısı (aşağıdaki listede). "Aktarma": bağlantı uçuşuna giden bavullar; banda gelmedikleri için varış sütunlarına girmez, toplama girer.</p>

      {ml && <MlStatusLine status={ml} />}

      {error && (
        <p role="alert" className="mt-4 border-l-4 border-alert bg-tag p-3 text-sm text-alert">
          API&apos;ye ulaşılamıyor. docker compose ve API&apos;nin çalıştığından emin olun.
        </p>
      )}

      {flights && flights.length === 0 && <p className="mt-6 border-2 border-dashed border-line p-6 text-steel">Henüz uçuş yok. Simülatörü çalıştırın.</p>}

      {flights && flights.length > 0 && (
        <div className="mt-6 overflow-x-auto bg-ink p-1">
          <table className="w-full min-w-[56rem] border-collapse text-left text-tag">
            <caption className="sr-only">Uçuş özetleri</caption>
            <thead>
              <tr className="text-sm text-signal">
                <th scope="col" className="px-3 py-2 font-semibold">Uçuş</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Bant</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Toplam</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Varış öncesi</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Yolda</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Bantta</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Alındı</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Aktarma</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Çıkış okundu</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Çıkış okunmadı</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">İncelemede</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Eksik</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">Sıra dışı</th>
                <th scope="col" className="px-3 py-2 text-right font-semibold">İlk bagaj (dk)</th>
              </tr>
            </thead>
            <tbody className="font-display text-2xl tabular">
              {flights.map((f) => (
                <tr key={f.flightId} className="border-t border-steel">
                  <th scope="row" className="px-3 py-2 text-left font-bold">{f.flightId}</th>
                  <td className="px-3 py-2 text-right font-bold">{f.carousel ?? "–"}</td>
                  <td className="px-3 py-2 text-right">{f.totalBags}</td>
                  <td className="px-3 py-2 text-right">{f.beforeArrival}</td>
                  <td className="px-3 py-2 text-right">{f.inbound}</td>
                  <td className="px-3 py-2 text-right">{f.onCarousel}</td>
                  <td className="px-3 py-2 text-right">{f.claimed}</td>
                  <td className="px-3 py-2 text-right">{f.transfer}</td>
                  <td className="px-3 py-2 text-right">{f.exitRead}</td>
                  <td className={["px-3 py-2 text-right", f.exitOverdue > 0 ? "font-bold text-signal" : ""].join(" ")}>{f.exitOverdue}</td>
                  <td className={["px-3 py-2 text-right", f.exitReview > 0 ? "font-bold text-signal" : ""].join(" ")}>{f.exitReview}</td>
                  <td className={["px-3 py-2 text-right", f.openAlerts > 0 ? "font-bold text-signal" : ""].join(" ")}>{f.openAlerts}</td>
                  <td className={["px-3 py-2 text-right", f.anomalies > 0 ? "font-bold text-signal" : ""].join(" ")}>{f.anomalies}</td>
                  <td className="px-3 py-2 text-right">{f.firstBagMinutes === null ? "–" : decimalMinutes(f.firstBagMinutes)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {session.role === "security" && <SecurityPanel token={session.token} onExpired={onExpired} />}
      {alerts && <AlertsPanel rows={alerts} />}
      {transfers && <TransferPanel rows={transfers} />}
      {sla && sla.length > 0 && <SlaPanel rows={sla} />}
      {updatedAt && (
        <p className="mt-3 text-xs text-steel">
          Son güncelleme {clockTime(updatedAt)}. {live === "live" ? "Canlı bağlantı açık." : "Sayfa 3 saniyede bir yenilenir."}
        </p>
      )}
    </div>
  );
}
