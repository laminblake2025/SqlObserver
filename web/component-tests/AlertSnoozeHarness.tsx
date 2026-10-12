import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { alertSelectionFromHash } from "../src/components/commandPaletteModel";
import { FleetAlertsPage } from "../src/features/alerts/FleetAlertsPage";
import { TargetAlertsPanel } from "../src/features/alerts/TargetAlertsPanel";
import { overviewHref, readOverviewScope } from "../src/features/overview/overviewModel";
import "../src/styles.css";
import "./harness.css";
const alpha = "11111111-1111-4111-8111-111111111111", beta = "44444444-4444-4444-8444-444444444444";
function Harness() {
  const [hash, setHash] = useState(location.hash), [refresh, setRefresh] = useState(0), [revision, setRevision] = useState(7);
  const [mounted, setMounted] = useState(true), [allowed, setAllowed] = useState(true), [commit, setCommit] = useState("");
  const region = useRef<HTMLDivElement>(null), scope = readOverviewScope(hash), selection = alertSelectionFromHash(hash), fleet = !scope.target;
  useEffect(() => { const update = () => setHash(location.hash); addEventListener("hashchange", update); return () => removeEventListener("hashchange", update); }, []);
  useLayoutEffect(() => { setCommit(JSON.stringify({ target: scope.target, revision, text: region.current?.textContent ?? "" })); },
    [scope.target, scope.range, scope.from, scope.to, scope.databaseId, revision]);
  return <main className="component-harness"><h1>Alert notification harness</h1>
    <fieldset className="harness-controls"><legend>Panel context</legend>
      <label>Inbox context<select value={fleet ? "fleet" : scope.target} onChange={event => { location.hash = overviewHref({ ...scope,
        target: event.target.value === "fleet" ? "" : event.target.value }, "alerts"); }}>
        <option value="fleet">Fleet</option><option value={alpha}>Alpha</option><option value={beta}>Beta</option></select></label>
      <label>Window context<select value={scope.range} onChange={event => { location.hash = overviewHref({ ...scope, range: event.target.value as "1h" | "custom",
        from: "2026-09-26T12:00:00.123456Z", to: "2026-09-26T13:00:00.123456Z" }, "alerts"); }}>
        <option value="1h">Live hour</option><option value="custom">Rewind</option></select></label>
      <label><input type="checkbox" checked={allowed} onChange={event => setAllowed(event.target.checked)} />Operator permission</label>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Workspace tick</button>
      <button type="button" onClick={() => setRevision(value => value + 1)}>Advance revision</button>
      <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Unmount alerts" : "Mount alerts"}</button>
    </fieldset>
    <output className="harness-observations" aria-label="First committed notification context">{commit}</output>
    <div ref={region}>{mounted && (fleet ? <FleetAlertsPage refresh={refresh} selection={selection} /> : <TargetAlertsPanel instanceId={scope.target}
      targetRevision={revision} displayName={scope.target === alpha ? "Alpha" : "Beta"} canAcknowledge={allowed} canManageRules={false}
      canManageDestinations={false} onClose={() => setMounted(false)} refresh={refresh}
      selectedAlertId={selection?.targetId === scope.target ? selection.alertId : undefined} />)}</div>
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
