import { useEffect, useId, useRef, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { getDatabaseFileHealth } from "../health/healthApi";
import type { DatabaseFileHealthPage } from "../health/healthTypes";
import { OverviewChart } from "../overview/OverviewChart";
import type { OverviewScope, OverviewSeries } from "../overview/overviewTypes";
import { useOverviewAnalytics } from "../overview/useOverviewAnalytics";
import { fileSizeGib, lifetimeAverageStallMilliseconds } from "./resourceFileModel";
import { DatabaseFileIoTrendPanel, type SelectedDatabaseFile } from "./DatabaseFileIoTrendPanel";
import { BufferNodePlePanel } from "./BufferNodePlePanel";
import { SqlVolumePanel } from "./SqlVolumePanel";
import { useChartReferences } from "../overview/useChartReferences.ts";
import { resourceInspectionInputUtc, resourceInspectionUtc } from "./resourceInspectionModel.ts";

const memoryMetrics = new Set([
  "engine.process_physical_memory_bytes", "engine.os_available_memory_bytes",
  "host.memory.available_bytes",
]);

export function TargetResourcesPanel({ instanceId, displayName, onClose, scope, refresh, onSelectWindow, targetRevision, canReadRules }: {
  readonly instanceId: string;
  readonly displayName: string;
  readonly onClose: () => void;
  readonly scope: OverviewScope;
  readonly refresh: number;
  readonly targetRevision: number;
  readonly canReadRules: boolean;
  readonly onSelectWindow?: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
}) {
  const { mode } = useTimeDisplay();
  const result = useOverviewAnalytics({ ...scope, target: instanceId }, refresh);
  const evidence = result.data?.evidence.find(item => item.targetId === instanceId);
  const previous = result.previous?.evidence.find(item => item.targetId === instanceId);
  const chartReferences = useChartReferences(instanceId, targetRevision, result.data && evidence ? result.data : undefined, canReadRules, refresh, evidence?.series.map(item => item.metric));
  const inspectionKey = JSON.stringify([instanceId, scope.range, scope.from, scope.to, scope.databaseId]);
  const inspectionDescriptionId = useId();
  const [inspection, setInspection] = useState<{ key: string; utc: string }>();
  const [inspectionDraft, setInspectionDraft] = useState<{ key: string; value: string; error?: string }>();
  const draft = inspectionDraft?.key === inspectionKey ? inspectionDraft : undefined;
  const inspectionWindow = result.data && evidence ? { fromUtc: result.data.fromUtc, toUtc: result.data.toUtc } : undefined;
  const crosshairUtc = (() => {
    if (inspection?.key !== inspectionKey || !inspectionWindow) return null;
    try { return resourceInspectionUtc(inspection.utc, inspectionWindow.fromUtc, inspectionWindow.toUtc); }
    catch { return null; }
  })();
  const chartScope: OverviewScope = inspectionWindow
    ? { ...scope, range: "custom", from: inspectionWindow.fromUtc, to: inspectionWindow.toUtc } : scope;
  useEffect(() => { setInspection(undefined); setInspectionDraft(undefined); }, [inspectionKey]);
  useEffect(() => {
    if (!inspectionWindow) return;
    setInspection(current => {
      if (!current || current.key !== inspectionKey) return undefined;
      try { resourceInspectionUtc(current.utc, inspectionWindow.fromUtc, inspectionWindow.toUtc); return current; }
      catch { return undefined; }
    });
  }, [inspectionKey, inspectionWindow?.fromUtc, inspectionWindow?.toUtc]);
  const changeInspectionTime = (utc: string | null) => {
    if (utc === null) { setInspection(undefined); return; }
    if (!inspectionWindow) return;
    try {
      setInspection({ key: inspectionKey, utc: resourceInspectionUtc(utc, inspectionWindow.fromUtc, inspectionWindow.toUtc) });
    } catch { setInspection(undefined); }
  };
  const alignInspectionTime = () => {
    if (!inspectionWindow) return;
    try {
      const utc = resourceInspectionInputUtc(draft?.value ?? "", inspectionWindow.fromUtc, inspectionWindow.toUtc);
      setInspection({ key: inspectionKey, utc });
      setInspectionDraft({ key: inspectionKey, value: draft?.value ?? "" });
    } catch (failure) {
      setInspection(undefined);
      setInspectionDraft({ key: inspectionKey, value: draft?.value ?? "", error: failure instanceof Error ? failure.message : "Choose a valid inspection time in UTC." });
    }
  };
  const [files, setFiles] = useState<DatabaseFileHealthPage>();
  const [fileError, setFileError] = useState<string>();
  const [filesLoading, setFilesLoading] = useState(false);
  const [filePageIndex, setFilePageIndex] = useState(0);
  const [fileCursors, setFileCursors] = useState<readonly (string | undefined)[]>([undefined]);
  const [selectedFile, setSelectedFile] = useState<SelectedDatabaseFile | null>(null);
  const activeSelectedFile = scope.databaseId === undefined || selectedFile?.databaseId === scope.databaseId ? selectedFile : null;
  const visibleFiles = files?.items.filter(file => scope.databaseId === undefined || file.databaseId === scope.databaseId) ?? [];
  const fileRequest = useRef<AbortController | undefined>(undefined);
  const isRewind = scope.range === "custom";

  useEffect(() => {
    const controller = new AbortController();
    fileRequest.current = controller;
    setFiles(undefined);
    setFileError(undefined);
    setFilePageIndex(0);
    setFileCursors([undefined]);
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
  }, [instanceId, refresh]);

  useEffect(() => { setSelectedFile(null); }, [instanceId]);
  useEffect(() => {
    setSelectedFile(current => scope.databaseId !== undefined && current?.databaseId !== scope.databaseId ? null : current);
  }, [scope.databaseId]);

  async function navigateFilePage(index: number, cursor?: string) {
    if (fileRequest.current || !files) return;
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
      references={chartReferences.references}
      crosshairUtc={crosshairUtc} onCrosshairChange={changeInspectionTime}
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
      <p>SQL Server CPU, memory, per-NUMA page life expectancy, and selected-file latency follow the shared time window. The file table shows the latest cumulative counters.</p>
    </div><button className="secondary-button" onClick={onClose} type="button">Close</button></div>
    <p className="table-note">CPU, memory, page life expectancy, and volume capacity remain server-wide. Database file rows are filtered only within the loaded page.</p>
    <section className="panel server-dashboard-chart" aria-label="Resource chart alignment">
      <h3>Inspect resource charts at one time</h3>
      <p id={inspectionDescriptionId} className="activity-evidence">Align loaded CPU, memory, PLE and file-latency charts to one UTC instant. The marker does not add observations or fill gaps. This keeps the workspace window unchanged.</p>
      <label className="scope-control">Resource inspection time (UTC)
        <input type="datetime-local" step="0.001" value={draft?.value ?? ""}
          aria-invalid={Boolean(draft?.error)} aria-describedby={inspectionDescriptionId} disabled={!inspectionWindow}
          onChange={event => setInspectionDraft({ key: inspectionKey, value: event.target.value })} />
      </label>
      <div className="screen-actions">
        <button type="button" className="secondary-button" disabled={!inspectionWindow} onClick={alignInspectionTime}>Align resource charts</button>
        <button type="button" className="secondary-button" disabled={crosshairUtc === null} onClick={() => setInspection(undefined)}>Clear resource alignment</button>
      </div>
      {draft?.error && <p role="alert">{draft.error}</p>}
      <p role="status">{crosshairUtc ? <>Resource inspection time: <time dateTime={crosshairUtc}>{crosshairUtc}</time>. Loaded charts share this marker; missing evidence remains unavailable.</>
        : "No resource alignment selected. Use the UTC input or move the pointer over a loaded chart."}</p>
    </section>
    {result.loading && <p role="status">{result.data && evidence
      ? `Refreshing resource history; previously loaded evidence remains visible for ${formatDisplayTime(result.data.fromUtc, mode)} to ${formatDisplayTime(result.data.toUtc, mode)}.`
      : "Loading resource history…"}</p>}
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
            references={chartReferences.references}
            crosshairUtc={crosshairUtc} onCrosshairChange={changeInspectionTime}
            fromUtc={result.data.fromUtc} toUtc={result.data.toUtc} onSelectWindow={onSelectWindow} />
          <p className="activity-evidence">GiB. SQL process used, OS available via SQL, and collector-host available memory are separate gauges; the collector host may be a different machine.</p>
        </section>
      </div>
      {evidence.gaps.length > 0 && <details className="panel"><summary>Collection gaps ({evidence.gaps.length})</summary>
        <ul>{evidence.gaps.map((gap, index) => <li key={index}>{gap}</li>)}</ul></details>}
      {result.comparisonError && <p role="status">{result.comparisonError}</p>}
      <p className="activity-evidence">{chartReferences.baselineStatus}</p>
      <p className="activity-evidence">{chartReferences.ruleStatus}</p>
    </>}
    {inspectionWindow && <BufferNodePlePanel instanceId={instanceId} scope={chartScope} refresh={refresh}
      crosshairUtc={crosshairUtc} onCrosshairChange={changeInspectionTime}
      onSelectWindow={onSelectWindow} />}
    <SqlVolumePanel instanceId={instanceId} refresh={refresh} isRewind={isRewind} />
    <section className="panel" aria-label="Database file I/O" aria-busy={filesLoading}>
      <h3>Database file I/O</h3>
        <p>Choose a file to chart read and write latency in the selected window. The table is the latest snapshot even in Rewind; its averages use cumulative counters since reset. This is not free disk space.</p>
        {filesLoading && <p role="status">Loading file snapshot…</p>}
        {fileError && <p role="alert">{fileError} Refresh or retry page navigation.</p>}
        {files && <><p className="activity-evidence">Collector: {files.collector.state.replaceAll("_", " ")} ·
          Repository time: {formatDisplayTime(files.repositoryTimeUtc, mode)} · Page {filePageIndex + 1} · {files.items.length} files
          {files.nextCursor ? " · More files available" : " · End of file snapshot"}.</p>
          {scope.databaseId !== undefined ? <p className="table-note">Showing {visibleFiles.length} of {files.items.length} loaded files for database ID {scope.databaseId}.</p> : null}
          {visibleFiles.length ? <div className="table-scroll" tabIndex={0} role="region" aria-label="Latest database file counters"><table><caption>Latest SQL Server file counters</caption>
            <thead><tr><th scope="col">Database / file</th><th scope="col">Size</th>
              <th scope="col">Reads</th><th scope="col">Avg read stall (ms)</th>
              <th scope="col">Writes</th><th scope="col">Avg write stall (ms)</th>
              <th scope="col">Observed</th><th scope="col">History</th></tr></thead><tbody>
              {visibleFiles.map(file => <tr key={`${file.databaseId}:${file.fileId}`}>
                <td>{file.databaseId} / {file.logicalName}</td><td title={`${file.sizeBytes} bytes`}>{fileSizeGib(file.sizeBytes) ?? "—"}</td>
                <td>{file.readCount}</td><td>{lifetimeAverageStallMilliseconds(file.readStallMilliseconds, file.readCount) ?? "—"}</td>
                <td>{file.writeCount}</td><td>{lifetimeAverageStallMilliseconds(file.writeStallMilliseconds, file.writeCount) ?? "—"}</td>
                <td>{formatDisplayTime(file.observedAtUtc, mode)}</td>
                <td><button type="button" className="secondary-button" style={{ whiteSpace: "nowrap", minWidth: "max-content" }} aria-label={`View read and write latency trend for database ${file.databaseId}, file ${file.logicalName}`} aria-pressed={activeSelectedFile?.databaseId === file.databaseId && activeSelectedFile?.fileId === file.fileId}
                  onClick={() => setSelectedFile({ databaseId: file.databaseId, fileId: file.fileId, logicalName: file.logicalName })}>View trend</button></td></tr>)}
            </tbody></table></div> : <p>{scope.databaseId === undefined ? "No file rows were returned for this snapshot." : "No file rows for the selected database are present in this loaded page. Browse other file pages to continue."}</p>}
          <nav aria-label="Database file pages" className="pager">
            <button type="button" disabled={filesLoading || filePageIndex === 0} onClick={() => void navigateFilePage(0)}>First file page</button>{" "}
            <button type="button" disabled={filesLoading || filePageIndex === 0} onClick={() => void navigateFilePage(filePageIndex - 1, fileCursors[filePageIndex - 1])}>Previous file page</button>{" "}
            <button type="button" disabled={filesLoading || !files.nextCursor} onClick={() => void navigateFilePage(filePageIndex + 1, files.nextCursor ?? undefined)}>Next file page</button>
          </nav>
        </>}
    </section>
    {inspectionWindow && <DatabaseFileIoTrendPanel instanceId={instanceId} file={activeSelectedFile} scope={chartScope}
      crosshairUtc={crosshairUtc} onCrosshairChange={changeInspectionTime}
      refresh={refresh} onSelectWindow={onSelectWindow} />}
  </section>;
}
