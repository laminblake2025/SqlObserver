import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider, useTimeDisplay } from "../src/TimeDisplayContext";
import { TargetHealthPanel } from "../src/features/health/TargetHealthPanel";
import type { OverviewScope } from "../src/features/overview/overviewTypes";
import { overviewHref, readOverviewScope } from "../src/features/overview/overviewModel";
import { healthSectionHref, readRoute, type HealthSection } from "../src/dashboardModel";
import "../src/styles.css";
import "./harness.css";

const targets = {
  alpha: "11111111-1111-4111-8111-111111111111",
  beta: "22222222-2222-4222-8222-222222222222",
};
// Deliberate UI-role fixture only; this flag does not authorize any server API.
const canManageConfiguration = new URLSearchParams(location.search).get("canManageConfiguration") === "true";
const navigation = new URLSearchParams(location.search).get("navigation") === "true";

function Harness() {
  const { mode, setMode } = useTimeDisplay();
  const [target, setTarget] = useState<keyof typeof targets>("alpha");
  const [refresh, setRefresh] = useState(0);
  const [databaseId, setDatabaseId] = useState<number>();
  const [section, setSection] = useState<"summary" | "databases" | "collection">("collection");
  const [closed, setClosed] = useState(false);
  const [hash, setHash] = useState(location.hash);
  const root = useRef<HTMLElement>(null);
  const [commit, setCommit] = useState<{ hash: string; tab: string | null }>();
  useEffect(() => { const update = () => setHash(location.hash); window.addEventListener("hashchange", update);
    return () => window.removeEventListener("hashchange", update); }, []);
  useLayoutEffect(() => { if (navigation) setCommit({ hash, tab: root.current?.querySelector('[role="tab"][aria-selected="true"]')?.textContent ?? null }); }, [hash]);
  const route = readRoute(hash);
  const scope: OverviewScope = navigation ? readOverviewScope(hash) : {
    target: targets[target], range: "custom", compare: false,
    from: "2026-01-15T12:00:00.000Z", to: "2026-01-15T13:00:00.000Z", databaseId,
  };
  const selectedTarget = navigation ? (scope.target === targets.beta ? "beta" : "alpha") : target;
  const selectedDatabase = navigation ? scope.databaseId : databaseId;
  const navigate = (nextScope: OverviewScope, nextSection?: HealthSection) => {
    location.hash = nextSection ? healthSectionHref(nextScope, nextScope.target, nextSection) : overviewHref(nextScope, "health");
  };
  const selectDatabase = (value: number | undefined) => navigation
    ? navigate({ ...readOverviewScope(location.hash), databaseId: value }, readRoute(location.hash).healthSection) : setDatabaseId(value);
  return <main ref={root} className="component-harness">
    <h1>Health panel harness</h1>
    <div className="harness-controls">
      <label>Selected server<select value={selectedTarget} onChange={event => {
        if (navigation) navigate({ ...readOverviewScope(location.hash), target: targets[event.target.value as keyof typeof targets], databaseId: undefined }, readRoute(location.hash).healthSection);
        else { setTarget(event.target.value as keyof typeof targets); setDatabaseId(undefined); }
      }}><option value="alpha">Alpha</option><option value="beta">Beta</option></select></label>
      <label>Time display<select value={mode} onChange={event => setMode(event.target.value === "utc" ? "utc" : "local")}>
        <option value="utc">UTC</option><option value="local">Local</option>
      </select></label>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh health evidence</button>
      {navigation && <><label>Database context<select value={scope.databaseId ?? ""} onChange={event => selectDatabase(event.target.value ? Number(event.target.value) : undefined)}>
        <option value="">All databases</option><option value="7">Synthetic inventory</option>
      </select></label><button type="button" onClick={() => navigate(readOverviewScope(location.hash))}>Open default health section</button></>}
    </div>
    <output aria-label="Selected database context">{selectedDatabase ?? "None"}</output>
    {navigation && <output hidden aria-label="First committed health navigation">{JSON.stringify(commit)}</output>}
    {closed ? <p>Health panel closed</p> : <TargetHealthPanel
      instanceId={targets[selectedTarget]} displayName={selectedTarget === "alpha" ? "Alpha" : "Beta"}
      onClose={() => setClosed(true)} scope={scope} refresh={refresh} healthRefresh={refresh}
      blockingRefresh={refresh} selectedDatabaseId={selectedDatabase} onSelectDatabase={selectDatabase}
      canManageConfiguration={canManageConfiguration} targetRevision={1} selectionHash=""
      selectedSection={navigation ? route.healthSection : section} onSelectSection={navigation ? next => navigate(readOverviewScope(location.hash), next) : setSection}
    />}
  </main>;
}

createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
