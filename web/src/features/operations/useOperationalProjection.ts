import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { getOperationalPage, OperationalApiError } from "./operationsApi.ts";
import { maximumProjectionPages, maximumProjectionRows, projectionPageLimit, validateOperationalProjectionPage,
  type OperationalProjectionKind } from "./operationsProjectionModel.ts";
import type { OperationalPage } from "./operationsTypes.ts";
import type { OperationsCardState } from "./operationsState.ts";
interface Receipt { readonly key: string; readonly page: OperationalPage; readonly index: number; }
interface Attempt { readonly index: number; readonly cursor?: string; readonly latest: boolean; }
interface Activity { readonly key: string; readonly refresh: number; readonly loading: boolean; readonly error?: string; readonly retryable?: boolean; readonly attempt?: Attempt; }
interface Owner { readonly key: string; readonly controller: AbortController; readonly attempt: Attempt; readonly refresh: number; readonly automatic: boolean; }
export function useOperationalProjection(kind: OperationalProjectionKind, targetId: string, context: string, refresh: number) {
  const latest = useRef({ context, refresh }); latest.current = { context, refresh };
  const request = useRef<Owner | undefined>(undefined);
  const cache = useRef<{ key: string; held: boolean; pages: Map<number, OperationalPage>; cursors: (string | undefined)[] }>({ key: context, held: false, pages: new Map(), cursors: [undefined] });
  const [receipt, setReceipt] = useState<Receipt>();
  const [activity, setActivity] = useState<Activity>();
  const [held, setHeld] = useState<{ key: string; value: boolean }>();
  const current = receipt?.key === context ? receipt : undefined;
  const retained = held?.key === context && held.value;
  const status = activity?.key === context && (retained || activity.refresh === refresh) ? activity : undefined;
  useLayoutEffect(() => {
    if (cache.current.key !== context) {
      request.current?.controller.abort(); request.current = undefined;
      cache.current = { key: context, held: false, pages: new Map(), cursors: [undefined] };
    }
    return () => { request.current?.controller.abort(); request.current = undefined; };
  }, [context]);
  function retain() { cache.current.held = true; setHeld({ key: context, value: true }); }
  async function load(attempt: Attempt, explicit: boolean) {
    if (latest.current.context !== context || request.current || cache.current.key !== context) return;
    const store = cache.current;
    if (!attempt.latest && !store.pages.has(attempt.index) && (store.pages.size >= maximumProjectionPages ||
      [...store.pages.values()].reduce((rows, page) => rows + page.items.length, 0) >= maximumProjectionRows)) {
      retain();
      setActivity({ key: context, refresh: latest.current.refresh, loading: false, attempt,
        error: "Retained projection limit reached: 400 pages or 10,000 rows. Return to latest to continue.", retryable: false });
      return;
    }
    const controller = new AbortController();
    const owner = { key: context, controller, attempt, refresh: latest.current.refresh, automatic: !explicit };
    request.current = owner;
    if (explicit) retain();
    setActivity({ key: context, refresh: owner.refresh, loading: true, attempt });
    const isCurrent = () => !controller.signal.aborted && latest.current.context === context && request.current === owner && cache.current === store &&
      (!owner.automatic || owner.refresh === latest.current.refresh);
    try {
      const page = await getOperationalPage(targetId, kind, { limit: projectionPageLimit, cursor: attempt.cursor, signal: controller.signal });
      if (!isCurrent()) return;
      const anchor = attempt.latest ? undefined : store.pages.get(0);
      if (!attempt.latest && (!anchor || !attempt.cursor)) throw new Error("The retained projection cursor is unavailable. Return to latest before continuing.");
      validateOperationalProjectionPage(page, kind, targetId, anchor,
        attempt.latest ? undefined : store.pages.get(attempt.index - 1)?.items.at(-1),
        attempt.latest ? [] : store.cursors.slice(0, attempt.index + 1));
      if (!attempt.latest && (store.pages.size >= maximumProjectionPages ||
        [...store.pages.values()].reduce((rows, previous) => rows + previous.items.length, page.items.length) > maximumProjectionRows))
        throw new Error("Retained projection limit reached: 400 pages or 10,000 rows. Return to latest to continue.");
      if (attempt.latest) {
        store.pages = new Map([[0, page]]); store.cursors = [undefined]; store.held = false;
        setHeld({ key: context, value: false });
      } else {
        store.pages.set(attempt.index, page); store.cursors[attempt.index] = attempt.cursor;
      }
      setReceipt({ key: context, page, index: attempt.index });
      setActivity({ key: context, refresh: latest.current.refresh, loading: false });
    } catch (error) {
      if (!isCurrent()) return;
      const api = error instanceof OperationalApiError ? error : undefined;
      if (api?.status === 401 || api?.status === 403) {
        store.pages.clear(); store.cursors = [undefined]; setReceipt(undefined); retain();
      }
      setActivity({ key: context, refresh: latest.current.refresh, loading: false, attempt,
        error: api?.message ?? (error instanceof Error && /^Operational projection|^The retained projection|^Retained projection limit/u.test(error.message)
          ? error.message : "Operational projection is temporarily unavailable. Retry this read."),
        retryable: api ? api.retryable : true });
    } finally { if (request.current === owner) request.current = undefined; }
  }
  useEffect(() => {
    const owner = request.current;
    if (owner?.automatic && owner.refresh !== refresh) {
      owner.controller.abort(); request.current = undefined;
    }
    if (!cache.current.held && !request.current) void load({ index: 0, latest: true }, false);
  }, [context, refresh]);
  function navigate(index: number, cursor?: string) {
    if (latest.current.context !== context || request.current || cache.current.key !== context || !current || index < 0) return;
    const stored = cache.current.pages.get(index);
    retain();
    if (stored) {
      setReceipt({ key: context, page: stored, index }); setActivity({ key: context, refresh, loading: false }); return;
    }
    if (kind === "tempdb-summary" || index !== current.index + 1 || !cursor || cursor !== current.page.nextCursor) return;
    void load({ index, cursor, latest: false }, true);
  }
  function cancel() {
    const owner = request.current;
    if (!owner || owner.key !== context || latest.current.context !== context) return;
    owner.controller.abort(); request.current = undefined; retain();
    setActivity({ key: context, refresh: latest.current.refresh, loading: false, attempt: owner.attempt,
      error: "The projection read was cancelled. The received page was preserved; retry repeats the cancelled read.", retryable: true });
  }
  const card: OperationsCardState = { page: current?.page, loading: status?.loading ?? (!current || !retained && activity?.refresh !== refresh),
    error: status?.error, retryable: status?.retryable, retained: Boolean(retained), pageIndex: current?.index ?? 0 };
  return { card, cancel, retry: () => { if (status?.attempt) void load(status.attempt, true); },
    latest: () => void load({ index: 0, latest: true }, true),
    next: () => navigate((current?.index ?? 0) + 1, current?.page.nextCursor ?? undefined),
    previous: () => navigate((current?.index ?? 0) - 1), first: () => navigate(0) };
}
