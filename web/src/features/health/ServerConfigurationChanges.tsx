import { useEffect, useMemo, useRef, useState } from "react";
import { supportsChangeTimelineWindow } from "../../components/dashboardTimelineModel.ts";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { overviewWindow } from "../overview/overviewModel";
import type { OverviewScope } from "../overview/overviewTypes";
import { useHealthPageRequest, canAppendHealthPage } from "./useHealthPageRequest";
import { getServerConfigurationChanges, HealthRequestError } from "./healthApi";
import type { CollectorHealthSummary, ServerConfigurationChange, ServerConfigurationChangeResult } from "./healthTypes";

export function ServerConfigurationChanges({ instanceId, targetRevision, scope, refresh, collector, canManage, selectedRunId }: {
  readonly selectedRunId?: string;
  readonly targetRevision: number;
  readonly instanceId: string;
  readonly scope: OverviewScope;
  readonly refresh: number;
  readonly collector?: CollectorHealthSummary;
  readonly canManage: boolean;
}) {
  const formatTime = useTimeFormatter();
  const selection = useMemo(() => {
    try {
      const window = overviewWindow(scope, Date.now());
      return supportsChangeTimelineWindow(window.fromUtc, window.toUtc)
        ? { window } : { error: "Configuration history supports windows up to seven days." };
    } catch { return { error: "Choose a valid UTC window for configuration history." }; }
  }, [scope.range, scope.from, scope.to, refresh]);
  const disabled = collector?.state === "disabled";
  // Live refresh can move its effective bounds. A retained trail keeps the bounds that produced its first receipt.
  const requestKey = JSON.stringify([instanceId.toLowerCase(), targetRevision, scope.range, scope.from,
    scope.to, selectedRunId?.toLowerCase(), disabled]);
  type Trail = { key: string; index: number; pages: readonly ServerConfigurationChangeResult[]; retained: boolean };
  const [trail, setTrail] = useState<Trail>();
  const current = trail?.key === requestKey ? trail : undefined;
  const result = !disabled ? current?.pages[current.index] : undefined;
  const [error, setError] = useState<string>();
  const failed = !disabled && error === requestKey;
  const commands = useHealthPageRequest(requestKey);
  const [limitKey, setLimitKey] = useState<string>();
  const atLimit = limitKey === requestKey;

  async function loadLatest(explicit = false) {
    if (!selection.window || disabled) return;
    const command = commands.begin(); if (!command) return;
    setError(undefined);
    if (explicit && current) setTrail({ ...current, retained: true });
    try {
      const next = await getServerConfigurationChanges(instanceId, selection.window, command.signal);
      if (next.targetRevision !== targetRevision) throw new Error("Configuration target revision changed.");
      if (command.current()) {
        setLimitKey(undefined);
        setTrail({ key: requestKey, index: 0, pages: [next], retained: !explicit && current === undefined && selectedRunId !== undefined });
      }
    } catch (error) { if (command.current()) {
      setError(requestKey);
      if (error instanceof HealthRequestError && (error.status === 401 || error.status === 403)) setTrail(undefined);
    } }
    finally { command.finish(); }
  }
  useEffect(() => {
    if (!current?.retained && !disabled && selection.window) void loadLatest();
  }, [requestKey, refresh]);

  async function next() {
    if (!result?.nextCursor || !current || commands.pending) return;
    const index = current.index + 1;
    if (current.pages[index]) { setTrail({ ...current, index, retained: true }); setError(undefined); return; }
    if (!canAppendHealthPage(current.pages)) { setLimitKey(requestKey); setTrail({ ...current, retained: true }); return; }
    const command = commands.begin(); if (!command) return;
    setTrail({ ...current, retained: true }); setError(undefined);
    try {
      const page = await getServerConfigurationChanges(instanceId,
        { fromUtc: result.fromUtc, toUtc: result.toUtc }, command.signal, result.nextCursor);
      if (!command.current()) return;
      if (!canAppendHealthPage(current.pages, page.items.length)) { setLimitKey(requestKey); return; }
      if (page.targetRevision !== result.targetRevision || page.publicationGeneration !== result.publicationGeneration ||
          page.nextCursor && current.pages.some(item => item.nextCursor === page.nextCursor))
        throw new Error("Configuration history snapshot changed.");
      setTrail({ ...current, pages: [...current.pages.slice(0, index), page], index, retained: true });
    } catch (error) { if (command.current()) {
      setError(requestKey);
      if (error instanceof HealthRequestError && (error.status === 401 || error.status === 403)) setTrail(undefined);
    } }
    finally { command.finish(); }
  }
  function previous() {
    if (!current || current.index === 0 || commands.pending) return;
    setTrail({ ...current, index: current.index - 1, retained: true }); setError(undefined);
  }

  const sectionRef = useRef<HTMLElement>(null);
  const focusedSelection = useRef<string | undefined>(undefined);
  const rowOwnedFocus = useRef(false);
  const focusKey = selectedRunId ? `${instanceId}:${scope.range}:${scope.from ?? ""}:${scope.to ?? ""}:${selectedRunId}` : undefined;
  useEffect(() => {
    if (!focusKey) { focusedSelection.current = undefined; return; }
    const restoreOwnedFocus = rowOwnedFocus.current && document.activeElement === document.body;
    if (!result && !failed && !disabled && !selection.error) return;
    const row = [...(sectionRef.current?.querySelectorAll<HTMLElement>("[data-configuration-run]") ?? [])]
      .find(item => item.dataset.configurationRun === selectedRunId?.toLowerCase());
    const destinationKey = `${focusKey}:${row ? "matched" : "missing"}`;
    if (focusedSelection.current === destinationKey && !restoreOwnedFocus) return;
    const destination = row ?? sectionRef.current?.querySelector<HTMLElement>("h4");
    destination?.focus({ preventScroll: true });
    destination?.scrollIntoView({ block: "center" });
    focusedSelection.current = destinationKey;
  }, [focusKey, selectedRunId, result, failed, disabled, selection.error]);

  return <section ref={sectionRef} onFocusCapture={event => { rowOwnedFocus.current = event.target instanceof HTMLElement && event.target.dataset.configurationRun === selectedRunId?.toLowerCase(); }}
    onBlurCapture={event => { if (event.relatedTarget !== null) rowOwnedFocus.current = false; }} className="bounded-health-section" aria-busy={commands.pending} aria-labelledby="server-configuration-changes-heading">
    <div className="bounded-health-heading"><h4 tabIndex={-1} id="server-configuration-changes-heading">Server configuration changes</h4></div>
    <p>Changes are detected between complete scans. Detection time is the later observation, not the exact time a setting changed.</p>
    {disabled && <p role="status">Configuration collection is disabled for this server. {canManage
      ? "Verify SQL Server read permissions, then enable it under Current server settings."
      : "Ask a target administrator to enable it after read permissions are verified."}</p>}
    {selection.error && <p role="status">{selection.error}</p>}
    {failed && <p role="alert">Configuration history is unavailable or changed while paging. {result ? "The loaded page remains available." : "Return to latest to retry."}</p>}
    {!disabled && !selection.error && <button type="button" disabled={commands.pending} onClick={() => void loadLatest(true)}>Return to latest configuration history</button>}
    {result && <p className="table-note">Loaded window: {formatTime(result.fromUtc)} to {formatTime(result.toUtc)} · Publication {result.publicationGeneration} · Target revision {result.targetRevision}. {current?.retained ? "Retained configuration history. Workspace refresh does not replace this investigated page." : "Latest history for the selected window."}</p>}
    {atLimit && <p role="status">Browser investigation limit reached: 400 pages or 10,000 configuration rows. Return to latest or change context to start a new investigation.</p>}
    {commands.pending && result && <p role="status">Loading configuration history; the loaded page remains available…</p>}
    {!disabled && !selection.error && !failed && !result && <p role="status">Loading configuration history…</p>}
    {selectedRunId && result && !result.items.some(item => item.runId.toLowerCase() === selectedRunId.toLowerCase()) && <p role="status">The selected configuration scan is not in this loaded page. Check the selected window or additional history pages.</p>}
    {result && (result.items.length === 0
      ? <p>{result.coverage === "never_collected" ? "No configuration change evidence has been published yet." : "No configuration changes were recorded in this window. Collection gaps may exist."}</p>
      : <><ul>{result.items.map(item => <li tabIndex={-1} data-configuration-run={item.runId.toLowerCase()} key={`${item.runId}:${item.configurationId}`}>
          <strong>{item.configurationName}</strong> · {changeText(item)}
          <small> · Detected {formatTime(item.detectedAtUtc)}; compared with {formatTime(item.previousObservedAtUtc)}</small>
        </li>)}</ul><nav className="pager" aria-label="Server configuration change pages">
          {current && current.index > 0 && <button type="button" disabled={commands.pending} onClick={previous}>Previous 100</button>}
          <span>Page {(current?.index ?? 0) + 1}</span>
          {result.nextCursor && <button type="button" disabled={commands.pending || atLimit} onClick={() => void next()}>Next 100</button>}
        </nav></>)}
  </section>;
}

function changeText(item: ServerConfigurationChange): string {
  const changes: string[] = [];
  if (item.configuredChanged)
    changes.push(`configured ${item.previousConfiguredValue} → ${item.configuredValue}`);
  if (item.inUseChanged)
    changes.push(`in use ${item.previousInUseValue} → ${item.inUseValue}`);
  return changes.join("; ");
}
