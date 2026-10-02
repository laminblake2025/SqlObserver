import { useEffect, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { OverviewChart } from "../overview/OverviewChart";
import { overviewWindow } from "../overview/overviewModel";
import type { OverviewScope } from "../overview/overviewTypes";
import { getDatabaseFileIoTrend, type DatabaseFileIoTrend } from "./databaseFileIoTrendApi";
import { fileIoTrendSeries } from "./databaseFileIoTrendModel";

export interface SelectedDatabaseFile {
  readonly databaseId: number;
  readonly fileId: number;
  readonly logicalName: string;
}

export function DatabaseFileIoTrendPanel({ instanceId, file, scope, refresh, onSelectWindow, crosshairUtc, onCrosshairChange }: {
  readonly instanceId: string;
  readonly file: SelectedDatabaseFile | null;
  readonly scope: OverviewScope;
  readonly refresh: number;
  readonly onSelectWindow?: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
  readonly crosshairUtc?: string | null;
  readonly onCrosshairChange?: (utc: string | null) => void;
}) {
  const { mode } = useTimeDisplay();
  const [receivedTrend, setTrend] = useState<{ key: string; value: DatabaseFileIoTrend }>();
  const [receivedError, setError] = useState<{ key: string; message: string }>();
  const [receivedLoading, setLoading] = useState<{ key: string; value: boolean }>();
  const key = JSON.stringify([instanceId, file?.databaseId, file?.fileId, scope, refresh]);
  const trend = receivedTrend?.key === key ? receivedTrend.value : undefined;
  const error = receivedError?.key === key ? receivedError.message : undefined;
  const loading = receivedLoading?.key === key ? receivedLoading.value : true;

  useEffect(() => {
    const controller = new AbortController();
    setTrend(undefined);
    setError(undefined);
    setLoading({ key, value: false });
    if (file === null) return () => controller.abort();
    let window: { fromUtc: string; toUtc: string };
    try {
      window = overviewWindow(scope, Date.now());
      if (Date.parse(window.toUtc) - Date.parse(window.fromUtc) > 7 * 86400000)
        throw new Error("File latency history supports windows of seven days or less.");
    } catch (failure) {
      setError({ key, message: failure instanceof Error ? failure.message : "The selected window is invalid." });
      return () => controller.abort();
    }
    setLoading({ key, value: true });
    void getDatabaseFileIoTrend(instanceId, file.databaseId, file.fileId,
      window.fromUtc, window.toUtc, controller.signal)
      .then(value => {
        if (controller.signal.aborted) return;
        if (value.targetId.toLowerCase() !== instanceId.toLowerCase() ||
            value.databaseId !== file.databaseId || value.fileId !== file.fileId ||
            Date.parse(value.fromUtc) !== Date.parse(window.fromUtc) ||
            Date.parse(value.toUtc) !== Date.parse(window.toUtc) ||
            value.semantics !== "file_counter_delta" || value.revisionScope !== "current" ||
            value.bucketSeconds !== 3600 || !Array.isArray(value.points) || value.points.length > 169)
          throw new Error("File trend evidence did not match the selected file.");
        setTrend({ key, value });
      })
      .catch((failure: unknown) => {
        if (!controller.signal.aborted)
          setError({ key, message: failure instanceof Error ? failure.message : "File I/O history is unavailable." });
      })
      .finally(() => { if (!controller.signal.aborted) setLoading({ key, value: false }); });
    return () => controller.abort();
  }, [key]);

  return <section className="panel server-dashboard-chart" aria-label="Selected file latency history" aria-busy={loading}>
    <h3>Read and write latency over time</h3>
    {file === null ? <p>Select a file in the table to inspect its history.</p> : <>
      <p className="activity-evidence">Database {file.databaseId} / {file.logicalName} (file {file.fileId}).
        Hourly averages use only comparable counter deltas in the current target revision.</p>
      {loading && <p role="status">Loading file latency history…</p>}
      {error && <p role="alert">{error}</p>}
      {trend && <>
        <p className="activity-evidence">{formatDisplayTime(trend.fromUtc, mode)} to {formatDisplayTime(trend.toUtc, mode)}.
          {trend.points.reduce((sum, point) => sum + point.resetRows + point.gapRows + point.legacyRows + point.baselineRows, 0)} samples
          {' '}could not be compared because of a reset, gap, missing directional counters, or missing baseline.</p>
        {trend.points.length ? <OverviewChart series={fileIoTrendSeries(trend)}
          crosshairUtc={crosshairUtc} onCrosshairChange={onCrosshairChange}
          fromUtc={trend.fromUtc} toUtc={trend.toUtc} onSelectWindow={onSelectWindow} />
          : <p>No file samples were stored in this window.</p>}
      </>}
    </>}
  </section>;
}
