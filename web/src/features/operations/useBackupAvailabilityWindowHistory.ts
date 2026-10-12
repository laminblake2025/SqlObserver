import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { ApiRequestError } from "../../apiClient.ts";
import { getBackupAvailabilityReceipts, getBackupAvailabilitySnapshot } from "./backupAvailabilityWindowHistoryApi.ts";
import { readBackupAvailabilitySelection } from "./backupAvailabilitySelectionModel.ts";
import { backupAvailabilityContextIdentity, backupAvailabilitySelectionIdentity, validBackupAvailabilitySelection,
  maximumBackupAvailabilityReceiptPages, maximumBackupAvailabilitySnapshotPages,
  type BackupAvailabilityBinding, type BackupAvailabilityKind, type BackupAvailabilitySelection,
  type BackupAvailabilityReceiptPage, type BackupAvailabilitySnapshotPage, type BackupAvailabilitySource } from "./backupAvailabilityWindowHistoryModel.ts";

export interface BackupAvailabilityRead<T> {
  readonly pages: readonly T[]; readonly index: number; readonly loading: boolean;
  readonly error?: string; readonly errorStatus?: number;
  readonly attempt?: { readonly cursor?: string; readonly replace: boolean; readonly index: number };
}
interface State { readonly list: BackupAvailabilityRead<BackupAvailabilityReceiptPage>; readonly backup: BackupAvailabilityRead<BackupAvailabilitySnapshotPage>; readonly replicas: BackupAvailabilityRead<BackupAvailabilitySnapshotPage>; readonly databases: BackupAvailabilityRead<BackupAvailabilitySnapshotPage> }
const emptyRead = <T,>(): BackupAvailabilityRead<T> => ({ pages: [], index: 0, loading: false });
const empty = (): State => ({ list: emptyRead(), backup: emptyRead(), replicas: emptyRead(), databases: emptyRead() });
const snapshotSources = (kind: BackupAvailabilityKind): readonly BackupAvailabilitySource[] => kind === "backup" ? ["backup"] : ["replicas", "databases"];

/** Supporting legacy evidence is dormant until opened or explicitly pinned.
 * Its independent streams share one exact receipt identity and denial fence. */
