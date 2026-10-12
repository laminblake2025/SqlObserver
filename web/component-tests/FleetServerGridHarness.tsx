import { useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { FleetServerGrid } from "../src/features/overview/FleetServerGrid";
import type { OverviewEvidence, OverviewScope, OverviewSnapshot, OverviewTarget } from "../src/features/overview/overviewTypes";
import "../src/styles.css";
import "./harness.css";

const alpha = "11111111-1111-4111-8111-000000000001";
const beta = "11111111-1111-4111-8111-000000000002";
const longName = `SYNTHETIC-${"LongServerName".repeat(30)}`;
const initial: readonly OverviewTarget[] = [
  { targetId: alpha, displayName: "SYNTHETIC Alpha", lifecycle: "active", environment: "production", tags: ["critical", "east"], groupingRevision: 1, canManageGrouping: true },
  { targetId: beta, displayName: "SYNTHETIC Beta", lifecycle: "active", environment: "test", tags: ["west"], groupingRevision: 4, canManageGrouping: true },
];
const scope: OverviewScope = { target: "all", range: "custom", from: "2026-10-02T11:00:00.123456Z", to: "2026-10-02T12:00:00.123456Z", compare: false };
function evidence(target: OverviewTarget): OverviewEvidence {
  const unavailable = { value: null, state: "unavailable", observedAtUtc: null };
  return { targetId: target.targetId, displayName: target.displayName, collectionState: "unavailable", lastObservedUtc: null,
    activeAlerts: unavailable, blockedSessions: unavailable, deadlocks: unavailable, issues: [], resources: [], series: [],
    gaps: ["Synthetic target has no collected SQL evidence"], backupSummary: null };
}

function Harness() {
  const [targets, setTargets] = useState(initial);
  const [search, setSearch] = useState("");
  const [mounted, setMounted] = useState(true);
  const [callbacks, setCallbacks] = useState(0);
  const [generation, setGeneration] = useState(0);
  const refresh = () => { setTargets(values => values.map(value => ({ ...value, tags: [...value.tags] }))); setGeneration(value => value + 1); };
  const alphaAuthority = targets.find(value => value.targetId === alpha)!.canManageGrouping;
  const snapshot = useMemo<OverviewSnapshot>(() => ({ refreshedAtUtc: "2026-10-02T12:00:00.123456Z", fromUtc: scope.from!, toUtc: scope.to!,
    targetId: null, targets, excludedTargets: 0, evidence: targets.map(evidence) }), [targets, generation]);
  return <main className="component-harness">
    <h1>Fleet server grid harness</h1>
    <div className="harness-controls">
      <button type="button" onClick={refresh}>Refresh same revision</button>
      <button type="button" onClick={() => setTargets(values => values.map(value => value.targetId === alpha
        ? { ...value, groupingRevision: 2, environment: "remote-east", tags: ["remote"] } : value))}>Publish Alpha revision 2</button>
      <button type="button" onClick={() => setTargets(values => values.map(value => value.targetId === alpha
        ? { ...value, canManageGrouping: !value.canManageGrouping } : value))}>{alphaAuthority ? "Remove Alpha authority" : "Restore Alpha authority"}</button>
      <button type="button" onClick={() => setTargets(values => values.map(value => value.targetId === alpha
        ? { ...value, displayName: longName, environment: "e".repeat(64), tags: ["t".repeat(64), "x".repeat(64)] } : value))}>Use long native labels</button>
      <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Unmount server grid" : "Mount server grid"}</button>
    </div>
    <output aria-label="Grouping refresh callbacks">{callbacks}</output>
    <output aria-label="Overview fixture generation">{generation}</output>
    {mounted ? <FleetServerGrid snapshot={snapshot} scope={scope} search={search} onSearch={setSearch} formatTime={value => value}
      onGroupingUpdated={() => { setCallbacks(value => value + 1); refresh(); }} /> : <p>Server grid unmounted</p>}
  </main>;
}

createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
