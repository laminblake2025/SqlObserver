import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { ApiRequestError } from "../../apiClient.ts";
import { getCpuCaptureReceipts, getCpuCaptureDetail } from "./cpuCaptureWindowHistoryApi.ts";
import { cpuCaptureContextIdentity, cpuCaptureSelectionIdentity, validCpuCaptureSelection,
  type CpuCaptureBinding, type CpuCaptureSelection, type CpuCaptureReceiptPage, type CpuCaptureDetailPage } from "./cpuCaptureWindowHistoryModel.ts";

type Source = "list" | "detail";
export interface CpuCaptureRead<T> {
  readonly pages: readonly T[];
  readonly index: number;
  readonly loading: boolean;
  readonly error?: string;
  readonly errorStatus?: number;
  readonly attempt?: { readonly cursor?: string; readonly replace: boolean; readonly index: number };
}
interface State { readonly list: CpuCaptureRead<CpuCaptureReceiptPage>; readonly detail: CpuCaptureRead<CpuCaptureDetailPage> }
const emptyRead = <T,>(): CpuCaptureRead<T> => ({ pages: [], index: 0, loading: false });
const empty = (): State => ({ list: emptyRead(), detail: emptyRead() });
export const maximumCpuCapturePages = 100;

/** Owns received pages and an independent exact run read. A scope denial clears
 * both sources synchronously; ordinary polling cannot reopen denied evidence. */
