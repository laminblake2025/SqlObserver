import { useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import type { ActivityPage } from "./activityTypes";
import { ActivityReadBoundary, activityAccessDenied, useActivityBoundary, useBoundarySnapshot, type ActivityReadOwner } from "./activityAccess.ts";

type HistoryWindow = { readonly fromUtc: string; readonly toUtc: string };

// Native history is mutable within a fixed window; retention preserves received evidence, not repository immutability.
export function useHistoryPaging<T>(instanceId: string, initialPage: ActivityPage<T>,
  latestWindow: HistoryWindow | undefined,
  read: (instanceId: string, window: HistoryWindow, signal: AbortSignal, cursor?: string) => Promise<ActivityPage<T>>,
  initialGeneration = 0, latestGeneration = 0, sharedAccess?: ActivityReadBoundary, accessSource = "history") {
  const localAccess = useActivityBoundary(instanceId.toLowerCase());
  const access = sharedAccess ?? localAccess;
  useBoundarySnapshot(access);
  const ownerKey = useId();
  const [page, setPage] = useState<ActivityPage<T> | undefined>(initialPage);
  const [cursors, setCursors] = useState<readonly (string | undefined)[]>([undefined]);
  const [pageIndex, setPageIndex] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>();
  const [retained, setRetained] = useState(false);
  const pinned = useRef(false);
  const rejectedInitial = useRef<ActivityPage<T> | undefined>(undefined);
  const currentInitial = useRef(initialPage); currentInitial.current = initialPage;
  const latestFloor = useRef<number | undefined>(undefined);
  const currentGeneration = useRef(latestGeneration); currentGeneration.current = latestGeneration;
  const request = useRef<ActivityReadOwner | undefined>(undefined);
  const currentAccess = useRef(access); currentAccess.current = access;
  useLayoutEffect(() => {
    const unsubscribe = access.subscribeInvalidation(() => {
      request.current = undefined; pinned.current = true; rejectedInitial.current = currentInitial.current;
      setPage(undefined); setCursors([undefined]); setPageIndex(0); setLoading(false); setError(undefined); setRetained(true);
    });
    return () => { access.cancel(request.current); request.current = undefined; unsubscribe(); };
  }, [access]);
  useEffect(() => {
    if (access.allowed(accessSource) && initialPage !== rejectedInitial.current && !pinned.current && !request.current &&
        (latestFloor.current === undefined || initialGeneration > latestFloor.current)) setPage(initialPage);
  }, [initialPage, initialGeneration, access, accessSource]);
  const visiblePage = access.allowed(accessSource) ? page : undefined;
  const window = visiblePage?.fromUtc && visiblePage.toUtc ? { fromUtc: visiblePage.fromUtc, toUtc: visiblePage.toUtc } : undefined;
  async function load(index: number, selectedWindow: HistoryWindow, cursor?: string, latest = false) {
    if (currentAccess.current !== access || request.current && !latest) return;
    access.cancel(request.current);
    const ticket = access.begin(accessSource, ownerKey, true); if (!ticket) return;
    request.current = ticket; pinned.current = true;
    setRetained(true); setLoading(true); setError(undefined);
    const current = () => access.current(ticket) && request.current === ticket && currentAccess.current === access;
    try {
      const next = await read(instanceId, selectedWindow, ticket.controller.signal, cursor);
      if (!current() || !access.recover(ticket)) return;
      setPage(next); setPageIndex(index);
      setCursors(previous => latest ? [undefined] : [...previous.slice(0, index), cursor]);
      if (latest) {
        // Only subsequent workspace generations may replace a validated explicit recovery.
        latestFloor.current = currentGeneration.current; pinned.current = false; setRetained(false);
      }
    } catch (failure) {
      if (!current()) return;
      if (activityAccessDenied(failure)) { access.deny(); return; }
      setError(failure instanceof Error ? failure.message : "History could not be loaded. Try again.");
    } finally {
      access.finish(ticket);
      if (request.current === ticket) { request.current = undefined; setLoading(false); }
    }
  }
  return { page: visiblePage, pageIndex, loading, error, retained, window, denied: !access.allowed(accessSource),
    navigate: (index: number, cursor?: string) => window ? load(index, window, cursor) : Promise.resolve(),
    previousCursor: cursors[pageIndex - 1],
    returnToLatest: () => latestWindow ? load(0, latestWindow, undefined, true) : Promise.resolve(),
    latestAvailable: latestWindow !== undefined,
  };
}
