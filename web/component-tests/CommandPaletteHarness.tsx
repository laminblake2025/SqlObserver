import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { CommandPalette } from "../src/components/CommandPalette";
import { TimeDisplayProvider } from "../src/TimeDisplayContext";
import type { OverviewScope } from "../src/features/overview/overviewTypes";
import "../src/styles.css";
import "./harness.css";

const alpha = "11111111-1111-4111-8111-000000000001";
const beta = "11111111-1111-4111-8111-000000000002";
const initial: OverviewScope = { target: alpha, range: "custom", from: "2026-10-02T10:00:00.123456Z",
  to: "2026-10-02T11:00:00.123456Z", databaseId: 5, compare: true };

function Harness() {
  const [scope, setScope] = useState(initial);
  const [open, setOpen] = useState(false);
  const [mounted, setMounted] = useState(true);
  const [hash, setHash] = useState(location.hash);
  const [commit, setCommit] = useState<object>();
  const identity = JSON.stringify([scope.target, scope.databaseId, scope.from, scope.to, open, mounted]);
  const previous = useRef(identity);
  // Keep the first committed projection before passive request cancellation.
  useLayoutEffect(() => {
    if (previous.current === identity) return;
    previous.current = identity;
    const dialog = document.querySelector('dialog');
    setCommit({ identity, results: Array.from(dialog?.querySelectorAll('.command-palette-result') ?? [], item => item.textContent),
      status: Array.from(dialog?.querySelectorAll('[role="status"]') ?? [], item => item.textContent) });
  }, [identity]);
  useEffect(() => {
    const changed = () => setHash(location.hash);
    const keys = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") {
        event.preventDefault(); if (!event.repeat) setOpen(true);
      } else if (event.key === "F2") {
        event.preventDefault(); setScope(value => ({ ...value, target: value.target === alpha ? beta : alpha }));
      } else if (event.key === "F3") {
        event.preventDefault(); setScope(value => ({ ...value, target: "" }));
      } else if (event.key === "F4") {
        event.preventDefault(); setScope(value => ({ ...value, databaseId: value.databaseId === 5 ? 6 : 5 }));
      } else if (event.key === "F6") {
        event.preventDefault(); setScope(value => ({ ...value, from: event.shiftKey ? "2026-09-01T09:00:00.123456Z" : "2026-10-02T09:00:00.123456Z",
          to: "2026-10-02T10:00:00.123456Z" }));
      } else if (event.key === "F8") {
        event.preventDefault(); setMounted(value => !value);
      }
    };
    window.addEventListener("hashchange", changed); window.addEventListener("keydown", keys);
    return () => { window.removeEventListener("hashchange", changed); window.removeEventListener("keydown", keys); };
  }, []);
  return <main className="component-harness">
    <h1>Context search harness</h1>
    <p>Native synthetic palette. F2 changes server; F3 clears server; F4 changes database; F6 changes the shared window;
      Shift+F6 selects an unsupported window; F8 unmounts or mounts search.</p>
    <button type="button" onClick={() => setOpen(true)} aria-keyshortcuts="Control+K Meta+K">Open evidence search</button>
    <output className="harness-observations" aria-label="Workspace scope">{JSON.stringify(scope)}</output>
    <output className="harness-observations" aria-label="Search navigation">{hash}</output>
    {commit && <output className="harness-observations" aria-label="Search context commit">{JSON.stringify(commit)}</output>}
    {mounted ? <CommandPalette open={open} scope={scope} onClose={() => setOpen(false)} /> : <p>Search unmounted</p>}
  </main>;
}

createRoot(document.getElementById("root")!).render(<TimeDisplayProvider><Harness /></TimeDisplayProvider>);
