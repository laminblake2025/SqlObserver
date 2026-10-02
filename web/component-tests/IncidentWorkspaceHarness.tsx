import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider, useTimeDisplay, useTimeFormatter } from "../src/TimeDisplayContext";
import { IncidentWorkspace } from "../src/features/incidents/IncidentWorkspace";
import { SavedIncidentView } from "../src/features/incidents/SavedIncidentView";
import type { SavedIncident } from "../src/features/incidents/incidentModel";
import { resolveWorkspaceIncidentWindow } from "../src/features/incidents/workspaceIncidentWindow";
import { readOverviewScope } from "../src/features/overview/overviewModel";
import "../src/styles.css";
import "../src/features/incidents/incidentWorkspace.css";
import "./harness.css";

const alpha = "11111111-1111-4111-8111-000000000001";
const beta = "11111111-1111-4111-8111-000000000002";
const alphaThread = "aaaaaaaa-aaaa-4aaa-8aaa-000000000001";
const betaThread = "aaaaaaaa-aaaa-4aaa-8aaa-000000000002";
const defaultHash = `#/health?${new URLSearchParams({ target: alpha, range: "custom", from: "2026-09-29T12:00:00.000001Z",
  to: "2026-09-29T13:00:00.000002Z", databaseId: "5", compare: "1", incident: alphaThread })}`;
const directIncident: SavedIncident = { targetId: alpha, targetRevision: 7, threadId: alphaThread,
  operationId: "bbbbbbbb-bbbb-4bbb-8bbb-000000000001", fromUtc: "2026-09-29T12:00:00.000001Z", toUtc: "2026-09-29T13:00:00.000002Z",
  savedAtUtc: "2026-09-29T14:00:00.000003Z", title: "SYNTHETIC direct saved view", notes: "PRIVATE_DIRECT_SAVED_NOTES",
  evidenceTruncated: false, evidence: [] };
function Harness() {
  const [hash, setHash] = useState(() => location.hash || defaultHash);
  const [canRead, setCanRead] = useState(() => new URLSearchParams(location.search).get("read") !== "denied");
  const [canSave, setCanSave] = useState(false);
  const [mounted, setMounted] = useState(true);
  const [, refreshContext] = useState(0);
  const { mode, setMode } = useTimeDisplay();
  const formatTime = useTimeFormatter();
  useEffect(() => {
    if (!location.hash) location.hash = defaultHash;
    const changed = () => setHash(location.hash);
    window.addEventListener("hashchange", changed);
    return () => window.removeEventListener("hashchange", changed);
  }, []);
  const scope = readOverviewScope(hash);
  const changeSelection = (id: string, selected: boolean) => {
    const query = new URLSearchParams(hash.split("?")[1]); query.set("target", id);
    if (selected) query.set("incident", id === alpha ? alphaThread : betaThread); else query.delete("incident");
    location.hash = `#/health?${query}`;
  };
  return <main className="component-harness">
    <h1>Saved incident workspace harness</h1>
    <div className="harness-controls">
      <label>Server context<select value={scope.target} onChange={event => changeSelection(event.target.value, true)}>
        <option value={alpha}>Alpha</option><option value={beta}>Beta</option>
      </select></label>
      <label>Read access<select value={canRead ? "viewer" : "unverified"} onChange={event => setCanRead(event.target.value === "viewer")}>
        <option value="viewer">Verified Viewer</option><option value="unverified">Unverified access</option>
      </select></label>
      <label>Save access<select value={canSave ? "operator" : "viewer"} onChange={event => setCanSave(event.target.value === "operator")}>
        <option value="viewer">Viewer</option><option value="operator">Operator</option>
      </select></label>
      <label>Time display<select value={mode} onChange={event => setMode(event.target.value === "utc" ? "utc" : "local")}>
        <option value="local">Local</option><option value="utc">UTC</option>
      </select></label>
      <button type="button" onClick={() => changeSelection(scope.target, false)}>Browse investigations</button>
      <button type="button" onClick={() => refreshContext(value => value + 1)}>Refresh workspace context</button>
      <button type="button" onClick={() => {
        const query = new URLSearchParams(hash.split("?")[1]); query.set("range", "custom");
        query.set("from", "2026-09-29T11:00:00.111111Z"); query.set("to", "2026-09-29T12:00:00.222222Z");
        location.hash = `#/health?${query}`;
      }}>Shift shared window</button>
      <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Unmount incident workspace" : "Mount incident workspace"}</button>
    </div>
    {mounted ? new URLSearchParams(location.search).get("mode") === "view"
      ? <SavedIncidentView incident={directIncident} canRead={canRead} href={defaultHash} formatTime={formatTime} compact
        onClose={() => setMounted(false)} />
      : <IncidentWorkspace targetId={scope.target} targetRevision={scope.target === alpha ? 7 : 8}
      window={resolveWorkspaceIncidentWindow(hash, scope.target, Date.now()).window} canSave={canSave} canRead={canRead}
      scope={scope} selectionHash={hash} formatTime={formatTime} compact /> : <p>Incident workspace unmounted</p>}
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
