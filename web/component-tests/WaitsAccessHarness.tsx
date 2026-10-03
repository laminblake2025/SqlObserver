import { StrictMode, useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { flushSync } from "react-dom";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { TargetWaitsPanel } from "../src/features/activity/TargetWaitsPanel";
import { TargetActivityPanel } from "../src/features/activity/TargetActivityPanel";
import { overviewHref, readOverviewScope } from "../src/features/overview/overviewModel";
import "../src/styles.css";
import "./harness.css";
const alpha = "11111111-1111-4111-8111-111111111111", beta = "22222222-2222-4222-8222-222222222222";
function Harness() {
  const [refresh, setRefresh] = useState(0), [revision, setRevision] = useState(1), [mounted, setMounted] = useState(true);
  const [hash, setHash] = useState(location.hash || `#/waits?target=${alpha}&range=1h`);
  const [diagnostic, setDiagnostic] = useState("waits");
  useEffect(() => { if (!location.hash) location.hash = hash;
    const update = () => setHash(location.hash); window.addEventListener("hashchange", update); return () => window.removeEventListener("hashchange", update); }, []);
  const scope = readOverviewScope(hash), target = scope.target || alpha;
  const navigate = (nextTarget: string) => { const next = overviewHref({ ...readOverviewScope(location.hash), target: nextTarget }, diagnostic);
    location.hash = next; setHash(next); };
  return <main className="component-harness"><h1>Waits access harness</h1><div className="harness-controls">
    <button type="button" onClick={() => setRefresh(value => value + 1)}>Advance workspace poll</button>
    <label>Selected server<select value={target} onChange={event => navigate(event.target.value)}><option value={alpha}>Alpha</option><option value={beta}>Beta</option></select></label>
    <button type="button" onClick={() => setRevision(value => value + 1)}>Change target revision</button>
    <button type="button" onClick={() => { flushSync(() => navigate(beta)); flushSync(() => navigate(alpha)); }}>Target ABA</button>
    <button type="button" onClick={() => { const next = overviewHref({ ...scope, range: "custom", from: "2026-09-30T13:00:00.123456Z", to: "2026-09-30T14:00:00.654321Z" }, diagnostic); location.hash = next; setHash(next); }}>Use Rewind window</button>
    <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Remove diagnostic screen" : "Restore diagnostic screen"}</button>
    <label>Diagnostic screen<select value={diagnostic} onChange={event => setDiagnostic(event.target.value)}><option value="waits">Waits</option><option value="activity">Activity</option></select></label>
  </div>{!mounted ? <p>Diagnostic screen removed</p> : diagnostic === "waits" ? <TargetWaitsPanel instanceId={target} displayName={target === alpha ? "Alpha" : "Beta"} scope={scope} refresh={refresh} targetRevision={revision} selectionHash={hash} onClose={() => setMounted(false)} />
    : <TargetActivityPanel instanceId={target} displayName={target === alpha ? "Alpha" : "Beta"} scope={scope} refresh={refresh} manualRefresh={refresh} sessionTick={refresh} livePaused targetRevision={revision} selectionHash={hash} onSelectDatabase={() => undefined} onClose={() => setMounted(false)} />}</main>;
}
createRoot(document.getElementById("root")!).render(<StrictMode><TimeDisplayProvider><Harness /></TimeDisplayProvider></StrictMode>);
