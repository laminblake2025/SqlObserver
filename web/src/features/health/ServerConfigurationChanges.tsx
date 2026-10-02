import { useEffect, useMemo, useRef, useState } from "react";
import { supportsChangeTimelineWindow } from "../../components/dashboardTimelineModel.ts";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { overviewWindow } from "../overview/overviewModel";
import type { OverviewScope } from "../overview/overviewTypes";
import { getServerConfigurationChanges } from "./healthApi";
import type { CollectorHealthSummary, ServerConfigurationChange, ServerConfigurationChangeResult } from "./healthTypes";

export function ServerConfigurationChanges({ instanceId, scope, refresh, collector, canManage, selectedRunId }: {
  readonly selectedRunId?: string;
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
  const [retry, setRetry] = useState(0);
  const requestKey = `${instanceId}:${selection.window?.fromUtc ?? ""}:${selection.window?.toUtc ?? ""}:${refresh}:${retry}`;
  const [paging, setPaging] = useState<{
    key: string; index: number; cursors: readonly (string | undefined)[];
  }>({ key: "", index: 0, cursors: [undefined] });
  const current = paging.key === requestKey ? paging :
    { key: requestKey, index: 0, cursors: [undefined] as readonly (string | undefined)[] };
  const cursor = current.cursors[current.index];
  const [page, setPage] = useState<{
    key: string; index: number; result: ServerConfigurationChangeResult;
  }>();
  const [error, setError] = useState<{ key: string; index: number }>();
  const disabled = collector?.state === "disabled";
  const result = !disabled && page?.key === requestKey && page.index === current.index ? page.result : undefined;
  const failed = !disabled && error?.key === requestKey && error.index === current.index;

  useEffect(() => {
    if (!selection.window || disabled) {
      setPage(undefined);
      setError(undefined);
      return;
    }
    const controller = new AbortController();
    setPage(undefined);
    setError(undefined);
    void getServerConfigurationChanges(instanceId, selection.window, controller.signal, cursor)
      .then(next => {
        if (next.targetId.toLowerCase() !== instanceId.toLowerCase() ||
            Date.parse(next.fromUtc) !== Date.parse(selection.window!.fromUtc) ||
            Date.parse(next.toUtc) !== Date.parse(selection.window!.toUtc))
          throw new Error("Configuration history does not match the selected window.");
        if (!controller.signal.aborted) setPage({ key: requestKey, index: current.index, result: next });
      })
      .catch(() => { if (!controller.signal.aborted) setError({ key: requestKey, index: current.index }); });
    return () => controller.abort();
  }, [instanceId, selection.window?.fromUtc, selection.window?.toUtc,
    disabled, requestKey, current.index, cursor]);

  const sectionRef = useRef<HTMLElement>(null);
  const focusedSelection = useRef<string | undefined>(undefined);
  const rowOwnedFocus = useRef(false);
  const focusKey = selectedRunId ? `${instanceId}:${scope.range}:${scope.from ?? ""}:${scope.to ?? ""}:${selectedRunId}` : undefined;
  useEffect(() => {
    if (!focusKey) { focusedSelection.current = undefined; return; }
    const restoreOwnedFocus = rowOwnedFocus.current && document.activeElement === document.body;
    if (focusedSelection.current === focusKey && !restoreOwnedFocus || !result && !failed && !disabled && !selection.error) return;
    const row = [...(sectionRef.current?.querySelectorAll<HTMLElement>("[data-configuration-run]") ?? [])]
      .find(item => item.dataset.configurationRun === selectedRunId?.toLowerCase());
    const destination = row ?? sectionRef.current?.querySelector<HTMLElement>("h4");
    destination?.focus({ preventScroll: true });
    destination?.scrollIntoView({ block: "center" });
    focusedSelection.current = focusKey;
  }, [focusKey, selectedRunId, result, failed, disabled, selection.error]);

  const restart = () => setRetry(value => value + 1);
  const previous = () => setPaging({ ...current, index: current.index - 1 });
  const next = () => {
    if (result?.nextCursor)
      setPaging({ key: requestKey, index: current.index + 1,
        cursors: [...current.cursors.slice(0, current.index + 1), result.nextCursor] });
  };

  return <section ref={sectionRef} onFocusCapture={event => { rowOwnedFocus.current = event.target instanceof HTMLElement && event.target.dataset.configurationRun === selectedRunId?.toLowerCase(); }}
    onBlurCapture={event => { if (event.relatedTarget !== null) rowOwnedFocus.current = false; }} className="bounded-health-section" aria-labelledby="server-configuration-changes-heading">
    <div className="bounded-health-heading"><h4 tabIndex={-1} id="server-configuration-changes-heading">Server configuration changes</h4></div>
    <p>Changes are detected between complete scans. Detection time is the later observation, not the exact time a setting changed.</p>
    {disabled && <p role="status">Configuration collection is disabled for this server. {canManage
      ? "Verify SQL Server read permissions, then enable it under Current server settings."
      : "Ask a target administrator to enable it after read permissions are verified."}</p>}
    {selection.error && <p role="status">{selection.error}</p>}
    {failed && <p role="alert">Configuration history is unavailable or changed while paging. <button type="button" onClick={restart}>Restart from latest</button></p>}
    {!disabled && !selection.error && !failed && !result && <p role="status">Loading configuration history…</p>}
    {selectedRunId && result && !result.items.some(item => item.runId.toLowerCase() === selectedRunId.toLowerCase()) && <p role="status">The selected configuration scan is not in this loaded page. Check the selected window or additional history pages.</p>}
    {result && (result.items.length === 0
      ? <p>{result.coverage === "never_collected" ? "No configuration change evidence has been published yet." : "No configuration changes were recorded in this window. Collection gaps may exist."}</p>
      : <><ul>{result.items.map(item => <li tabIndex={-1} data-configuration-run={item.runId.toLowerCase()} key={`${item.runId}:${item.configurationId}`}>
          <strong>{item.configurationName}</strong> · {changeText(item)}
          <small> · Detected {formatTime(item.detectedAtUtc)}; compared with {formatTime(item.previousObservedAtUtc)}</small>
        </li>)}</ul><nav className="pager" aria-label="Server configuration change pages">
          {current.index > 0 && <button type="button" onClick={previous}>Previous 100</button>}
          <span>Page {current.index + 1}</span>
          {result.nextCursor && <button type="button" onClick={next}>Next 100</button>}
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
