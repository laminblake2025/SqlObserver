import { useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider, useTimeDisplay } from "../src/TimeDisplayContext";
import { TargetActivityPanel } from "../src/features/activity/TargetActivityPanel";
import type { OverviewScope } from "../src/features/overview/overviewTypes";
import "../src/styles.css";
import "./harness.css";

const targets = {
  alpha: "11111111-1111-4111-8111-111111111111",
  beta: "22222222-2222-4222-8222-222222222222",
};
type WindowChoice = "recent" | "earlier" | "long";

function Harness() {
  const { mode, setMode } = useTimeDisplay();
  const [target, setTarget] = useState<keyof typeof targets>("alpha");
  const [windowChoice, setWindowChoice] = useState<WindowChoice>("recent");
  const [refresh, setRefresh] = useState(0);
  const [databaseId, setDatabaseId] = useState<number>();
  const scope: OverviewScope = {
    target: targets[target], range: windowChoice === "long" ? "7d" : "custom", compare: false,
    from: windowChoice === "earlier" ? "2026-09-30T11:00:00.000Z" : "2026-09-30T12:00:00.000Z",
    to: windowChoice === "earlier" ? "2026-09-30T12:00:00.000Z" : "2026-09-30T13:00:00.000Z",
    databaseId,
  };
  return <main className="component-harness">
    <h1>Activity panel harness</h1>
    <div className="harness-controls">
      <label>Selected server<select value={target} onChange={event => {
        setTarget(event.target.value as keyof typeof targets); setDatabaseId(undefined);
      }}><option value="alpha">Alpha</option><option value="beta">Beta</option></select></label>
      <label>History window<select value={windowChoice} onChange={event => setWindowChoice(event.target.value as WindowChoice)}>
        <option value="recent">Recent hour</option><option value="earlier">Earlier hour</option><option value="long">Seven days</option>
      </select></label>
      <label>Time display<select value={mode} onChange={event => setMode(event.target.value === "utc" ? "utc" : "local")}>
        <option value="utc">UTC</option><option value="local">Local</option>
      </select></label>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh activity evidence</button>
    </div>
    <TargetActivityPanel instanceId={targets[target]} displayName={target === "alpha" ? "Alpha" : "Beta"}
      onClose={() => undefined} scope={scope} refresh={refresh} manualRefresh={refresh}
      sessionTick={0} livePaused={true} onSelectDatabase={setDatabaseId} />
  </main>;
}

createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
