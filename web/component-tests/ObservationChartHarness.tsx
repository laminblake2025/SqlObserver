import { useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider, useTimeDisplay } from "../src/TimeDisplayContext";
import { ObservationChart } from "../src/components/ObservationChart";
import { OverviewChart } from "../src/features/overview/OverviewChart";
import type { ObservationSeries } from "../src/components/observationChartModel";
import "../src/styles.css";
import "./harness.css";

const initialWindow = { fromUtc: "2026-01-15T12:00:00.000Z", toUtc: "2026-01-15T13:00:00.000Z" };
type Window = typeof initialWindow;
type Preset = "mixed" | "missing" | "single" | "gap" | "large";
const samples = {
  mixed: [
    { time: initialWindow.fromUtc, value: 0 },
    { time: "2026-01-15T12:15:00.000Z", value: null },
    { time: "2026-01-15T12:30:00.000Z", value: 20 },
    { time: initialWindow.toUtc, value: 40 },
  ],
  missing: [
    { time: initialWindow.fromUtc, value: null },
    { time: initialWindow.toUtc, value: null },
  ],
  single: [{ time: "2026-01-15T12:30:00.000Z", value: 7 }],
  gap: [
    { time: initialWindow.fromUtc, value: 0 },
    { time: "2026-01-15T12:10:00.000Z", value: 10 },
    { time: "2026-01-15T12:30:00.000Z", value: null },
    { time: "2026-01-15T12:50:00.000Z", value: 30 },
    { time: initialWindow.toUtc, value: 40 },
  ],
  large: [
    { time: initialWindow.fromUtc, value: 1_000_000_000 },
    { time: "2026-01-15T12:15:00.000Z", value: null },
    { time: "2026-01-15T12:30:00.000Z", value: 1_100_000_000 },
    { time: initialWindow.toUtc, value: 1_200_000_000 },
  ],
} satisfies Record<Preset, ObservationSeries["items"]>;
const companion: readonly ObservationSeries[] = [{ id: "companion", label: "Throughput", items: [
  { time: initialWindow.fromUtc, value: 5 },
  { time: "2026-01-15T12:30:00.000Z", value: 10 },
  { time: initialWindow.toUtc, value: 15 },
] }];
const baselineBand = { lower: 8, upper: 12, label: "Expected range" };
const threshold = { value: 30, label: "Investigation threshold" };
const milliseconds = (value: number) => `${value.toFixed(1)} ms`;

function Harness() {
  const { mode, setMode } = useTimeDisplay();
  const [preset, setPreset] = useState<Preset>("mixed");
  const [references, setReferences] = useState(false);
  const [units, setUnits] = useState(false);
  const [window, setWindow] = useState<Window>(initialWindow);
  const [crosshairUtc, setCrosshairUtc] = useState<string | null>(null);
  const [selectionCount, setSelectionCount] = useState(0);
  const [kind, setKind] = useState('observation');
  const [scrollable, setScrollable] = useState(false);
  const [target, setTarget] = useState('alpha');
  const series: readonly ObservationSeries[] = [
    { id: `${target}:latency`, label: "Latency", items: samples[preset] },
    { id: "unavailable", label: "Uncollected", items: [] },
  ];
  function selectWindow(selected: Window) {
    setWindow(selected);
    setSelectionCount(count => count + 1);
    setCrosshairUtc(null);
  }
  function resetInteraction() {
    setWindow(initialWindow);
    setCrosshairUtc(null);
    setSelectionCount(0);
  }
  const chartProps = {
    ...window, crosshairUtc, onCrosshairChange: setCrosshairUtc, onSelectWindow: selectWindow,
    baselineBand: references ? baselineBand : undefined,
    threshold: references ? threshold : undefined,
    valueFormatter: units ? milliseconds : undefined,
  };
  return <main className="component-harness">
    <h1>Observation chart harness</h1>
    <fieldset className="harness-controls">
      <legend>Chart props and display</legend>
      <label>Evidence preset
        <select value={preset} onChange={event => setPreset(event.target.value as Preset)}>
          <option value="mixed">Zero and missing observations</option>
          <option value="missing">No numeric observations</option>
          <option value="single">One observation</option>
          <option value="gap">Two runs separated by a gap</option>
          <option value="large">Large exact axis values</option>
        </select>
      </label>
      <label>Time display
        <select value={mode} onChange={event => setMode(event.target.value === "utc" ? "utc" : "local")}>
          <option value="local">Local</option><option value="utc">UTC</option>
        </select>
      </label>
      <label className="harness-checkbox"><input type="checkbox" checked={references}
        onChange={event => setReferences(event.target.checked)} />Show reference values</label>
      <label className="harness-checkbox"><input type="checkbox" checked={units}
        onChange={event => setUnits(event.target.checked)} />Format values in milliseconds</label>
      <button type="button" onClick={resetInteraction}>Reset interaction</button>
      <label>Primary chart kind<select value={kind} onChange={event => setKind(event.target.value)}><option value="observation">Observation</option><option value="overview">Overview</option></select></label>
      <label className="harness-checkbox"><input type="checkbox" checked={scrollable} onChange={event => setScrollable(event.target.checked)} />Scrollable resource plot</label>
      <button type="button" onClick={() => setWindow({ fromUtc: '2026-01-15T12:10:00.000Z', toUtc: '2026-01-15T13:10:00.000Z' })}>Advance chart window</button>
      <button type="button" onClick={() => setTarget(value => value === 'alpha' ? 'beta' : 'alpha')}>Replace chart target</button>
    </fieldset>
    <section className="harness-observations" aria-label="Interaction results">
      <p>Shared pointer: <output aria-label="Shared crosshair UTC">{crosshairUtc ?? "None"}</output></p>
      <p>Selected from: <output aria-label="Selected from UTC">{window.fromUtc}</output></p>
      <p>Selected to: <output aria-label="Selected to UTC">{window.toUtc}</output></p>
      <p>Selections: <output aria-label="Selection callback count">{selectionCount}</output></p>
    </section>
    <section className="harness-chart-panel" aria-label="Primary chart">
      <h2>Primary chart</h2>{kind === 'observation' ? <ObservationChart label="Latency evidence" series={series} {...chartProps} />
        : <OverviewChart series={[{ targetId: target, label: 'Latency', metric: 'latency', unit: 'ms', state: 'fresh', dimension: null,
          points: samples[preset].map(item => ({ timeUtc: item.time, value: item.value, samples: 1 })) }]} {...window}
          scrollablePlotLabel={scrollable ? 'Resource evidence plot' : undefined}
          crosshairUtc={crosshairUtc} onCrosshairChange={setCrosshairUtc} onSelectWindow={selectWindow} />}
    </section>
    <section className="harness-chart-panel" aria-label="Companion chart">
      <h2>Companion chart</h2><ObservationChart label="Throughput evidence" series={companion} {...chartProps} />
    </section>
  </main>;
}

const root = document.getElementById("root");
if (!root) throw new Error("Component harness root is missing.");
createRoot(root).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
