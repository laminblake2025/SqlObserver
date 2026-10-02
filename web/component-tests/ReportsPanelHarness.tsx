import { useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider, useTimeDisplay } from "../src/TimeDisplayContext";
import { ReportsPanel } from "../src/features/reports/ReportsPanel";
import "../src/styles.css";
import "./harness.css";

const targets = [
  { id: "11111111-1111-4111-8111-111111111111", name: "Production SQL <img src=x onerror=alert(1)>" },
  { id: "44444444-4444-4444-8444-444444444444", name: "Reporting SQL" },
] as const;

function Harness() {
  const [targetId, setTargetId] = useState<string>(targets[0].id);
  const [closed, setClosed] = useState(false);
  const { mode, setMode } = useTimeDisplay();
  const displayName = targets.find(target => target.id === targetId)?.name ?? "Unknown server";
  return <main className="component-harness">
    <h1>Reports panel harness</h1>
    <fieldset className="harness-controls">
      <legend>Panel context</legend>
      <label>Server context<select value={targetId} onChange={event => setTargetId(event.target.value)}>
        {targets.map(target => <option key={target.id} value={target.id}>{target.name}</option>)}
      </select></label>
      <label>Time display<select value={mode} onChange={event => setMode(event.target.value === "utc" ? "utc" : "local")}>
        <option value="local">Local</option><option value="utc">UTC</option>
      </select></label>
    </fieldset>
    {closed ? <p role="status">Panel closed</p> : <ReportsPanel instanceId={targetId} displayName={displayName} onClose={() => setClosed(true)} />}
  </main>;
}

const root = document.getElementById("root");
if (!root) throw new Error("Reports harness root is missing.");
createRoot(root).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
