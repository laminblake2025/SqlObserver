import { useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { EvidenceStatus } from "../../components/DiagnosticUi";
import { activityHistoryHref } from "../../dashboardModel";
import { deadlockAccessDenied, getDeadlock } from "./deadlockApi";
import { buildDeadlockGraph } from "./deadlockGraph";
import type { DeadlockDetail, DeadlockSummary } from "./deadlockTypes";
import { overviewWindow } from "../overview/overviewModel";
import type { OverviewScope } from "../overview/overviewTypes";
import { useDeadlockPages } from "./useDeadlockPages";
import "./deadlockInvestigation.css";
import { deadlockOccurrenceInScope, matchesDeadlockSelection, type DeadlockSelection } from "./deadlockSelectionModel.ts";

export function TargetDeadlockPanel({ instanceId, displayName, onClose, scope, refresh, selectedEventId, selectedOccurredAtUtc, onSelectEvent, targetRevision }: {
  readonly instanceId: string; readonly displayName: string; readonly onClose: () => void;
  readonly scope: OverviewScope; readonly refresh: number;
  readonly targetRevision?: number;
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
  const context = JSON.stringify([instanceId.toLowerCase(), targetRevision, scope.range, scope.from, scope.to, scope.databaseId]);
  const contextIdentity = useRef({ context, generation: 0 });
  if (contextIdentity.current.context !== context) contextIdentity.current = { context, generation: contextIdentity.current.generation + 1 };
  const contextGeneration = contextIdentity.current.generation;
  const [denial, setDenial] = useState<number>();
  const accessDenied = denial === contextGeneration;
  const denialEpoch = useRef(0);
  const detailRequest = useRef<AbortController | undefined>(undefined);
  const deny = () => { denialEpoch.current++; detailRequest.current?.abort(); detailRequest.current = undefined;
    pages.refuseAccess(); setDetail(undefined); setDetailState(undefined); setDenial(contextGeneration); };
  const pages = useDeadlockPages(instanceId, context, window, refresh, accessDenied, deny, () => setDenial(undefined));
  const page = accessDenied ? undefined : pages.receipt?.page;
  const listLoading = pages.loading;
  const message = !window ? "Choose a valid UTC range of up to 31 days for deadlock history." : pages.error;
  const [localSelection, setLocalSelection] = useState<{ generation: number; selection: DeadlockSelection }>();
  const local = localSelection?.generation === contextGeneration ? localSelection.selection : undefined;
  const requested = selectedEventId === null ? null : selectedEventId !== undefined
    ? { eventId: selectedEventId.toLowerCase(), occurredAtUtc: selectedOccurredAtUtc }
    : onSelectEvent ? undefined : local;
  const activeId = requested === null ? undefined : requested?.eventId ?? page?.items[0]?.eventId.toLowerCase();
  const selectionKey = JSON.stringify([contextGeneration, activeId, requested?.occurredAtUtc, requested === null]);
  const selectionIdentity = useRef({ key: selectionKey, generation: 0 });
  if (selectionIdentity.current.key !== selectionKey) selectionIdentity.current = { key: selectionKey, generation: selectionIdentity.current.generation + 1 };
  const selectionGeneration = selectionIdentity.current.generation;
  const currentIdentity = useRef({ contextGeneration, selectionGeneration, refresh, accessDenied });
  currentIdentity.current = { contextGeneration, selectionGeneration, refresh, accessDenied };
  const [receivedDetail, setDetail] = useState<{ generation: number; receivedAtUtc: string; detail: DeadlockDetail }>();
  const detail = !accessDenied && window && receivedDetail?.generation === selectionGeneration && activeId !== undefined &&
    receivedDetail.detail.summary.eventId.toLowerCase() === activeId &&
    (!requested?.occurredAtUtc || matchesDeadlockSelection({ eventId: activeId, occurredAtUtc: requested.occurredAtUtc }, receivedDetail.detail.summary))
    ? receivedDetail.detail : undefined;
  const [detailState, setDetailState] = useState<{ generation: number; loading: boolean; error?: string }>();
  const detailLoading = Boolean(!accessDenied && activeId && window && requested !== null &&
    (detailState?.generation !== selectionGeneration || detailState.loading));
  const detailError = !accessDenied && detailState?.generation === selectionGeneration ? detailState.error : undefined;
  useLayoutEffect(() => {
    detailRequest.current?.abort(); detailRequest.current = undefined;
    setDetail(undefined); setDetailState(undefined);
    return () => { detailRequest.current?.abort(); detailRequest.current = undefined; };
  }, [contextGeneration, selectionGeneration]);
  const detailHeld = useRef(false);
  useLayoutEffect(() => { detailHeld.current = false; }, [contextGeneration, selectionGeneration]);
  async function readDetail(explicit = false) {
    if (detailRequest.current || accessDenied || !activeId || !window || requested === null ||
        currentIdentity.current.selectionGeneration !== selectionGeneration || !explicit && detailHeld.current) return;
    const controller = new AbortController(); detailRequest.current = controller;
    const epoch = denialEpoch.current;
    const current = () => !controller.signal.aborted && detailRequest.current === controller && denialEpoch.current === epoch &&
      currentIdentity.current.selectionGeneration === selectionGeneration && currentIdentity.current.contextGeneration === contextGeneration &&
      !currentIdentity.current.accessDenied;
    setDetailState({ generation: selectionGeneration, loading: true });
    try {
      const next = await getDeadlock(instanceId, activeId, controller.signal);
      if (!current()) return;
      if (next.summary.eventId.toLowerCase() !== activeId || !deadlockOccurrenceInScope(next.summary.occurredAtUtc, scope) ||
          requested?.occurredAtUtc && !matchesDeadlockSelection({ eventId: activeId, occurredAtUtc: requested.occurredAtUtc }, next.summary))
        throw new Error("The retained deadlock did not match the requested occurrence and workspace window.");
      detailHeld.current = false;
      setDetail({ generation: selectionGeneration, receivedAtUtc: new Date().toISOString(), detail: next }); setDetailState({ generation: selectionGeneration, loading: false });
    } catch (error) {
      if (!current()) return;
      if (deadlockAccessDenied(error)) { deny(); return; }
      detailHeld.current = true;
      setDetailState({ generation: selectionGeneration, loading: false, error: requested
        ? "The requested deadlock detail is unavailable. It may have expired or access may have changed. No other event has been selected."
        : error instanceof Error ? error.message : "Deadlock detail is unavailable." });
    } finally { if (detailRequest.current === controller) detailRequest.current = undefined; }
  }
  useEffect(() => { void readDetail(); }, [instanceId, contextGeneration, selectionGeneration, refresh, Boolean(window), accessDenied]);
  function cancelDetail() {
    const controller = detailRequest.current;
    if (!controller || currentIdentity.current.selectionGeneration !== selectionGeneration) return;
    controller.abort(); detailRequest.current = undefined; detailHeld.current = true;
    setDetailState({ generation: selectionGeneration, loading: false, error: "Deadlock detail read cancelled. Retry repeats the selected occurrence read." });
  }
  function selectEvent(item: DeadlockSummary) {
    const selection = { eventId: item.eventId.toLowerCase(), occurredAtUtc: item.occurredAtUtc };
    if (onSelectEvent) onSelectEvent(selection);
    else setLocalSelection({ generation: contextGeneration, selection });
  }
  function clearSelection() {
    detailRequest.current?.abort(); setDetail(undefined); setLocalSelection(undefined);
    onSelectEvent?.(undefined);
  }
  const requestedNotOnPage = Boolean(requested && page && !page.items.some(item => item.eventId.toLowerCase() === requested.eventId));
  return <section className="deadlock-screen" aria-labelledby="deadlock-heading">
    <div className="health-heading-row">
      <div><p className="eyebrow">Deadlocks · passive system_health evidence</p><h2 id="deadlock-heading">Deadlock investigation</h2><p className="screen-subtitle">Captured events and lock relationships for {displayName}</p></div>
      <button className="secondary-button" onClick={onClose} type="button">Close</button>
    </div>
    {message ? <p role="alert" className="status-message">{message} Received event pages remain available; retry repeats this read.</p> : null}
    {scope.databaseId !== undefined && <p className="table-note">Deadlock events and relationships remain server-wide; this source does not establish database attribution.</p>}
    {window && <p className="activity-evidence">Workspace time range: {timeLabel(window.fromUtc)} to {timeLabel(window.toUtc)}.</p>}
    {accessDenied && <p role="alert" className="status-message">Deadlock access was denied. Received event pages and details were removed. Return to latest after access is restored.</p>}
    {pages.receipt && !accessDenied && <p className="deadlock-investigation-receipt">Received evidence window: {timeLabel(pages.receipt.window.fromUtc)} to {timeLabel(pages.receipt.window.toUtc)}. Collected evidence cutoff: {timeLabel(pages.receipt.page.repositoryTimeUtc)}. Page {pages.selected + 1} of {pages.receipts.length} received pages.</p>}
    {pages.retained && !accessDenied && <p className="deadlock-investigation-note">Browsing received deadlock evidence. Workspace refresh keeps this investigation. First, Previous and visited Next pages use received rows; unread continuation keeps its collected cutoff and window. Repository retention can still remove events.</p>}
    {pages.bounded && <p role="status">Retained limit reached: 20 pages / 500 events. Return to latest to start another investigation.</p>}
    {listLoading && <p role="status">{pages.receipt ? "Reading deadlock evidence; received event pages remain displayed..." : "Loading bounded deadlock evidence..."}</p>}
    <nav className="deadlock-investigation-controls" aria-label="Deadlock investigation reads">
      <button type="button" disabled={listLoading || !window} onClick={pages.latest}>Return to latest deadlock events</button>
      {pages.canRetry && <button type="button" disabled={listLoading} onClick={pages.retry}>Retry deadlock read</button>}
      {listLoading && <button type="button" onClick={pages.cancel}>Cancel deadlock read</button>}
    </nav>
    {requested === null && <p role="alert">The requested deadlock selection is invalid or contradicts the workspace context. Clear the deadlock selection to continue.</p>}
    {requested !== undefined && <button type="button" className="secondary-button" onClick={clearSelection}>Clear deadlock selection</button>}
    <div className="deadlock-layout">
      <aside className="deadlock-events" aria-label="Captured deadlock events" aria-busy={listLoading}>
        {page && <>
          <div className="deadlock-events-heading"><h3>{page.items.length} captured events</h3><span>{page.nextCursor ? "More bounded events available" : "History bound reached"}</span></div>
          {page.items.length === 0 ? <p className="empty-state">No captured deadlock events are available in this bounded page. This does not establish that no deadlocks occurred.</p> : <div className="event-list" tabIndex={0} role="region" aria-label="Received deadlock event rows">{page.items.map(item => <button className={item.eventId.toLowerCase() === activeId ? "event-card is-selected" : "event-card"} aria-pressed={item.eventId.toLowerCase() === activeId} aria-controls="selected-deadlock-detail" key={item.eventId.toLowerCase()} onClick={() => selectEvent(item)} type="button"><span className="event-severity" aria-hidden="true">!</span><span><strong>{timeLabel(item.occurredAtUtc)}</strong><small>{item.participantCount} participants · {item.relationCount} relationships</small><small>Fingerprint {item.fingerprint.slice(0, 12)}…{item.parseTruncated ? " · truncated" : ""}</small></span><span aria-hidden="true">›</span></button>)}</div>}
          <p className="table-note">Repository time: {timeLabel(page.repositoryTimeUtc)}</p>
          <nav className="pager deadlock-investigation-controls" aria-label="Deadlock event pages"><button type="button" disabled={listLoading || pages.selected === 0} onClick={pages.first}>First page</button><button type="button" disabled={listLoading || pages.selected === 0} onClick={pages.previous}>Previous</button><button type="button" disabled={listLoading || !pages.canNext} onClick={pages.next}>Next</button></nav>
        </>}
      </aside>
      <section id="selected-deadlock-detail" className="deadlock-detail" aria-label="Selected deadlock detail" aria-busy={detailLoading}>
        {requestedNotOnPage && detail && <p role="status">The requested deadlock is not on the loaded event page. Its retained detail is shown without selecting another event.</p>}
        {detailError && <p role="alert">{detailError} Retry the selected detail or clear the deadlock selection.{detail ? " Previously received matching detail remains available." : ""}</p>}
        {detailError && !accessDenied && <button type="button" disabled={detailLoading} onClick={() => void readDetail(true)}>Retry deadlock detail</button>}
        {detailLoading && <button type="button" onClick={cancelDetail}>Cancel deadlock detail</button>}
        {detail && receivedDetail && <p className="deadlock-investigation-note">Detail received: {timeLabel(receivedDetail.receivedAtUtc)}. This exact occurrence is read independently of the event-list collected cutoff.</p>}
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
