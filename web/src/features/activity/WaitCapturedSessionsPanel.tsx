import { useEffect, useRef, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import type { OverviewScope } from "../overview/overviewTypes";
import { readLive, type LivePage, type LiveSnapshot } from "./liveActivityApi";
import { activitySelectionHref, activityUtc } from "./activitySelectionModel.ts";
import { boundedWaitSessionCaptures, capturedSessionWaitCategory, capturedWaitSnapshotExpired, loadCapturedWaitSessions,
  readSessionWaitCategory, sessionWaitCategories, sessionWaitCategoryHref, waitSessionHistoryWindow,
  readSessionWaitCapture, sessionWaitCaptureHref, validateWaitCaptureWindow,
  type CapturedWaitSessions, type SessionWaitCategory } from "./waitCapturedSessionsModel.ts";

export function WaitCapturedSessionsPanel({ instanceId, scope, timeWindow, refresh, selectionHash }: {
  readonly instanceId: string; readonly scope: OverviewScope;
  readonly timeWindow?: { readonly fromUtc: string; readonly toUtc: string };
  readonly refresh: number; readonly selectionHash: string;
}) {
  const { mode } = useTimeDisplay();
  const labelTime = (value: string) => formatDisplayTime(value, mode);
  const selectedCategory = readSessionWaitCategory(selectionHash, instanceId);
  const category = selectedCategory === undefined ? "Lock" : selectedCategory;
  const requestedCapture = readSessionWaitCapture(selectionHash, instanceId);
  const isRewind = scope.range === "custom";
  const [retry, setRetry] = useState(0);
  const historyKey = JSON.stringify([instanceId.toLowerCase(), scope.range, scope.from, scope.to, scope.databaseId,
    timeWindow?.fromUtc, timeWindow?.toUtc, refresh, retry]);
  const currentHistoryKey = useRef(historyKey); currentHistoryKey.current = historyKey;
  const [historyResult, setHistoryResult] = useState<{ key: string; captures?: readonly LiveSnapshot[]; omitted?: number; error?: string; unavailable?: string }>();
  const history = historyResult?.key === historyKey ? historyResult : undefined;
  const captures = history?.captures;
  const selectedCapture = requestedCapture ? { ...requestedCapture, truncated: false }
    : requestedCapture === undefined && isRewind ? captures?.[0] : undefined;
  const selectedCaptureId = selectedCapture?.id.toLowerCase();
  const pageKey = JSON.stringify([historyKey, selectedCapture?.id ?? "latest", selectedCapture?.observedUtc, category === null, requestedCapture === null, selectionHash]);
  const currentPageKey = useRef(pageKey); currentPageKey.current = pageKey;
  const [result, setResult] = useState<{ key: string; data?: CapturedWaitSessions; error?: string; loading: boolean }>();
  const evidence = result?.key === pageKey ? result : undefined;
  const data = evidence?.data;

  useEffect(() => {
    const controller = new AbortController();
    if (!isRewind) { setHistoryResult(undefined); return () => controller.abort(); }
    setHistoryResult({ key: historyKey });
    let retainedWindow: { fromUtc: string; toUtc: string } | undefined;
    try { retainedWindow = timeWindow ? waitSessionHistoryWindow(timeWindow, Date.now()) : undefined; }
    catch { setHistoryResult({ key: historyKey, error: "The selected session history window is invalid." }); return () => controller.abort(); }
    if (!retainedWindow) {
      setHistoryResult({ key: historyKey, unavailable: timeWindow
        ? "This selected window is outside the 24-hour session capture retention. Historical server waits cannot restore expired sessions."
        : "Choose a valid selected window of up to 24 hours to inspect retained session captures." });
      return () => controller.abort();
    }
    const requestedWindow = retainedWindow;
    void readLive<LiveSnapshot[]>(instanceId, "/history", new URLSearchParams({ from: requestedWindow.fromUtc, to: requestedWindow.toUtc }), controller.signal)
      .then(snapshots => {
        if (controller.signal.aborted || currentHistoryKey.current !== historyKey) return;
        const bounded = boundedWaitSessionCaptures(snapshots, requestedWindow);
        setHistoryResult({ key: historyKey, captures: bounded.captures, omitted: bounded.omitted });
      })
      .catch((error: unknown) => { if (!controller.signal.aborted && currentHistoryKey.current === historyKey)
        setHistoryResult({ key: historyKey, error: error instanceof Error ? error.message : "Retained session captures are unavailable." }); });
    return () => controller.abort();
  }, [historyKey, isRewind]);

  useEffect(() => {
    const controller = new AbortController();
    if (category === null || requestedCapture === null || isRewind && !selectedCapture) { setResult(undefined); return () => controller.abort(); }
    if (requestedCapture) {
      try {
        if (!timeWindow) throw new Error("Choose a valid loaded window to inspect this requested session capture. No other capture has been selected.");
        validateWaitCaptureWindow(requestedCapture, timeWindow, Date.now());
      } catch (error) {
        setResult({ key: pageKey, loading: false, error: error instanceof Error ? error.message : "The requested capture is unavailable." });
        return () => controller.abort();
      }
    }
    setResult({ key: pageKey, loading: true });
    const filters = new URLSearchParams({ includeIdle: "true", includeSystem: "true", sort: "session", descending: "false" });
    if (scope.databaseId !== undefined) filters.set("databaseId", String(scope.databaseId));
    void loadCapturedWaitSessions((parameters, signal) => readLive<LivePage>(instanceId, "", parameters, signal), filters, controller.signal, selectedCapture)
      .then(loaded => {
        if (controller.signal.aborted || currentPageKey.current !== pageKey || location.hash !== selectionHash) return;
        if ((isRewind || requestedCapture) && loaded.page.observedUtc !== null && timeWindow
            && (activityUtc(loaded.page.observedUtc) < activityUtc(timeWindow.fromUtc) || activityUtc(loaded.page.observedUtc) >= activityUtc(timeWindow.toUtc)))
          throw new Error("The session capture did not belong to the selected window.");
        if (requestedCapture && capturedWaitSnapshotExpired(loaded.page, Date.now()))
          throw new Error("The requested session capture has expired beyond the 24-hour retention. No other capture has been selected.");
        setResult({ key: pageKey, loading: false, data: loaded });
      })
      .catch((error: unknown) => { if (!controller.signal.aborted && currentPageKey.current === pageKey && location.hash === selectionHash)
        setResult({ key: pageKey, loading: false, error: error instanceof Error ? error.message : "Captured session evidence is unavailable." }); });
    return () => controller.abort();
  }, [pageKey]);

  const rows = data?.rows.filter(row => capturedSessionWaitCategory(row.waitType) === category) ?? [];
  const expired = data ? capturedWaitSnapshotExpired(data.page, Date.now()) : false;
  const inspectionHref = (identity: string) => {
    if (!data?.page.snapshotId || !data.page.observedUtc || expired) return undefined;
    try { return activitySelectionHref({ ...scope, target: instanceId }, { snapshotId: data.page.snapshotId, observedUtc: data.page.observedUtc, identity }); }
    catch { return undefined; }
  };
  const shareable = data?.page.snapshotId && data.page.observedUtc ? inspectionHref(data.rows[0]?.identity ?? "0".repeat(64)) !== undefined : true;
  return <section className="panel server-dashboard-chart" aria-label="Captured sessions by server wait category" aria-busy={Boolean(evidence?.loading)}>
    <h3>Captured sessions with matching waits</h3>
    <p>These are native session wait types observed in one retained activity capture, classified with the server-wait mapping. They do not attribute the server wait trend to sessions or reconstruct every session in the selected historical window. Query Store contributors above are independent evidence.</p>
    <label>Session wait category <select value={category ?? "invalid"} onChange={event => {
      if (location.hash !== selectionHash) return;
      location.hash = sessionWaitCategoryHref({ ...scope, target: instanceId }, event.target.value as SessionWaitCategory, selectionHash);
    }}>{category === null ? <option disabled value="invalid">Invalid session wait category</option> : null}
      {sessionWaitCategories.map(value => <option key={value} value={value}>{value}</option>)}
    </select></label>
    {category === null ? <p role="status">The requested session wait category is invalid or duplicated. Choose a supported category; no other category was substituted.</p> : null}
    {requestedCapture === null ? <p role="status">The requested session capture is invalid, duplicated, or outside the selected window. No other capture has been selected.</p> : null}
    {requestedCapture !== undefined ? <button type="button" className="secondary-button" onClick={() => {
      if (location.hash === selectionHash && currentPageKey.current === pageKey)
        location.hash = sessionWaitCaptureHref({ ...scope, target: instanceId }, undefined, selectionHash);
    }}>Clear retained capture</button> : null}
    <p className="table-note">Classification follows telemetry.server_wait_category_v1: Lock, Log, I/O, CPU/signal, Memory, Parallelism and Other; benign waits are excluded unless Idle is selected. User and system request metadata is requested; rows without a captured wait type are excluded. This is separate from Query Store category numbers. No captured SQL text is loaded.</p>
    {scope.databaseId !== undefined ? <p className="table-note">Captured requests are scoped to database ID {scope.databaseId}; server wait counters remain server-wide.</p> : null}
    {isRewind && !history ? <p role="status">Finding retained session captures inside this window…</p> : null}
    {history?.error ? <p role="alert">{history.error} <button type="button" onClick={() => setRetry(value => value + 1)}>Retry captured sessions</button></p> : null}
    {history?.unavailable ? <p role="status">{history.unavailable}</p> : null}
    {isRewind && captures && captures.length === 0 && requestedCapture === undefined ? <p>No retained native session capture is available inside this window. The current capture has not been substituted.</p> : null}
    {requestedCapture && captures && (evidence?.loading || data) && !captures.some(capture => capture.id.toLowerCase() === requestedCapture.id.toLowerCase()) ? <p className="table-note">The requested capture is not in the offered history list. Its exact identity is retained; bounded metadata is read directly.</p> : null}
    {isRewind && captures && captures.length > 0 ? <>
      <label>Retained session capture <select value={requestedCapture === null ? "invalid" : selectedCaptureId ?? ""} onChange={event => {
        if (currentHistoryKey.current !== historyKey || location.hash !== selectionHash) return;
        const capture = captures.find(item => item.id.toLowerCase() === event.target.value.toLowerCase());
        if (capture) location.hash = sessionWaitCaptureHref({ ...scope, target: instanceId }, capture, selectionHash);
      }}>{requestedCapture === null ? <option disabled value="invalid">Invalid requested capture</option> : null}
        {requestedCapture && !captures.some(capture => capture.id.toLowerCase() === requestedCapture.id.toLowerCase()) ? <option value={requestedCapture.id}>{labelTime(requestedCapture.observedUtc)} · requested capture outside offered list</option> : null}
        {captures.map(capture => <option key={capture.id} value={capture.id.toLowerCase()}>{labelTime(capture.observedUtc)}{capture.truncated ? " · truncated capture" : ""}</option>)}</select></label>
      <p className="table-note">{captures.length} retained captures offered{history?.omitted ? `; ${history.omitted} older captures omitted by the 100-capture selector bound` : ""}. Sessions are shown only for the chosen capture, not the whole window.</p>
    </> : null}
    {evidence?.loading ? <p role="status">Loading bounded captured session metadata…</p> : null}
    {evidence?.error ? <p role="alert">{evidence.error} <button type="button" onClick={() => setRetry(value => value + 1)}>Retry captured sessions</button></p> : null}
    {data && (!data.page.snapshotId || !data.page.observedUtc) ? <p>The session capture is unavailable or expired. No current capture has been substituted for the selected historical capture.</p> : null}
    {data?.page.snapshotId && data.page.observedUtc ? <>
      <p className="activity-evidence">{isRewind || requestedCapture ? "Selected retained capture" : "Latest native session capture"} observed {labelTime(data.page.observedUtc)} · repository time {labelTime(data.page.repositoryTimeUtc)} · {data.page.state === "stale" ? "stale evidence" : "available captured evidence"} · {rows.length} matching requests of {data.rows.length} loaded · {data.pagesRead} metadata pages.
        {data.page.truncated ? " Capture is truncated; missing requests may have matching waits." : ""}
        {data.limited ? " Metadata paging stopped at the 11-page / 512-row bound; coverage is partial." : " End of the loaded capture's metadata pages."}</p>
      {expired ? <p className="table-note">This captured snapshot has expired beyond the 24-hour retention. Its returned metadata is shown as expired evidence; inspection links are unavailable. Refresh to request current retained evidence.</p>
        : !shareable ? <p className="table-note">This latest capture lies outside the selected custom window. Its metadata is visible, but an inspection link cannot preserve that contradictory window.</p> : null}
      {rows.length === 0 ? <p>No captured request with a native {category} wait is present in the loaded metadata. This does not prove there were no waits in the selected window.</p> : <div className="table-scroll" tabIndex={0} role="region" aria-label="Captured session wait matches"><table className="evidence-table">
        <caption>Observed native session wait types in one capture; not historical attribution</caption>
        <thead><tr><th scope="col">Session / request</th><th scope="col">Database</th><th scope="col">Native wait type</th><th scope="col">Status</th><th scope="col">Elapsed ms</th><th scope="col">Inspection</th></tr></thead>
        <tbody>{rows.map(row => <tr key={row.identity}><td>{row.sessionId} / {row.requestId ?? "idle"}</td><td>{row.databaseName ?? (row.databaseId === null ? "Database unavailable" : `Database ID ${row.databaseId}`)}</td><td>{row.waitType}</td><td>{row.status}{row.isUser ? " · user" : " · system"}</td><td>{row.elapsedMs}</td><td>{inspectionHref(row.identity) ? <a href={inspectionHref(row.identity)}>Inspect captured session {row.sessionId} / request {row.requestId ?? "idle"}</a> : "Inspection unavailable for this window"}</td></tr>)}</tbody>
      </table></div>}
    </> : null}
  </section>;
}
