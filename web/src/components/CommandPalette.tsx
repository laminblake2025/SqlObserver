import { useEffect, useMemo, useRef, useState } from "react";
import { listObservationTargets } from "../features/targets/targetApi";
import type { ObservationTargetSummary } from "../features/targets/targetTypes";
import { getFleetActiveAlerts } from "../features/alerts/alertApi";
import type { FleetAlert } from "../features/alerts/alertTypes";
import { overviewHref } from "../features/overview/overviewModel";
import type { OverviewScope } from "../features/overview/overviewTypes";
import { alertJumpHref, matchingAlerts } from "./commandPaletteModel";

const pageSize = 100;
const maximumPages = 20;
const maximumAlertPages = 5;

export function CommandPalette({ open, onClose, scope }: {
  readonly open: boolean;
  readonly onClose: () => void;
  readonly scope: OverviewScope;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const search = useRef<HTMLInputElement>(null);
  const [mode, setMode] = useState<"servers" | "alerts">("servers");
  const [query, setQuery] = useState("");
  const [targets, setTargets] = useState<readonly ObservationTargetSummary[]>([]);
  const [alerts, setAlerts] = useState<readonly FleetAlert[]>([]);
  const [loading, setLoading] = useState(false);
  const [moreAvailable, setMoreAvailable] = useState(false);
  const [error, setError] = useState(false);

  useEffect(() => {
    if (!open) return;
    const previous = document.activeElement;
    dialog.current?.showModal();
    search.current?.focus();
    setMode("servers");
    return () => {
      dialog.current?.close();
      if (previous instanceof HTMLElement) previous.focus();
    };
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const controller = new AbortController();
    setQuery("");
    setTargets([]);
    setAlerts([]);
    setLoading(true);
    setMoreAvailable(false);
    setError(false);
    void (async () => {
      if (mode === "alerts") {
        const collected: FleetAlert[] = [];
        const visited = new Set<string>();
        let cursor: string | undefined;
        for (let pageNumber = 0; pageNumber < maximumAlertPages; pageNumber += 1) {
          const page = await getFleetActiveAlerts(controller.signal, pageSize, cursor);
          if (controller.signal.aborted) return;
          collected.push(...page.items);
          setAlerts([...collected]);
          if (!page.nextCursor) return;
          if (visited.has(page.nextCursor)) throw new Error("Alert paging repeated a cursor.");
          visited.add(page.nextCursor);
          cursor = page.nextCursor;
          if (pageNumber === maximumAlertPages - 1) setMoreAvailable(true);
        }
        return;
      }
      const collected: ObservationTargetSummary[] = [];
      const visited = new Set<string>();
      let cursor: string | undefined;
      for (let pageNumber = 0; pageNumber < maximumPages; pageNumber += 1) {
        const page = await listObservationTargets(controller.signal, cursor, pageSize);
        if (controller.signal.aborted) return;
        collected.push(...page.items);
        setTargets([...collected]);
        if (!page.nextCursor) return;
        if (visited.has(page.nextCursor)) throw new Error("Server paging repeated a cursor.");
        visited.add(page.nextCursor);
        cursor = page.nextCursor;
        if (pageNumber === maximumPages - 1) setMoreAvailable(true);
      }
    })().catch(() => {
      if (!controller.signal.aborted) setError(true);
    }).finally(() => {
      if (!controller.signal.aborted) setLoading(false);
    });
    return () => controller.abort();
  }, [open, mode]);

  const matches = useMemo(() => {
    const needle = query.trim().toLocaleLowerCase();
    return targets.filter(target => !needle || [target.displayName, target.instanceKey, target.host, target.instanceId]
      .some(value => value.toLocaleLowerCase().includes(needle)))
      .sort((left, right) => left.displayName.localeCompare(right.displayName) || left.instanceId.localeCompare(right.instanceId))
      .slice(0, 10);
  }, [targets, query]);
  const alertMatches = useMemo(() => matchingAlerts(alerts, query), [alerts, query]);

  const jump = (target: ObservationTargetSummary) => {
    location.hash = overviewHref(scope, "health", target.instanceId);
    onClose();
  };
  const jumpAlert = (alert: FleetAlert) => {
    location.hash = alertJumpHref(scope, alert);
    onClose();
  };

  return <dialog ref={dialog} className="command-palette" aria-labelledby="command-palette-title" onCancel={onClose}>
    <div className="command-palette-header">
      <div><h2 id="command-palette-title">Jump to evidence</h2><p>Find an authorized server or a current fleet alert.</p></div>
      <button type="button" className="secondary-button" onClick={onClose} aria-label="Close search">Close</button>
    </div>
    <div className="command-palette-modes" role="group" aria-label="Search category">
      <button type="button" aria-pressed={mode === "servers"} onClick={() => setMode("servers")}>Servers</button>
      <button type="button" aria-pressed={mode === "alerts"} onClick={() => setMode("alerts")}>Current alerts</button>
    </div>
    <input ref={search} type="search" aria-label={mode === "servers" ? "Search servers" : "Search current alerts"}
      placeholder={mode === "servers" ? "Search servers…" : "Search alert, rule, or server…"} value={query}
      onChange={event => setQuery(event.target.value)}
      onKeyDown={event => {
        if (event.key === "ArrowDown") {
          event.preventDefault();
          dialog.current?.querySelector<HTMLButtonElement>(".command-palette-result")?.focus();
        }
      }} />
    <p className="command-palette-status" role="status">
      {mode === "servers"
        ? loading ? `Searching authorized servers… ${targets.length} loaded` : error ? "Server search is incomplete. Try again later." : moreAvailable ? "Showing the first 2,000 authorized servers. Use Servers to browse further." : `${targets.length} authorized servers searched`
        : loading ? `Searching current alerts… ${alerts.length} loaded` : error ? "Alert search is incomplete. Try the Alert inbox." : moreAvailable ? "Showing the first 500 current alerts. Use the Alert inbox to browse further." : `${alerts.length} current alerts searched`}
    </p>
    <div className="command-palette-results" aria-label={mode === "servers" ? "Matching servers" : "Matching current alerts"}>
      {mode === "servers" ? matches.map(target => <button key={target.instanceId} className="command-palette-result" type="button" onClick={() => jump(target)}
        onKeyDown={event => {
          if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
          event.preventDefault();
          const buttons = Array.from(dialog.current?.querySelectorAll<HTMLButtonElement>(".command-palette-result") ?? []);
          const index = buttons.indexOf(event.currentTarget);
          (buttons[index + (event.key === "ArrowDown" ? 1 : -1)] ?? (event.key === "ArrowUp" ? search.current : buttons[0]))?.focus();
        }}>
        <strong>{target.displayName}</strong><span>{target.host} · {target.instanceKey}</span>
      </button>) : alertMatches.map(alert => <button key={`${alert.targetId}:${alert.alertId}`} className="command-palette-result" type="button" onClick={() => jumpAlert(alert)}
        onKeyDown={event => {
          if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
          event.preventDefault();
          const buttons = Array.from(dialog.current?.querySelectorAll<HTMLButtonElement>(".command-palette-result") ?? []);
          const index = buttons.indexOf(event.currentTarget);
          (buttons[index + (event.key === "ArrowDown" ? 1 : -1)] ?? (event.key === "ArrowUp" ? search.current : buttons[0]))?.focus();
        }}>
        <strong>{alert.ruleName}</strong><span>{alert.targetName} · {alert.state} · alert {alert.alertId.slice(0, 12)}…</span>
      </button>)}
      {!loading && (mode === "servers" ? matches.length : alertMatches.length) === 0 && <p className="command-palette-empty">No matching {mode === "servers" ? "servers" : "current alerts"} in the loaded results.</p>}
    </div>
  </dialog>;
}
