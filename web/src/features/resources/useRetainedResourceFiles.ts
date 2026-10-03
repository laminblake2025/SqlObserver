import { useEffect, useRef, useState } from "react";
import { getDatabaseFileHealth, HealthRequestError } from "../health/healthApi";
import type { DatabaseFileHealthPage, DatabaseFileHealthSummary } from "../health/healthTypes";
import { resourceFileCursor } from "./resourceFileCursor";

interface Receipt { key: string; page: DatabaseFileHealthPage; index: number; cursors: readonly (string | undefined)[]; }
interface RequestState { key: string; loading: boolean; error?: string; }
const mismatch = "File evidence changed or returned an invalid continuation. Return to the latest file snapshot to retry.";
const compare = (left: DatabaseFileHealthSummary, right: DatabaseFileHealthSummary) =>
  left.databaseId - right.databaseId || left.fileId - right.fileId;

// Snapshot run/revision are carried inside the native opaque cursor, not in the
// response. Keep received pages locally; never treat a cursorless GET as the
// first page of the already-read snapshot or infer identity from repository time.
export function useRetainedResourceFiles(instanceId: string, context: string, refresh: number, selected: boolean) {
  const current = useRef(context); current.current = context;
  const request = useRef<AbortController | undefined>(undefined);
  const explicitRequest = useRef(false);
  const cache = useRef<{ key: string; pages: Map<number, DatabaseFileHealthPage>; browsing: boolean }>({ key: context, pages: new Map(), browsing: false });
  const [receipt, setReceipt] = useState<Receipt>();
  const [state, setState] = useState<RequestState>();
  const [browsing, setBrowsing] = useState<{ key: string; value: boolean }>();
  const loaded = receipt?.key === context ? receipt : undefined;
  const requestState = state?.key === context ? state : undefined;
  const retained = Boolean(loaded && (selected || browsing?.key === context && browsing.value));
  const selection = useRef(selected); selection.current = selected;

  useEffect(() => () => { request.current?.abort(); request.current = undefined; }, []);
  useEffect(() => {
    if (selected && loaded && request.current && !explicitRequest.current && !cache.current.browsing) {
      request.current.abort(); request.current = undefined;
      setState({ key: context, loading: false });
    }
  }, [context, selected]);
  useEffect(() => {
    if (cache.current.key !== context) {
      request.current?.abort(); request.current = undefined;
      cache.current = { key: context, pages: new Map(), browsing: false };
    }
    if (request.current || cache.current.pages.size && (cache.current.browsing || selection.current)) return;
    void loadLatest(false);
    // Continuations are owned by their request controller; a workspace tick
    // does not abort them. Context and unmount are checked independently.
  }, [context, refresh]);

  function validate(page: DatabaseFileHealthPage, after?: DatabaseFileHealthSummary, cursors: readonly (string | undefined)[] = []) {
    if (page.instanceId.toLowerCase() !== instanceId.toLowerCase() || page.collector.collectorId !== "database.files"
      || page.items.some(file => file.collector.collectorId !== "database.files")
      || page.nextCursor !== null && (!page.items.length || cursors.includes(page.nextCursor))) throw new Error(mismatch);
    let previous = after;
    for (const file of page.items) {
      if (previous && compare(file, previous) <= 0) throw new Error(mismatch);
      previous = file;
    }
    if (page.nextCursor !== null) {
      const next = resourceFileCursor(page.nextCursor, instanceId);
      if (!previous || next.databaseId !== previous.databaseId || next.fileId !== previous.fileId) throw new Error(mismatch);
      const requested = cursors.at(-1);
      if (requested) {
        const prior = resourceFileCursor(requested, instanceId);
        if (prior.run !== next.run || prior.revision !== next.revision) throw new Error(mismatch);
      }
    }
  }
  function failure(controller: AbortController, error: unknown) {
    if (controller.signal.aborted || current.current !== context || request.current !== controller) return;
    if (error instanceof HealthRequestError && (error.status === 401 || error.status === 403)) {
      setReceipt(undefined); cache.current.pages.clear();
      cache.current.browsing = true; setBrowsing({ key: context, value: true });
    }
    setState({ key: context, loading: false, error: error instanceof Error ? error.message : "File evidence is unavailable." });
  }
  async function loadLatest(explicit: boolean) {
    if (current.current !== context || request.current) return;
    const controller = new AbortController(); request.current = controller;
    explicitRequest.current = explicit;
    setState({ key: context, loading: true });
    try {
      const page = await getDatabaseFileHealth(instanceId, controller.signal);
      if (controller.signal.aborted || current.current !== context || request.current !== controller) return;
      if (!explicit && selection.current && cache.current.pages.size > 0) {
        setState({ key: context, loading: false }); return;
      }
      validate(page);
      cache.current.pages = new Map([[0, page]]); cache.current.browsing = false;
      setBrowsing({ key: context, value: false });
      setReceipt({ key: context, page, index: 0, cursors: [undefined] });
      setState({ key: context, loading: false });
    } catch (error) { failure(controller, error); }
    finally {
      if (current.current === context && request.current === controller) request.current = undefined;
    }
    // An unsuccessful explicit renewal keeps the previously displayed page and
    // its cursor chain. It never silently advances the investigation.
  }
  async function navigate(index: number, cursor?: string) {
    if (current.current !== context || request.current || !loaded || cache.current.key !== context) return;
    cache.current.browsing = true; setBrowsing({ key: context, value: true });
    const cached = cache.current.pages.get(index);
    if (cached) {
      setReceipt({ ...loaded, page: cached, index }); setState({ key: context, loading: false }); return;
    }
    if (index !== loaded.index + 1 || cursor === undefined || cursor !== loaded.page.nextCursor) return;
    const controller = new AbortController(); request.current = controller;
    setState({ key: context, loading: true });
    try {
      const page = await getDatabaseFileHealth(instanceId, controller.signal, cursor);
      if (controller.signal.aborted || current.current !== context || request.current !== controller) return;
      const cursors = [...loaded.cursors.slice(0, index), cursor];
      validate(page, loaded.page.items.at(-1), cursors);
      // Bound retained browser memory independently of native collector limits.
      if (cache.current.pages.size >= 400 || [...cache.current.pages.values()].reduce((count, item) => count + item.items.length, page.items.length) > 10_000)
        throw new Error("The retained file investigation reached its browsing limit. Return to the latest file snapshot to continue.");
      cache.current.pages.set(index, page);
      setReceipt({ key: context, page, index, cursors });
      setState({ key: context, loading: false });
    } catch (error) { failure(controller, error); }
    finally { if (current.current === context && request.current === controller) request.current = undefined; }
  }
  return { files: loaded?.page, filePageIndex: loaded?.index ?? 0, fileCursors: loaded?.cursors ?? [undefined],
    filesLoading: requestState?.loading ?? true, fileError: requestState?.error, retained,
    navigateFilePage: navigate, returnToLatestFiles: () => loadLatest(true) };
}
