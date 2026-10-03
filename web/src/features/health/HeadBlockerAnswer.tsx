import { headBlockerFindings } from "./headBlockerModel.ts";
import type { CurrentBlockingEvidence } from "./useCurrentBlockingEvidence.ts";

export function HeadBlockerAnswer({ evidence, targetRevision, formatTime, onInspect }: {
  readonly evidence: CurrentBlockingEvidence; readonly targetRevision: number;
  readonly formatTime: (value: string) => string; readonly onInspect: (sessionId: number) => void;
}) {
  const findings = evidence.page ? headBlockerFindings(evidence.page, targetRevision) : undefined;
  return <div className="dashboard-head-blockers">
    <h5>Current head blockers</h5>
    <p className="dashboard-investigation-note">Native blocking graph, server-wide and independent of the selected history window. Wait duration is observed waiting time, not the transaction age or condition start.</p>
    {!evidence.page && <p role="status">{evidence.error ?? "Loading current head-blocker evidence…"}</p>}
    {evidence.page && <>
      <p className="dashboard-investigation-note">{evidence.retained ? "Previously received snapshot" : "Received current snapshot"} · {evidence.page.evidence?.completedAtUtc ? `collection completed ${formatTime(evidence.page.evidence.completedAtUtc)}` : `collection time unavailable; repository checked ${formatTime(evidence.page.repositoryTimeUtc)}`}. {evidence.loading ? "Refresh pending." : evidence.error ?? ""}</p>
      {findings?.items.length ? <ul>{findings.items.slice(0, 3).map(root => <li key={root.sessionId}>
        <strong>Head blocker · session {root.sessionId}</strong><span>{root.blockedSessions} distinct blocked {root.blockedSessions === 1 ? "session" : "sessions"} in loaded resolved edges · longest observed wait {root.maximumWaitMilliseconds} ms</span>
        <button type="button" onClick={() => onInspect(root.sessionId)}>Inspect head blocker {root.sessionId}</button>
      </li>)}</ul> : <p>{findings?.complete && !evidence.retained ? "No blocked sessions in this complete received collection." : "No confirmed head blocker in the comparable loaded edges; this is not an all-clear signal."}</p>}
      <p className="dashboard-investigation-note">{findings?.complete && !evidence.retained ? "Complete received graph." : "Partial, stale, retained or unconfirmed graph; counts cover loaded resolved edges only."} {findings?.unresolvedEdges ?? 0} unresolved or non-comparable edges. Showing at most three roots, ranked by distinct blocked sessions and longest wait. Inspect opens the same received graph below; it does not select a historical session capture.</p>
    </>}
  </div>;
}
