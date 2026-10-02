import { useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider, useTimeDisplay } from "../src/TimeDisplayContext";
import { TargetHealthPanel } from "../src/features/health/TargetHealthPanel";
import type { OverviewScope } from "../src/features/overview/overviewTypes";
import "../src/styles.css";
import "./harness.css";

const targets = {
  alpha: "11111111-1111-4111-8111-111111111111",
  beta: "22222222-2222-4222-8222-222222222222",
};
// Deliberate UI-role fixture only; this flag does not authorize any server API.
const canManageConfiguration = new URLSearchParams(location.search).get("canManageConfiguration") === "true";

function Harness() {
  const { mode, setMode } = useTimeDisplay();
  const [target, setTarget] = useState<keyof typeof targets>("alpha");
  const [refresh, setRefresh] = useState(0);
  const [databaseId, setDatabaseId] = useState<number>();
  const [section, setSection] = useState<"summary" | "databases" | "collection">("collection");
  const [closed, setClosed] = useState(false);
  const scope: OverviewScope = {
    target: targets[target], range: "custom", compare: false,
    from: "2026-01-15T12:00:00.000Z", to: "2026-01-15T13:00:00.000Z", databaseId,
  };
  return <main className="component-harness">
    <h1>Health panel harness</h1>
    <div className="harness-controls">
      <label>Selected server<select value={target} onChange={event => {
        setTarget(event.target.value as keyof typeof targets); setDatabaseId(undefined);
      }}><option value="alpha">Alpha</option><option value="beta">Beta</option></select></label>
      <label>Time display<select value={mode} onChange={event => setMode(event.target.value === "utc" ? "utc" : "local")}>
        <option value="utc">UTC</option><option value="local">Local</option>
      </select></label>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh health evidence</button>
    </div>
    <output aria-label="Selected database context">{databaseId ?? "None"}</output>
    {closed ? <p>Health panel closed</p> : <TargetHealthPanel
      instanceId={targets[target]} displayName={target === "alpha" ? "Alpha" : "Beta"}
      onClose={() => setClosed(true)} scope={scope} refresh={refresh} healthRefresh={refresh}
      blockingRefresh={refresh} selectedDatabaseId={databaseId} onSelectDatabase={setDatabaseId}
      canManageConfiguration={canManageConfiguration} targetRevision={1} selectionHash=""
      selectedSection={section} onSelectSection={setSection}
    />}
  </main>;
}

createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
