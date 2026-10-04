import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { ActivityRequestError, getCurrentBlockingPage } from "../activity/activityApi";
import type { ActivityPage, BlockingEdge } from "../activity/activityTypes";

export interface CurrentBlockingEvidence {
  readonly page?: ActivityPage<BlockingEdge>;
  readonly loading: boolean;
  readonly retained: boolean;
  readonly error?: string;
  readonly denied?: boolean;
  readonly retry?: () => void;
}

/** One native read feeds both the answer and its graph. Target/revision changes fence render before effects. */
export function useCurrentBlockingEvidence(instanceId: string, targetRevision: number, refresh: number, enabled = true,
  onAccessDenied?: (status: 401 | 403) => void): CurrentBlockingEvidence {
  const accessDenied = useRef(onAccessDenied); accessDenied.current = onAccessDenied;
  const key = JSON.stringify([instanceId.toLowerCase(), targetRevision]);
  const activation = useRef({ key, enabled, generation: 0 });
  if (activation.current.key !== key || activation.current.enabled !== enabled)
    activation.current = { key, enabled, generation: activation.current.generation + 1 };
  const generation = activation.current.generation;
  const deniedKey = useRef<string | undefined>(undefined);
  const controller = useRef<AbortController | undefined>(undefined);
  const [retrySequence, setRetrySequence] = useState(0);
  const [receipt, setReceipt] = useState<{ key: string; generation: number; refresh: number; retrySequence: number; page?: ActivityPage<BlockingEdge>; error?: string; loading: boolean }>();
  const readKey = JSON.stringify([key, generation, refresh, retrySequence, enabled]);
  const latestRead = useRef(readKey); latestRead.current = readKey;
  useLayoutEffect(() => {
    if (!enabled) setReceipt(undefined);
    return () => { controller.current?.abort(); };
  }, [key, enabled, generation]);
  const retry = useCallback(() => {
    if (!enabled) return;
    controller.current?.abort();
    if (deniedKey.current === key) deniedKey.current = undefined;
    setRetrySequence(value => value + 1);
  }, [key, enabled]);
  useEffect(() => {
    if (!enabled) return;
    if (deniedKey.current === key) {
      setReceipt(previous => ({ key, generation, refresh, retrySequence, loading: false,
        error: previous?.key === key ? previous.error : "Current blocking access was denied. Restore target read access and explicitly retry." }));
      return;
    }
    const owner = new AbortController(); controller.current = owner;
    const current = () => !owner.signal.aborted && latestRead.current === readKey;
    setReceipt(previous => ({ key, generation, refresh, retrySequence,
      page: previous?.key === key && previous.generation === generation ? previous.page : undefined, loading: true }));
    void getCurrentBlockingPage(instanceId, owner.signal).then(page => {
      if (current()) setReceipt({ key, generation, refresh, retrySequence, page, loading: false });
    }).catch(failure => {
      if (!current()) return;
      const denied = failure instanceof ActivityRequestError && [401, 403].includes(failure.status ?? 0);
      if (denied) deniedKey.current = key;
      setReceipt(previous => ({ key, generation, refresh, retrySequence,
        page: !denied && previous?.key === key && previous.generation === generation ? previous.page : undefined,
        loading: false, error: failure instanceof Error ? failure.message : "Current blocking is unavailable." }));
      if (current() && failure instanceof ActivityRequestError && (failure.status === 401 || failure.status === 403))
        accessDenied.current?.(failure.status);
    });
    return () => owner.abort();
  }, [instanceId, key, generation, refresh, retrySequence, enabled, readKey]);
  const denied = deniedKey.current === key;
  const current = enabled && receipt?.key === key && receipt.generation === generation ? receipt : undefined;
  const loading = enabled && !denied && (current === undefined || current.refresh !== refresh || current.retrySequence !== retrySequence || current.loading);
  return { page: current?.page, loading, retained: Boolean(current?.page && (loading || current.error)),
    denied, retry, error: denied ? current?.error ?? "Current blocking access was denied. Restore target read access and explicitly retry."
      : current?.refresh === refresh && current.retrySequence === retrySequence ? current.error : undefined };
}
