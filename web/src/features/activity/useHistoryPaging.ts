import { useEffect, useRef, useState } from "react";
import type { ActivityPage } from "./activityTypes";

type HistoryWindow = { readonly fromUtc: string; readonly toUtc: string };

// These cursors select mutable history within a fixed window. Retaining a page
// preserves the investigation; it does not turn the repository into a snapshot.
export function useHistoryPaging<T>(instanceId: string, initialPage: ActivityPage<T>,
  latestWindow: HistoryWindow | undefined,
  read: (instanceId: string, window: HistoryWindow, signal: AbortSignal, cursor?: string) => Promise<ActivityPage<T>>, initialGeneration = 0, latestGeneration = 0) {
  const [page, setPage] = useState(initialPage);
  const [cursors, setCursors] = useState<readonly (string | undefined)[]>([undefined]);
  const [pageIndex, setPageIndex] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>();
  const [retained, setRetained] = useState(false);
  const pinned = useRef(false);
  const latestFloor = useRef<number | undefined>(undefined);
  const currentGeneration = useRef(latestGeneration); currentGeneration.current = latestGeneration;
  const request = useRef<AbortController | undefined>(undefined);
  useEffect(() => () => request.current?.abort(), []);
  useEffect(() => {
    if (!pinned.current && !request.current && (latestFloor.current === undefined || initialGeneration > latestFloor.current)) setPage(initialPage);
  }, [initialPage, initialGeneration]);
  const window = page.fromUtc && page.toUtc ? { fromUtc: page.fromUtc, toUtc: page.toUtc } : undefined;

  async function load(index: number, selectedWindow: HistoryWindow, cursor?: string, latest = false) {
    if (request.current && !latest) return;
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    pinned.current = true;
    setRetained(true); setLoading(true); setError(undefined);
    try {
      const next = await read(instanceId, selectedWindow, controller.signal, cursor);
      if (controller.signal.aborted || request.current !== controller) return;
      setPage(next); setPageIndex(index);
      setCursors(previous => latest ? [undefined] : [...previous.slice(0, index), cursor]);
      if (latest) {
        // Aggregate supporting reads started before this receipt can finish later.
        // Only a subsequent workspace generation may replace explicit recovery.
        latestFloor.current = currentGeneration.current;
        pinned.current = false; setRetained(false);
      }
    } catch (failure: unknown) {
      if (!controller.signal.aborted && request.current === controller)
        setError(failure instanceof Error ? failure.message : "History could not be loaded. Try again.");
    } finally {
      if (!controller.signal.aborted && request.current === controller) {
        request.current = undefined; setLoading(false);
      }
    }
  }
  return { page, pageIndex, loading, error, retained, window,
    navigate: (index: number, cursor?: string) => window ? load(index, window, cursor) : Promise.resolve(),
    previousCursor: cursors[pageIndex - 1],
    returnToLatest: () => latestWindow ? load(0, latestWindow, undefined, true) : Promise.resolve(),
    latestAvailable: latestWindow !== undefined,
  };
}