export function useCpuCaptureWindowHistory(binding: CpuCaptureBinding | undefined,
  selected?: CpuCaptureSelection | null, manualRefresh = 0, accessContext = "") {
  const context = JSON.stringify([cpuCaptureContextIdentity(binding), accessContext]);
  const authority = JSON.stringify([binding?.targetId.toLowerCase(), binding?.targetRevision, accessContext]);
  const previousContext = useRef(context), previousSelection = useRef(""), mounted = useRef(false);
  const epoch = useRef(0), detailEpoch = useRef(0), owners = useRef(new Map<Source, AbortController>());
  const denial = useRef<string | undefined>(undefined), currentContext = useRef(context), currentIntent = useRef(selected);
  currentContext.current = context; currentIntent.current = selected;
  const [state, setState] = useState(empty), received = useRef(state), [denied, setDenied] = useState(false);
  const effectiveSelection = selected === null ? null : selected ??
    (previousContext.current === context ? state.list.pages[0]?.items[0] : undefined);
  const selectionIdentity = cpuCaptureSelectionIdentity(effectiveSelection), currentSelection = useRef(selectionIdentity);
  currentSelection.current = selectionIdentity;

  function publish(change: (old: State) => State) {
    const next = change(received.current); received.current = next; setState(next);
  }
  function stop(source?: Source) {
    if (source) { owners.current.get(source)?.abort(); owners.current.delete(source); }
    else { epoch.current++; detailEpoch.current++; for (const owner of owners.current.values()) owner.abort(); owners.current.clear(); }
  }
  useLayoutEffect(() => { mounted.current = true; return () => { mounted.current = false; stop(); }; }, []);
  useLayoutEffect(() => {
    if (previousContext.current === context) return;
    previousContext.current = context; previousSelection.current = selectionIdentity; stop(); publish(empty);
    if (binding && denial.current !== undefined && denial.current !== authority) { denial.current = undefined; setDenied(false); }
  }, [context]);
  useLayoutEffect(() => {
    if (previousSelection.current === selectionIdentity) return;
    previousSelection.current = selectionIdentity; detailEpoch.current++; stop("detail");
    publish(old => ({ ...old, detail: emptyRead() }));
  }, [selectionIdentity]);

  function active(source: Source, owner: AbortController, key: string, version: number, selectedVersion: number, identity: string) {
    return mounted.current && denial.current === undefined && !owner.signal.aborted && owners.current.get(source) === owner &&
      currentContext.current === key && epoch.current === version &&
      (source === "list" || detailEpoch.current === selectedVersion && currentSelection.current === identity);
  }
  function refuse(failure: unknown) {
    if (!(failure instanceof ApiRequestError) || ![401, 403].includes(failure.status)) return false;
    denial.current = authority; stop(); publish(empty); setDenied(true); return true;
  }
  async function read(source: Source, cursor?: string, replace = false, index = 0) {
    const exact = currentIntent.current === null ? null : currentIntent.current ?? received.current.list.pages[0]?.items[0];
    if (!binding || !mounted.current || denial.current !== undefined || owners.current.has(source) ||
      source === "detail" && !validCpuCaptureSelection(exact)) return;
    const old = received.current[source], prior = replace ? [] : old.pages;
    if (source === "list" && prior.length >= maximumCpuCapturePages) return;
    const owner = new AbortController(), key = context, version = epoch.current, selectedVersion = detailEpoch.current;
    const identity = cpuCaptureSelectionIdentity(exact); owners.current.set(source, owner);
    publish(old => ({ ...old, [source]: { ...old[source], loading: true, error: undefined, errorStatus: undefined, attempt: { cursor, replace, index } } }));
    try {
      if (source === "list") {
        const page = await getCpuCaptureReceipts(binding, owner.signal, prior as readonly CpuCaptureReceiptPage[], cursor);
        if (!active(source, owner, key, version, selectedVersion, identity)) return;
        const pages = [...prior as readonly CpuCaptureReceiptPage[], page];
        if (pages.length === maximumCpuCapturePages && page.nextCursor !== null || pages.reduce((count, value) => count + value.items.length, 0) > 10000)
          throw new Error("CPU capture browsing reached its retained page bound. Narrow the shared window; received pages remain evidence.");
        if (currentIntent.current === undefined && cpuCaptureSelectionIdentity(received.current.list.pages[0]?.items[0]) !== cpuCaptureSelectionIdentity(pages[0]?.items[0])) {
          detailEpoch.current++; stop("detail"); publish(old => ({ ...old, detail: emptyRead() }));
        }
        publish(old => ({ ...old, list: { pages, index, loading: false } }));
      } else {
        const page = await getCpuCaptureDetail(binding, exact!, owner.signal);
        if (!active(source, owner, key, version, selectedVersion, identity)) return;
        publish(old => ({ ...old, detail: { pages: [page], index: 0, loading: false } }));
      }
    } catch (failure) {
      if (!active(source, owner, key, version, selectedVersion, identity) || refuse(failure)) return;
      publish(old => ({ ...old, [source]: { ...old[source], loading: false,
        errorStatus: failure instanceof ApiRequestError ? failure.status : undefined,
        error: failure instanceof Error ? failure.message : "CPU capture evidence is unavailable. Retry this exact read." } }));
    } finally { if (owners.current.get(source) === owner) owners.current.delete(source); }
  }
  useEffect(() => { if (denial.current === undefined) void read("list", undefined, true); }, [context]);
  useEffect(() => { if (denial.current === undefined) void read("detail", undefined, true); }, [context, selectionIdentity]);
  const previousRefresh = useRef(manualRefresh);
  useEffect(() => {
    if (previousRefresh.current === manualRefresh) return;
    previousRefresh.current = manualRefresh;
    if (denial.current === undefined) { void read("list", undefined, true); void read("detail", undefined, true); }
  }, [manualRefresh]);
  function navigate(index: number) {
    if (denial.current !== undefined || owners.current.has("list") || index < 0) return;
    const old = received.current.list;
    if (old.pages[index]) publish(value => ({ ...value, list: { ...value.list, index } }));
    else if (index === old.pages.length && old.pages.at(-1)?.nextCursor) void read("list", old.pages.at(-1)!.nextCursor!, false, index);
  }
  function cancel(source: Source) {
    if (!owners.current.has(source)) return;
    stop(source); publish(old => ({ ...old, [source]: { ...old[source], loading: false,
      error: "Waiting cancelled. Received CPU evidence remains; retry keeps the exact run and continuation." } }));
  }
  function retry(source: Source) { const attempt = received.current[source].attempt; if (attempt) void read(source, attempt.cursor, attempt.replace, attempt.index); }
  function retryAccess() {
    if (!binding || !mounted.current) return;
    denial.current = undefined; setDenied(false); void read("list", undefined, true);
    if (currentIntent.current) void read("detail", undefined, true);
  }
  const visible = previousContext.current === context, visibleSelection = previousSelection.current === selectionIdentity;
  return { contextKey: context, selectionIdentity, list: visible ? state.list : emptyRead<CpuCaptureReceiptPage>(),
    detail: visible && visibleSelection ? state.detail : emptyRead<CpuCaptureDetailPage>(), selected: effectiveSelection,
    denied, navigate, cancel, retry, refresh: (source: Source) => void read(source, undefined, true), retryAccess };
}
export type CpuCaptureWindowHistoryRead = ReturnType<typeof useCpuCaptureWindowHistory>;
