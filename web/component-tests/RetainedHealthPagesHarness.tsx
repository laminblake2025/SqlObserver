import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { DatabaseHealth, DatabaseFileHealth } from "../src/features/health/TargetHealthPanel";
import { ServerConfigurationChanges } from "../src/features/health/ServerConfigurationChanges";
import { getDatabaseHealth, getDatabaseFileHealth } from "../src/features/health/healthApi";
import type { DatabaseHealthPage, DatabaseFileHealthPage, CollectorHealthSummary } from "../src/features/health/healthTypes";
import type { OverviewScope } from "../src/features/overview/overviewTypes";
import "../src/styles.css";
import "./harness.css";

const alpha = "11111111-1111-4111-8111-000000000001";
const beta = "11111111-1111-4111-8111-000000000002";
const run = "33333333-3333-4333-8333-000000000002";
function Harness() {
  const [target, setTarget] = useState(alpha), [revision, setRevision] = useState(1);
  const [refresh, setRefresh] = useState(0), [range, setRange] = useState("first");
  const [database, setDatabase] = useState<number>(), [selectedRun, setSelectedRun] = useState<string>();
  const [disabled, setDisabled] = useState(false), [mounted, setMounted] = useState(true);
  const [receipt, setReceipt] = useState<{ key: string; databases: DatabaseHealthPage; files: DatabaseFileHealthPage }>();
  const [failure, setFailure] = useState<string>();
  const [commit, setCommit] = useState("");
  const databaseRegion = useRef<HTMLDivElement>(null), configRegion = useRef<HTMLDivElement>(null);
  const key = `${target}:${revision}`;
  const scope: OverviewScope = { target, range: range === "live" ? "1h" : "custom", compare: false,
    from: range === "live" ? undefined : range === "first" ? "2026-01-15T12:00:00.123456Z" : "2026-01-15T12:30:00.123456Z",
    to: range === "live" ? undefined : range === "first" ? "2026-01-15T13:00:00.123456Z" : "2026-01-15T13:30:00.123456Z" };
  const data = receipt?.key === key ? receipt : undefined;
  useEffect(() => {
    if (!mounted) return;
    const controller = new AbortController();
    void Promise.all([getDatabaseHealth(target, controller.signal), getDatabaseFileHealth(target, controller.signal)])
      .then(([databases, files]) => { if (!controller.signal.aborted) { setReceipt({ key, databases, files }); setFailure(undefined); } })
      .catch(() => { if (!controller.signal.aborted) setFailure(key); });
    return () => controller.abort();
  }, [key, refresh, mounted]);
  useLayoutEffect(() => {
    setCommit(JSON.stringify({ target, revision, range, database, selectedRun, disabled,
      databaseText: databaseRegion.current?.textContent ?? "", configurationText: configRegion.current?.textContent ?? "" }));
  }, [key, range, database, selectedRun, disabled, mounted]);
  const collector = data?.databases.collector;
  const configCollector = collector ? { ...collector, collectorId: "engine.configurations",
    state: disabled ? "disabled" as const : collector.state } as CollectorHealthSummary : undefined;
  return <main className="component-harness">
    <h1>Retained health pages harness</h1>
    <div className="harness-controls">
      <label>Selected server<select value={target} onChange={event => setTarget(event.target.value)}><option value={alpha}>Alpha</option><option value={beta}>Beta</option></select></label>
      <label>Diagnostic window<select value={range} onChange={event => setRange(event.target.value)}><option value="first">Initial precise window</option><option value="second">Changed precise window</option><option value="live">Live hour</option></select></label>
      <label>Database context<select value={database ?? "all"} onChange={event => setDatabase(event.target.value === "all" ? undefined : Number(event.target.value))}><option value="all">All databases</option><option value="6">Database 6</option><option value="9">Database 9</option><option value="99">Missing database</option></select></label>
      <label>Selected configuration run<select value={selectedRun ?? "all"} onChange={event => setSelectedRun(event.target.value === "all" ? undefined : event.target.value)}><option value="all">No selected scan</option><option value={run}>Second scan</option></select></label>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh workspace</button>
      <button type="button" onClick={() => setRevision(value => value + 1)}>Advance target revision</button>
      <button type="button" onClick={() => setDisabled(value => !value)}>{disabled ? "Enable collection context" : "Disable collection context"}</button>
      <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Unmount health pages" : "Mount health pages"}</button>
    </div>
    <output aria-label="First committed health context" className="harness-observations">{commit}</output>
    <h2>{target === alpha ? "Alpha" : "Beta"}</h2><h3>Health investigation</h3>
    {mounted ? <>
      {failure === key && <p role="alert">Fixture first page unavailable.</p>}
      <div ref={databaseRegion}>{data && <><DatabaseHealth instanceId={target} targetRevision={revision} firstPage={data.databases}
        recoveryContext={{ instanceId: target, scope, canGeneratePermissions: false }} selectedDatabaseId={database} onSelectDatabase={setDatabase} />
        <DatabaseFileHealth page={data.files} recoveryContext={{ instanceId: target, scope, canGeneratePermissions: false }} selectedDatabaseId={database} /></>}</div>
      <div ref={configRegion}><ServerConfigurationChanges instanceId={target} targetRevision={revision} scope={scope} refresh={refresh}
        collector={configCollector} canManage={false} selectedRunId={selectedRun} /></div>
    </> : <p>Health pages unmounted</p>}
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
