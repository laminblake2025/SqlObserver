import { useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { AnalyticsSurfacePanel } from "../src/features/analytics/AnalyticsSurfacePanel";
import "../src/styles.css";
import "./harness.css";

function Harness() {
  const [target, setTarget] = useState("11111111-1111-4111-8111-111111111111");
  const [surface, setSurface] = useState<"jobs" | "backfill">("jobs");
  const [earlier, setEarlier] = useState(false);
  const [refresh, setRefresh] = useState(0);
  return <main className="component-harness">
    <h1>Analytics panel harness</h1>
    <div className="harness-controls">
      <label>Server context<select value={target} onChange={event => setTarget(event.target.value)}>
        <option value="11111111-1111-4111-8111-111111111111">Alpha</option>
        <option value="22222222-2222-4222-8222-222222222222">Beta</option>
      </select></label>
      <label>Evidence surface<select value={surface} onChange={event => setSurface(event.target.value as "jobs" | "backfill")}>
        <option value="jobs">Jobs</option><option value="backfill">Backfill</option>
      </select></label>
      <button onClick={() => setEarlier(value => !value)}>Change window</button>
      <button onClick={() => setRefresh(value => value + 1)}>Refresh context</button>
    </div>
    <AnalyticsSurfacePanel targetId={target} surface={surface} refresh={refresh}
      timeWindow={{ fromUtc: earlier ? "2026-09-07T00:00:00Z" : "2026-09-08T00:00:00Z",
        toUtc: earlier ? "2026-09-08T00:00:00Z" : "2026-09-09T00:00:00Z" }} />
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
