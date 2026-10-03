import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { ApiRequestError } from "../../apiClient.ts";
import { getActiveAlerts, getFleetActiveAlerts } from "./alertApi.ts";
import { alertTriageIdentity, alertTriageRows, maximumAlertTriagePages, maximumAlertTriageRows, validateAlertTriagePage,
  type AlertInboxKind, type AlertInboxReceipt } from "./alertTriageModel.ts";
import type { ActiveAlert } from "./alertTypes.ts";

interface Attempt { readonly latest: boolean; readonly requestedKey?: string; }
interface Owner { readonly controller: AbortController; readonly attempt: Attempt; readonly refresh: number; readonly automatic: boolean; }
interface Store { readonly context: string; held: boolean; receipts: AlertInboxReceipt[]; denial: number; }
interface Activity { readonly context: string; readonly loading: boolean; readonly error?: string; readonly attempt?: Attempt; }
export function useAlertInbox(kind: AlertInboxKind, targetId: string, context: string, refresh: number, requestedKey?: string) {
  const latest = useRef({ context, refresh, requestedKey }); latest.current = { context, refresh, requestedKey };
  const store = useRef<Store>({ context, held: false, receipts: [], denial: 0 });
  const owner = useRef<Owner | undefined>(undefined);
  const [version, setVersion] = useState(0);
  const [activity, setActivity] = useState<Activity>();
  const redraw = () => setVersion(value => value + 1);
  void version;
  useLayoutEffect(() => {
    if (store.current.context !== context) {
      owner.current?.controller.abort(); owner.current = undefined;
      store.current = { context, held: false, receipts: [], denial: store.current.denial + 1 };
    }
    return () => { owner.current?.controller.abort(); owner.current = undefined; };
  }, [context]);
  function hold() {
    if (store.current.context !== context) return;
    if (owner.current?.automatic) {
      owner.current.controller.abort(); owner.current = undefined;
      setActivity({ context, loading: false });
    }
    store.current.held = true; redraw();
  }
  function deny(message = "Alert access was denied. Received evidence and actions were removed. Return to latest after access is restored.") {
    if (latest.current.context !== context || store.current.context !== context) return;
    owner.current?.controller.abort(); owner.current = undefined;
    store.current.receipts = []; store.current.held = true; store.current.denial++;
    setActivity({ context, loading: false, error: message }); redraw();
  }
  async function load(attempt: Attempt, explicit: boolean) {
    if (latest.current.context !== context || store.current.context !== context || owner.current) return;
    const cache = store.current;
    if (!attempt.latest && (cache.receipts.length >= maximumAlertTriagePages || alertTriageRows(cache.receipts, targetId).length >= maximumAlertTriageRows)) {
      hold(); setActivity({ context, loading: false, error: "Retained inbox limit reached: 20 pages / 2,000 rows. Return to latest to continue." }); return;
    }
    const request: Owner = { controller: new AbortController(), attempt, refresh: latest.current.refresh, automatic: !explicit };
    owner.current = request; if (explicit) hold();
    setActivity({ context, loading: true, attempt });
    const current = () => !request.controller.signal.aborted && owner.current === request && store.current === cache && latest.current.context === context &&
      (!request.automatic || latest.current.refresh === request.refresh);
    try {
      const receipts = attempt.latest ? [] : [...cache.receipts];
      let cursor = attempt.latest ? undefined : receipts.at(-1)?.page.nextCursor;
      if (!attempt.latest && !cursor) throw new Error("No unread alert continuation is available.");
      const reads = attempt.latest && attempt.requestedKey ? 5 : 1;
      for (let index = 0; index < reads; index++) {
        const page = kind === "fleet" ? await getFleetActiveAlerts(request.controller.signal, 100, cursor)
          : await getActiveAlerts(targetId, request.controller.signal, 100, cursor);
        if (!current()) return;
        validateAlertTriagePage(page, kind, targetId, receipts, cursor);
        receipts.push({ page, cursor, receivedAtUtc: new Date().toISOString() });
        cursor = page.nextCursor;
        if (!attempt.requestedKey || receipts.some(receipt => receipt.page.items.some(item => alertTriageIdentity(item, targetId) === attempt.requestedKey)) || !cursor) break;
      }
      if (!current()) return;
      cache.receipts = receipts;
      if (attempt.latest) cache.held = Boolean(attempt.requestedKey);
      setActivity({ context, loading: false }); redraw();
    } catch (failure) {
      if (!current()) return;
      if (failure instanceof ApiRequestError && [401, 403].includes(failure.status)) { deny(); return; }
      setActivity({ context, loading: false, attempt, error: failure instanceof Error ? failure.message : "Alert read is unavailable. Retry this read." });
    } finally { if (owner.current === request) owner.current = undefined; }
  }
  const currentStore = store.current.context === context ? store.current : undefined;
  const receipts = currentStore?.receipts ?? [];
  const rows = alertTriageRows(receipts, targetId);
  const state = activity?.context === context ? activity : undefined;
  useEffect(() => {
    const request = owner.current;
    if (request?.automatic && request.refresh !== refresh) { request.controller.abort(); owner.current = undefined; }
    if (!store.current.held && !owner.current) void load({ latest: true, requestedKey }, Boolean(requestedKey));
  }, [context, refresh]);
  const selection = useRef<{ context: string; requestedKey?: string } | undefined>(undefined);
  useEffect(() => {
    const previous = selection.current; selection.current = { context, requestedKey };
    if (!requestedKey || previous?.context !== context || previous.requestedKey === requestedKey) return;
    hold();
    if (!rows.some(item => alertTriageIdentity(item, targetId) === requestedKey)) {
      owner.current?.controller.abort(); owner.current = undefined;
      void load({ latest: true, requestedKey }, true);
    }
  }, [context, requestedKey]);
  function cancel() {
    const request = owner.current;
    if (!request || latest.current.context !== context) return;
    request.controller.abort(); owner.current = undefined; hold();
    setActivity({ context, loading: false, attempt: request.attempt, error: "Alert read cancelled. Received pages remain visible; retry repeats this read." });
  }
  function acknowledged(item: ActiveAlert) {
    if (latest.current.context !== context || store.current.context !== context) return;
    cacheAcknowledged(store.current.receipts, item, targetId); redraw();
  }
  function pinMutation() {
    // A replacement already in flight cannot take away the selected writer.
    if (owner.current) cancel();
    hold();
  }
  return { rows, receipts, snapshotUtc: receipts[0]?.page.snapshotUtc, nextCursor: receipts.at(-1)?.page.nextCursor,
    loading: state?.loading ?? !receipts.length, error: state?.error, retained: currentStore?.held ?? false, denial: currentStore?.denial ?? store.current.denial + 1,
    retry: () => { if (state?.attempt) void load(state.attempt, true); }, canRetry: Boolean(state?.attempt), cancel, hold, pinMutation, deny, acknowledged,
    more: () => void load({ latest: false }, true), latest: () => void load({ latest: true, requestedKey }, true) };
}
function cacheAcknowledged(receipts: AlertInboxReceipt[], item: ActiveAlert, targetId: string) {
  const identity = alertTriageIdentity(item, targetId);
  for (let index = 0; index < receipts.length; index++) {
    const receipt = receipts[index]!;
    const mark = <T extends ActiveAlert,>(candidate: T): T => alertTriageIdentity(candidate, targetId) === identity && candidate.firstObservedUtc === item.firstObservedUtc
      ? { ...candidate, state: "acknowledged" } : candidate;
    const page = "targetId" in receipt.page
      ? { ...receipt.page, items: receipt.page.items.map(mark) }
      : { ...receipt.page, items: receipt.page.items.map(mark) };
    receipts[index] = { ...receipt, page };
  }
}
