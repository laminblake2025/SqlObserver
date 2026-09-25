import { useEffect, useRef, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { getDatabaseFileHealth } from "../health/healthApi";
import type { DatabaseFileHealthPage } from "../health/healthTypes";
import { OverviewChart } from "../overview/OverviewChart";
import type { OverviewScope, OverviewSeries } from "../overview/overviewTypes";
import { useOverviewAnalytics } from "../overview/useOverviewAnalytics";
import { fileSizeGib, lifetimeAverageStallMilliseconds } from "./resourceFileModel";
import { SqlVolumePanel } from "./SqlVolumePanel";

const memoryMetrics = new Set([
  "engine.process_physical_memory_bytes", "engine.os_available_memory_bytes",
  "host.memory.available_bytes",
]);

export function TargetResourcesPanel({ instanceId, displayName, onClose, scope, refresh, onSelectWindow }: {
  readonly instanceId: string;
  readonly displayName: string;
  readonly onClose: () => void;
  readonly scope: OverviewScope;
  readonly refresh: number;
  readonly onSelectWindow?: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
}) {
  const { mode } = useTimeDisplay();
  const result = useOverviewAnalytics({ ...scope, target: instanceId }, refresh);
  const evidence = result.data?.evidence.find(item => item.targetId === instanceId);
  const previous = result.previous?.evidence.find(item => item.targetId === instanceId);
  const [files, setFiles] = useState<DatabaseFileHealthPage>();
  const [fileError, setFileError] = useState<string>();
  const [filesLoading, setFilesLoading] = useState(false);
  const [filePageIndex, setFilePageIndex] = useState(0);
  const [fileCursors, setFileCursors] = useState<readonly (string | undefined)[]>([undefined]);
  const fileRequest = useRef<AbortController | undefined>(undefined);
  const isRewind = scope.range === "custom";

  useEffect(() => {
    const controller = new AbortController();
    fileRequest.current = controller;
    setFiles(undefined);
    setFileError(undefined);
    setFilePageIndex(0);
    setFileCursors([undefined]);
    if (isRewind) { fileRequest.current = undefined; setFilesLoading(false); return () => controller.abort(); }
    setFilesLoading(true);
    void getDatabaseFileHealth(instanceId, controller.signal)
      .then(page => {
        if (!controller.signal.aborted) {
          if (page.instanceId.toLowerCase() !== instanceId.toLowerCase())
            throw new Error("File evidence did not match the selected server.");
          setFiles(page);
        }
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted) setFileError(error instanceof Error ? error.message : "File evidence is unavailable.");
      })
      .finally(() => { if (!controller.signal.aborted) { fileRequest.current = undefined; setFilesLoading(false); } });
    return () => controller.abort();
  }, [instanceId, refresh, isRewind]);

  async function navigateFilePage(index: number, cursor?: string) {
    if (fileRequest.current || !files || isRewind) return;
    const controller = new AbortController();
    fileRequest.current = controller;
    setFilesLoading(true);
    setFileError(undefined);
    try {
      const page = await getDatabaseFileHealth(instanceId, controller.signal, cursor);
      if (controller.signal.aborted) return;
      if (page.instanceId.toLowerCase() !== instanceId.toLowerCase())
        throw new Error("File evidence did not match the selected server.");
      setFiles(page);
      setFilePageIndex(index);
      setFileCursors(previous => [...previous.slice(0, index), cursor]);
    } catch (error: unknown) {
      if (!controller.signal.aborted) setFileError(error instanceof Error ? error.message : "File evidence is unavailable.");
    } finally {
      if (!controller.signal.aborted) { fileRequest.current = undefined; setFilesLoading(false); }
    }
  }

  const chart = (title: string, metric: string, note: string) => <section className="panel server-dashboard-chart" key={metric}>
    <h3>{title}</h3>
    <OverviewChart series={evidence?.series.filter(item => item.metric === metric) ?? []}
      previous={previous?.series.filter(item => item.metric === metric)}
      fromUtc={result.data!.fromUtc} toUtc={result.data!.toUtc}
      onSelectWindow={onSelectWindow} />
    <p className="activity-evidence">{note}</p>
  </section>;
  const memory = (source: typeof evidence): readonly OverviewSeries[] => source?.series
    .filter(item => memoryMetrics.has(item.metric))
    .map(item => ({ ...item, dimension: item.metric === "engine.process_physical_memory_bytes"
      ? "SQL process used" : item.metric === "engine.os_available_memory_bytes"
        ? "OS available via SQL" : "Collector host available" })) ?? [];

  return <section className="activity-screen" aria-labelledby="resources-heading">
    <div className="screen-intro"><div><p className="eyebrow">Server · resources</p>
      <h2 id="resources-heading">Resources for {displayName}</h2>
      <p>SQL Server CPU pressure and memory evidence follow the shared time window. Live mode also shows cumulative file I/O counters.</p>
    </div><button className="secondary-button" onClick={onClose} type="button">Close</button></div>
    {result.loading && <p role="status">Loading resource history…</p>}
    {result.error && <p role="alert">{result.error}</p>}
    {result.data && !evidence && <p role="status">No authorized resource evidence was returned for this server.</p>}
    {result.data && evidence && <>
      <p className="activity-evidence">{formatDisplayTime(result.data.fromUtc, mode)} to {formatDisplayTime(result.data.toUtc, mode)}.
        {evidence.gaps.length ? ` ${evidence.gaps.length} collection gaps are reported below.` : ""}</p>
      <div className="server-dashboard-charts">
        {chart("SQL scheduler CPU", "engine.sql_scheduler_cpu_percent",
          "Percent of visible SQL scheduler capacity used by non-preemptive work between samples. Host CPU from the collector machine is not treated as target CPU.")}
        {chart("Runnable SQL tasks", "engine.scheduler_runnable_tasks",
          "Tasks waiting for CPU on visible online SQL schedulers at sample time.")}
        {chart("Memory grants pending", "engine.memory_grants_pending",
          "Queries waiting for execution memory grants at sample time.")}
        <section className="panel server-dashboard-chart"><h3>Memory pressure</h3>
          <OverviewChart series={memory(evidence)} previous={memory(previous)}
            fromUtc={result.data.fromUtc} toUtc={result.data.toUtc} onSelectWindow={onSelectWindow} />
          <p className="activity-evidence">GiB. SQL process used, OS available via SQL, and collector-host available memory are separate gauges; the collector host may be a different machine.</p>
        </section>
      </div>
      {evidence.gaps.length > 0 && <details className="panel"><summary>Collection gaps ({evidence.gaps.length})</summary>
        <ul>{evidence.gaps.map((gap, index) => <li key={index}>{gap}</li>)}</ul></details>}
      {result.comparisonError && <p role="status">{result.comparisonError}</p>}
    </>}
    <SqlVolumePanel instanceId={instanceId} refresh={refresh} isRewind={isRewind} />
    <section className="panel" aria-label="Database file I/O" aria-busy={filesLoading}>
      <h3>Database file I/O</h3>
      {isRewind ? <p>Historical per-file counters are not available in Rewind. Choose a live range to see the latest snapshot.</p> : <>
        <p>Read and write stalls are kept separate. Average stall per operation uses cumulative counters since their source reset, not the selected time window. This is not free disk space.</p>
        {filesLoading && <p role="status">Loading file snapshot…</p>}
        {fileError && <p role="alert">{fileError} Refresh or retry page navigation.</p>}
        {files && <><p className="activity-evidence">Collector: {files.collector.state.replaceAll("_", " ")} ·
          Repository time: {formatDisplayTime(files.repositoryTimeUtc, mode)} · Page {filePageIndex + 1} · {files.items.length} files
          {files.nextCursor ? " · More files available" : " · End of file snapshot"}.</p>
          {files.items.length ? <div className="table-scroll"><table><caption>Latest SQL Server file counters</caption>
            <thead><tr><th scope="col">Database / file</th><th scope="col">Size</th>
              <th scope="col">Reads</th><th scope="col">Avg read stall (ms)</th>
              <th scope="col">Writes</th><th scope="col">Avg write stall (ms)</th>
              <th scope="col">Observed</th></tr></thead><tbody>
              {files.items.map(file => <tr key={`${file.databaseId}:${file.fileId}`}>
                <td>{file.databaseId} / {file.logicalName}</td><td title={`${file.sizeBytes} bytes`}>{fileSizeGib(file.sizeBytes) ?? "—"}</td>
                <td>{file.readCount}</td><td>{lifetimeAverageStallMilliseconds(file.readStallMilliseconds, file.readCount) ?? "—"}</td>
                <td>{file.writeCount}</td><td>{lifetimeAverageStallMilliseconds(file.writeStallMilliseconds, file.writeCount) ?? "—"}</td>
                <td>{formatDisplayTime(file.observedAtUtc, mode)}</td></tr>)}
            </tbody></table></div> : <p>No file rows were returned for this snapshot.</p>}
          <nav aria-label="Database file pages" className="pager">
            <button type="button" disabled={filesLoading || filePageIndex === 0} onClick={() => void navigateFilePage(0)}>First file page</button>{" "}
            <button type="button" disabled={filesLoading || filePageIndex === 0} onClick={() => void navigateFilePage(filePageIndex - 1, fileCursors[filePageIndex - 1])}>Previous file page</button>{" "}
            <button type="button" disabled={filesLoading || !files.nextCursor} onClick={() => void navigateFilePage(filePageIndex + 1, files.nextCursor ?? undefined)}>Next file page</button>
          </nav>
        </>}
      </>}
    </section>
  </section>;
}
