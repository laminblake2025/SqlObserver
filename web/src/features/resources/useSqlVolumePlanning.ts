import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { ApiRequestError } from "../../apiClient.ts";
import { getSqlVolumePlanningReceipts, getSqlVolumePlanningSnapshot } from "./sqlVolumePlanningApi.ts";
import { sqlVolumePlanningContextIdentity, sqlVolumePlanningSelectionIdentity, validSqlVolumePlanningSelection,
  type SqlVolumePlanningBinding, type SqlVolumePlanningSelection, type SqlVolumePlanningReceiptPage, type SqlVolumePlanningSnapshotPage } from "./sqlVolumePlanningModel.ts";

type Source = "list" | "snapshot";
export interface SqlVolumePlanningSourceRead<T> {
  readonly pages: readonly T[];
  readonly index: number;
  readonly loading: boolean;
  readonly error?: string;
  readonly errorStatus?: number;
  readonly attempt?: { readonly cursor?: string; readonly replace: boolean; readonly index: number };
}
interface State { readonly list: SqlVolumePlanningSourceRead<SqlVolumePlanningReceiptPage>; readonly snapshot: SqlVolumePlanningSourceRead<SqlVolumePlanningSnapshotPage> }
const emptyRead = <T,>(): SqlVolumePlanningSourceRead<T> => ({ pages: [], index: 0, loading: false });
const empty = (): State => ({ list: emptyRead(), snapshot: emptyRead() });
export const maximumSqlVolumePlanningPages = 100;

/** Owns received pages and an independent exact run read. A scope denial clears
 * both sources synchronously; ordinary polling cannot reopen denied evidence. */
