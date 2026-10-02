import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { alertSelectionFromHash } from "../src/components/commandPaletteModel";
import { TargetAlertsPanel } from "../src/features/alerts/TargetAlertsPanel";
import "../src/styles.css";
import "./harness.css";

const targets = [
  { id: "11111111-1111-4111-8111-111111111111", name: "Production SQL" },
  { id: "44444444-4444-4444-8444-444444444444", name: "Reporting SQL" },
] as const;

function Harness() {
  const [targetId, setTargetId] = useState<string>(() => alertSelectionFromHash(location.hash)?.targetId ?? targets[0].id);
  const [hash, setHash] = useState(location.hash);
  const [canAcknowledge, setCanAcknowledge] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const [closed, setClosed] = useState(false);
  useEffect(() => {
    const updateHash = () => setHash(location.hash);
    window.addEventListener("hashchange", updateHash);
    return () => window.removeEventListener("hashchange", updateHash);
  }, []);
  const selection = alertSelectionFromHash(hash);
  const displayName = targets.find(target => target.id === targetId)?.name ?? "Unknown server";
  return <main style={{ padding: "20px" }}>
    <h1>Alert panel harness</h1>
    <fieldset className="harness-controls">
      <legend>Panel context</legend>
      <label>Server context<select value={targetId} onChange={event => {
        setTargetId(event.target.value);
        location.hash = `#/alerts?target=${event.target.value}`;
      }}>{targets.map(target => <option key={target.id} value={target.id}>{target.name}</option>)}</select></label>
      <label className="harness-checkbox"><input type="checkbox" checked={canAcknowledge}
        onChange={event => setCanAcknowledge(event.target.checked)} />Operator permission</label>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh context</button>
    </fieldset>
    {closed ? <p role="status">Panel closed</p> : <TargetAlertsPanel instanceId={targetId} displayName={displayName}
      onClose={() => setClosed(true)} canAcknowledge={canAcknowledge} canManageRules={false}
      canManageDestinations={false} refresh={refresh}
      selectedAlertId={selection?.targetId === targetId ? selection.alertId : undefined} />}
  </main>;
}

const root = document.getElementById("root");
if (!root) throw new Error("Alert harness root is missing.");
createRoot(root).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
