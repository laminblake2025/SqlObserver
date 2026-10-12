import { useLayoutEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { BufferNodePlePanel } from "../src/features/resources/BufferNodePlePanel";
import { DatabaseFileIoTrendPanel } from "../src/features/resources/DatabaseFileIoTrendPanel";
import { useOverviewAnalytics } from "../src/features/overview/useOverviewAnalytics";
import { OverviewChart } from "../src/features/overview/OverviewChart";
import type { OverviewScope } from "../src/features/overview/overviewTypes";
import "../src/styles.css";
import "../src/components/diagnosticWorkflow.css";
import "./harness.css";

const alpha = "11111111-1111-4111-8111-000000000001", beta = "11111111-1111-4111-8111-000000000002";
const first = { fromUtc: "2026-01-15T12:00:00.123456Z", toUtc: "2026-01-15T13:00:00.123456Z" };
const second = { fromUtc: "2026-01-15T12:30:00.123456Z", toUtc: "2026-01-15T13:30:00.123456Z" };
declare global { interface Window { diagnosticWorkflowCommits: readonly { context: string; charts: number; options: readonly string[]; errors: number }[]; } }

function Harness() {
  const [target, setTarget] = useState(alpha), [revision, setRevision] = useState(1), [refresh, setRefresh] = useState(0);
  const [bounds, setBounds] = useState(first), [selectedNode, setSelectedNode] = useState<number>();
  const [invalid, setInvalid] = useState(false), [mounted, setMounted] = useState(true), [compare, setCompare] = useState(false);
  const root = useRef<HTMLDivElement>(null);
  const scope: OverviewScope = { target, range: "custom", from: bounds.fromUtc, to: bounds.toUtc, compare };
  const result = useOverviewAnalytics(scope, refresh, revision);
  const context = JSON.stringify([target, revision, bounds.fromUtc, bounds.toUtc, selectedNode, invalid]);
  useLayoutEffect(() => {
    window.diagnosticWorkflowCommits = [...(window.diagnosticWorkflowCommits ?? []), { context,
      charts: root.current?.querySelectorAll("svg.overview-chart").length ?? 0,
      options: [...(root.current?.querySelectorAll("select option") ?? [])].map(option => option.textContent ?? ""),
      errors: root.current?.querySelectorAll('[role="alert"]').length ?? 0 }];
  }, [context, mounted]);
  return <main className="component-harness">
    <h1>Diagnostic workflow qualification</h1>
    <div className="harness-controls diagnostic-workflow-controls">
      <label>Selected server<select value={target} onChange={event => setTarget(event.target.value)}><option value={alpha}>Alpha</option><option value={beta}>Beta</option></select></label>
      <label>Target revision<select value={revision} onChange={event => setRevision(Number(event.target.value))}><option value="1">1</option><option value="2">2</option></select></label>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh diagnostics</button>
      <button type="button" onClick={() => setBounds(second)}>Change window</button>
      <button type="button" onClick={() => { setSelectedNode(9); setInvalid(false); }}>Request missing node</button>
      <button type="button" onClick={() => { setSelectedNode(undefined); setInvalid(true); }}>Request invalid node</button>
      <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Unmount diagnostic panels" : "Mount diagnostic panels"}</button>
      <label className="harness-checkbox"><input type="checkbox" checked={compare} onChange={event => setCompare(event.target.checked)} />Compare previous</label>
    </div>
    <h2>{target === alpha ? "Alpha" : "Beta"} diagnostic evidence</h2>
    <div ref={root}>
      {mounted && <>
        <section className="panel server-dashboard-chart" aria-label="Overview revision evidence">
          <h3>Overview revision evidence</h3>
          {result.loading && <p role="status">{result.data ? "Refreshing the same diagnostic context" : "Loading the current diagnostic context"}</p>}
          {result.error && <p role="alert">{result.error}</p>}
          {result.data && <><p>Overview sample <output aria-label="Overview sample">{result.data.evidence[0]?.series[0]?.points[0]?.value}</output></p>
            <OverviewChart series={result.data.evidence[0]?.series ?? []} fromUtc={result.data.fromUtc} toUtc={result.data.toUtc} />
            {result.previous && <p>Previous sample <output aria-label="Previous sample">{result.previous.evidence[0]?.series[0]?.points[0]?.value}</output></p>}
          </>}
        </section>
        <BufferNodePlePanel instanceId={target} targetRevision={revision} scope={scope} refresh={refresh} selectedNodeId={selectedNode}
          nodeSelectionInvalid={invalid} onSelectNode={node => { setSelectedNode(node); setInvalid(false); }} />
        <DatabaseFileIoTrendPanel instanceId={target} targetRevision={revision} scope={scope} refresh={refresh}
          file={{ databaseId: 7, fileId: 1, logicalName: "SYNTHETIC captured file" }} />
      </>}
    </div>
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
