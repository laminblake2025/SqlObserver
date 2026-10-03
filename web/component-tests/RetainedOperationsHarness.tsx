import { useLayoutEffect, useMemo, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { AvailabilityEvidencePanel } from "../src/features/operations/AvailabilityEvidencePanel";
import { BackupAssessmentPanel } from "../src/features/operations/BackupAssessmentPanel";
import { AgentFailureHistoryPanel } from "../src/features/operations/AgentFailureHistoryPanel";
import { useAgentHistory } from "../src/features/operations/useAgentHistory";
import { resolveAgentWindow } from "../src/features/operations/agentHistoryModel";
import type { OverviewScope } from "../src/features/overview/overviewTypes";
import "../src/styles.css";
import "./harness.css";
const alpha = "11111111-1111-4111-8111-000000000001", beta = "11111111-1111-4111-8111-000000000002";
function DashboardAgent({ target, scope, refresh, revision }: { target: string; scope: OverviewScope; refresh: number; revision: number }) {
  const window = useMemo(() => resolveAgentWindow(scope, Date.now()), [scope.range, scope.from, scope.to, refresh]);
  const history = useAgentHistory(target, revision, window, refresh);
  return <section aria-label="Dashboard Agent first-page evidence"><h3>Dashboard Agent first-page evidence</h3>
    {history.page && <p>Received window {history.page.window.fromUtc} to {history.page.window.toUtc}. Rows: {history.page.rows.length}</p>}
    {history.loading && <p role="status">Loading dashboard Agent evidence</p>}{history.error && <p role="alert">{history.error}</p>}
  </section>;
}
function Harness() {
  const [target, setTarget] = useState(alpha), [surface, setSurface] = useState("availability");
  const [range, setRange] = useState<"custom" | "1h">("custom"), [window, setWindow] = useState("first");
  const [revision, setRevision] = useState(1), [refresh, setRefresh] = useState(0), [mounted, setMounted] = useState(true);
  const [commit, setCommit] = useState(""); const evidence = useRef<HTMLDivElement>(null);
  const scope: OverviewScope = { target, range, compare: false,
    from: window === "first" ? "2026-09-30T11:00:00.000001Z" : "2026-09-30T12:00:00.000002Z",
    to: window === "first" ? "2026-09-30T12:00:00.000001Z" : "2026-09-30T13:00:00.000002Z" };
  useLayoutEffect(() => { setCommit(JSON.stringify({ target, window, revision,
    rows: evidence.current?.querySelectorAll("tbody tr").length ?? 0,
    alerts: evidence.current?.querySelectorAll('[role="alert"]').length ?? 0 })); }, [target, window, revision, mounted, range, surface]);
  return <main className="component-harness"><h1>Retained operations harness</h1><div className="harness-controls">
    <label>Selected server<select value={target} onChange={event => setTarget(event.target.value)}><option value={alpha}>Alpha</option><option value={beta}>Beta</option></select></label>
    <label>Operations screen<select value={surface} onChange={event => setSurface(event.target.value)}><option value="availability">Availability groups</option><option value="backups">Backup assessment</option><option value="agent">Agent history</option><option value="dashboard">Dashboard Agent</option></select></label>
    <label>Time mode<select value={range} onChange={event => setRange(event.target.value as "custom" | "1h")}><option value="custom">Rewind</option><option value="1h">Live</option></select></label>
    <label>Selected window<select value={window} onChange={event => setWindow(event.target.value)}><option value="first">First window</option><option value="second">Changed precise window</option></select></label>
    <button type="button" onClick={() => setRefresh(value => value + 1)}>Poll workspace</button>
    <button type="button" onClick={() => setRevision(value => value + 1)}>Change target revision</button>
    <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Unmount operations" : "Mount operations"}</button>
  </div><output aria-label="First committed operations context" className="harness-observations">{commit}</output>
  {mounted && <><h2>{target === alpha ? "Alpha" : "Beta"}</h2><div ref={evidence}>
    {surface === "availability" && <AvailabilityEvidencePanel instanceId={target} scope={scope} refresh={refresh} />}
    {surface === "backups" && <BackupAssessmentPanel instanceId={target} scope={scope} refresh={refresh} />}
    {surface === "agent" && <AgentFailureHistoryPanel instanceId={target} targetRevision={revision} scope={scope} refresh={refresh} />}
    {surface === "dashboard" && <DashboardAgent target={target} revision={revision} scope={scope} refresh={refresh} />}
  </div></>}
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
