import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider, useTimeDisplay } from "../src/TimeDisplayContext";
import { SqlVolumePanel } from "../src/features/resources/SqlVolumePanel";
import "../src/styles.css";
import "./harness.css";

function Harness() {
  const [target, setTarget] = useState("11111111-1111-4111-8111-000000000001");
  const [revision, setRevision] = useState(1);
  const [refresh, setRefresh] = useState(0);
  const [isRewind, setRewind] = useState(false);
  const [mounted, setMounted] = useState(true);
  const [automatic, setAutomatic] = useState(false);
  const { mode, setMode } = useTimeDisplay();
  useEffect(() => {
    if (!automatic) return;
    const timer = window.setInterval(() => setRefresh(value => value + 1), 60_000);
    return () => window.clearInterval(timer);
  }, [automatic]);
  return <main className="component-harness">
    <h1>SQL volume browsing harness</h1>
    <div className="harness-controls">
      <label>Selected server<select value={target} onChange={event => setTarget(event.target.value)}>
        <option value="11111111-1111-4111-8111-000000000001">Alpha</option>
        <option value="11111111-1111-4111-8111-000000000002">Beta</option>
      </select></label>
      <label>Target revision<select value={revision} onChange={event => setRevision(Number(event.target.value))}>
        <option value={1}>Revision 1</option><option value={2}>Revision 2</option>
      </select></label>
      <label>Time context<select value={isRewind ? "rewind" : "live"} onChange={event => setRewind(event.target.value === "rewind")}>
        <option value="live">Live</option><option value="rewind">Rewind</option>
      </select></label>
      <label>Time display<select value={mode} onChange={event => setMode(event.target.value === "utc" ? "utc" : "local")}>
        <option value="local">Local</option><option value="utc">UTC</option>
      </select></label>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Advance workspace poll</button>
      <label className="harness-checkbox"><input type="checkbox" checked={automatic} onChange={event => setAutomatic(event.target.checked)} />Automatic workspace poll</label>
      <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Unmount volume panel" : "Mount volume panel"}</button>
    </div>
    <h2>Server resources</h2>
    {mounted ? <SqlVolumePanel instanceId={target} targetRevision={revision} refresh={refresh} isRewind={isRewind} /> : <p>Volume panel unmounted</p>}
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