export function useSqlVolumePlanning(binding: SqlVolumePlanningBinding | undefined,
  selected?: SqlVolumePlanningSelection | null, manualRefresh = 0, accessContext = "") {
  const context = JSON.stringify([sqlVolumePlanningContextIdentity(binding), accessContext]);
  const authority = JSON.stringify([binding?.targetId.toLowerCase(), binding?.targetRevision, accessContext]);
  const previousContext = useRef(context), previousSelection = useRef(""), mounted = useRef(false);
  const epoch = useRef(0), snapshotEpoch = useRef(0), owners = useRef(new Map<Source, AbortController>());
  const investigated = useRef<string | undefined>(undefined);
  const denial = useRef<string | undefined>(undefined), currentContext = useRef(context), currentIntent = useRef(selected);
  currentContext.current = context; currentIntent.current = selected;
  const [state, setState] = useState(empty), received = useRef(state), [denied, setDenied] = useState(false);
  const effectiveSelection = selected === null ? null : selected ??
    (previousContext.current === context ? state.list.pages[0]?.items[0] : undefined);
  const selectionIdentity = sqlVolumePlanningSelectionIdentity(effectiveSelection), currentSelection = useRef(selectionIdentity);
  currentSelection.current = selectionIdentity;

  function publish(change: (old: State) => State) {
    const next = change(received.current); received.current = next; setState(next);
  }
  function stop(source?: Source) {
    if (source) { owners.current.get(source)?.abort(); owners.current.delete(source); }
    else { epoch.current++; snapshotEpoch.current++; for (const owner of owners.current.values()) owner.abort(); owners.current.clear(); }
  }
  useLayoutEffect(() => { mounted.current = true; return () => { mounted.current = false; stop(); }; }, []);
  useLayoutEffect(() => {
    if (previousContext.current === context) return;
    previousContext.current = context; previousSelection.current = selectionIdentity; investigated.current = undefined; stop(); publish(empty);
    if (binding && denial.current !== undefined && denial.current !== authority) { denial.current = undefined; setDenied(false); }
  }, [context]);
  useLayoutEffect(() => {
    if (previousSelection.current === selectionIdentity) return;
    previousSelection.current = selectionIdentity; snapshotEpoch.current++; stop("snapshot");
    publish(old => ({ ...old, snapshot: emptyRead() }));
  }, [selectionIdentity]);

  function active(source: Source, owner: AbortController, key: string, version: number, selectedVersion: number, identity: string) {
    return mounted.current && denial.current === undefined && !owner.signal.aborted && owners.current.get(source) === owner &&
      currentContext.current === key && epoch.current === version &&
      (source === "list" || snapshotEpoch.current === selectedVersion && currentSelection.current === identity);
  }
  function refuse(failure: unknown) {
    if (!(failure instanceof ApiRequestError) || ![401, 403].includes(failure.status)) return false;
    denial.current = authority; stop(); publish(empty); setDenied(true); return true;
  }
  async function read(source: Source, cursor?: string, replace = false, index = 0) {
    const exact = currentIntent.current === null ? null : currentIntent.current ?? received.current.list.pages[0]?.items[0];
    if (!binding || !mounted.current || denial.current !== undefined || owners.current.has(source) ||
      source === "snapshot" && !validSqlVolumePlanningSelection(exact)) return;
    const old = received.current[source], prior = replace ? [] : old.pages;
    if (prior.length >= (source === "list" ? maximumSqlVolumePlanningPages : 10)) return;
    const owner = new AbortController(), key = context, version = epoch.current, selectedVersion = snapshotEpoch.current;
    const identity = sqlVolumePlanningSelectionIdentity(exact); owners.current.set(source, owner);
    publish(old => ({ ...old, [source]: { ...old[source], loading: true, error: undefined, errorStatus: undefined, attempt: { cursor, replace, index } } }));
    try {
      if (source === "list") {
        const page = await getSqlVolumePlanningReceipts(binding, owner.signal, prior as readonly SqlVolumePlanningReceiptPage[], cursor);
        if (!active(source, owner, key, version, selectedVersion, identity)) return;
        const pages = [...prior as readonly SqlVolumePlanningReceiptPage[], page];
        if (pages.length === maximumSqlVolumePlanningPages && page.nextCursor !== null || pages.reduce((count, value) => count + value.items.length, 0) > 10000)
          throw new Error("Volume planning browsing reached its retained page bound. Narrow the shared window; received pages remain evidence.");
        if (currentIntent.current === undefined && sqlVolumePlanningSelectionIdentity(received.current.list.pages[0]?.items[0]) !== sqlVolumePlanningSelectionIdentity(pages[0]?.items[0])) {
          snapshotEpoch.current++; stop("snapshot"); publish(old => ({ ...old, snapshot: emptyRead() }));
        }
        publish(old => ({ ...old, list: { pages, index, loading: false } }));
      } else {
        const page = await getSqlVolumePlanningSnapshot(binding, exact!, owner.signal, prior as readonly SqlVolumePlanningSnapshotPage[], cursor);
        if (!active(source, owner, key, version, selectedVersion, identity)) return;
        const pages = [...prior as readonly SqlVolumePlanningSnapshotPage[], page];
        if (pages.length === 10 && page.nextCursor !== null) throw new Error("Volume planning capacities reached their retained page bound. Refresh this source; received pages remain evidence.");
        publish(old => ({ ...old, snapshot: { pages, index, loading: false } }));
      }
    } catch (failure) {
      if (!active(source, owner, key, version, selectedVersion, identity) || refuse(failure)) return;
      publish(old => ({ ...old, [source]: { ...old[source], loading: false,
        errorStatus: failure instanceof ApiRequestError ? failure.status : undefined,
        error: failure instanceof Error ? failure.message : "Volume planning evidence is unavailable. Retry this exact read." } }));
    } finally { if (owners.current.get(source) === owner) owners.current.delete(source); }
  }
  useEffect(() => { if (denial.current === undefined) void read("list", undefined, true); }, [context]);
  useEffect(() => { if (denial.current === undefined) void read("snapshot", undefined, true); }, [context, selectionIdentity]);
  const previousRefresh = useRef(manualRefresh);
  useEffect(() => {
    if (previousRefresh.current === manualRefresh) return;
    previousRefresh.current = manualRefresh;
    if (denial.current === undefined && investigated.current !== context && currentIntent.current === undefined) { void read("list", undefined, true); void read("snapshot", undefined, true); }
  }, [manualRefresh]);
  function retain() { investigated.current = context; }
  function navigate(index: number, source: Source = "list") {
    if (denial.current !== undefined || owners.current.has(source) || index < 0) return;
    const old = received.current[source];
    retain();
    if (old.pages[index]) publish(value => ({ ...value, [source]: { ...value[source], index } }));
    else if (index === old.pages.length && old.pages.at(-1)?.nextCursor) void read(source, old.pages.at(-1)!.nextCursor!, false, index);
  }
  function cancel(source: Source) {
    if (!owners.current.has(source)) return;
    retain(); stop(source); publish(old => ({ ...old, [source]: { ...old[source], loading: false,
      error: "Waiting cancelled. Received Volume planning evidence remains; retry keeps the exact run and continuation." } }));
  }
  function retry(source: Source) { const attempt = received.current[source].attempt; if (attempt) void read(source, attempt.cursor, attempt.replace, attempt.index); }
  function retryAccess() {
    if (!binding || !mounted.current) return;
    denial.current = undefined; setDenied(false); void read("list", undefined, true);
    if (currentIntent.current) void read("snapshot", undefined, true);
  }
  const visible = previousContext.current === context, visibleSelection = previousSelection.current === selectionIdentity;
  return { contextKey: context, selectionIdentity, list: visible ? state.list : emptyRead<SqlVolumePlanningReceiptPage>(),
    snapshot: visible && visibleSelection ? state.snapshot : emptyRead<SqlVolumePlanningSnapshotPage>(), selected: effectiveSelection,
    denied, retain, navigate, cancel, retry, refresh: (source: Source) => void read(source, undefined, true), retryAccess };
}
export type SqlVolumePlanningRead = ReturnType<typeof useSqlVolumePlanning>;
