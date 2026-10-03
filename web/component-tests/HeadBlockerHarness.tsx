import { useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { DashboardInvestigationSummary } from "../src/features/health/DashboardInvestigationSummary";
import { CurrentBlockingPanel } from "../src/features/health/CurrentBlockingPanel";
import { useCurrentBlockingEvidence } from "../src/features/health/useCurrentBlockingEvidence";
import "../src/styles.css";
import "../src/features/health/dashboardInvestigationSummary.css";
import "./harness.css";
const alpha = "11111111-1111-4111-8111-000000000001", beta = "11111111-1111-4111-8111-000000000002";
function Harness() {
  const [target, setTarget] = useState(alpha), [refresh, setRefresh] = useState(0), [revision, setRevision] = useState(7);
  const [selection, setSelection] = useState<{ key: string; root: number }>();
  const evidence = useCurrentBlockingEvidence(target, revision, refresh);
  const key = JSON.stringify([target, revision, evidence.page?.evidence?.runId]);
  const scope = { target, range: "custom" as const, from: "2026-01-14T12:00:00Z", to: "2026-01-14T13:00:00Z", compare: false };
  return <main className="component-harness"><h1>Current blocker dashboard</h1>
    <div className="harness-controls"><label>Selected server<select value={target} onChange={event => setTarget(event.target.value)}><option value={alpha}>Alpha</option><option value={beta}>Beta</option></select></label>
      <button onClick={() => setRefresh(value => value + 1)}>Refresh blocking</button><button onClick={() => setRevision(value => value + 1)}>Change revision</button></div>
    <h2>{target === alpha ? "Alpha" : "Beta"}</h2><h3>Investigate server</h3>
    <DashboardInvestigationSummary targetId={target} scope={scope} fromUtc={scope.from} toUtc={scope.to} issues={[]} events={[]} formatTime={value => value}
      blocking={{ evidence, targetRevision: revision, onInspect: root => { setSelection({ key, root }); const heading = document.getElementById("server-blocking-heading"); heading?.focus(); heading?.scrollIntoView({ block: "start" }); } }} />
    <CurrentBlockingPanel instanceId={target} scope={scope} evidence={evidence} targetRevision={revision} selectedRoot={selection?.key === key ? selection.root : undefined} />
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
