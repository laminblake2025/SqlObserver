import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { AnalyticsRequestError, getAnalyticsSurface } from "./analyticsApi.ts";
import { analyticsRetainedRowCount, maximumAnalyticsPages, maximumAnalyticsRows, validateAnalyticsReceipt,
  type AnalyticsReceipt, type AnalyticsWindow } from "./analyticsSurfacePaging.ts";
import type { AnalyticsSurface } from "./analyticsTypes.ts";

interface Attempt { readonly latest: boolean; readonly window?: AnalyticsWindow; readonly cursor?: string; }
interface Owner { readonly controller: AbortController; readonly attempt: Attempt; readonly refresh: number; readonly automatic: boolean; readonly generation: number; }
interface Store { readonly context: string; held: boolean; receipts: AnalyticsReceipt[]; selected: number; }
interface Activity { readonly context: string; readonly loading: boolean; readonly error?: string; readonly attempt?: Attempt; }

export function useAnalyticsSurface(targetId: string, surface: AnalyticsSurface, context: string,
  timeWindow: AnalyticsWindow | undefined, targetRevision: number | undefined, refresh: number, active: boolean,
  accessDenied = false, onAccessDenied?: () => void) {
  const latest = useRef({ context, timeWindow, refresh, active });
  latest.current = { context, timeWindow, refresh, active };
  const activation = useRef({ context, active, generation: 0 });
  if (activation.current.context !== context || activation.current.active !== active) {
    activation.current = { context, active, generation: activation.current.generation + 1 };
  }
  const store = useRef<Store>({ context, held: accessDenied, receipts: [], selected: 0 });
  const owner = useRef<Owner | undefined>(undefined);
  const [version, setVersion] = useState(0);
  const [activity, setActivity] = useState<Activity>();
  const redraw = () => setVersion(value => value + 1);
  void version;

  useLayoutEffect(() => {
    if (store.current.context !== context) {
      owner.current?.controller.abort(); owner.current = undefined;
      store.current = { context, held: accessDenied, receipts: [], selected: 0 };
      setActivity({ context, loading: false, error: accessDenied
        ? "Analytics access was denied. Received evidence was removed. Return to latest after access is restored." : undefined });
    }
    return () => { owner.current?.controller.abort(); owner.current = undefined; };
  }, [context]);
  useLayoutEffect(() => {
    if (active) return;
    const request = owner.current;
    if (request) {
      request.controller.abort(); owner.current = undefined;
      store.current.held = true;
      setActivity({ context, loading: false, attempt: request.attempt, error: "Analytics read cancelled after changing surface. Retry repeats this read." });
    } else if (store.current.receipts.length) { store.current.held = true; redraw(); }
  }, [context, active]);

  function hold() {
    if (store.current.context !== context) return;
    if (owner.current?.automatic) {
      owner.current.controller.abort(); owner.current = undefined;
      setActivity({ context, loading: false });
    }
    store.current.held = true; redraw();
  }
  async function load(attempt: Attempt, explicit: boolean) {
    if (latest.current.context !== context || !latest.current.active || owner.current) return;
    const cache = store.current;
    if (cache.context !== context) return;
    if (!attempt.latest && (cache.receipts.length >= maximumAnalyticsPages || analyticsRetainedRowCount(cache.receipts) >= maximumAnalyticsRows)) {
      hold(); setActivity({ context, loading: false, error: "Retained analytics limit reached: 20 pages / 4,000 rows. Return to latest to continue." }); return;
    }
    if (explicit) hold();
    const request: Owner = { controller: new AbortController(), attempt, refresh: latest.current.refresh, automatic: !explicit,
      generation: activation.current.generation };
    owner.current = request;
    setActivity({ context, loading: true, attempt });
    const current = () => !request.controller.signal.aborted && owner.current === request && store.current === cache
      && latest.current.context === context && latest.current.active && request.generation === activation.current.generation
      && (!request.automatic || latest.current.refresh === request.refresh);
    try {
      const page = await getAnalyticsSurface(targetId, surface, request.controller.signal, {
        ...attempt.window, cursor: attempt.cursor, expectedTargetRevision: targetRevision,
      });
      if (!current()) return;
      const prior = attempt.latest ? [] : cache.receipts;
      validateAnalyticsReceipt(page, surface, targetId, targetRevision, attempt.window, prior, attempt.cursor);
      cache.receipts = [...prior, { page, requestedCursor: attempt.cursor, receivedAtUtc: new Date().toISOString() }];
      cache.selected = cache.receipts.length - 1;
      if (attempt.latest) cache.held = false;
      setActivity({ context, loading: false }); redraw();
    } catch (failure) {
      if (!current()) return;
      cache.held = true;
      if (failure instanceof AnalyticsRequestError && [401, 403].includes(failure.status)) {
        cache.receipts = []; cache.selected = 0;
        setActivity({ context, loading: false, error: "Analytics access was denied. Received evidence was removed. Return to latest after access is restored." });
        onAccessDenied?.();
      } else {
        const message = failure instanceof AnalyticsRequestError ? failure.message
          : "The analytics response or continuation could not be validated. Received pages remain available; retry this read.";
        setActivity({ context, loading: false, attempt, error: message });
      }
      redraw();
    } finally { if (owner.current === request) owner.current = undefined; }
  }

  useEffect(() => {
    const request = owner.current;
    if (!active) return;
    if (request?.automatic && request.refresh !== refresh) { request.controller.abort(); owner.current = undefined; }
    if (!store.current.held && !owner.current) void load({ latest: true, window: latest.current.timeWindow }, false);
  }, [context, active, refresh]);

  function navigate(index: number) {
    if (latest.current.context !== context || owner.current && !owner.current.automatic) return;
    hold();
    if (index >= 0 && index < store.current.receipts.length) {
      store.current.selected = index;
      setActivity({ context, loading: false }); redraw();
    }
  }
  function next() {
    const cache = store.current;
    if (cache.context !== context || owner.current && !owner.current.automatic) return;
    if (cache.selected < cache.receipts.length - 1) { navigate(cache.selected + 1); return; }
    const page = cache.receipts.at(-1)?.page;
    if (page?.nextCursor) { hold(); void load({ latest: false, cursor: page.nextCursor, window: { fromUtc: page.fromUtc, toUtc: page.toUtc } }, true); }
  }
  function cancel() {
    const request = owner.current;
    if (!request || latest.current.context !== context) return;
    request.controller.abort(); owner.current = undefined;
    store.current.held = true;
    setActivity({ context, loading: false, attempt: request.attempt, error: "Analytics read cancelled. Received pages remain visible; retry repeats this read." }); redraw();
  }
  const currentStore = store.current.context === context ? store.current : undefined;
  const receipts = currentStore?.receipts ?? [];
  const selected = currentStore?.selected ?? 0;
  const state = activity?.context === context ? activity : undefined;
  const bounded = receipts.length >= maximumAnalyticsPages || analyticsRetainedRowCount(receipts) >= maximumAnalyticsRows;
  return { receipt: receipts[selected], receipts, selected, loading: state?.loading ?? (active && !receipts.length),
    error: state?.error ?? (accessDenied && !receipts.length
      ? "Analytics access was denied. Received evidence was removed. Return to latest after access is restored." : undefined),
    retained: currentStore?.held ?? accessDenied, bounded,
    first: () => navigate(0), previous: () => navigate(selected - 1), next,
    canNext: selected < receipts.length - 1 || Boolean(!bounded && receipts.at(-1)?.page.nextCursor),
    latest: () => { if (owner.current?.automatic) { owner.current.controller.abort(); owner.current = undefined; }
      void load({ latest: true, window: latest.current.timeWindow }, true); },
    retry: () => { if (state?.attempt) void load(state.attempt, true); }, canRetry: Boolean(state?.attempt), cancel };
}
