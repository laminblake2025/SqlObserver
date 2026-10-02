import { useEffect, useMemo, useRef, useState } from "react";
import { utcMicroseconds } from "../../utcTimestamp.ts";
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
      const instant = (value: string) => utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1"));
      return instant(window.toUtc) - instant(window.fromUtc) <= 7n * 86_400_000_000n
        ? { window } : { error: "Option history supports windows up to seven days." };
    } catch { return { error: "Choose a valid UTC window for option history." }; }
  }, [scope.range, scope.from, scope.to, refresh]);
  const key = JSON.stringify([instanceId.toLowerCase(), selection.window?.fromUtc, selection.window?.toUtc, refresh]);
  const latest = useRef(key); latest.current = key;
  const [state, setState] = useState<{ readonly key: string; readonly result?: DatabaseOptionChangeResult; readonly error?: string }>();
  const current = selection.window && state?.key === key ? state : undefined;
  const result = current?.result, error = current?.error;
  const visibleItems = scope.databaseId === undefined ? result?.items ?? []
    : result?.items.filter(item => item.databaseId === scope.databaseId) ?? [];

  useEffect(() => {
    const controller = new AbortController();
    setState({ key });
    if (!selection.window) return () => controller.abort();
    const current = () => !controller.signal.aborted && latest.current === key;
    void getDatabaseOptionChanges(instanceId, selection.window, controller.signal)
      .then(next => {
        if (next.targetId.toLowerCase() !== instanceId.toLowerCase())
          throw new Error("Option history target mismatch.");
        if (current()) setState({ key, result: next });
      })
      .catch(() => { if (current()) setState({ key, error: "Option history is unavailable. Try again." }); });
    return () => controller.abort();
  }, [instanceId, selection.window?.fromUtc, selection.window?.toUtc, key]);

  return <section className="bounded-health-section" aria-labelledby="database-option-changes-heading">
    <div className="bounded-health-heading"><h4 id="database-option-changes-heading">Database option changes</h4></div>
    <p>Detected between consecutive complete inventory runs. The change may have happened any time between observations.</p>
    {scope.databaseId !== undefined ? <p className="table-note">Database ID {scope.databaseId} · filtering the loaded option-change response only.</p> : null}
    {selection.error && <p role="status">{selection.error}</p>}
    {error && <p role="alert">{error}</p>}
    {!selection.error && !error && !result && <p role="status">Loading option history…</p>}
    {result && <p className="table-note">Loaded option history: <time dateTime={result.fromUtc}>{formatTime(result.fromUtc)}</time> to{" "}
      <time dateTime={result.toUtc}>{formatTime(result.toUtc)}</time> (exclusive).</p>}
    {result && (visibleItems.length === 0
      ? <p>No changes for the selected database context are present in this bounded window response.</p>
      : <><ul>{visibleItems.map((item, index) => <li key={`${item.collectionRunId}:${item.databaseId}:${item.optionKey}:${index}`}>
          <strong>{item.databaseName}</strong> · {labels[item.optionKey] ?? item.optionKey}: {item.previousValue} → {item.currentValue}
          <small> · Detected {formatTime(item.detectedAtUtc)}</small>
        </li>)}</ul>{result.items.length === 100 && <p>Showing the latest 100 changes. Narrow the time range to see more.</p>}</>)}
  </section>;
}
