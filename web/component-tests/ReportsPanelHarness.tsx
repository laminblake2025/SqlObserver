import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider, useTimeDisplay } from "../src/TimeDisplayContext";
import { ReportsPanel } from "../src/features/reports/ReportsPanel";
import { TimeRangeControls } from "../src/components/TimeRangeControls";
import { overviewHref, readOverviewScope } from "../src/features/overview/overviewModel";
import { readReportSelection, reportSelectionHref, retainReportSelectionHref } from "../src/features/reports/reportsWorkspaceModel";
import type { OverviewScope } from "../src/features/overview/overviewTypes";
import "../src/styles.css";
import "./harness.css";

const targets = [
  { id: "11111111-1111-4111-8111-111111111111", name: "Production SQL <img src=x onerror=alert(1)>" },
  { id: "44444444-4444-4444-8444-444444444444", name: "Reporting SQL" },
] as const;

function Harness() {
  const [hash, setHash] = useState(() => location.hash || reportSelectionHref({ target: targets[0].id,
    range: "custom", from: "2026-08-01T13:45:00.000Z", to: "2026-08-02T13:45:00.000Z", compare: false }, "instance-health"));
  const [refresh, setRefresh] = useState(0);
  const [closed, setClosed] = useState(false);
  const [mounted, setMounted] = useState(true);
  const { mode, setMode } = useTimeDisplay();
  useEffect(() => {
    if (!location.hash) location.hash = hash;
    const update = () => setHash(location.hash);
    window.addEventListener("hashchange", update);
    return () => window.removeEventListener("hashchange", update);
  }, []);
  const scope = readOverviewScope(hash);
  const selection = readReportSelection(hash);
  const displayName = targets.find(target => target.id === scope.target)?.name ?? "Unknown server";
  const changeScope = (change: Partial<OverviewScope>) => {
    location.hash = retainReportSelectionHref(overviewHref({ ...scope, ...change }, "reports"), hash);
  };
  return <main className="component-harness">
    <h1>Reports panel harness</h1>
    <fieldset className="harness-controls">
      <legend>Panel context</legend>
      <label>Server context<select value={scope.target} onChange={event => changeScope({ target: event.target.value })}>
        {targets.map(target => <option key={target.id} value={target.id}>{target.name}</option>)}
      </select></label>
      <div className="topbar-time-controls overview-controls" aria-label="Workspace time context"
        style={{ flexBasis: "100%", width: "100%", minWidth: 0 }}>
        <TimeRangeControls scope={scope} onChange={changeScope} maximumDays={31} />
      </div>
      <label>Time display<select value={mode} onChange={event => setMode(event.target.value === "utc" ? "utc" : "local")}>
        <option value="local">Local</option><option value="utc">UTC</option>
      </select></label>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh context</button>
      <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Unmount reports panel" : "Mount reports panel"}</button>
      <span>Context refresh count: {refresh}</span>
    </fieldset>
    {closed ? <p role="status">Panel closed</p> : mounted && <ReportsPanel instanceId={scope.target} displayName={displayName}
      scope={scope} selection={selection} onSelectKind={kind => { location.hash = reportSelectionHref(scope, kind); }}
      onClose={() => setClosed(true)} />}
  </main>;
}

const root = document.getElementById("root");
if (!root) throw new Error("Reports harness root is missing.");
createRoot(root).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
