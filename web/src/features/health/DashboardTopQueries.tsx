import { useEffect, useRef, useState } from "react";
import { ApiRequestError } from "../../apiClient";
import { overviewHref } from "../overview/overviewModel";
import type { OverviewScope } from "../overview/overviewTypes";
import { getQueryPerformanceTop } from "../queries/queryPerformanceApi";
import type { QueryPerformancePage } from "../queries/queryPerformanceTypes";

export function DashboardTopQueries({ targetId, targetRevision, window, scope, refresh, onAccessDenied }: {
  readonly targetId: string; readonly targetRevision: number; readonly window?: { fromUtc: string; toUtc: string };
  readonly scope: OverviewScope; readonly refresh: number; readonly onAccessDenied?: (status: 401 | 403) => void;
}) {
  const key = JSON.stringify([targetId.toLowerCase(), targetRevision, window?.fromUtc, window?.toUtc, refresh]);
  const [received, setReceived] = useState<{ key: string; page?: QueryPerformancePage; error?: string }>();
  const denied = useRef(onAccessDenied); denied.current = onAccessDenied;
  useEffect(() => {
    if (!window) return;
    const controller = new AbortController();
    void getQueryPerformanceTop(targetId, "duration", undefined, window.fromUtc, window.toUtc, controller.signal)
      .then(page => { if (!controller.signal.aborted) setReceived({ key, page }); })
      .catch(failure => {
        if (controller.signal.aborted) return;
        setReceived({ key, error: "Query ranking evidence is unavailable for this window." });
        if (failure instanceof ApiRequestError && (failure.status === 401 || failure.status === 403)) denied.current?.(failure.status);
      });
    return () => controller.abort();
  }, [key]);
  const result = received?.key === key ? received : undefined;
  const all = result?.page?.items ?? [];
  const scoped = scope.databaseId === undefined ? all : all.filter(item => item.query.databaseId === scope.databaseId);
  return <section className="panel dashboard-top-queries" aria-label="Top queries in window">
    <div className="section-heading"><h4>Top queries in window</h4><a href={overviewHref(scope, "queries", targetId)}>Open queries →</a></div>
    {result?.error ? <p role="status">{result.error}</p> : !result?.page ? <p role="status">{window ? "Loading query ranking metadata…" : "Waiting for a comparable server window."}</p> : <>
      <div className="table-scroll" tabIndex={0} role="region" aria-label="Query ranking metadata"><table><caption>Duration ranking · loaded evidence</caption>
        <thead><tr><th scope="col">Database</th><th scope="col">Query identity</th><th scope="col">Duration ms</th><th scope="col">Executions</th></tr></thead>
        <tbody>{scoped.slice(0, 3).map(item => <tr key={`${item.collectionRunId}:${item.observationKey}`}><td>ID {item.query.databaseId}</td>
          <td><abbr title={item.query.queryFingerprint}>{item.query.queryFingerprint.slice(0, 12)}…</abbr><small>{item.source.replaceAll("_", " ")} · {item.semantics.replaceAll("_", " ")}</small></td>
          <td>{item.metrics.durationMilliseconds?.toLocaleString() ?? "—"}</td><td>{item.metrics.executions?.toLocaleString() ?? "—"}</td></tr>)}</tbody>
      </table></div>
      {scoped.length === 0 && <p className="empty-state">No ranked queries in this loaded page for the selected database.</p>}
      <details className="supporting-evidence"><summary>Ranking coverage</summary><p>First {Math.min(3, scoped.length)} of {scoped.length} matching rows in a server-wide page{result.page.nextCursor ? "; more pages available" : ""}. Database filtering is local to this bounded page. Source semantics and collection gaps can affect rankings.</p><ul>{scoped.slice(0, 3).map(item => <li key={item.observationKey}>{item.query.queryFingerprint}: {item.coverage}; {item.fresh ? "fresh" : "stale"}{item.truncated ? "; truncated" : ""} · {item.intervalStartUtc} to {item.intervalEndUtc}</li>)}</ul><p>Query text is loaded only by a deliberate request in Queries, subject to Query Text Reader access.</p></details>
    </>}
  </section>;
}
