import { useState } from "react";
import { createRoot } from "react-dom/client";
import { LifecycleCollectionControl } from "../src/features/health/LifecycleCollectionControl.tsx";
import "../src/styles.css";
import "./harness.css";
function Harness() {
  const [target, setTarget] = useState("11111111-1111-4111-8111-000000000001"), [revision, setRevision] = useState(1), [authority, setAuthority] = useState("admin-a"), [mounted, setMounted] = useState(true);
  return <main className="component-harness" style={{maxWidth:1200}}><h1>Lifecycle collection control</h1><fieldset className="harness-controls"><legend>Test context</legend>
    <label>Selected server<select value={target} onChange={e=>setTarget(e.target.value)}><option value="11111111-1111-4111-8111-000000000001">Alpha</option><option value="11111111-1111-4111-8111-000000000002">Beta</option></select></label>
    <label>Target revision<select value={revision} onChange={e=>setRevision(Number(e.target.value))}><option value={1}>1</option><option value={2}>2</option></select></label>
    <label>Authority context<select value={authority} onChange={e=>setAuthority(e.target.value)}><option value="admin-a">Original administrator</option><option value="admin-b">Changed administrator</option><option value="viewer">Viewer</option></select></label>
    <button type="button" onClick={()=>setMounted(!mounted)}>{mounted?"Unmount control":"Mount control"}</button>
  </fieldset><h2>Server dashboard</h2><h3>Collection settings</h3>{mounted&&<LifecycleCollectionControl targetId={target} targetRevision={revision} canManage={authority.startsWith("admin-")} accessContext={authority}/>}</main>;
}
createRoot(document.getElementById("root")!).render(<Harness/>);
