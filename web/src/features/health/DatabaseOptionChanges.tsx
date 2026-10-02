import { useEffect, useMemo, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { overviewWindow } from "../overview/overviewModel";
import type { OverviewScope } from "../overview/overviewTypes";
import { getDatabaseOptionChanges } from "./healthApi";
import type { DatabaseOptionChangeResult } from "./healthTypes";

const labels: Record<string, string> = {
  recovery_model: "Recovery model", user_access: "Access mode",
  is_read_only: "Read-only", compatibility_level: "Compatibility level",
};

export function DatabaseOptionChanges({ instanceId, scope, refresh }: {
  readonly instanceId: string; readonly scope: OverviewScope; readonly refresh: number;
}) {
  const formatTime = useTimeFormatter();
  const selection = useMemo(() => {
    try {
      const window = overviewWindow(scope, Date.now());
      return Date.parse(window.toUtc) - Date.parse(window.fromUtc) <= 7 * 86400000
        ? { window } : { error: "Option history supports windows up to seven days." };
    } catch { return { error: "Choose a valid UTC window for option history." }; }
  }, [scope.range, scope.from, scope.to, refresh]);
  const [result, setResult] = useState<DatabaseOptionChangeResult>();
  const [error, setError] = useState<string>();
  const visibleItems = scope.databaseId === undefined ? result?.items ?? []
    : result?.items.filter(item => item.databaseId === scope.databaseId) ?? [];

  useEffect(() => {
    if (!selection.window) {
      setResult(undefined);
      setError(undefined);
      return;
    }
    const controller = new AbortController();
    setResult(undefined);
    setError(undefined);
    void getDatabaseOptionChanges(instanceId, selection.window, controller.signal)
      .then(next => {
        if (next.targetId.toLowerCase() !== instanceId.toLowerCase())
          throw new Error("Option history target mismatch.");
        if (!controller.signal.aborted) setResult(next);
      })
      .catch(() => { if (!controller.signal.aborted) setError("Option history is unavailable. Try again."); });
    return () => controller.abort();
  }, [instanceId, selection.window?.fromUtc, selection.window?.toUtc]);

  return <section className="bounded-health-section" aria-labelledby="database-option-changes-heading">
    <div className="bounded-health-heading"><h4 id="database-option-changes-heading">Database option changes</h4></div>
    <p>Detected between consecutive complete inventory runs. The change may have happened any time between observations.</p>
    {scope.databaseId !== undefined ? <p className="table-note">Database ID {scope.databaseId} · filtering the loaded option-change response only.</p> : null}
    {selection.error && <p role="status">{selection.error}</p>}
    {error && <p role="alert">{error}</p>}
    {!selection.error && !error && !result && <p role="status">Loading option history…</p>}
    {result && (visibleItems.length === 0
      ? <p>No changes for the selected database context are present in this bounded window response.</p>
      : <><ul>{visibleItems.map((item, index) => <li key={`${item.collectionRunId}:${item.databaseId}:${item.optionKey}:${index}`}>
          <strong>{item.databaseName}</strong> · {labels[item.optionKey] ?? item.optionKey}: {item.previousValue} → {item.currentValue}
          <small> · Detected {formatTime(item.detectedAtUtc)}</small>
        </li>)}</ul>{result.items.length === 100 && <p>Showing the latest 100 changes. Narrow the time range to see more.</p>}</>)}
  </section>;
}
