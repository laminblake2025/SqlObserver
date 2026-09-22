import { useEffect, useRef, useState } from "react";
import type { InvestigationWindow } from "../../investigationWindow";
import { RequestStatus } from "../../components/RequestStatus";
import { EvidenceStatus } from "../../components/DiagnosticUi";
import { activityHistoryHref } from "../../dashboardModel";
import { getDeadlock, getDeadlocks } from "./deadlockApi";
import { buildDeadlockGraph } from "./deadlockGraph";
import type { DeadlockDetail, DeadlockSummary } from "./deadlockTypes";

export function TargetDeadlockPanel({ instanceId, displayName, onClose, refresh = 0, timeWindow, windowError }: { readonly instanceId: string; readonly displayName: string; readonly onClose: () => void; readonly refresh?: number; readonly timeWindow?: InvestigationWindow; readonly windowError?: string }) {
  const [updatedAt, setUpdatedAt] = useState<string>();
  const [page, setPage] = useState<Awaited<ReturnType<typeof getDeadlocks>>>();
  const [detail, setDetail] = useState<DeadlockDetail>();
  const [selectedEventId, setSelectedEventId] = useState<string>();
  const [message, setMessage] = useState<string>();
  const [listLoading, setListLoading] = useState(false);
  const [cursorTrail, setCursorTrail] = useState<readonly string[]>([]);
  const listRequest = useRef<AbortController | undefined>(undefined);
  const detailRequest = useRef<AbortController | undefined>(undefined);

  useEffect(() => {
    if (windowError || !timeWindow) return;
    void loadPage(cursorTrail.at(-1), cursorTrail, true);
    return () => { listRequest.current?.abort(); detailRequest.current?.abort(); };
  }, [instanceId, refresh, timeWindow?.fromUtc, timeWindow?.toUtc, windowError]);

  useEffect(() => {
    if (!page || page.items.length === 0) return;
    const item = page.items.find((candidate) => candidate.eventId === selectedEventId) ?? page.items[0];
    if (!item) return;
    setSelectedEventId(item.eventId);
    void loadDetail(item);
  }, [page]);

  async function loadDetail(item: DeadlockSummary) {
    detailRequest.current?.abort();
    const controller = new AbortController();
    detailRequest.current = controller;
    setDetail(previous => previous?.summary.eventId === item.eventId ? previous : undefined);
    setMessage(undefined);
    try {
      const next = await getDeadlock(instanceId, item.eventId, controller.signal);
      if (!controller.signal.aborted) setDetail(next);
    } catch (error: unknown) {
      if (!controller.signal.aborted) setMessage(error instanceof Error ? error.message : "Deadlock detail is unavailable.");
    }
  }

  async function selectEvent(item: DeadlockSummary) {
    setSelectedEventId(item.eventId);
    await loadDetail(item);
  }

  async function loadPage(cursor: string | undefined, nextTrail: readonly string[], preserveSelection = false) {
    if (listLoading && !preserveSelection) return;
    listRequest.current?.abort();
    const controller = new AbortController();
    listRequest.current = controller;
    setListLoading(true);
    setMessage(undefined);
    detailRequest.current?.abort();
    try {
      const next = await getDeadlocks(instanceId, controller.signal, cursor, timeWindow);
      if (controller.signal.aborted) return;
      setPage(next);
      setCursorTrail(nextTrail);
      setUpdatedAt(new Date().toISOString());
      if (!preserveSelection) { setSelectedEventId(undefined); setDetail(undefined); }
    } catch (error: unknown) {
      if (!controller.signal.aborted) setMessage(error instanceof Error ? error.message : "Deadlock evidence is unavailable.");
    } finally {
      if (!controller.signal.aborted) setListLoading(false);
    }
  }

  return <section className="deadlock-screen" aria-labelledby="deadlock-heading">
    <div className="health-heading-row">
      <div><p className="eyebrow">Deadlocks · passive system_health evidence</p><h2 id="deadlock-heading">Deadlock investigation</h2><p className="screen-subtitle">Captured events and lock relationships for {displayName}</p></div>
      <button className="secondary-button" onClick={onClose} type="button">Close</button>
    </div>
    <RequestStatus loading={listLoading} error={windowError ?? message} updatedAt={updatedAt} hasData={Boolean(page)} label="Deadlock evidence" onRetry={windowError ? undefined : () => void loadPage(cursorTrail.at(-1), cursorTrail, true)} />
    {page ? <div className="deadlock-layout">
      <aside className="deadlock-events" aria-label="Captured deadlock events">
        <div className="deadlock-events-heading"><h3>{page.items.length} captured events</h3><span>{page.nextCursor ? "More bounded events available" : "History bound reached"}</span></div>
        {page.items.length === 0 ? <p className="empty-state">No captured deadlock events are available in this bounded page.</p> : <div className="event-list">{page.items.map((item) => <button className={item.eventId === selectedEventId ? "event-card is-selected" : "event-card"} key={item.eventId} aria-pressed={item.eventId === selectedEventId} onClick={() => void selectEvent(item)} type="button"><span className="event-severity" aria-hidden="true">!</span><span><strong>{formatUtc(item.occurredAtUtc)}</strong><small>{item.participantCount} participants · {item.relationCount} relationships</small><small>Fingerprint {item.fingerprint.slice(0, 12)}…{item.parseTruncated ? " · truncated" : ""}</small></span><span aria-hidden="true">›</span></button>)}</div>}
        <p className="table-note">Repository time: {formatUtc(page.repositoryTimeUtc)}</p>
        <nav className="pager" aria-label="Deadlock event pages"><button type="button" disabled={listLoading || cursorTrail.length === 0} onClick={() => void loadPage(undefined, [])}>First page</button><button type="button" disabled={listLoading || cursorTrail.length === 0} onClick={() => { const previousTrail = cursorTrail.slice(0, -1); void loadPage(previousTrail.at(-1), previousTrail); }}>Previous</button><button type="button" disabled={listLoading || !page.nextCursor} onClick={() => void loadPage(page.nextCursor ?? undefined, page.nextCursor ? [...cursorTrail, page.nextCursor] : cursorTrail)}>Next</button></nav>
      </aside>
      <section className="deadlock-detail" aria-label="Selected deadlock detail">
        {detail ? <DeadlockDetailView detail={detail} instanceId={instanceId} /> : <div className="empty-state">Select an event to inspect its available participants and relationships.</div>}
      </section>
    </div> : null}
  </section>;
}

