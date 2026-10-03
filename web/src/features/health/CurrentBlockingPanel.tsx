import { useTimeFormatter } from "../../TimeDisplayContext";
import { groupBlockingEdges } from "../activity/blockingProjectionModel";
import { headBlockerFindings } from "./headBlockerModel";
import type { CurrentBlockingEvidence } from "./useCurrentBlockingEvidence.ts";
import { overviewHref } from "../overview/overviewModel";
import type { OverviewScope } from "../overview/overviewTypes";

export function CurrentBlockingPanel({ instanceId, scope, evidence, selectedRoot, targetRevision }: {
  readonly instanceId: string;
  readonly scope: OverviewScope;
  readonly evidence: CurrentBlockingEvidence;
  readonly selectedRoot?: number;
  readonly targetRevision: number;
}) {
  const formatTime = useTimeFormatter();
  const { page, error, loading, retained } = evidence;

  const groups = page ? groupBlockingEdges(page.items) : [];
  const complete = page ? headBlockerFindings(page, targetRevision).complete : false;
  return <section className="panel server-dashboard-blocking" aria-labelledby="server-blocking-heading">
    <div className="section-heading"><div><p className="eyebrow">Current activity</p><h4 id="server-blocking-heading" tabIndex={-1}>Blocking chains</h4></div><a href={overviewHref(scope, "activity", instanceId)}>Open sessions and blocking →</a></div>
    {page === undefined && error === undefined && <p role="status">Loading current blocking…</p>}
    {error && <p role="status">{error} {page ? "Showing the previous snapshot." : ""}</p>}
    {page && <>
      <p className="server-dashboard-blocking-meta">{page.evidence?.completedAtUtc ? `Collection completed ${formatTime(page.evidence.completedAtUtc)}` : `Collection time unavailable; repository checked ${formatTime(page.repositoryTimeUtc)}`}. {retained ? "Previous received snapshot" : "Received current snapshot"}, independent of the selected timeline window. {loading ? "Refresh pending." : ""}</p>
      {groups.length === 0 && <p className="empty-state">{complete && !retained ? "No blocked sessions in this complete received collection." : "No blocking rows on this page; collection is retained, incomplete or unavailable."}</p>}
      {groups.length > 0 && <div className="server-dashboard-blocking-groups">{groups.map(group => <div className={`server-dashboard-blocking-group${group.key === `session:${selectedRoot}` ? " selected-blocker" : ""}`} key={group.key}>
        {group.key === `session:${selectedRoot}` && <p role="status">Selected head-blocker evidence</p>}
        <h5>{group.edges.every(edge => edge.chainState === "resolved") ? group.label : `Unresolved blocking group · ${group.key}`} · {group.edges.length} {group.edges.length === 1 ? "edge" : "edges"} in this snapshot</h5>
        <ul>{group.edges.map((edge, index) => <li key={`${edge.blockedSessionId}:${edge.blockerKind}:${edge.blockerSessionId ?? "none"}:${edge.waitType}:${index}`} style={{ marginLeft: `${Math.min(edge.chainDepth - 1, 8) * 14}px` }}>
          <strong>Session {edge.blockedSessionId}</strong> ← {edge.blockerSessionId === undefined ? edge.blockerKind : `session ${edge.blockerSessionId}`}
          <span>{edge.waitType} · {edge.waitDurationMilliseconds} ms · depth {edge.chainDepth} · {edge.chainState} · observed {formatTime(edge.observedAtUtc)}</span>
        </li>)}</ul>
      </div>)}</div>}
      {(!complete || error) && <p className="server-dashboard-blocking-meta">Partial view: {page.nextCursor ? "more blocker edges exist; " : ""}{error ? "the latest refresh failed; " : ""}collection freshness or completeness is unconfirmed. Open activity for the full evidence.</p>}
    </>}
  </section>;
}
