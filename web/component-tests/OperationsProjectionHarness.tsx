import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { OperationsPanel } from "../src/features/operations/OperationsPanel";
import { healthCheckHref, healthCheckTab } from "../src/features/operations/agentHistoryModel";
import { readOverviewScope } from "../src/features/overview/overviewModel";
import "../src/styles.css";
import "./harness.css";

const alpha = "11111111-1111-4111-8111-000000000001", beta = "11111111-1111-4111-8111-000000000002";
function Harness() {
  const [hash, setHash] = useState(location.hash), [revision, setRevision] = useState(1);
  const [refresh, setRefresh] = useState(0), [mounted, setMounted] = useState(true), [commit, setCommit] = useState("");
  const owningPanel = useRef<HTMLDivElement>(null);
  const scope = readOverviewScope(hash);
  const selectedTab = healthCheckTab(new URLSearchParams(hash.split("?")[1]).get("check")) ?? "availability";
  useEffect(() => {
    const listener = () => setHash(location.hash);
    addEventListener("hashchange", listener); return () => removeEventListener("hashchange", listener);
  }, []);
  useLayoutEffect(() => {
    const projectionText = [...(owningPanel.current?.querySelectorAll('.operation-card') ?? [])].map(element => element.textContent);
    setCommit(JSON.stringify({ target: scope.target, revision, range: scope.range, from: scope.from, to: scope.to,
      database: scope.databaseId, mounted, projectionText }));
  }, [scope.target, revision, scope.range, scope.from, scope.to, scope.databaseId, mounted]);
  const changeWindow = (name: string) => {
    const next = name === "live" ? { ...scope, range: "1h" as const, from: undefined, to: undefined }
      : { ...scope, range: "custom" as const, from: name === "first" ? "2026-01-15T12:00:00.123456Z" : "2026-01-15T12:30:00.123456Z",
        to: name === "first" ? "2026-01-15T13:00:00.123456Z" : "2026-01-15T13:30:00.123456Z" };
    location.hash = healthCheckHref(next, next.target, selectedTab);
  };
  return <main style={{ maxWidth: 1200, padding: 20, minWidth: 0, margin: "0 auto" }}>
    <h1>Operations projection harness</h1>
    <div className="harness-controls">
      <label>Selected server<select value={scope.target} onChange={event => {
        location.hash = healthCheckHref({ ...scope, target: event.target.value }, event.target.value, selectedTab);
      }}><option value={alpha}>Alpha</option><option value={beta}>Beta</option></select></label>
      <label>Diagnostic window<select value={scope.range === "1h" ? "live" : scope.from === "2026-01-15T12:30:00.123456Z" ? "second" : "first"}
        onChange={event => changeWindow(event.target.value)}><option value="first">Initial precise window</option><option value="second">Changed precise window</option><option value="live">Live hour</option></select></label>
      <label>Database context<select value={scope.databaseId ?? "all"} onChange={event => {
        location.hash = healthCheckHref({ ...scope, databaseId: event.target.value === "all" ? undefined : Number(event.target.value) }, scope.target, selectedTab);
      }}><option value="all">All databases</option><option value="7">Database 7</option><option value="8">Database 8</option></select></label>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh workspace</button>
      <button type="button" onClick={() => setRevision(value => value + 1)}>Advance target revision</button>
      <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Unmount operations" : "Mount operations"}</button>
    </div>
    <output className="harness-observations" aria-label="First committed projection context">{commit}</output>
    <div ref={owningPanel}>{mounted ? <OperationsPanel instanceId={scope.target} targetRevision={revision} initialTab={selectedTab}
      refresh={refresh} scope={scope} /> : <p>Operations unmounted</p>}</div>
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
