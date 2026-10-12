import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider, useTimeDisplay } from "../src/TimeDisplayContext";
import { TargetQueryPerformancePanel } from "../src/features/queries/TargetQueryPerformancePanel";
import { readRoute } from "../src/dashboardModel";
import { readOverviewScope, overviewHref } from "../src/features/overview/overviewModel";
import { readQueryViewSelection, queryViewHref } from "../src/features/queries/queryViewModel";
import { queryJumpHref } from "../src/components/commandPaletteModel";
import "../src/styles.css";
import "./harness.css";

const alpha = "11111111-1111-4111-8111-000000000001";
const beta = "11111111-1111-4111-8111-000000000002";
function Harness() {
  const [hash, setHash] = useState(location.hash);
  const [refresh, setRefresh] = useState(0);
  const [canReadText, setReadText] = useState(true);
  const [mounted, setMounted] = useState(true);
  const { mode, setMode } = useTimeDisplay();
  useEffect(() => { const changed = () => setHash(location.hash); window.addEventListener("hashchange", changed); return () => window.removeEventListener("hashchange", changed); }, []);
  const scope = readOverviewScope(hash), route = readRoute(hash), viewSelection = readQueryViewSelection(hash);
  const projectionContext = JSON.stringify([scope.target, scope.from, scope.to, refresh]);
  const previousContext = useRef(projectionContext);
  const [commitProjection, setCommitProjection] = useState<object>();
  // Observe the real committed DOM before passive request-cleanup effects. Keep
  // only evidence-presence flags, never captured SQL or provider strings.
  useLayoutEffect(() => {
    if (previousContext.current === projectionContext) return;
    previousContext.current = projectionContext;
    const section = document.querySelector('.query-performance-screen');
    setCommitProjection({ context: projectionContext,
      rankingRows: section?.querySelectorAll('[aria-label="Ranked query observations"] tbody > tr').length ?? 0,
      historyVisible: Array.from(section?.querySelectorAll('summary') ?? []).some(element => element.textContent === 'Bounded history observations'),
      selectedQueryVisible: Array.from(section?.querySelectorAll('.fingerprint') ?? []).some(element => element.textContent === 'a'.repeat(64)),
      planVisible: section?.textContent?.includes(`Plan: ${'d'.repeat(64)}`) ?? false,
      statusReady: section?.textContent?.includes('query_store evidence · complete') ?? false,
      waitsReady: section?.textContent?.includes('No comparable Query Store wait groups') ?? false,
    });
  }, [projectionContext]);
  const replaceParams = (values: Record<string, string | undefined>) => {
    const params = new URLSearchParams(hash.split("?")[1]);
    for (const [key, value] of Object.entries(values)) { if (value === undefined) params.delete(key); else params.set(key, value); }
    location.hash = `#/queries?${params}`;
  };
  return <main className="component-harness">
    <h1>Query inspection harness</h1>
    <div className="harness-controls">
      <label>Selected server<select value={scope.target} onChange={event => replaceParams({ target: event.target.value })}><option value={alpha}>Alpha</option><option value={beta}>Beta</option></select></label>
      <label>Protected content access<select value={canReadText ? "reader" : "viewer"} onChange={event => setReadText(event.target.value === "reader")}><option value="reader">QueryTextReader</option><option value="viewer">Viewer</option></select></label>
      <label>Time display<select value={mode} onChange={event => setMode(event.target.value === "utc" ? "utc" : "local")}><option value="utc">UTC</option><option value="local">Local</option></select></label>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh query workspace</button>
      <button type="button" onClick={() => replaceParams({ from: "2026-10-02T09:00:00.123456Z", to: "2026-10-02T10:00:00.123456Z" })}>Change bounded window</button>
      <button type="button" onClick={() => replaceParams({ source: "invalid" })}>Invalidate exact selection</button>
      <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Unmount query panel" : "Mount query panel"}</button>
    </div>
    {commitProjection ? <output aria-label="Context commit evidence projection">{JSON.stringify(commitProjection)}</output> : null}
    {mounted ? <TargetQueryPerformancePanel instanceId={scope.target} displayName={scope.target === beta ? "Beta" : "Alpha"}
      selectedDatabaseId={scope.databaseId} onSelectDatabase={databaseId => { location.hash = queryViewHref(overviewHref({ ...scope, databaseId }, "queries"), viewSelection); }}
      timeWindow={{ fromUtc: scope.from!, toUtc: scope.to! }} refresh={refresh} canReadText={canReadText} selectionInvalid={route.querySelectionInvalid === true}
      initialQuerySelection={route.databaseId !== undefined && route.queryFingerprint ? { databaseId: route.databaseId, queryFingerprint: route.queryFingerprint, planFingerprint: route.planFingerprint, sourceKind: route.querySource } : undefined}
      viewSelection={viewSelection} onViewSelectionChange={selection => { location.hash = queryViewHref(hash, selection); }}
      onSelectQuery={(databaseId, query, plan, source) => { location.hash = queryViewHref(queryJumpHref(scope, scope.target, databaseId, query, plan, source), viewSelection); }}
      onSelectWindow={window => replaceParams({ range: "custom", from: window.fromUtc, to: window.toUtc })} onClose={() => setMounted(false)} /> : <p>Query panel unmounted</p>}
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
