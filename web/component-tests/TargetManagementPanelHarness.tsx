import { useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { TargetManagementPanel } from "../src/features/targets/TargetManagementPanel";
import type { MyAccess } from "../src/features/targets/meApi";
import type { ObservationTargetSummary } from "../src/features/targets/targetTypes";
import "../src/styles.css";
import "./harness.css";

const alpha = "11111111-1111-4111-8111-000000000001";
const beta = "11111111-1111-4111-8111-000000000002";
function Harness() {
  const [target, setTarget] = useState(alpha);
  const [role, setRole] = useState("administrator");
  const [, refreshAccess] = useState(0);
  const [mounted, setMounted] = useState(true);
  const [changed, setChanged] = useState<readonly ObservationTargetSummary[]>([]);
  const [closed, setClosed] = useState(0);
  const access: MyAccess | undefined = role === "unverified" ? undefined : role === "inactive"
    ? { active: false, grantedRoles: [], allTargetRoles: [], targetId: target, targetRoles: [] }
    : { active: true, grantedRoles: [role === "reader" ? "Viewer" : "TargetAdministrator"], allTargetRoles: [],
      targetId: role === "mixed" ? target === alpha ? beta : alpha : target,
      targetRoles: [role === "reader" ? "Viewer" : "TargetAdministrator"] };
  return <main className="component-harness">
    <h1>Server settings panel harness</h1>
    <div className="harness-controls">
      <label>Selected server<select value={target} onChange={event => setTarget(event.target.value)}>
        <option value={alpha}>Alpha</option><option value={beta}>Beta</option>
      </select></label>
      <label>Access scenario<select value={role} onChange={event => setRole(event.target.value)}>
        <option value="administrator">Target administrator</option><option value="reader">Reader</option>
        <option value="mixed">Other server administrator</option><option value="unverified">Access pending</option>
        <option value="inactive">Inactive identity</option>
      </select></label>
      <button type="button" onClick={() => refreshAccess(value => value + 1)}>Refresh access snapshot</button>
      <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Unmount settings" : "Mount settings"}</button>
    </div>
    <div className="harness-observations">
      <output aria-label="Settings callbacks">{changed.length}</output>{" "}
      <output aria-label="Last changed target">{changed.at(-1)?.instanceId ?? "None"}</output>{" "}
      <output aria-label="Changed revision">{changed.at(-1)?.configurationRevision ?? "None"}</output>{" "}
      <output aria-label="Close callbacks">{closed}</output>
    </div>
    {mounted ? <TargetManagementPanel instanceId={target}
      scope={{ target, range: "custom", from: "2026-09-26T08:00:00.123456Z", to: "2026-09-26T12:00:00.654321Z", compare: false }}
      access={access} onChanged={value => setChanged(previous => [...previous, value])}
      onClose={() => { setClosed(value => value + 1); setMounted(false); }} /> : <p>Settings unmounted</p>}
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
