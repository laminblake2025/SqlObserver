import { useEffect, useLayoutEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider, useTimeDisplay } from "../src/TimeDisplayContext";
import { TargetResourcesPanel } from "../src/features/resources/TargetResourcesPanel";
import { readOverviewScope } from "../src/features/overview/overviewModel";
import "../src/styles.css";
import "./harness.css";

declare global { interface Window { resourceFileCommits: { context: string; text: string }[]; } }
const alpha = "11111111-1111-4111-8111-000000000001";
function Harness() {
  const [hash, setHash] = useState(location.hash);
  const [revision, setRevision] = useState(1);
  const [refresh, setRefresh] = useState(0);
  const [mounted, setMounted] = useState(true);
  const { mode, setMode } = useTimeDisplay();
  const scope = readOverviewScope(hash);
  useEffect(() => { const changed = () => setHash(location.hash); window.addEventListener("hashchange", changed); return () => window.removeEventListener("hashchange", changed); }, []);
  useLayoutEffect(() => {
    window.resourceFileCommits ??= [];
    window.resourceFileCommits.push({ context: JSON.stringify([scope.target, revision, scope.from, scope.to, scope.databaseId]),
      text: document.querySelector('[aria-label="Database file I/O"]')?.textContent ?? "" });
  });
  function change(key: string, value: string) {
    const parameters = new URLSearchParams(location.hash.split("?")[1]);
    if (value) parameters.set(key, value); else parameters.delete(key);
    if (key === "target") { parameters.delete("resourceDatabaseId"); parameters.delete("resourceFileId"); }
    location.hash = `resources?${parameters}`; setHash(location.hash);
  }
  return <main className="component-harness">
    <h1>Retained resource-file evidence harness</h1>
    <div className="harness-controls">
      <label>Selected server<select value={scope.target || alpha} onChange={event => change("target", event.target.value)}>
        <option value={alpha}>Alpha</option><option value="11111111-1111-4111-8111-000000000002">Beta</option>
      </select></label>
      <label>Target revision<select value={revision} onChange={event => setRevision(Number(event.target.value))}><option value={1}>Revision 1</option><option value={2}>Revision 2</option></select></label>
      <label>Database scope<select value={scope.databaseId ?? ""} onChange={event => change("databaseId", event.target.value)}><option value="">All databases</option><option value={5}>Database 5</option><option value={6}>Database 6</option></select></label>
      <label>Time display<select value={mode} onChange={event => setMode(event.target.value === "utc" ? "utc" : "local")}><option value="utc">UTC</option><option value="local">Local</option></select></label>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Advance workspace poll</button>
      <button type="button" onClick={() => change("to", "2026-10-02T11:00:00.000Z")}>Change shared window</button>
      <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Unmount resources" : "Mount resources"}</button>
    </div>
    {mounted && <TargetResourcesPanel instanceId={scope.target || alpha} displayName={scope.target === alpha ? "Alpha" : "Beta"}
      scope={scope} targetRevision={revision} refresh={refresh} canReadRules={false} onClose={() => setMounted(false)} />}
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
