import { useEffect, useRef, useState } from "react";
import type { AdminListPage } from "./alertAdminListsApi";

export function useAlertAdminList<T>(
  targetId: string, refresh: number, load: (targetId: string, signal: AbortSignal, cursor?: string) => Promise<AdminListPage<T>>,
  id: (item: T) => string,
) {
  const [items, setItems] = useState<readonly T[]>([]);
  const [nextCursor, setNextCursor] = useState<string>();
  const [listingStartedUtc, setListingStartedUtc] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [loadingMore, setLoadingMore] = useState(false);
  const [error, setError] = useState<string>();
  const [reload, setReload] = useState(0);
  const moreRequest = useRef<AbortController | null>(null);

  useEffect(() => {
    const controller = new AbortController();
    moreRequest.current?.abort();
    setItems([]);
    setNextCursor(undefined);
    setListingStartedUtc(undefined);
    setError(undefined);
    setLoading(true);
    setLoadingMore(false);
    void load(targetId, controller.signal)
      .then(page => { if (!controller.signal.aborted) { setItems(page.items); setNextCursor(page.nextCursor); setListingStartedUtc(page.listingStartedUtc); setLoading(false); } })
      .catch(failure => { if (!controller.signal.aborted) { setError(failure instanceof Error ? failure.message : "Administration data is unavailable."); setLoading(false); } });
    return () => { controller.abort(); moreRequest.current?.abort(); };
  }, [targetId, refresh, reload, load]);

  async function loadMore() {
    if (!nextCursor || loadingMore || moreRequest.current && !moreRequest.current.signal.aborted) return;
    const controller = new AbortController();
    moreRequest.current = controller;
    setLoadingMore(true);
    try {
      const page = await load(targetId, controller.signal, nextCursor);
      if (controller.signal.aborted) return;
      if (page.listingStartedUtc !== listingStartedUtc || page.items.some(item => items.some(existing => id(existing) === id(item))))
        throw new Error("The listing changed. Refresh to load it again.");
      setItems(current => [...current, ...page.items]);
      setNextCursor(page.nextCursor);
      setError(undefined);
    } catch (failure) {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : "Administration data is unavailable.");
    } finally {
      if (!controller.signal.aborted) setLoadingMore(false);
      if (moreRequest.current === controller) moreRequest.current = null;
    }
  }

  return { items, nextCursor, loading, loadingMore, error, loadMore, retry: () => setReload(value => value + 1) };
}
