import { useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { TargetWaitsPanel } from "../src/features/activity/TargetWaitsPanel";
import "../src/styles.css";
import "./harness.css";

function Harness() {
  const [refresh, setRefresh] = useState(0);
  const [target, setTarget] = useState("11111111-1111-4111-8111-111111111111");
  return <main className="component-harness">
    <h1>Waits panel harness</h1>
    <div className="harness-controls">
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Advance workspace poll</button>
      <label>Selected server<select value={target} onChange={event => setTarget(event.target.value)}>
        <option value="11111111-1111-4111-8111-111111111111">Alpha</option>
        <option value="22222222-2222-4222-8222-222222222222">Beta</option>
      </select></label>
    </div>
    <TargetWaitsPanel instanceId={target} displayName={target.startsWith("1111") ? "Alpha" : "Beta"}
      scope={{ target, range: "1h", compare: false }} refresh={refresh} onClose={() => undefined} />
  </main>;
}

createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
