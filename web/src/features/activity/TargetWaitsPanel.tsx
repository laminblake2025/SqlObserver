import { useEffect, useMemo, useRef, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import type { OverviewScope } from "../overview/overviewTypes";
import { getCurrentServerWaitPage, getServerWaitHistoryPage } from "./activityApi";
import type { ActivityPage, ActivityWait, ServerWaitHistoryItem } from "./activityTypes";
import { resolveActivityWindow } from "./activityWindowModel";
import { ActivityTable, Evidence, WaitCategoryChart, WaitHistory } from "./TargetActivityPanel";

export interface TargetWaitsPanelProps {
  readonly instanceId: string;
  readonly displayName: string;
  readonly onClose: () => void;
  readonly scope: OverviewScope;
  readonly refresh: number;
}

export function TargetWaitsPanel({ instanceId, displayName, onClose, scope, refresh }: TargetWaitsPanelProps) {
  const { mode } = useTimeDisplay();
  const selection = useMemo(() => resolveActivityWindow(scope, Date.now()),
    [scope.range, scope.from, scope.to, refresh]);
  const window = selection.state === "available" ? selection.window : undefined;
  const [history, setHistory] = useState<ActivityPage<ServerWaitHistoryItem>>();
  const [historyError, setHistoryError] = useState<string>();
  const [historyLoading, setHistoryLoading] = useState(false);

  useEffect(() => {
    const controller = new AbortController();
    setHistory(undefined);
    setHistoryError(undefined);
    if (!window) { setHistoryLoading(false); return () => controller.abort(); }
    setHistoryLoading(true);
    void getServerWaitHistoryPage(instanceId, window, controller.signal)
      .then(page => { if (!controller.signal.aborted) setHistory(page); })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setHistoryError(error instanceof Error ? error.message : "Wait history is unavailable.");
      })
      .finally(() => { if (!controller.signal.aborted) setHistoryLoading(false); });
    return () => controller.abort();
  }, [instanceId, window?.fromUtc, window?.toUtc, refresh]);

  return <section className="activity-screen" aria-labelledby="waits-heading">
    <div className="screen-intro"><div><p className="eyebrow">Server · waits</p><h2 id="waits-heading">Waits for {displayName}</h2>
      <p>Wait totals are cumulative SQL Server counters. Historical rows show a delta only when the preceding collector run is comparable; a reset or missing baseline remains unknown.</p>
    </div><button className="secondary-button" onClick={onClose} type="button">Close</button></div>
    {scope.range !== "custom" && <CurrentWaits key={`${instanceId}:${refresh}`} instanceId={instanceId} refresh={refresh} />}
    <section className="panel" aria-label="Selected wait history">
      <h3>Selected-window history</h3>
      {window && <p className="activity-evidence">{formatDisplayTime(window.fromUtc, mode)} to {formatDisplayTime(window.toUtc, mode)}. Rows are paged; the table does not claim full-window coverage until its final page.</p>}
      {selection.state === "unavailable" && <p role="status">{selection.message}</p>}
      {historyLoading && <p role="status">Loading wait history…</p>}
      {historyError && <p role="alert">{historyError} Refresh to retry.</p>}
      {history && <WaitHistory key={`${instanceId}:${history.fromUtc}:${history.toUtc}:${history.repositoryTimeUtc}`} instanceId={instanceId} initialPage={history} />}
    </section>
  </section>;
}

function CurrentWaits({ instanceId, refresh }: { readonly instanceId: string; readonly refresh: number }) {
  const [page, setPage] = useState<ActivityPage<ActivityWait>>();
  const [pageIndex, setPageIndex] = useState(0);
  const [cursors, setCursors] = useState<readonly (string | undefined)[]>([undefined]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>();
  const request = useRef<AbortController | undefined>(undefined);
  useEffect(() => () => request.current?.abort(), []);
  useEffect(() => {
    const controller = new AbortController();
    request.current = controller;
    setPage(undefined); setPageIndex(0); setCursors([undefined]); setError(undefined); setLoading(true);
    void getCurrentServerWaitPage(instanceId, controller.signal)
      .then(value => { if (!controller.signal.aborted) setPage(value); })
      .catch((failure: unknown) => {
        if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : "Current waits are unavailable.");
      })
      .finally(() => { if (!controller.signal.aborted) { request.current = undefined; setLoading(false); } });
    return () => controller.abort();
  }, [instanceId, refresh]);

  async function navigate(index: number, cursor?: string) {
    if (request.current) return;
    const controller = new AbortController();
    request.current = controller;
    setLoading(true); setError(undefined);
    try {
      const next = await getCurrentServerWaitPage(instanceId, controller.signal, cursor);
      if (controller.signal.aborted) return;
      setPage(next); setPageIndex(index);
      setCursors(previous => [...previous.slice(0, index), cursor]);
    } catch (failure: unknown) {
      if (!controller.signal.aborted) setError(failure instanceof Error ? failure.message : "Current waits could not be loaded.");
    } finally {
      if (!controller.signal.aborted) { request.current = undefined; setLoading(false); }
    }
  }

  return <section className="panel" aria-label="Current server waits" aria-busy={loading}>
    <h3>Live wait snapshot</h3>
    {loading && !page && <p role="status">Loading current waits…</p>}
    {error && <p role="alert">{error} Refresh or retry navigation.</p>}
    {page && <>
      <Evidence page={page} />
      <WaitCategoryChart page={page} />
      <ActivityTable title="Wait types" columns={["Wait type", "Tasks", "Total wait ms", "Signal wait ms", "Comparable delta ms", "Baseline"]}
        rows={page.items.map(item => [item.waitType, item.waitingTasksCount, item.waitTimeMilliseconds,
          item.signalWaitTimeMilliseconds, item.resetDetected ? "reset" : item.waitTimeMillisecondsDelta ?? "—",
          item.baselineAvailable ? "available" : "not available"])} />
      <p role="status">Page {pageIndex + 1} · {page.items.length} wait types{page.nextCursor ? " · More rows available" : " · End of snapshot"}</p>
      <nav aria-label="Current wait pages">
        <button type="button" disabled={loading || pageIndex === 0} onClick={() => void navigate(0)}>First wait page</button>{" "}
        <button type="button" disabled={loading || pageIndex === 0} onClick={() => void navigate(pageIndex - 1, cursors[pageIndex - 1])}>Previous wait page</button>{" "}
        <button type="button" disabled={loading || !page.nextCursor} onClick={() => void navigate(pageIndex + 1, page.nextCursor)}>Next wait page</button>
      </nav>
    </>}
  </section>;
}
