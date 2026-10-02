import { useEffect, useRef, useState } from "react";
import type { AdminListPage } from "./alertAdminListsApi";
import { adminRestorePageLimit, appendAdminPage } from "./adminSelectionModel.ts";

export function useAlertAdminList<T>(
  targetId: string, refresh: number, load: (targetId: string, signal: AbortSignal, cursor?: string) => Promise<AdminListPage<T>>,
  id: (item: T) => string, selectedId?: string | null,
) {
  const [reload, setReload] = useState(0);
  const context = `${targetId.toLowerCase()}:${refresh}:${reload}`;
  const [received, setReceived] = useState<{ context: string; page: AdminListPage<T>; pages: number; cursors: readonly string[] }>();
  const current = received?.context === context ? received : undefined;
  const [initialStatus, setInitialStatus] = useState<{ context: string; error?: string }>();
  const [moreStatus, setMoreStatus] = useState<{ context: string; loading: boolean; error?: string }>();
  const moreRequest = useRef<AbortController | null>(null);
  const activeContext = useRef(context);
  activeContext.current = context;
  const loading = !current && initialStatus?.context !== context;
  const loadingMore = moreStatus?.context === context && moreStatus.loading;
  const error = moreStatus?.context === context ? moreStatus.error : initialStatus?.context === context ? initialStatus.error : undefined;

  useEffect(() => {
    const controller = new AbortController();
    moreRequest.current?.abort();
    moreRequest.current = null;
    setReceived(undefined);
    setInitialStatus(undefined);
    setMoreStatus(undefined);
    void load(targetId, controller.signal)
      .then(page => {
        if (!controller.signal.aborted && activeContext.current === context) {
          setReceived({ context, page, pages: 1, cursors: [] });
          setInitialStatus({ context });
        }
      })
      .catch(failure => {
        if (!controller.signal.aborted && activeContext.current === context)
          setInitialStatus({ context, error: failure instanceof Error ? failure.message : "Administration data is unavailable." });
      });
    return () => { controller.abort(); moreRequest.current?.abort(); };
  }, [targetId, refresh, reload, load]);

  async function loadMore() {
    if (!current?.page.nextCursor || moreRequest.current && !moreRequest.current.signal.aborted) return;
    const cursor = current.page.nextCursor;
    if (current.cursors.includes(cursor)) {
      setMoreStatus({ context, loading: false, error: "The listing repeated a page. Refresh to load it again." });
      return;
    }
    const controller = new AbortController();
    moreRequest.current = controller;
    setMoreStatus({ context, loading: true });
    try {
      const page = await load(targetId, controller.signal, cursor);
      if (controller.signal.aborted || activeContext.current !== context) return;
      const appended = appendAdminPage(current.page, page, id);
      setReceived({ context, page: appended, pages: current.pages + 1, cursors: [...current.cursors, cursor] });
      setMoreStatus({ context, loading: false });
    } catch (failure) {
      if (!controller.signal.aborted && activeContext.current === context)
        setMoreStatus({ context, loading: false, error: failure instanceof Error ? failure.message : "Administration data is unavailable." });
    } finally {
      if (moreRequest.current === controller) moreRequest.current = null;
    }
  }

  const findingSelection = typeof selectedId === "string" && !current?.page.items.some(item => id(item).toLowerCase() === selectedId.toLowerCase()) &&
    (loading || Boolean(current?.page.nextCursor && current.pages < adminRestorePageLimit && !error));
  useEffect(() => {
    if (findingSelection && current && !loadingMore && !error) void loadMore();
  }, [findingSelection, current, loadingMore, error, selectedId]);

  return {
    items: current?.page.items ?? [], nextCursor: current?.page.nextCursor,
    loading, loadingMore, findingSelection, error, loadMore,
    retry: () => setReload(value => value + 1),
  };
}
