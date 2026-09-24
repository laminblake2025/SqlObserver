import { useEffect, useState } from "react";
import { getCurrentBlockingPage } from "../activity/activityApi";
import { blockingPageIsComplete, groupBlockingEdges } from "../activity/blockingProjectionModel";
import type { ActivityPage, BlockingEdge } from "../activity/activityTypes";
import { overviewHref } from "../overview/overviewModel";
import type { OverviewScope } from "../overview/overviewTypes";

export function CurrentBlockingPanel({ instanceId, scope, refresh }: {
  readonly instanceId: string;
  readonly scope: OverviewScope;
  readonly refresh: number;
}) {
  const [page, setPage] = useState<ActivityPage<BlockingEdge>>();
  const [error, setError] = useState<string>();

  useEffect(() => {
    let active = true;
    const controller = new AbortController();
    async function poll() {
      try {
        const next = await getCurrentBlockingPage(instanceId, controller.signal);
        if (active) { setPage(next); setError(undefined); }
      } catch (failure) {
        if (active && !controller.signal.aborted) {
          setError(failure instanceof Error ? failure.message : "Current blocking is unavailable.");
        }
      }
    }
    setError(undefined);
    void poll();
    return () => { active = false; controller.abort(); };
  }, [instanceId, refresh]);

  const groups = page ? groupBlockingEdges(page.items) : [];
  const complete = page ? blockingPageIsComplete(page) : false;
  return <section className="panel server-dashboard-blocking" aria-labelledby="server-blocking-heading">
    <div className="section-heading"><div><p className="eyebrow">Current activity</p><h4 id="server-blocking-heading">Blocking chains</h4></div><a href={overviewHref(scope, "activity", instanceId)}>Open sessions and blocking →</a></div>
    {page === undefined && error === undefined && <p role="status">Loading current blocking…</p>}
    {error && <p role="status">{error} {page ? "Showing the previous snapshot." : ""}</p>}
    {page && <>
      <p className="server-dashboard-blocking-meta">Observed {page.evidence?.completedAtUtc ?? "time unavailable"}. {error ? "Previous current snapshot" : "Current snapshot"}, independent of the selected timeline window.</p>
      {groups.length === 0 && <p className="empty-state">{complete && !error ? "No blocked sessions in the current collection." : "No blocking rows on this page; collection is incomplete or unavailable."}</p>}
      {groups.length > 0 && <div className="server-dashboard-blocking-groups">{groups.map(group => <div className="server-dashboard-blocking-group" key={group.key}>
        <h5>{group.label} · {group.edges.length} {group.edges.length === 1 ? "edge" : "edges"} on this page</h5>
        <ul>{group.edges.map((edge, index) => <li key={`${edge.blockedSessionId}:${edge.blockerKind}:${edge.blockerSessionId ?? "none"}:${edge.waitType}:${index}`} style={{ marginLeft: `${Math.min(edge.chainDepth - 1, 8) * 14}px` }}>
          <strong>Session {edge.blockedSessionId}</strong> ← {edge.blockerSessionId === undefined ? edge.blockerKind : `session ${edge.blockerSessionId}`}
          <span>{edge.waitType} · {edge.waitDurationMilliseconds} ms · depth {edge.chainDepth} · {edge.chainState}</span>
        </li>)}</ul>
      </div>)}</div>}
      {(!complete || error) && <p className="server-dashboard-blocking-meta">Partial view: {page.nextCursor ? "more blocker edges exist; " : ""}{error ? "the latest refresh failed; " : ""}collection freshness or completeness is unconfirmed. Open activity for the full evidence.</p>}
    </>}
  </section>;
}