function DeadlockDetailView({ detail, instanceId }: { readonly detail: DeadlockDetail; readonly instanceId: string }) {
  const graph = buildDeadlockGraph(detail);
  const nodeById = new Map(graph.nodes.map((node) => [node.sessionId, node]));
  const victimIds = detail.participants.filter((participant) => participant.isVictim).map((participant) => participant.sessionId);
  return <>
    <div className="detail-heading"><div><h3>Event at {formatUtc(detail.summary.occurredAtUtc)}</h3><p>Fingerprint <code>{detail.summary.fingerprint}</code></p></div><div className="detail-statuses"><EvidenceStatus label={detail.summary.parseTruncated ? "Truncated capture" : "Complete capture"} detail={`Collected ${formatUtc(detail.summary.collectedAtUtc)}`} tone={detail.summary.parseTruncated ? "warning" : "current"} /></div></div>
    {detail.summary.parseTruncated ? <p className="evidence-callout warning">This capture is truncated. The diagram and table show every relationship available in this response; they do not infer missing participants.</p> : null}
    <section className="related-evidence" aria-labelledby="deadlock-participant-activity-heading"><h3 id="deadlock-participant-activity-heading">Participant activity</h3><p>Open the Activity history snapshot captured for this deadlock, when available, or the nearest cadence sample.</p><a href={activityHistoryHref(instanceId, detail.summary.occurredAtUtc, detail.summary.eventId, location.hash)}>View activity at event time <span aria-hidden="true">→</span></a></section>
    <section className="deadlock-diagram-section" aria-labelledby="deadlock-diagram-heading"><div className="section-heading"><div><h3 id="deadlock-diagram-heading">Lock relationships</h3><p>Arrows point from waiter to blocker.</p></div><span className="table-note">{graph.nodes.length} participants · {graph.edges.length} relationships</span></div><div className="deadlock-diagram-scroll"><svg className="deadlock-diagram" viewBox={`0 0 ${graph.width} ${graph.height}`} role="img" aria-labelledby="deadlock-diagram-title deadlock-diagram-description"><title id="deadlock-diagram-title">Deadlock relationship diagram</title><desc id="deadlock-diagram-description">Directed edges point from a waiting session to the session it is waiting on. Victim sessions are labeled.</desc><defs><marker id="deadlock-arrow" markerWidth="10" markerHeight="10" viewBox="0 0 10 10" refX="8" refY="5" markerUnits="userSpaceOnUse" orient="auto"><path d="M0,0 L10,5 L0,10 z" /></marker></defs>{graph.edges.map((edge, index) => { const from = nodeById.get(edge.waiterSessionId); const to = nodeById.get(edge.blockerSessionId); if (!from || !to) return null; return <g key={`${edge.waiterSessionId}:${edge.blockerSessionId}:${index}`}><path className="deadlock-edge" d={edge.path} markerEnd="url(#deadlock-arrow)" /><text className="deadlock-edge-label" x={edge.label.x} y={edge.label.y}>{edge.resourceCategory.toUpperCase()} / {edge.lockMode} · {edge.waiterSessionId}→{edge.blockerSessionId}</text><title>Session {edge.waiterSessionId} waits on session {edge.blockerSessionId}: {edge.resourceCategory} / {edge.lockMode}</title></g>;})}{graph.nodes.map((node) => <g className={node.isVictim ? "deadlock-node victim" : "deadlock-node"} key={node.sessionId} transform={`translate(${node.x - 78},${node.y - 42})`}><rect width="156" height="84" rx="10" /><text x="78" y="32" textAnchor="middle">Session {node.sessionId}</text><text className="deadlock-node-state" x="78" y="58" textAnchor="middle">{node.isVictim ? "Victim" : "Participant"}</text></g>)}</svg></div><div className="diagram-legend"><span><i className="legend-arrow" aria-hidden="true" /> Arrow direction: waiter → blocker</span><span><i className="legend-victim" aria-hidden="true" /> Victim: {victimIds.length ? victimIds.join(", ") : "not identified"}</span></div></section>
    <section className="deadlock-relations" aria-labelledby="deadlock-relations-heading"><div className="section-heading"><div><h3 id="deadlock-relations-heading">Relationship table</h3><p>Complete available relationship evidence; no rows are hidden for the diagram.</p></div></div><div className="table-scroll"><table><caption>Waiting session to blocking session relationships</caption><thead><tr><th scope="col">Waiting session</th><th scope="col">Blocking session</th><th scope="col">Resource</th><th scope="col">Lock mode</th></tr></thead><tbody>{detail.relations.length ? detail.relations.map((relation, index) => <tr key={`${relation.waiterSessionId}:${relation.blockerSessionId}:${index}`}><td>{relation.waiterSessionId}</td><td>{relation.blockerSessionId}</td><td>{relation.resourceCategory}</td><td>{relation.lockMode}</td></tr>) : <tr><td colSpan={4}>No lock relationships were reported.</td></tr>}</tbody></table></div></section>
    <p className="evidence-callout">SQL Server selected {victimIds.length ? `session${victimIds.length === 1 ? "" : "s"} ${victimIds.join(", ")} as the victim${victimIds.length === 1 ? "" : "s"}.` : "no victim identity was available in this detail."}</p>
    <p className="table-note">Fingerprint {detail.summary.fingerprint} · Source: system_health · Observation time: {formatUtc(detail.summary.occurredAtUtc)}</p>
  </>;
}

function formatUtc(value: string): string {
  const parsed = new Date(value);
  return Number.isNaN(parsed.valueOf()) ? "Invalid timestamp" : parsed.toISOString().replace("T", " ").replace(".000Z", " UTC");
}
