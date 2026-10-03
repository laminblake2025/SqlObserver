import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { deadlockAccessDenied, getDeadlocks } from "./deadlockApi";
import { maximumDeadlockPages, maximumDeadlockRows, validateDeadlockReceipt, type DeadlockReceipt, type DeadlockWindow } from "./deadlockPaging.ts";

interface Attempt { readonly latest: boolean; readonly window: DeadlockWindow; readonly cursor?: string; }
interface Owner { readonly controller: AbortController; readonly attempt: Attempt; readonly automatic: boolean; readonly refresh: number; readonly generation: number; }
interface Cache { readonly generation: number; held: boolean; readonly context: string; receipts: DeadlockReceipt[]; selected: number; }
interface State { readonly generation: number; readonly loading: boolean; readonly error?: string; readonly attempt?: Attempt; }

export function useDeadlockPages(targetId: string, context: string, timeWindow: DeadlockWindow | undefined,
  refresh: number, accessDenied: boolean, onAccessDenied: () => void, onValidatedRead: () => void) {
  const identity = useRef({ context, generation: 0 });
  if (identity.current.context !== context) identity.current = { context, generation: identity.current.generation + 1 };
  const generation = identity.current.generation;
  const latest = useRef({ context, generation, timeWindow, refresh, accessDenied });
  latest.current = { context, generation, timeWindow, refresh, accessDenied };
  const cache = useRef<Cache>({ context, generation, held: accessDenied, receipts: [], selected: 0 });
  const owner = useRef<Owner | undefined>(undefined);
  const [, redraw] = useState(0);
  const [state, setState] = useState<State>();
  const changed = () => redraw(value => value + 1);
  useLayoutEffect(() => {
    owner.current?.controller.abort(); owner.current = undefined;
    cache.current = { context, generation, held: accessDenied, receipts: [], selected: 0 };
    setState({ generation, loading: false });
    return () => { owner.current?.controller.abort(); owner.current = undefined; };
  }, [context, generation]);
  useLayoutEffect(() => {
    if (!accessDenied) return;
    owner.current?.controller.abort(); owner.current = undefined;
    cache.current = { context, generation, held: true, receipts: [], selected: 0 };
    setState({ generation, loading: false }); changed();
  }, [accessDenied, context, generation]);

  function hold() {
    if (cache.current.generation !== generation || latest.current.generation !== generation) return;
    if (owner.current?.automatic) { owner.current.controller.abort(); owner.current = undefined; }
    cache.current.held = true; changed();
  }
  async function load(attempt: Attempt, explicit: boolean) {
    if (latest.current.generation !== generation || owner.current || cache.current.generation !== generation) return;
    if (!explicit && latest.current.accessDenied) return;
    if (!attempt.latest && (cache.current.receipts.length >= maximumDeadlockPages || rowCount(cache.current.receipts) >= maximumDeadlockRows)) return;
    if (explicit) hold();
    const received = cache.current;
    const request: Owner = { controller: new AbortController(), attempt, automatic: !explicit, refresh: latest.current.refresh, generation };
    owner.current = request; setState({ generation, loading: true, attempt });
    const current = () => !request.controller.signal.aborted && owner.current === request && cache.current === received &&
      latest.current.generation === generation && (!request.automatic || latest.current.refresh === request.refresh);
    try {
      const page = await getDeadlocks(targetId, request.controller.signal, attempt.cursor, attempt.window);
      if (!current()) return;
      const prior = attempt.latest ? [] : received.receipts;
      validateDeadlockReceipt(page, targetId, attempt.window, prior, attempt.cursor);
      received.receipts = [...prior, { page, window: attempt.window, requestedCursor: attempt.cursor }];
      received.selected = received.receipts.length - 1;
      // Release the investigation only after a validated latest receipt.
      received.held = !attempt.latest;
      setState({ generation, loading: false }); onValidatedRead(); changed();
    } catch (error) {
      if (!current()) return;
      received.held = true;
      if (deadlockAccessDenied(error)) {
        received.receipts = []; received.selected = 0;
        setState({ generation, loading: false }); onAccessDenied();
      } else setState({ generation, loading: false, attempt, error: error instanceof Error ? error.message : "Deadlock evidence is unavailable." });
      changed();
    } finally { if (owner.current === request) owner.current = undefined; }
  }
  useEffect(() => {
    if (!timeWindow || accessDenied) return;
    if (owner.current?.automatic && owner.current.refresh !== refresh) { owner.current.controller.abort(); owner.current = undefined; }
    if (!cache.current.held && !owner.current) void load({ latest: true, window: timeWindow }, false);
  }, [context, generation, refresh]);

  function navigate(index: number) {
    if (latest.current.generation !== generation || owner.current && !owner.current.automatic) return;
    hold();
    if (index >= 0 && index < cache.current.receipts.length) {
      cache.current.selected = index; setState({ generation, loading: false }); changed();
    }
  }
  function next() {
    if (latest.current.generation !== generation || owner.current && !owner.current.automatic) return;
    const received = cache.current;
    if (received.selected < received.receipts.length - 1) { navigate(received.selected + 1); return; }
    const receipt = received.receipts.at(-1);
    if (receipt?.page.nextCursor) { hold(); void load({ latest: false, window: receipt.window, cursor: receipt.page.nextCursor }, true); }
  }
  function cancel() {
    if (latest.current.generation !== generation) return;
    const request = owner.current; if (!request) return;
    request.controller.abort(); owner.current = undefined; cache.current.held = true;
    setState({ generation, loading: false, attempt: request.attempt, error: "Deadlock read cancelled. Retry repeats the same read." }); changed();
  }
  function refuseAccess() {
    owner.current?.controller.abort(); owner.current = undefined;
    cache.current = { context, generation, held: true, receipts: [], selected: 0 };
    setState({ generation, loading: false }); changed();
  }
  const visible = cache.current.generation === generation ? cache.current : undefined;
  const activity = state?.generation === generation ? state : undefined;
  const receipts = visible?.receipts ?? [], selected = visible?.selected ?? 0;
  const bounded = receipts.length >= maximumDeadlockPages || rowCount(receipts) >= maximumDeadlockRows;
  return { receipt: receipts[selected], receipts, selected, loading: activity?.loading ?? false, error: activity?.error,
    retained: visible?.held ?? false, bounded, canNext: selected < receipts.length - 1 || Boolean(!bounded && receipts.at(-1)?.page.nextCursor),
    first: () => navigate(0), previous: () => navigate(selected - 1), next, cancel, refuseAccess,
    latest: () => { if (owner.current?.automatic) { owner.current.controller.abort(); owner.current = undefined; }
      if (latest.current.timeWindow) void load({ latest: true, window: latest.current.timeWindow }, true); },
    retry: () => { if (activity?.attempt) void load(activity.attempt, true); }, canRetry: Boolean(activity?.attempt),
  };
}
function rowCount(receipts: readonly DeadlockReceipt[]): number { return receipts.reduce((total, receipt) => total + receipt.page.items.length, 0); }
