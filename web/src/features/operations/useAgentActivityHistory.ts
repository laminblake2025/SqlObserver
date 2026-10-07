import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { ApiRequestError } from "../../apiClient.ts";
import { getAgentActivityReceipts, getAgentActivityDetail } from "./agentActivityHistoryApi.ts";
import { readAgentActivitySelection } from "./agentActivitySelectionModel.ts";
import { agentActivityContextIdentity, agentActivitySelectionIdentity, validAgentActivitySelection,
  type AgentActivityBinding, type AgentActivitySelection, type AgentActivityReceiptPage, type AgentActivityDetailPage } from "./agentActivityHistoryModel.ts";

type Source = "list" | "detail";
export interface AgentActivityRead<T> {
  readonly pages: readonly T[];
  readonly index: number;
  readonly loading: boolean;
  readonly error?: string;
  readonly errorStatus?: number;
  readonly attempt?: { readonly cursor?: string; readonly replace: boolean; readonly index: number };
}
interface State { readonly list: AgentActivityRead<AgentActivityReceiptPage>; readonly detail: AgentActivityRead<AgentActivityDetailPage> }
const emptyRead = <T,>(): AgentActivityRead<T> => ({ pages: [], index: 0, loading: false });
const empty = (): State => ({ list: emptyRead(), detail: emptyRead() });
export const maximumAgentActivityPages = 100;

/** Owns received pages and an independent exact run read. A scope denial clears
 * both sources synchronously; ordinary polling cannot reopen denied evidence. */
