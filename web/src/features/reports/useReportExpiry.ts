import { useEffect, useRef, useState } from "react";
import { utcMicroseconds } from "../../utcTimestamp.ts";
import type { ReportRun } from "./reportsApi.ts";

/** A verified receipt is usable only before its half-open UTC expiry boundary. */
export function useReportExpiry(run: ReportRun | undefined) {
  const key = run ? `${run.runId}:${run.expiresAtUtc}` : undefined;
  const receipt = useRef<{ key: string | undefined; expired: boolean }>({ key, expired: false });
  if (receipt.current.key !== key) receipt.current = { key, expired: false };
  const [, setClock] = useState(Date.now);
  const end = run ? utcMicroseconds(run.expiresAtUtc) : undefined;
  const isExpired = () => {
    if (end === undefined) return false;
    if (BigInt(Date.now()) * 1000n >= end) receipt.current.expired = true;
    return receipt.current.expired;
  };
  const expired = isExpired();

  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined, active = true;
    const tick = () => {
      if (!active || receipt.current.key !== key) return;
      clearTimeout(timer);
      const current = Date.now();
      if (end !== undefined && BigInt(current) * 1000n >= end) receipt.current.expired = true;
      setClock(current);
      if (end !== undefined && !receipt.current.expired) {
        // Browser timers use milliseconds; round up without losing UTC microseconds.
        const delay = (end - BigInt(current) * 1000n + 999n) / 1000n;
        timer = setTimeout(tick, Number(delay > 2_147_483_647n ? 2_147_483_647n : delay));
      }
    };
    tick();
    window.addEventListener("focus", tick);
    document.addEventListener("visibilitychange", tick);
    return () => {
      active = false; clearTimeout(timer);
      window.removeEventListener("focus", tick); document.removeEventListener("visibilitychange", tick);
    };
  }, [key, end]);

  return { expired, check: () => { const value = isExpired(); setClock(Date.now()); return value; } };
}
