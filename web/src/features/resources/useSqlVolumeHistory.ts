import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { ApiRequestError } from "../../apiClient.ts";
import { getSqlVolumeHistory } from "./sqlVolumeHistoryApi.ts";
import { previousVolumeHistoryWindow, volumeHistoryWindow, type VolumeHistoryBinding, type VolumeHistoryIdentity, type VolumeHistoryReadPage } from "./sqlVolumeHistoryModel.ts";
export type VolumeHistorySource = "catalog" | "selected" | "previous";
export interface VolumeHistoryReadState { readonly pages: readonly VolumeHistoryReadPage[]; readonly index: number; readonly loading: boolean;
  readonly error?: string; readonly attempt?: { readonly cursor?: string; readonly replace: boolean; readonly index: number } }
const empty = (): Record<VolumeHistorySource, VolumeHistoryReadState> => ({ catalog: { pages: [], index: 0, loading: false }, selected: { pages: [], index: 0, loading: false }, previous: { pages: [], index: 0, loading: false } });
export function useSqlVolumeHistory(instanceId: string, targetRevision: number, fromUtc: string, toUtc: string, compare: boolean, selectedVolume: VolumeHistoryIdentity | null | undefined) {
  const context = JSON.stringify([instanceId.toLowerCase(), targetRevision, fromUtc, toUtc, compare, selectedVolume]), latest = useRef(context); latest.current = context;
  const epoch = useRef(0), mounted = useRef(false), deniedRef = useRef(false), owners = useRef(new Map<VolumeHistorySource, AbortController>());
  const [denied, setDenied] = useState(false), [reads, setReads] = useState(empty), received = useRef(reads), previousContext = useRef(context);
  const deniedContext = useRef<string | undefined>(undefined), authorityContext = JSON.stringify([instanceId.toLowerCase(), targetRevision]);
  const change = (source: VolumeHistorySource, update: (old: VolumeHistoryReadState) => VolumeHistoryReadState) => {
    const next = { ...received.current, [source]: update(received.current[source]) }; received.current = next; setReads(next);
  };
  const stop = () => { epoch.current++; for (const controller of owners.current.values()) controller.abort(); owners.current.clear(); };
  useLayoutEffect(() => { mounted.current = true; return () => { mounted.current = false; stop(); }; }, []);
  useLayoutEffect(() => {
    if (previousContext.current === context) return; previousContext.current = context; stop(); const next = empty(); received.current = next; setReads(next);
    if (deniedRef.current && deniedContext.current !== authorityContext) { deniedRef.current = false; deniedContext.current = undefined; setDenied(false); }
  }, [context]);
  const active = (source: VolumeHistorySource, controller: AbortController, captured: number, key: string) => mounted.current && !deniedRef.current && !controller.signal.aborted
    && latest.current === key && epoch.current === captured && owners.current.get(source) === controller;
  function refuseAccess(failure: unknown) {
    if (!(failure instanceof ApiRequestError) || ![401,403].includes(failure.status)) return false;
    deniedRef.current = true; deniedContext.current = authorityContext; stop(); const next = empty(); received.current = next; setReads(next); setDenied(true); return true;
  }
  async function read(source: VolumeHistorySource, cursor?: string, replace = false, index = 0) {
    if (!mounted.current || deniedRef.current || owners.current.has(source) || source !== "catalog" && !selectedVolume) return;
    const old = received.current[source]; if (!replace && old.pages.length >= 40) return;
    let binding: VolumeHistoryBinding;
    try { const window = source === "previous" ? previousVolumeHistoryWindow(fromUtc, toUtc) : volumeHistoryWindow(fromUtc, toUtc);
      binding = { instanceId, targetRevision, ...window, ...(source === "catalog" ? {} : { selected: selectedVolume! }) }; }
    catch { change(source, state => ({ ...state, error: "Choose a valid shared UTC window of at most 31 days.", loading: false })); return; }
    const controller = new AbortController(), captured = epoch.current, key = context; owners.current.set(source, controller);
    change(source, state => ({ ...state, loading: true, error: undefined, attempt: { cursor, replace, index } }));
    try {
      const prior = replace ? [] : old.pages, page = await getSqlVolumeHistory(binding, controller.signal, prior, cursor);
      if (!active(source, controller, captured, key)) return;
      const pages = replace ? [page] : [...prior, page], maximum = source === "catalog" ? 1000 : 10000;
      if (pages.reduce((count, p) => count + p.items.length, 0) > maximum || pages.length === 40 && page.nextCursor !== null)
        throw new Error("This history read exceeds its supported bound. Narrow the shared window; received pages remain evidence.");
      change(source, () => ({ pages, index, loading: false }));
    } catch (failure) {
      if (!active(source, controller, captured, key) || refuseAccess(failure)) return;
      change(source, state => ({ ...state, loading: false, error: failure instanceof Error ? failure.message : "Volume history is unavailable. Retry this read." }));
    } finally { if (owners.current.get(source) === controller) owners.current.delete(source); }
  }
  useEffect(() => { if (!deniedRef.current) { void read("catalog", undefined, true); if (selectedVolume) { void read("selected", undefined, true); if (compare) void read("previous", undefined, true); } } }, [context]);
  function navigate(source: VolumeHistorySource, index: number) {
    if (owners.current.has(source) || deniedRef.current) return;
    const state = received.current[source]; if (index < 0) return;
    if (state.pages[index]) { change(source, old => ({ ...old, index })); return; }
    if (index !== state.pages.length || state.pages.length >= 40) return;
    const cursor = state.pages.at(-1)?.nextCursor; if (cursor) void read(source, cursor, false, index);
  }
  function cancel(source: VolumeHistorySource) {
    const controller = owners.current.get(source); if (!controller) return; controller.abort(); owners.current.delete(source);
    change(source, old => ({ ...old, loading: false, error: "Waiting cancelled. Received pages remain; retry keeps the exact read and continuation." }));
  }
  function retry(source: VolumeHistorySource) { const attempt = received.current[source].attempt; if (attempt) void read(source, attempt.cursor, attempt.replace, attempt.index); }
  function refresh(source: VolumeHistorySource) { if (!owners.current.has(source)) void read(source, undefined, true); }
  function retryAccess() { deniedRef.current = false; deniedContext.current = undefined; setDenied(false); void read("catalog", undefined, true); if (selectedVolume) { void read("selected", undefined, true); if (compare) void read("previous", undefined, true); } }
  // Render ownership changes immediately, before passive cleanup or late promise completion.
  const visible = previousContext.current === context ? reads : empty();
  return { reads: visible, denied, navigate, cancel, retry, refresh, retryAccess };
}
