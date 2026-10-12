import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { TimeDisplayProvider } from "../src/TimeDisplayContext.tsx";
import { readOverviewScope, overviewWindow } from "../src/features/overview/overviewModel.ts";
import { agentActivityWindow, type AgentActivityBinding, type AgentActivitySelection } from "../src/features/operations/agentActivityHistoryModel.ts";
import { readAgentActivitySelection, retainedAgentActivitySelectionHref, clearAgentActivitySelectionHref } from "../src/features/operations/agentActivitySelectionModel.ts";
import { useAgentActivityHistory } from "../src/features/operations/useAgentActivityHistory.ts";
import { AgentActivityHistoryPanel } from "../src/features/operations/AgentActivityHistoryPanel.tsx";
import "../src/styles.css";
import "./harness.css";
const alpha = "11111111-1111-4111-8111-000000000001", beta = "11111111-1111-4111-8111-000000000002";
function Content({ binding, selection, refresh, access, onSelect, retain }: { binding?: AgentActivityBinding; selection?: AgentActivitySelection | null; refresh: number; access: string; onSelect: (value?: AgentActivitySelection) => void; retain: () => void }) {
  const history = useAgentActivityHistory(binding, selection, refresh, access);
  return <AgentActivityHistoryPanel history={history} selected={selection} onSelectReceipt={onSelect} onRetainWindow={retain}/>;
}
function Harness() {
  const [hash, setHash] = useState(location.hash), [revision, setRevision] = useState(1), [access, setAccess] = useState("grant-a"), [refresh, setRefresh] = useState(0), [mounted, setMounted] = useState(true);
  useEffect(() => { const update = () => setHash(location.hash); window.addEventListener("hashchange", update); return () => window.removeEventListener("hashchange", update); }, []);
  const scope = readOverviewScope(hash), selected = readAgentActivitySelection(hash, scope.target);
  let binding: AgentActivityBinding | undefined, error: string | undefined;
  try { binding = { targetId: scope.target, targetRevision: revision, ...agentActivityWindow(overviewWindow(scope, Date.now()).fromUtc, overviewWindow(scope, Date.now()).toUtc) }; }
  catch (failure) { error = failure instanceof Error ? failure.message : "Invalid completion window."; }
  function change(value: string) { location.hash = value; setHash(location.hash); }
  function query(key: string, value: string) { const params = new URLSearchParams(hash.split("?")[1]); params.set(key, value); change("#/operations?" + params); }
  function select(value?: AgentActivitySelection) { if (!binding) return; change(value ? retainedAgentActivitySelectionHref(scope, binding, value) : clearAgentActivitySelectionHref(hash)); }
  function retain() { if (binding && scope.range !== "custom") { const params = new URLSearchParams(hash.split("?")[1]); params.set("range", "custom"); params.set("from", binding.fromUtc); params.set("to", binding.toUtc); change("#/operations?" + params); } }
  return <main className="component-harness"><h1>Agent activity history harness</h1><div className="harness-controls">
    <label>Selected server<select value={scope.target} onChange={event => query("target", event.target.value)}><option value={alpha}>Alpha</option><option value={beta}>Beta</option></select></label>
    <label>Target revision<select value={revision} onChange={event => setRevision(Number(event.target.value))}><option value={1}>1</option><option value={2}>2</option></select></label>
    <label>Authority context<select value={access} onChange={event => setAccess(event.target.value)}><option value="grant-a">Original grant</option><option value="grant-b">Changed grant</option></select></label>
    <button type="button" onClick={() => setRefresh(refresh + 1)}>Refresh workspace</button>
    <button type="button" onClick={() => query("to", "2026-10-02T11:00:00.123457Z")}>Change shared window</button>
    <button type="button" onClick={() => query("to", "2026-10-02T11:00:00.123456Z")}>Restore shared window</button>
    <button type="button" onClick={() => query("from", "2026-08-01T00:00:00Z")}>Use invalid long window</button>
    <button type="button" onClick={() => setMounted(!mounted)}>{mounted ? "Unmount Agent activity" : "Mount Agent activity"}</button>
  </div><h2>Operations</h2>{error && <p role="alert">{error}</p>}{mounted && <Content binding={binding} selection={selected} refresh={refresh} access={access} onSelect={select} retain={retain}/>}</main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness/></TimeDisplayProvider>);
