import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { OperationsPanel } from "../src/features/operations/OperationsPanel";
import { healthCheckHref, healthCheckTab } from "../src/features/operations/agentHistoryModel";
import {readTempDbSelection,tempDbSelectionHref,retainedTempDbSelectionHref} from "../src/features/operations/tempDbSelectionModel";
import { readOverviewScope } from "../src/features/overview/overviewModel";
import "../src/styles.css";
import "./harness.css";

const targets = [
  { id: "11111111-1111-4111-8111-111111111111", name: "Production SQL" },
  { id: "44444444-4444-4444-8444-444444444444", name: "Reporting SQL" },
] as const;

function Harness() {
  const [hash, setHash] = useState(location.hash);
  const [refresh, setRefresh] = useState(0);
  useEffect(() => {
    const update = () => setHash(location.hash);
    window.addEventListener("hashchange", update);
    return () => window.removeEventListener("hashchange", update);
  }, []);
  const scope = readOverviewScope(hash);
  const tab = healthCheckTab(new URLSearchParams(hash.split("?")[1]).get("check")) ?? "backups";
  return <main style={{ padding: "20px", minWidth: 0 }}>
    <h1>Operations panel harness</h1>
    <fieldset className="harness-controls"><legend>Panel context</legend>
      <label>Server context<select value={scope.target} onChange={event => {
        location.hash = healthCheckHref({ ...scope, target: event.target.value }, event.target.value, tab);
      }}>{targets.map(target => <option key={target.id} value={target.id}>{target.name}</option>)}</select></label>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh context</button>
    </fieldset>
    <OperationsPanel instanceId={scope.target} targetRevision={4} scope={scope} initialTab={tab} refresh={refresh} selectedTempDb={readTempDbSelection(hash,scope.target)} onSelectTempDb={(selection,window) => {location.hash=window ? retainedTempDbSelectionHref(scope,window,selection) : tempDbSelectionHref(scope,selection);}} onRetainTempDbWindow={(window,selection)=>{location.hash=retainedTempDbSelectionHref(scope,window,selection);}} />
  </main>;
}

const root = document.getElementById("root");
if (!root) throw new Error("Operations harness root is missing.");
createRoot(root).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