export function useAgentActivityHistory(binding: AgentActivityBinding | undefined,
  selected?: AgentActivitySelection | null, manualRefresh = 0, accessContext = "") {
  const context = JSON.stringify([agentActivityContextIdentity(binding), accessContext]);
  function routeWindow() {
    if (!binding) return undefined;
    const [path, query] = location.hash.replace(/^#\/?/u, "").split("?"), params = new URLSearchParams(query);
    const targets = params.getAll("target"), checks = params.getAll("check"), ranges = params.getAll("range");
    if (path !== "operations" || targets.length !== 1 || targets[0]!.toLowerCase() !== binding.targetId.toLowerCase() ||
      checks.length !== 1 || checks[0] !== "agent" || ranges.length !== 1) return undefined;
    const from = params.getAll("from"), to = params.getAll("to");
    if (ranges[0] === "custom") {
      if (from.length !== 1 || to.length !== 1) return undefined;
      return agentActivityContextIdentity({ ...binding, fromUtc: from[0]!, toUtc: to[0]! }) === agentActivityContextIdentity(binding) ? "received-window" : undefined;
    }
    return ["1h", "6h", "24h", "7d"].includes(ranges[0]!) && !from.length && !to.length ? ranges[0] : undefined;
  }
  const renderedRouteWindow = routeWindow();
  const contextFence = useRef({ context });
  if (contextFence.current.context !== context) contextFence.current = { context };
  const renderedContext = contextFence.current;
  const authority = JSON.stringify([binding?.targetId.toLowerCase(), binding?.targetRevision, accessContext]);
  const previousContext = useRef(context), previousSelection = useRef(""), mounted = useRef(false);
  const epoch = useRef(0), detailEpoch = useRef(0), owners = useRef(new Map<Source, AbortController>());
  const investigated = useRef<string | undefined>(undefined);
  const denial = useRef<string | undefined>(undefined), currentContext = useRef(context), currentIntent = useRef(selected);
  currentContext.current = context; currentIntent.current = selected;
  const [state, setState] = useState(empty), received = useRef(state), [denied, setDenied] = useState(false);
  const effectiveSelection = selected === null ? null : selected ??
    (previousContext.current === context ? state.list.pages[0]?.items[0] : undefined);
  const selectionIdentity = agentActivitySelectionIdentity(effectiveSelection), currentSelection = useRef(selectionIdentity);
  currentSelection.current = selectionIdentity;

  function ownsContext() {
    const route = routeWindow();
    // Opening a received live page promotes the same window to custom. It must
    // not retire its cursor, while a real route/window change fences immediately.
    return mounted.current && contextFence.current === renderedContext && currentContext.current === context &&
      route !== undefined && (route === renderedRouteWindow || route === "received-window");
  }
  function ownsExactRoute(identity: string) {
    const pin = readAgentActivitySelection(location.hash, binding?.targetId);
    return pin === undefined ? currentIntent.current === undefined : agentActivitySelectionIdentity(pin) === identity;
  }
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
    previousContext.current = context; previousSelection.current = selectionIdentity; investigated.current = undefined; stop(); publish(empty);
    if (binding && denial.current !== undefined && denial.current !== authority) { denial.current = undefined; setDenied(false); }
  }, [context]);
  useLayoutEffect(() => {
    if (previousSelection.current === selectionIdentity) return;
    previousSelection.current = selectionIdentity; detailEpoch.current++; stop("detail");
    publish(old => ({ ...old, detail: emptyRead() }));
  }, [selectionIdentity]);

  function active(source: Source, owner: AbortController, key: string, version: number, selectedVersion: number, identity: string) {
    return ownsContext() && denial.current === undefined && !owner.signal.aborted && owners.current.get(source) === owner &&
      currentContext.current === key && epoch.current === version &&
      (source === "list" || detailEpoch.current === selectedVersion && currentSelection.current === identity && ownsExactRoute(identity));
  }
  function refuse(failure: unknown) {
    if (!(failure instanceof ApiRequestError) || ![401, 403].includes(failure.status)) return false;
    denial.current = authority; stop(); publish(empty); setDenied(true); return true;
  }
  async function read(source: Source, cursor?: string, replace = false, index = 0) {
    const exact = currentIntent.current === null ? null : currentIntent.current ?? received.current.list.pages[0]?.items[0];
    if (!binding || !ownsContext() || denial.current !== undefined || owners.current.has(source) ||
      source === "detail" && (!validAgentActivitySelection(exact) || !ownsExactRoute(agentActivitySelectionIdentity(exact)))) return;
    const old = received.current[source], prior = replace ? [] : old.pages;
    if (prior.length >= (source === "list" ? maximumAgentActivityPages : 21)) return;
    const owner = new AbortController(), key = context, version = epoch.current, selectedVersion = detailEpoch.current;
    const identity = agentActivitySelectionIdentity(exact); owners.current.set(source, owner);
    publish(old => ({ ...old, [source]: { ...old[source], loading: true, error: undefined, errorStatus: undefined, attempt: { cursor, replace, index } } }));
    try {
      if (source === "list") {
        const page = await getAgentActivityReceipts(binding, owner.signal, prior as readonly AgentActivityReceiptPage[], cursor);
        if (!active(source, owner, key, version, selectedVersion, identity)) return;
        const pages = [...prior as readonly AgentActivityReceiptPage[], page];
        if (pages.length === maximumAgentActivityPages && page.nextCursor !== null || pages.reduce((count, value) => count + value.items.length, 0) > 10000)
          throw new Error("Agent activity browsing reached its retained page bound. Narrow the shared window; received pages remain evidence.");
        if (currentIntent.current === undefined && agentActivitySelectionIdentity(received.current.list.pages[0]?.items[0]) !== agentActivitySelectionIdentity(pages[0]?.items[0])) {
          detailEpoch.current++; stop("detail"); publish(old => ({ ...old, detail: emptyRead() }));
        }
        publish(old => ({ ...old, list: { pages, index, loading: false } }));
      } else {
        const page = await getAgentActivityDetail(binding, exact!, owner.signal);
        if (!active(source, owner, key, version, selectedVersion, identity)) return;
        publish(old => ({ ...old, detail: { pages: [page], index: 0, loading: false } }));
      }
    } catch (failure) {
      if (!active(source, owner, key, version, selectedVersion, identity) || refuse(failure)) return;
      publish(old => ({ ...old, [source]: { ...old[source], loading: false,
        errorStatus: failure instanceof ApiRequestError ? failure.status : undefined,
        error: failure instanceof Error ? failure.message : "Agent activity evidence is unavailable. Retry this exact read." } }));
    } finally { if (owners.current.get(source) === owner) owners.current.delete(source); }
  }
  useEffect(() => { if (denial.current === undefined) void read("list", undefined, true); }, [context]);
  useEffect(() => { if (denial.current === undefined) void read("detail", undefined, true); }, [context, selectionIdentity]);
  const previousRefresh = useRef(manualRefresh);
  useEffect(() => {
    if (previousRefresh.current === manualRefresh) return;
    previousRefresh.current = manualRefresh;
    if (denial.current === undefined && investigated.current !== context && currentIntent.current === undefined) { void read("list", undefined, true); void read("detail", undefined, true); }
  }, [manualRefresh]);
  function retain() { if (ownsContext()) investigated.current = context; }
  function navigate(index: number, source: Source = "list") {
    if (!ownsContext() || denial.current !== undefined || owners.current.has(source) || index < 0) return;
    const old = received.current[source];
    retain();
    if (old.pages[index]) publish(value => ({ ...value, [source]: { ...value[source], index } }));
    else if (source === "list" && index === old.pages.length && received.current.list.pages.at(-1)?.nextCursor) void read(source, received.current.list.pages.at(-1)!.nextCursor!, false, index);
  }
  function cancel(source: Source) {
    if (!ownsContext() || !owners.current.has(source)) return;
    retain(); stop(source); publish(old => ({ ...old, [source]: { ...old[source], loading: false,
      error: "Waiting cancelled. Received Agent activity evidence remains; retry keeps the exact run and continuation." } }));
  }
  function retry(source: Source) { if (!ownsContext()) return; const attempt = received.current[source].attempt; if (attempt) void read(source, attempt.cursor, attempt.replace, attempt.index); }
  function retryAccess() {
    if (!binding || !ownsContext()) return;
    denial.current = undefined; setDenied(false); void read("list", undefined, true);
    if (currentIntent.current) void read("detail", undefined, true);
  }
  const visible = previousContext.current === context, visibleSelection = previousSelection.current === selectionIdentity;
  return { contextKey: context, selectionIdentity, list: visible ? state.list : emptyRead<AgentActivityReceiptPage>(),
    detail: visible && visibleSelection ? state.detail : emptyRead<AgentActivityDetailPage>(), selected: effectiveSelection,
    denied, retain, navigate, cancel, retry, refresh: (source: Source) => void read(source, undefined, true), retryAccess };
}
export type AgentActivityHistoryRead = ReturnType<typeof useAgentActivityHistory>;
