import { useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import { TargetAlertRulesPanel } from "../src/features/alerts/TargetAlertRulesPanel";
import "../src/styles.css";
import "./harness.css";

const alpha = "11111111-1111-4111-8111-000000000001";
const beta = "11111111-1111-4111-8111-000000000002";
const first = "22222222-2222-4222-8222-000000000001";
const second = "22222222-2222-4222-8222-000000000002";

function Harness() {
  const [target, setTarget] = useState(alpha);
  const [selected, setSelected] = useState(first);
  const [canManage, setCanManage] = useState(true);
  const [mounted, setMounted] = useState(true);
  const [refresh, setRefresh] = useState(0);
  const [selections, setSelections] = useState(0);
  return <main className="component-harness">
    <h1>Alert rules panel harness</h1>
    <div className="harness-controls">
      <label>Selected server<select value={target} onChange={event => setTarget(event.target.value)}>
        <option value={alpha}>Alpha</option><option value={beta}>Beta</option>
      </select></label>
      <label>Selected rule<select value={selected} onChange={event => setSelected(event.target.value)}>
        <option value={first}>First rule</option><option value={second}>Second rule</option>
      </select></label>
      <label>Rule authority<select value={canManage ? "administrator" : "reader"} onChange={event => setCanManage(event.target.value === "administrator")}>
        <option value="administrator">Target administrator</option><option value="reader">Reader</option>
      </select></label>
      <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh rules</button>
      <button type="button" onClick={() => setMounted(value => !value)}>{mounted ? "Unmount rules" : "Mount rules"}</button>
    </div>
    <output aria-label="Rule selection callbacks">{selections}</output>
    {mounted ? <TargetAlertRulesPanel instanceId={target} displayName={`SYNTHETIC ${target === alpha ? "Alpha" : "Beta"}`}
      refresh={refresh} canManage={canManage} selectedId={selected}
      onSelect={id => { setSelected(id); setSelections(value => value + 1); }} onClose={() => setMounted(false)} /> : <p>Rules unmounted</p>}
  </main>;
}

createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
