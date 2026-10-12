import { useState } from "react";
import { createRoot } from "react-dom/client";
import { ServerConfigurationSnapshot } from "../src/features/health/ServerConfigurationSnapshot";
import { TimeDisplayProvider, useTimeDisplay } from "../src/TimeDisplayContext";
import "../src/styles.css";
import "./harness.css";

const alpha = "11111111-1111-4111-8111-000000000001";
const beta = "11111111-1111-4111-8111-000000000002";
function Harness() {
  const [target, setTarget] = useState(alpha);
  const [canManage, setCanManage] = useState(true);
  const [refresh, setRefresh] = useState(0);
  const [mounted, setMounted] = useState(true);
  const [rewind, setRewind] = useState(false);
  const { mode, setMode } = useTimeDisplay();
  return <main className="component-harness health-screen">
    <h1>Configuration snapshot harness</h1>
    <div className="harness-controls">
      <label>Target<select value={target} onChange={event => setTarget(event.target.value)}><option value={alpha}>Alpha</option><option value={beta}>Beta</option></select></label>
      <button type="button" onClick={() => setCanManage(value => !value)}>{canManage ? "Remove configuration authority" : "Restore configuration authority"}</button>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh workspace</button>
      <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Close configuration panel" : "Open configuration panel"}</button>
      <button type="button" onClick={() => setRewind(value => !value)}>{rewind ? "Return to Live" : "Rewind workspace"}</button>
      <label>Time display<select value={mode} onChange={event => setMode(event.target.value === "utc" ? "utc" : "local")}><option value="utc">UTC</option><option value="local">Local</option></select></label>
    </div>
    <p>Workspace time: {rewind ? "Rewind" : "Live"}. Configuration settings independently follow the latest complete scan.</p>
    <h2>Server summary</h2><h3>Configuration health</h3>
    {mounted && <ServerConfigurationSnapshot instanceId={target} refresh={refresh} canManage={canManage} />}
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
