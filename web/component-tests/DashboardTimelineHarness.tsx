import { useState } from "react";
import { createRoot } from "react-dom/client";
import { DashboardTimeline } from "../src/components/DashboardTimeline";
import type { TimelineEvent } from "../src/components/dashboardTimelineModel";
import { TimeDisplayProvider, useTimeDisplay } from "../src/TimeDisplayContext";
import { overviewHref } from "../src/features/overview/overviewModel";
import "../src/styles.css";
import "./harness.css";

const initial = { fromUtc: "2026-01-15T12:00:00.000001Z", toUtc: "2026-01-15T13:00:00.000001Z" };
const target = "11111111-1111-4111-8111-111111111111";
const events: readonly TimelineEvent[] = [
  { id: "deadlock:one", kind: "deadlock", timeUtc: "2026-01-15T12:15:00.000001Z", title: "Recorded deadlock", detail: "Native event occurrence time; no common cause is inferred.", timeBasis: "occurred", href: `#/deadlocks?target=${target}` },
  { id: "agent:one", kind: "agent_failure", timeUtc: "2026-01-15T12:45:00.000001Z", title: "Job failure first observed", detail: "Collection observation time; SQL-local execution time is unavailable.", timeBasis: "first_observed", href: `#/operations?target=${target}&check=agent` },
];
function Harness() {
  const { mode, setMode } = useTimeDisplay();
  const [window, setWindow] = useState(initial);
  const [crosshair, setCrosshair] = useState<string | null>(null);
  const [count, setCount] = useState(0);
  const [mounted, setMounted] = useState(true);
  const selectWindow = (selected: { fromUtc: string; toUtc: string }) => {
    setWindow(selected); setCount(value => value + 1);
    location.hash = overviewHref({ target, databaseId: 7, compare: true, range: "custom", from: selected.fromUtc, to: selected.toUtc }, "health");
  };
  return <main className="component-harness">
    <h1>Dashboard timeline harness</h1>
    <div className="harness-controls">
      <label>Time display<select value={mode} onChange={event => setMode(event.target.value === "utc" ? "utc" : "local")}><option value="utc">UTC</option><option value="local">Local</option></select></label>
      <button type="button" onClick={() => setWindow({ fromUtc: "2026-01-15T12:01:00.000001Z", toUtc: "2026-01-15T13:01:00.000001Z" })}>Advance loaded window</button>
      <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Unmount timeline" : "Mount timeline"}</button>
    </div>
    <section className="harness-observations" aria-label="Shared selection results">
      <p>Selections: <output aria-label="Selection callback count">{count}</output></p>
      <p>From: <output aria-label="Shared from UTC">{window.fromUtc}</output></p>
      <p>To: <output aria-label="Shared to UTC">{window.toUtc}</output></p>
      <p>Alignment: <output aria-label="Shared crosshair UTC">{crosshair ?? "None"}</output></p>
    </section>
    <h2>Server dashboard</h2><h3>Timeline evidence</h3>
    {mounted && <DashboardTimeline events={events} {...window} crosshairUtc={crosshair}
      onSelectTime={setCrosshair} onSelectWindow={selectWindow}
      sourceNotes={["Only these loaded native events are represented. Missing intervals are not normal-health evidence."]} />}
  </main>;
}
const root = document.getElementById("root");
if (!root) throw new Error("Timeline harness root is missing.");
createRoot(root).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
