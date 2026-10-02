import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { TargetDeadlockPanel } from "../src/features/deadlocks/TargetDeadlockPanel";
import { deadlockSelectionHref, readDeadlockSelection } from "../src/features/deadlocks/deadlockSelectionModel";
import { readOverviewScope } from "../src/features/overview/overviewModel";
import "../src/styles.css";
import "./harness.css";

const targets = [
  { id: "11111111-1111-4111-8111-111111111111", name: "Production SQL" },
  { id: "44444444-4444-4444-8444-444444444444", name: "Reporting SQL" },
] as const;

function Harness() {
  const [hash, setHash] = useState(location.hash);
  const [refresh, setRefresh] = useState(0);
  useEffect(() => {
    const update = () => setHash(location.hash);
    window.addEventListener("hashchange", update);
    return () => window.removeEventListener("hashchange", update);
  }, []);
  const scope = readOverviewScope(hash);
  const selection = readDeadlockSelection(hash, scope.target);
  const displayName = new URLSearchParams(location.search).get("displayName") ??
    targets.find(target => target.id === scope.target)?.name ?? "Unknown server";
  return <main style={{ padding: "20px" }}>
    <h1>Deadlock panel harness</h1>
    <fieldset className="harness-controls"><legend>Panel context</legend>
      <label>Server context<select value={scope.target} onChange={event => {
        location.hash = deadlockSelectionHref({ ...scope, target: event.target.value });
      }}>{targets.map(target => <option key={target.id} value={target.id}>{target.name}</option>)}</select></label>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh context</button>
    </fieldset>
    <TargetDeadlockPanel instanceId={scope.target} displayName={displayName} scope={scope} refresh={refresh}
      onClose={() => undefined} selectedEventId={selection === null ? null : selection?.eventId}
      selectedOccurredAtUtc={selection?.occurredAtUtc}
      onSelectEvent={next => { location.hash = deadlockSelectionHref(scope, next); }} />
  </main>;
}

const root = document.getElementById("root");
if (!root) throw new Error("Deadlock harness root is missing.");
createRoot(root).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
