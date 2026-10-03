import { useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { TargetActivityPanel } from "../src/features/activity/TargetActivityPanel";
import "../src/styles.css";
import "./harness.css";

function Harness() {
  const [refresh, setRefresh] = useState(0);
  const [linked, setLinked] = useState(false);
  const target = "11111111-1111-4111-8111-111111111111";
  return <main className="component-harness"><h1>Live session access harness</h1>
    <button type="button" onClick={() => setRefresh(value => value + 1)}>Advance workspace poll</button>
    <button type="button" onClick={() => setLinked(true)}>Open missing deadlock</button>
    <TargetActivityPanel instanceId={target} displayName="SYNTHETIC Alpha" scope={{ target, range: "1h", compare: false }}
      refresh={refresh} manualRefresh={refresh} sessionTick={refresh} livePaused={false} targetRevision={1}
      initialHistoryAtUtc={linked ? "2026-09-30T13:30:00Z" : undefined}
      initialHistoryEventId={linked ? "66666666-6666-4666-8666-666666666666" : undefined}
      canReadText onSelectDatabase={() => undefined} onClose={() => undefined} />
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
