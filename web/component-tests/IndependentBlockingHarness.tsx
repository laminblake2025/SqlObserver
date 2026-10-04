import { useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { ServerDashboard } from "../src/features/health/ServerDashboard";
import { overviewHref } from "../src/features/overview/overviewModel";
import "../src/styles.css";
import "../src/features/health/dashboardInvestigationSummary.css";
import "./harness.css";

const alpha = "11111111-1111-4111-8111-000000000001", beta = "11111111-1111-4111-8111-000000000002";
function Harness() {
  const [target, setTarget] = useState(alpha), [revision, setRevision] = useState(7);
  const [refresh, setRefresh] = useState(0), [blockingRefresh, setBlockingRefresh] = useState(0), [active, setActive] = useState(true);
  const [earlier, setEarlier] = useState(false);
  const [denials, setDenials] = useState<readonly number[]>([]);
  const scope = { target, range: "custom" as const, from: earlier ? "2026-01-13T12:00:00.123456Z" : "2026-01-14T12:00:00.123456Z",
    to: earlier ? "2026-01-13T13:00:00.123456Z" : "2026-01-14T13:00:00.123456Z", compare: false };
  const selectionHash = overviewHref(scope, "health", target);
  // The harness makes the same committed URL/interaction boundary the real App supplies.
  if (location.hash !== selectionHash) history.replaceState(null, "", selectionHash);
  return <main className="component-harness"><h1>Independent current blocking qualification</h1>
    <div className="harness-controls">
      <label>Selected server<select value={target} onChange={event => setTarget(event.target.value)}>
        <option value={alpha}>Alpha</option><option value={beta}>Beta</option>
      </select></label>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh analytics</button>
      <button type="button" onClick={() => setBlockingRefresh(value => value + 1)}>Refresh blocking</button>
      <button type="button" onClick={() => setRevision(value => value + 1)}>Change revision</button>
      <button type="button" onClick={() => setEarlier(value => !value)}>Change history window</button>
      <button type="button" onClick={() => setActive(value => !value)}>{active ? "Deactivate dashboard" : "Activate dashboard"}</button>
    </div>
    <output aria-label="Parent blocking denial notifications">{JSON.stringify(denials)}</output>
    <h2>Dashboard source evidence</h2>
    <ServerDashboard instanceId={target} displayName={target === alpha ? "Alpha" : "Beta"} targetRevision={revision}
      scope={scope} selectionHash={selectionHash} canReadRules={false} refresh={refresh} blockingRefresh={blockingRefresh} active={active}
      onAccessDenied={status => setDenials(previous => [...previous, status])} />
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
