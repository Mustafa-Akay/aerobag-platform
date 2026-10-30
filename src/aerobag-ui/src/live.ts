import { HubConnectionBuilder, LogLevel, type HubConnection } from "@microsoft/signalr";
import { useCallback, useEffect, useRef, useState } from "react";

export type LiveStatus = "off" | "connecting" | "live" | "offline";

/** keys: "etiket@uçuş" biçiminde bavul anahtarları (bkz. bagKey). Yolcu jetonu bu çiftlere bağlıdır. */
export type LiveTarget = { kind: "ops" } | { kind: "bags"; keys: string[] };

export function bagKey(bagTag: string, flightId: string): string {
  return `${bagTag}@${flightId}`;
}

async function join(connection: HubConnection, target: LiveTarget): Promise<void> {
  if (target.kind === "ops") {
    await connection.invoke("JoinOps");
  } else {
    await connection.invoke("JoinBags", target.keys);
  }
}

/**
 * Sunucudan gelen içeriksiz bildirimi dinler. Bildirim gelince onSignal çağrılır; veriyi istemci yeniden çeker.
 * target ya da token null ise bağlanmaz. Bağlantı kopup geri gelirse gruplara yeniden katılır ve bir yenileme tetikler.
 * Jeton her istekte en güncel değerden okunur; jeton yenilenince bağlantı yeniden kurulmaz.
 */
export function useLive(target: LiveTarget | null, token: string | null, event: string, onSignal: () => void): LiveStatus {
  const [status, setStatus] = useState<LiveStatus>("off");
  const handler = useRef(onSignal);
  handler.current = onSignal;
  const tokenRef = useRef(token);
  tokenRef.current = token;
  const hasToken = token !== null;
  const key = target === null ? "" : target.kind === "ops" ? "ops" : `bags:${[...target.keys].sort().join(",")}`;

  useEffect(() => {
    if (target === null || !hasToken) {
      setStatus("off");
      return;
    }

    let cancelled = false;
    const connection = new HubConnectionBuilder()
      .withUrl("/hubs/live", { accessTokenFactory: () => tokenRef.current ?? "" })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on(event, () => handler.current());
    connection.onreconnecting(() => !cancelled && setStatus("connecting"));
    connection.onreconnected(async () => {
      try {
        await join(connection, target);
        if (!cancelled) {
          setStatus("live");
          handler.current();
        }
      } catch {
        if (!cancelled) setStatus("offline");
      }
    });
    connection.onclose(() => !cancelled && setStatus("offline"));

    setStatus("connecting");
    connection
      .start()
      .then(() => join(connection, target))
      .then(() => !cancelled && setStatus("live"))
      .catch(() => !cancelled && setStatus("offline"));

    return () => {
      cancelled = true;
      void connection.stop();
    };
    // target içeriği key ile temsil edilir.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, event, hasToken]);

  return status;
}

/** Sık gelen bildirimleri birleştirir: delayMs içinde kaç bildirim gelirse gelsin tek bir tick üretir. */
export function useCoalescedTick(delayMs: number): [number, () => void] {
  const [tick, setTick] = useState(0);
  const timer = useRef<number | undefined>(undefined);

  useEffect(() => () => window.clearTimeout(timer.current), []);

  const signal = useCallback(() => {
    if (timer.current !== undefined) return;
    timer.current = window.setTimeout(() => {
      timer.current = undefined;
      setTick((t) => t + 1);
    }, delayMs);
  }, [delayMs]);

  return [tick, signal];
}
