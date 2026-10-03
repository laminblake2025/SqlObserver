import { useEffect, useState } from "react";
import { ActivityRequestError, getCurrentBlockingPage } from "../activity/activityApi";
import type { ActivityPage, BlockingEdge } from "../activity/activityTypes";

export interface CurrentBlockingEvidence {
  readonly page?: ActivityPage<BlockingEdge>;
  readonly loading: boolean;
  readonly retained: boolean;
  readonly error?: string;
}

/** One native read feeds both the answer and its graph. Target/revision changes fence render before effects. */
export function useCurrentBlockingEvidence(instanceId: string, targetRevision: number, refresh: number, enabled = true): CurrentBlockingEvidence {
  const key = JSON.stringify([instanceId.toLowerCase(), targetRevision]);
  const [receipt, setReceipt] = useState<{ key: string; refresh: number; page?: ActivityPage<BlockingEdge>; error?: string; loading: boolean }>();
  useEffect(() => {
    if (!enabled) { setReceipt(undefined); return; }
    const controller = new AbortController();
    setReceipt(previous => ({ key, refresh, page: previous?.key === key ? previous.page : undefined, loading: true }));
    void getCurrentBlockingPage(instanceId, controller.signal).then(page => {
      if (!controller.signal.aborted) setReceipt({ key, refresh, page, loading: false });
    }).catch(failure => {
      if (controller.signal.aborted) return;
      const denied = failure instanceof ActivityRequestError && [401, 403].includes(failure.status ?? 0);
      setReceipt(previous => ({ key, refresh, page: !denied && previous?.key === key ? previous.page : undefined,
        loading: false, error: failure instanceof Error ? failure.message : "Current blocking is unavailable." }));
    });
    return () => controller.abort();
  }, [instanceId, targetRevision, key, refresh, enabled]);
  const current = enabled && receipt?.key === key ? receipt : undefined;
  const loading = enabled && (current === undefined || current.refresh !== refresh || current.loading);
  return { page: current?.page, loading, retained: Boolean(current?.page && (loading || current.error)),
    error: current?.refresh === refresh ? current.error : undefined };
}