export function useBackupAvailabilityWindowHistory(binding: BackupAvailabilityBinding | undefined, kind: BackupAvailabilityKind,
  selected?: BackupAvailabilitySelection | null, manualRefresh = 0, accessContext = "") {
  const context = JSON.stringify([kind, backupAvailabilityContextIdentity(binding), accessContext]);
  function routeWindow() {
    if (!binding) return undefined;
    const [path, query] = location.hash.replace(/^#\/?/u, "").split("?"), p = new URLSearchParams(query);
    const targets = p.getAll("target"), checks = p.getAll("check"), ranges = p.getAll("range"), from = p.getAll("from"), to = p.getAll("to");
    if (path !== "operations" || targets.length !== 1 || targets[0]!.toLowerCase() !== binding.targetId.toLowerCase() || checks.length !== 1 || checks[0] !== (kind === "backup" ? "backups" : "availability") || ranges.length !== 1) return undefined;
    if (ranges[0] === "custom") return from.length === 1 && to.length === 1 && backupAvailabilityContextIdentity({ ...binding, fromUtc: from[0]!, toUtc: to[0]! }) === backupAvailabilityContextIdentity(binding) ? "received-window" : undefined;
    return ["1h", "6h", "24h", "7d"].includes(ranges[0]!) && !from.length && !to.length ? ranges[0] : undefined;
  }
  function rawPinKey() {
    const pin = readBackupAvailabilitySelection(location.hash, kind, binding?.targetId);
    if (pin !== null) return backupAvailabilitySelectionIdentity(pin);
    const p = new URLSearchParams(location.hash.split("?")[1]), prefix = kind === "backup" ? "backup" : "availability";
    return JSON.stringify(["invalid", p.getAll(prefix + "Run"), p.getAll(prefix + "Revision")]);
  }
  const renderedRouteWindow = routeWindow(), renderedPinKey = rawPinKey();
  const invalidIntentKey = selected === null ? renderedPinKey : "";
  const frame = useRef({ context }); if (frame.current.context !== context) frame.current = { context };
  const renderedFrame = frame.current, mounted = useRef(false), previousContext = useRef(context), previousSelection = useRef("");
  const previousInvalidIntent = useRef(invalidIntentKey);
  const epoch = useRef(0), exactEpoch = useRef(0), owners = useRef(new Map<BackupAvailabilitySource, AbortController>());
  const authority = JSON.stringify([kind, binding?.targetId.toLowerCase(), binding?.targetRevision, accessContext]);
  const denial = useRef<string | undefined>(undefined), investigated = useRef<string | undefined>(undefined);
  const currentIntent = useRef(selected); currentIntent.current = selected;
  const [state, setState] = useState(empty), received = useRef(state), [denied, setDenied] = useState(false);
  const [fold, setFold] = useState({ context, open: selected !== undefined });
  const opened = fold.context === context ? fold.open : selected !== undefined;
  const effective = selected === null ? null : selected ?? (previousContext.current === context ? state.list.pages[0]?.items[0] : undefined);
  const selectionIdentity = backupAvailabilitySelectionIdentity(effective), currentSelection = useRef(selectionIdentity); currentSelection.current = selectionIdentity;
  function owns() {
    const route = routeWindow(), pin = readBackupAvailabilitySelection(location.hash, kind, binding?.targetId);
    const matchesPin = pin === undefined ? currentIntent.current === undefined : pin === null ? currentIntent.current === null && rawPinKey() === renderedPinKey : backupAvailabilitySelectionIdentity(pin) === backupAvailabilitySelectionIdentity(currentIntent.current ?? effective);
    return mounted.current && frame.current === renderedFrame && route !== undefined && (route === renderedRouteWindow || route === "received-window") && matchesPin;
  }
  function publish(change: (old: State) => State) { const next = change(received.current); received.current = next; setState(next); }
  function stop(source?: BackupAvailabilitySource) {
    if (source) { owners.current.get(source)?.abort(); owners.current.delete(source); }
    else { epoch.current++; exactEpoch.current++; for (const owner of owners.current.values()) owner.abort(); owners.current.clear(); }
  }
  useLayoutEffect(() => { mounted.current = true; return () => { mounted.current = false; stop(); }; }, []);
  useLayoutEffect(() => {
    if (previousContext.current === context) return;
    previousContext.current = context; previousSelection.current = selectionIdentity; investigated.current = undefined;
    stop(); publish(empty); setFold({ context, open: selected !== undefined });
    if (binding && denial.current !== undefined && denial.current !== authority) { denial.current = undefined; setDenied(false); }
  }, [context]);
  useLayoutEffect(() => {
    if (previousSelection.current === selectionIdentity && previousInvalidIntent.current === invalidIntentKey) return;
    previousSelection.current = selectionIdentity; previousInvalidIntent.current = invalidIntentKey; exactEpoch.current++;
    if (owners.current.has("list")) { stop("list"); publish(old => ({ ...old, list: { ...old.list, loading: false, error: "The supporting receipt selection changed. Retry this exact list read; received pages remain available." } })); }
    for (const source of snapshotSources(kind)) stop(source);
    publish(old => ({ ...old, backup: emptyRead(), replicas: emptyRead(), databases: emptyRead() }));
    if (selected !== undefined) setFold({ context, open: true });
  }, [selectionIdentity, invalidIntentKey]);
  function active(source: BackupAvailabilitySource, owner: AbortController, version: number, exactVersion: number, identity: string, pinKey: string) {
    const currentPinKey = rawPinKey();
    return owns() && denial.current === undefined && !owner.signal.aborted && owners.current.get(source) === owner && epoch.current === version &&
      exactEpoch.current === exactVersion && (currentPinKey === pinKey || pinKey === "default" && currentPinKey === identity) &&
      (source === "list" || currentSelection.current === identity);
  }
  function refuse(failure: unknown) {
    if (!(failure instanceof ApiRequestError) || ![401, 403].includes(failure.status)) return false;
    denial.current = authority; stop(); publish(empty); setDenied(true); return true;
  }
  async function read(source: BackupAvailabilitySource, cursor?: string, replace = false, index = 0) {
    const exact = currentIntent.current === null ? null : currentIntent.current ?? received.current.list.pages[0]?.items[0];
    if (!binding || !owns() || denial.current !== undefined || owners.current.has(source) ||
      source !== "list" && (!snapshotSources(kind).includes(source) || !validBackupAvailabilitySelection(exact))) return;
    const prior = replace ? [] : received.current[source].pages;
    if (prior.length >= (source === "list" ? maximumBackupAvailabilityReceiptPages : maximumBackupAvailabilitySnapshotPages)) return;
    const owner = new AbortController(), version = epoch.current, exactVersion = exactEpoch.current, identity = backupAvailabilitySelectionIdentity(exact), pinKey = rawPinKey();
    owners.current.set(source, owner);
    publish(old => ({ ...old, [source]: { ...old[source], loading: true, error: undefined, errorStatus: undefined, attempt: { cursor, replace, index } } }));
    try {
      if (source === "list") {
        const page = await getBackupAvailabilityReceipts(binding, kind, owner.signal, prior as readonly BackupAvailabilityReceiptPage[], cursor);
        if (!active(source, owner, version, exactVersion, identity, pinKey)) return;
        if (prior.length + 1 === maximumBackupAvailabilityReceiptPages && page.nextCursor !== null) throw new Error("The legacy receipt page bound was reached. Narrow the shared window; received evidence remains available.");
        const pages = [...prior as readonly BackupAvailabilityReceiptPage[], page];
        if (currentIntent.current === undefined && backupAvailabilitySelectionIdentity(received.current.list.pages[0]?.items[0]) !== backupAvailabilitySelectionIdentity(pages[0]?.items[0])) {
          exactEpoch.current++; for (const child of snapshotSources(kind)) stop(child);
          publish(old => ({ ...old, backup: emptyRead(), replicas: emptyRead(), databases: emptyRead() }));
        }
        publish(old => ({ ...old, list: { pages, index, loading: false } }));
      } else {
        const page = await getBackupAvailabilitySnapshot(binding, kind, source, exact!, owner.signal, prior as readonly BackupAvailabilitySnapshotPage[], cursor);
        if (!active(source, owner, version, exactVersion, identity, pinKey)) return;
        if (prior.length + 1 === maximumBackupAvailabilitySnapshotPages && page.nextCursor !== null) throw new Error("The legacy observation page bound was reached. Refresh this exact source; received evidence remains available.");
        publish(old => ({ ...old, [source]: { pages: [...prior as readonly BackupAvailabilitySnapshotPage[], page], index, loading: false } }));
      }
    } catch (failure) {
      if (!active(source, owner, version, exactVersion, identity, pinKey) || refuse(failure)) return;
      publish(old => ({ ...old, [source]: { ...old[source], loading: false, errorStatus: failure instanceof ApiRequestError ? failure.status : undefined,
        error: failure instanceof Error ? failure.message : "Legacy source evidence is unavailable. Retry this exact read." } }));
    } finally { if (owners.current.get(source) === owner) owners.current.delete(source); }
  }
  const enabled = opened || selected !== undefined;
  useEffect(() => { if (enabled && denial.current === undefined && !received.current.list.pages.length && !received.current.list.error) void read("list", undefined, true); }, [context, enabled]);
  useEffect(() => {
    if (!enabled || denial.current !== undefined) return;
    for (const source of snapshotSources(kind)) if (!received.current[source].pages.length && !received.current[source].error) void read(source, undefined, true);
  }, [context, selectionIdentity, enabled]);
  const previousRefresh = useRef(manualRefresh);
  useEffect(() => {
    if (previousRefresh.current === manualRefresh) return;
    previousRefresh.current = manualRefresh;
    if (enabled && denial.current === undefined && investigated.current !== context && currentIntent.current === undefined) { void read("list", undefined, true); for (const source of snapshotSources(kind)) void read(source, undefined, true); }
  }, [manualRefresh]);
  function retain() { if (owns()) investigated.current = context; }
  function setOpen(open: boolean) { if (!owns()) return; if (open) retain(); setFold({ context, open }); }
  function navigate(index: number, source: BackupAvailabilitySource = "list") {
    if (!owns() || denial.current !== undefined || owners.current.has(source) || index < 0) return;
    retain(); const old = received.current[source];
    if (old.pages[index]) publish(value => ({ ...value, [source]: { ...value[source], index } }));
    else if (index === old.pages.length && old.pages.at(-1)?.nextCursor) void read(source, old.pages.at(-1)!.nextCursor!, false, index);
  }
  function cancel(source: BackupAvailabilitySource) {
    if (!owns() || !owners.current.has(source)) return;
    retain(); stop(source); publish(old => ({ ...old, [source]: { ...old[source], loading: false, error: "Waiting cancelled. Received legacy evidence remains; retry preserves the exact receipt and continuation." } }));
  }
  function retry(source: BackupAvailabilitySource) { if (!owns()) return; const attempt = received.current[source].attempt; if (attempt) void read(source, attempt.cursor, attempt.replace, attempt.index); }
  function retryAccess() {
    if (!binding || !owns()) return;
    denial.current = undefined; setDenied(false); void read("list", undefined, true);
    if (currentIntent.current) for (const source of snapshotSources(kind)) void read(source, undefined, true);
  }
  const visible = previousContext.current === context, exactVisible = previousSelection.current === selectionIdentity;
  return { kind, contextKey: context, selectionIdentity, selected: effective, opened, setOpen, denied, retain, navigate, cancel, retry,
    refresh: (source: BackupAvailabilitySource) => void read(source, undefined, true), retryAccess,
    list: visible ? state.list : emptyRead<BackupAvailabilityReceiptPage>(),
    backup: visible && exactVisible ? state.backup : emptyRead<BackupAvailabilitySnapshotPage>(),
    replicas: visible && exactVisible ? state.replicas : emptyRead<BackupAvailabilitySnapshotPage>(),
    databases: visible && exactVisible ? state.databases : emptyRead<BackupAvailabilitySnapshotPage>() };
}
export type BackupAvailabilityWindowHistoryRead = ReturnType<typeof useBackupAvailabilityWindowHistory>;
