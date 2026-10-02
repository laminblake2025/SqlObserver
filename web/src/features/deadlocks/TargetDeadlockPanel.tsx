import { useEffect, useMemo, useRef, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { EvidenceStatus } from "../../components/DiagnosticUi";
import { activityHistoryHref } from "../../dashboardModel";
import { getDeadlock, getDeadlocks } from "./deadlockApi";
import { buildDeadlockGraph } from "./deadlockGraph";
import type { DeadlockDetail, DeadlockPage, DeadlockSummary } from "./deadlockTypes";
import { overviewWindow } from "../overview/overviewModel";
import type { OverviewScope } from "../overview/overviewTypes";
import { deadlockOccurrenceInScope, matchesDeadlockSelection, type DeadlockSelection } from "./deadlockSelectionModel.ts";

export function TargetDeadlockPanel({ instanceId, displayName, onClose, scope, refresh, selectedEventId, selectedOccurredAtUtc, onSelectEvent }: {
  readonly instanceId: string; readonly displayName: string; readonly onClose: () => void;
  readonly scope: OverviewScope; readonly refresh: number;
  readonly selectedEventId?: string | null; readonly selectedOccurredAtUtc?: string;
  readonly onSelectEvent?: (selection: DeadlockSelection | undefined) => void;
}) {
  const { mode } = useTimeDisplay();
  const timeLabel = (value: string) => formatDisplayTime(value, mode);
  const window = useMemo(() => {
    // .NET may serialize PostgreSQL microseconds with a seventh zero; preserve all meaningful digits.
    const normalize = (value: string | undefined) => value?.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1");
    try { return overviewWindow({ ...scope, from: normalize(scope.from), to: normalize(scope.to) }, Date.now()); }
    catch { return undefined; }
  }, [scope.range, scope.from, scope.to, refresh]);
  const context = JSON.stringify([instanceId.toLowerCase(), scope.range, scope.from, scope.to, scope.databaseId]);
  const listKey = JSON.stringify([context, window?.fromUtc, window?.toUtc, refresh]);
  const [receivedPage, setPage] = useState<{ context: string; key: string; page: DeadlockPage; fromUtc: string; toUtc: string }>();
  const page = window && receivedPage?.context === context ? receivedPage.page : undefined;
  const [localSelection, setLocalSelection] = useState<{ context: string; selection: DeadlockSelection }>();
  const local = localSelection?.context === context ? localSelection.selection : undefined;
  const requested = selectedEventId === null ? null : selectedEventId !== undefined
    ? { eventId: selectedEventId.toLowerCase(), occurredAtUtc: selectedOccurredAtUtc }
    : onSelectEvent ? undefined : local;
  const activeId = requested === null ? undefined : requested?.eventId ?? page?.items[0]?.eventId.toLowerCase();
  const requestKey = JSON.stringify([context, activeId, requested?.occurredAtUtc, refresh]);
  const identity = useRef({ requestKey, listKey });
  identity.current = { requestKey, listKey };
  const [receivedDetail, setDetail] = useState<{ context: string; refresh: number; detail: DeadlockDetail }>();
  const detail = window && receivedDetail?.context === context && activeId !== undefined &&
    receivedDetail.detail.summary.eventId.toLowerCase() === activeId &&
    (!requested?.occurredAtUtc || matchesDeadlockSelection({ eventId: activeId, occurredAtUtc: requested.occurredAtUtc }, receivedDetail.detail.summary))
    ? receivedDetail.detail : undefined;
  const [listState, setListState] = useState<{ key: string; loading: boolean; error?: string }>();
  const listLoading = Boolean(window && (listState?.key !== listKey || listState.loading));
  const message = listState?.key === listKey ? listState.error : undefined;
  const [detailState, setDetailState] = useState<{ key: string; loading: boolean; error?: string }>();
  const detailLoading = Boolean(activeId && window && requested !== null && (detailState?.key !== requestKey || detailState.loading));
  const detailError = detailState?.key === requestKey ? detailState.error : undefined;
  const [cursorTrail, setCursorTrail] = useState<readonly string[]>([]);
  const listRequest = useRef<AbortController | undefined>(undefined);
  const detailRequest = useRef<AbortController | undefined>(undefined);

  useEffect(() => {
    listRequest.current?.abort();
    const controller = new AbortController(); listRequest.current = controller;
    setPage(previous => previous?.context === context ? previous : undefined);
    setListState({ key: listKey, loading: Boolean(window), ...(!window ? { error: "Choose a valid UTC range of up to 31 days for deadlock history." } : {}) });
    if (!window) return () => controller.abort();
    void getDeadlocks(instanceId, controller.signal, undefined, window)
      .then(next => {
        if (controller.signal.aborted || identity.current.listKey !== listKey) return;
        if (next.targetId.toLowerCase() !== instanceId.toLowerCase()) throw new Error("Deadlock evidence did not match the selected server.");
        setPage({ context, key: listKey, page: next, ...window }); setCursorTrail([]); setListState({ key: listKey, loading: false });
      }).catch((error: unknown) => {
        if (!controller.signal.aborted && identity.current.listKey === listKey) {
          setPage(undefined); setCursorTrail([]);
          setListState({ key: listKey, loading: false, error: error instanceof Error ? error.message : "Deadlock evidence is unavailable." });
        }
      });
    return () => { controller.abort(); listRequest.current?.abort(); };
  }, [instanceId, listKey]);

  useEffect(() => {
    detailRequest.current?.abort();
    if (!activeId || !window || requested === null) return;
    if (detail && receivedDetail?.refresh === refresh) {
      setDetailState({ key: requestKey, loading: false }); return;
    }
    const controller = new AbortController(); detailRequest.current = controller;
    setDetailState({ key: requestKey, loading: true });
    void getDeadlock(instanceId, activeId, controller.signal).then(next => {
      if (controller.signal.aborted || identity.current.requestKey !== requestKey) return;
      if (next.summary.eventId.toLowerCase() !== activeId || !deadlockOccurrenceInScope(next.summary.occurredAtUtc, scope) ||
          requested?.occurredAtUtc && !matchesDeadlockSelection({ eventId: activeId, occurredAtUtc: requested.occurredAtUtc }, next.summary))
        throw new Error("The retained deadlock did not match the requested occurrence and workspace window.");
      setDetail({ context, refresh, detail: next }); setDetailState({ key: requestKey, loading: false });
    }).catch((error: unknown) => {
      if (!controller.signal.aborted && identity.current.requestKey === requestKey) {
        setDetail(undefined); setDetailState({ key: requestKey, loading: false, error: requested
          ? "The requested deadlock detail is unavailable. It may have expired or access may have changed. No other event has been selected."
          : error instanceof Error ? error.message : "Deadlock detail is unavailable." });
      }
    });
    return () => controller.abort();
  }, [instanceId, requestKey, Boolean(window)]);

  function selectEvent(item: DeadlockSummary) {
    const selection = { eventId: item.eventId.toLowerCase(), occurredAtUtc: item.occurredAtUtc };
    if (onSelectEvent) onSelectEvent(selection);
    else setLocalSelection({ context, selection });
  }
  function clearSelection() {
    detailRequest.current?.abort(); setDetail(undefined); setLocalSelection(undefined);
    onSelectEvent?.(undefined);
  }
  async function loadPage(cursor: string | undefined, nextTrail: readonly string[]) {
    if (listLoading || !window) return;
    listRequest.current?.abort();
    const controller = new AbortController(); listRequest.current = controller;
    setListState({ key: listKey, loading: true });
    try {
      const next = await getDeadlocks(instanceId, controller.signal, cursor, window);
      if (controller.signal.aborted || identity.current.listKey !== listKey) return;
      if (next.targetId.toLowerCase() !== instanceId.toLowerCase()) throw new Error("Deadlock evidence did not match the selected server.");
      setPage({ context, key: listKey, page: next, ...window }); setCursorTrail(nextTrail); setListState({ key: listKey, loading: false });
    } catch (error: unknown) {
      if (!controller.signal.aborted && identity.current.listKey === listKey) {
        setPage(undefined); setCursorTrail([]);
        setListState({ key: listKey, loading: false, error: error instanceof Error ? error.message : "Deadlock evidence is unavailable." });
      }
    }
  }
  const requestedNotOnPage = Boolean(requested && page && !page.items.some(item => item.eventId.toLowerCase() === requested.eventId));
  return <section className="deadlock-screen" aria-labelledby="deadlock-heading">
    <div className="health-heading-row">
      <div><p className="eyebrow">Deadlocks · passive system_health evidence</p><h2 id="deadlock-heading">Deadlock investigation</h2><p className="screen-subtitle">Captured events and lock relationships for {displayName}</p></div>
      <button className="secondary-button" onClick={onClose} type="button">Close</button>
    </div>
    {message ? <p role="alert" className="status-message">{message} Refresh to retry the event list.</p> : null}
    {!page && !message ? <p role="status">Loading bounded deadlock evidence…</p> : null}
    {scope.databaseId !== undefined && <p className="table-note">Deadlock events and relationships remain server-wide; this source does not establish database attribution.</p>}
    {window && <p className="activity-evidence">Deadlock events in the selected window: {timeLabel(window.fromUtc)} to {timeLabel(window.toUtc)}.</p>}
    {page && listLoading && receivedPage && <p role="status">Refreshing deadlock events; showing the previously loaded event page for {timeLabel(receivedPage.fromUtc)} to {timeLabel(receivedPage.toUtc)}. Repository snapshot: {timeLabel(page.repositoryTimeUtc)}. Selection keeps its exact retained occurrence.</p>}
    {requested === null && <p role="alert">The requested deadlock selection is invalid or contradicts the workspace context. Clear the deadlock selection to continue.</p>}
    {requested !== undefined && <button type="button" className="secondary-button" onClick={clearSelection}>Clear deadlock selection</button>}
    <div className="deadlock-layout">
      <aside className="deadlock-events" aria-label="Captured deadlock events" aria-busy={listLoading}>
        {page && <>
          <div className="deadlock-events-heading"><h3>{page.items.length} captured events</h3><span>{page.nextCursor ? "More bounded events available" : "History bound reached"}</span></div>
          {page.items.length === 0 ? <p className="empty-state">No captured deadlock events are available in this bounded page. This does not establish that no deadlocks occurred.</p> : <div className="event-list">{page.items.map(item => <button className={item.eventId.toLowerCase() === activeId ? "event-card is-selected" : "event-card"} aria-pressed={item.eventId.toLowerCase() === activeId} aria-controls="selected-deadlock-detail" key={item.eventId.toLowerCase()} onClick={() => selectEvent(item)} type="button"><span className="event-severity" aria-hidden="true">!</span><span><strong>{timeLabel(item.occurredAtUtc)}</strong><small>{item.participantCount} participants · {item.relationCount} relationships</small><small>Fingerprint {item.fingerprint.slice(0, 12)}…{item.parseTruncated ? " · truncated" : ""}</small></span><span aria-hidden="true">›</span></button>)}</div>}
          <p className="table-note">Repository time: {timeLabel(page.repositoryTimeUtc)}</p>
          <nav className="pager" aria-label="Deadlock event pages"><button type="button" disabled={listLoading || cursorTrail.length === 0} onClick={() => void loadPage(undefined, [])}>First page</button><button type="button" disabled={listLoading || cursorTrail.length === 0} onClick={() => { const previousTrail = cursorTrail.slice(0, -1); void loadPage(previousTrail.at(-1), previousTrail); }}>Previous</button><button type="button" disabled={listLoading || !page.nextCursor} onClick={() => void loadPage(page.nextCursor ?? undefined, page.nextCursor ? [...cursorTrail, page.nextCursor] : cursorTrail)}>Next</button></nav>
        </>}
      </aside>
      <section id="selected-deadlock-detail" className="deadlock-detail" aria-label="Selected deadlock detail" aria-busy={detailLoading}>
        {requestedNotOnPage && detail && <p role="status">The requested deadlock is not on the loaded event page. Its retained detail is shown without selecting another event.</p>}
        {detailError && <p role="alert">{detailError} Refresh to retry or clear the deadlock selection.</p>}
        {detail && detailLoading && <p role="status">Refreshing the selected deadlock; showing its previously loaded retained detail.</p>}
        {detail && window && !deadlockOccurrenceInScope(detail.summary.occurredAtUtc, { ...scope, range: "custom", from: window.fromUtc, to: window.toUtc }) && <p role="status">The selected retained occurrence is outside the current moving Live window. Its exact captured identity remains selected.</p>}
        {detail ? <DeadlockDetailView detail={detail} instanceId={instanceId} scope={scope} /> : detailLoading ? <p role="status">Loading selected event participants and relationships…</p> : !detailError && requested !== null ? <div className="empty-state">Select an event to inspect its available participants and relationships.</div> : null}
      </section>
    </div>
  </section>;
}
function DeadlockDetailView({ detail, instanceId, scope }: { readonly detail: DeadlockDetail; readonly instanceId: string; readonly scope: OverviewScope }) {
  const { mode } = useTimeDisplay();
  const graph = buildDeadlockGraph(detail);
  const nodeById = new Map(graph.nodes.map((node) => [node.sessionId, node]));
  const victimIds = detail.participants.filter((participant) => participant.isVictim).map((participant) => participant.sessionId);
  return <>
    <div className="detail-heading"><div><h3>Event at {formatDisplayTime(detail.summary.occurredAtUtc, mode)}</h3><p>Fingerprint <code>{detail.summary.fingerprint}</code></p></div><div className="detail-statuses"><EvidenceStatus label={detail.summary.parseTruncated ? "Truncated capture" : "Complete capture"} detail={`Collected ${formatDisplayTime(detail.summary.collectedAtUtc, mode)}`} tone={detail.summary.parseTruncated ? "warning" : "current"} /></div></div>
    {detail.summary.parseTruncated ? <p className="evidence-callout warning">This capture is truncated. The diagram and table show every relationship available in this response; they do not infer missing participants.</p> : null}
    <section className="related-evidence" aria-labelledby="deadlock-participant-activity-heading"><h3 id="deadlock-participant-activity-heading">Participant activity</h3><p>Open the Activity history snapshot captured for this deadlock, when available, or the nearest cadence sample.</p><a href={activityHistoryHref(instanceId, detail.summary.occurredAtUtc, detail.summary.eventId, scope)}>View activity at event time <span aria-hidden="true">→</span></a></section>
    <section className="deadlock-diagram-section" aria-labelledby="deadlock-diagram-heading"><div className="section-heading"><div><h3 id="deadlock-diagram-heading">Lock relationships</h3><p>Arrows point from waiter to blocker.</p></div><span className="table-note">{graph.nodes.length} participants · {graph.edges.length} relationships</span></div><div className="deadlock-diagram-scroll" tabIndex={0} role="region" aria-label="Deadlock relationship diagram"><svg className="deadlock-diagram" viewBox={`0 0 ${graph.width} ${graph.height}`} role="img" aria-labelledby="deadlock-diagram-title deadlock-diagram-description"><title id="deadlock-diagram-title">Deadlock relationship diagram</title><desc id="deadlock-diagram-description">Directed edges point from a waiting session to the session it is waiting on. Victim sessions are labeled.</desc><defs><marker id="deadlock-arrow" markerWidth="10" markerHeight="10" viewBox="0 0 10 10" refX="8" refY="5" markerUnits="userSpaceOnUse" orient="auto"><path d="M0,0 L10,5 L0,10 z" /></marker></defs>{graph.edges.map((edge, index) => { const from = nodeById.get(edge.waiterSessionId); const to = nodeById.get(edge.blockerSessionId); if (!from || !to) return null; return <g key={`${edge.waiterSessionId}:${edge.blockerSessionId}:${index}`}><path className="deadlock-edge" d={edge.path} markerEnd="url(#deadlock-arrow)" /><text className="deadlock-edge-label" x={edge.label.x} y={edge.label.y}>{edge.resourceCategory.toUpperCase()} / {edge.lockMode} · {edge.waiterSessionId}→{edge.blockerSessionId}</text><title>Session {edge.waiterSessionId} waits on session {edge.blockerSessionId}: {edge.resourceCategory} / {edge.lockMode}</title></g>;})}{graph.nodes.map((node) => <g className={node.isVictim ? "deadlock-node victim" : "deadlock-node"} key={node.sessionId} transform={`translate(${node.x - 78},${node.y - 42})`}><rect width="156" height="84" rx="10" /><text x="78" y="32" textAnchor="middle">Session {node.sessionId}</text><text className="deadlock-node-state" x="78" y="58" textAnchor="middle">{node.isVictim ? "Victim" : "Participant"}</text></g>)}</svg></div><div className="diagram-legend"><span><i className="legend-arrow" aria-hidden="true" /> Arrow direction: waiter → blocker</span><span><i className="legend-victim" aria-hidden="true" /> Victim: {victimIds.length ? victimIds.join(", ") : "not identified"}</span></div></section>
    <section className="deadlock-relations" aria-labelledby="deadlock-relations-heading"><div className="section-heading"><div><h3 id="deadlock-relations-heading">Relationship table</h3><p>Complete available relationship evidence; no rows are hidden for the diagram.</p></div></div><div className="table-scroll" tabIndex={0} role="region" aria-label="Deadlock relationship table"><table><caption>Waiting session to blocking session relationships</caption><thead><tr><th scope="col">Waiting session</th><th scope="col">Blocking session</th><th scope="col">Resource</th><th scope="col">Lock mode</th></tr></thead><tbody>{detail.relations.length ? detail.relations.map((relation, index) => <tr key={`${relation.waiterSessionId}:${relation.blockerSessionId}:${index}`}><td>{relation.waiterSessionId}</td><td>{relation.blockerSessionId}</td><td>{relation.resourceCategory}</td><td>{relation.lockMode}</td></tr>) : <tr><td colSpan={4}>No lock relationships were reported.</td></tr>}</tbody></table></div></section>
    <p className="evidence-callout">SQL Server selected {victimIds.length ? `session${victimIds.length === 1 ? "" : "s"} ${victimIds.join(", ")} as the victim${victimIds.length === 1 ? "" : "s"}.` : "no victim identity was available in this detail."}</p>
    <p className="table-note">Fingerprint {detail.summary.fingerprint} · Source: system_health · Observation time: {formatDisplayTime(detail.summary.occurredAtUtc, mode)}</p>
  </>;
}
