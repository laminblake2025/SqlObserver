import { useLayoutEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { ServerWaitTrendPanel } from "../src/features/health/ServerWaitTrendPanel";
import { DatabaseOptionChanges } from "../src/features/health/DatabaseOptionChanges";
import { overviewHref } from "../src/features/overview/overviewModel";
import type { OverviewScope } from "../src/features/overview/overviewTypes";
import "../src/styles.css";
import "./harness.css";

const alpha = "11111111-1111-4111-8111-000000000001";
const beta = "11111111-1111-4111-8111-000000000002";
const presets = {
  first: { fromUtc: "2026-01-15T12:00:00.000000Z", toUtc: "2026-01-15T13:00:00.000000Z" },
  second: { fromUtc: "2026-01-15T12:30:00.123456Z", toUtc: "2026-01-15T13:30:00.123456Z" },
  waitLimit: { fromUtc: "2026-01-14T13:00:00.000000Z", toUtc: "2026-01-15T13:00:00.000001Z" },
  allLimit: { fromUtc: "2026-01-08T13:00:00.000000Z", toUtc: "2026-01-15T13:00:00.000001Z" },
};
function Harness() {
  const [target, setTarget] = useState(alpha);
  const [bounds, setBounds] = useState<{ fromUtc: string; toUtc: string }>(presets.first);
  const [preset, setPreset] = useState("first");
  const [database, setDatabase] = useState<number>();
  const [refresh, setRefresh] = useState(0);
  const [mounted, setMounted] = useState(true);
  const [crosshair, setCrosshair] = useState<string | null>(null);
  const [selections, setSelections] = useState(0);
  const [commit, setCommit] = useState("");
  const waits = useRef<HTMLDivElement>(null), options = useRef<HTMLDivElement>(null);
  const scope: OverviewScope = { target, range: "custom", from: bounds.fromUtc, to: bounds.toUtc, compare: false, databaseId: database };
  useLayoutEffect(() => {
    setCommit(JSON.stringify({ target, ...bounds, waitChart: Boolean(waits.current?.querySelector("svg.overview-chart")),
      optionRows: options.current?.querySelectorAll("li").length ?? 0,
      waitError: Boolean(waits.current?.querySelector('[role="alert"]')), optionError: Boolean(options.current?.querySelector('[role="alert"]')) }));
  }, [target, bounds.fromUtc, bounds.toUtc, mounted]);
  const markerUtc = new Date(Date.parse(bounds.fromUtc) + 15 * 60_000).toISOString();
  return <main className="component-harness">
    <h1>Context diagnostics harness</h1>
    <div className="harness-controls">
      <label>Selected server<select value={target} onChange={event => { setTarget(event.target.value); setCrosshair(null); }}>
        <option value={alpha}>Alpha</option><option value={beta}>Beta</option>
      </select></label>
      <label>Selected diagnostic window<select value={preset} onChange={event => { setPreset(event.target.value);
        setBounds(presets[event.target.value as keyof typeof presets]); setCrosshair(null); }}>
        <option value="first">Initial window</option><option value="second">Changed precise window</option>
        {preset === "selected" && <option value="selected">Selected chart window</option>}
        <option value="waitLimit">24 hours plus one microsecond</option><option value="allLimit">Seven days plus one microsecond</option>
      </select></label>
      <label>Database context<select value={database ?? "all"} onChange={event => setDatabase(event.target.value === "all" ? undefined : Number(event.target.value))}>
        <option value="all">All databases</option><option value="7">Database 7</option><option value="8">Database 8</option>
      </select></label>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh diagnostics</button>
      <button type="button" onClick={() => setCrosshair(markerUtc)}>Align charts to recorded marker</button>
      <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Unmount diagnostics" : "Mount diagnostics"}</button>
    </div>
    <div className="harness-observations">
      <output aria-label="First committed diagnostic context">{commit}</output>
      <p>Shared crosshair: <output aria-label="Shared crosshair UTC">{crosshair ?? "None"}</output></p>
      <p>Window selections: <output aria-label="Diagnostic window selections">{selections}</output></p>
      <a href={overviewHref(scope, "waits", target)}>Open selected wait history</a>
    </div>
    {mounted ? <>
      <h2>{target === alpha ? "Alpha" : "Beta"}</h2>
      <h3>Investigate {target === alpha ? "Alpha" : "Beta"}</h3>
      <div ref={waits}><ServerWaitTrendPanel instanceId={target} {...bounds} refresh={refresh}
        markers={[{ timeUtc: markerUtc, label: "SYNTHETIC recorded marker" }]} crosshairUtc={crosshair} onCrosshairChange={setCrosshair}
        onSelectWindow={next => { setBounds(next); setPreset("selected"); setCrosshair(null); setSelections(value => value + 1); }} /></div>
      <div ref={options}><DatabaseOptionChanges instanceId={target} scope={scope} refresh={refresh} /></div>
    </> : <p>Diagnostics unmounted</p>}
  </main>;
}

createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
