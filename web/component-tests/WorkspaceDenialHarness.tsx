import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { CommandPalette } from "../src/components/CommandPalette";
import { DatabaseScopeControls } from "../src/components/DatabaseScopeControls";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import type { OverviewScope } from "../src/features/overview/overviewTypes";
import "../src/styles.css";
import "./harness.css";

const alpha = "11111111-1111-4111-8111-000000000001", beta = "11111111-1111-4111-8111-000000000002";
function Harness() {
  const inventory = new URLSearchParams(location.search).get("inventory") !== "0";
  const [scope, setScope] = useState<OverviewScope>({ target: alpha, databaseId: 5, range: "custom", compare: false,
    from: "2026-10-02T10:00:00.123456Z", to: "2026-10-02T11:00:00.123456Z" });
  const [refresh, setRefresh] = useState(0), [open, setOpen] = useState(false), [mounted, setMounted] = useState(true);
  const [commit, setCommit] = useState<object>();
  const identity = JSON.stringify([scope.target, scope.databaseId, refresh, open, mounted]);
  const previous = useRef(identity);
  useLayoutEffect(() => {
    if (previous.current === identity) return; previous.current = identity;
    setCommit({ target: scope.target, options: [...document.querySelectorAll('.database-scope-controls option')].map(item => item.textContent),
      results: [...document.querySelectorAll('.command-palette-result')].map(item => item.textContent),
      status: [...document.querySelectorAll('[role="alert"], .command-palette-status')].map(item => item.textContent) });
  }, [identity]);
  useEffect(() => {
    const keys = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") { event.preventDefault(); setOpen(true); }
      else if (event.key === "F2") { event.preventDefault(); setScope(value => ({ ...value, target: value.target === alpha ? beta : alpha })); }
      else if (event.key === "F6") { event.preventDefault(); setRefresh(value => value + 1); }
      else if (event.key === "F8") { event.preventDefault(); setMounted(value => !value); }
    };
    window.addEventListener("keydown", keys); return () => window.removeEventListener("keydown", keys);
  }, []);
  return <main className="component-harness">
    <h1>Workspace denial harness</h1>
    <p>Native synthetic receipts. F2 changes server, F6 refreshes inventory, and F8 changes the mounted context.</p>
    <button type="button" onClick={() => setOpen(true)}>Open evidence search</button>
    <button type="button" onClick={() => setRefresh(value => value + 1)}>Refresh inventory</button>
    <label>Selected server<select value={scope.target} onChange={event => setScope(value => ({ ...value, target: event.target.value }))}>
      <option value={alpha}>Alpha</option><option value={beta}>Beta</option></select></label>
    {mounted && inventory && <DatabaseScopeControls targetId={scope.target} databaseId={scope.databaseId} refresh={refresh}
      onChange={databaseId => setScope(value => ({ ...value, databaseId }))} />}
    {mounted && <CommandPalette open={open} scope={scope} onClose={() => setOpen(false)} />}
    {commit && <output aria-label="First committed workspace context">{JSON.stringify(commit)}</output>}
  </main>;
}
createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
