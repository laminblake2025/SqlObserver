import { useEffect, useId, useRef, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { getDatabaseFileHealth } from "../health/healthApi";
import type { DatabaseFileHealthPage } from "../health/healthTypes";
import { OverviewChart } from "../overview/OverviewChart";
import type { OverviewScope, OverviewSeries } from "../overview/overviewTypes";
import { customUtcInputValue, readOverviewScope } from "../overview/overviewModel.ts";
import { useOverviewAnalytics } from "../overview/useOverviewAnalytics";
import { fileSizeGib, lifetimeAverageStallMilliseconds } from "./resourceFileModel";
import { DatabaseFileIoTrendPanel, type SelectedDatabaseFile } from "./DatabaseFileIoTrendPanel";
import { BufferNodePlePanel } from "./BufferNodePlePanel";
import { SqlVolumePanel } from "./SqlVolumePanel";
import { useChartReferences } from "../overview/useChartReferences.ts";
import { resourceInspectionInputUtc, resourceInspectionUtc } from "./resourceInspectionModel.ts";
import { readResourceSelection, resourceSelectionHref, type ResourceSelection } from "./resourceSelectionModel.ts";
import { ChartEvidenceIndicator, EvidenceDisclosure, ReferenceEvidenceStatus } from "../../components/CompactEvidenceStatus.tsx";

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
  const resourceSelection = readResourceSelection(location.hash, instanceId);
  const inspectionContext = JSON.stringify([instanceId, scope.range, scope.from, scope.to, scope.databaseId]);
  const inspectionKey = JSON.stringify([inspectionContext, resourceSelection.inspectionUtc, resourceSelection.invalidContext]);
  const inspectionDescriptionId = useId();
  const [inspection, setInspection] = useState<{ key: string; utc: string }>();
  const [inspectionDraft, setInspectionDraft] = useState<{ key: string; source?: string; value: string; error?: string }>();
  const draft = inspectionDraft?.key === inspectionContext && inspectionDraft.source === resourceSelection.inspectionUtc ? inspectionDraft : undefined;
  const inputValue = draft?.value ?? customUtcInputValue(resourceSelection.inspectionUtc);
  const inspectionWindow = result.data && evidence ? { fromUtc: result.data.fromUtc, toUtc: result.data.toUtc } : undefined;
  const crosshairUtc = (() => {
    if (!inspectionWindow || resourceSelection.invalidContext) return null;
    const requested = inspection?.key === inspectionKey ? inspection.utc : resourceSelection.inspectionUtc;
    if (!requested) return null;
    try { return resourceInspectionUtc(requested, inspectionWindow.fromUtc, inspectionWindow.toUtc); }
    catch { return null; }
  })();
  const requestedTimeOutside = (() => {
    if (!resourceSelection.inspectionUtc || !inspectionWindow) return false;
    try { resourceInspectionUtc(resourceSelection.inspectionUtc, inspectionWindow.fromUtc, inspectionWindow.toUtc); return false; }
    catch { return true; }
  })();
  const chartScope: OverviewScope = inspectionWindow
    ? { ...scope, range: "custom", from: inspectionWindow.fromUtc, to: inspectionWindow.toUtc } : scope;
  useEffect(() => { setInspectionDraft(undefined); }, [inspectionContext]);
  useEffect(() => { setInspection(undefined); }, [inspectionKey]);
  useEffect(() => {
    if (!inspectionWindow || resourceSelection.invalidContext) return;
    setInspection(current => {
      if (!current || current.key !== inspectionKey) return undefined;
      try { resourceInspectionUtc(current.utc, inspectionWindow.fromUtc, inspectionWindow.toUtc); return current; }
      catch { return undefined; }
    });
  }, [inspectionKey, inspectionWindow?.fromUtc, inspectionWindow?.toUtc]);
  const updateSelection = (change: Partial<ResourceSelection>, clearAll = false) => {
    const latestScope = readOverviewScope(location.hash);
    if (!/^#\/?resources(?:\?|$)/u.test(location.hash) || latestScope.target.toLowerCase() !== instanceId.toLowerCase()) return;
    location.hash = resourceSelectionHref(latestScope, clearAll ? {} : { ...readResourceSelection(location.hash, instanceId), ...change });
  };
  const changeInspectionTime = (utc: string | null) => {
    if (utc === null) { setInspection(undefined); return; }
    if (!inspectionWindow || resourceSelection.invalidContext) return;
    try {
      setInspection({ key: inspectionKey, utc: resourceInspectionUtc(utc, inspectionWindow.fromUtc, inspectionWindow.toUtc) });
    } catch { setInspection(undefined); }
  };
  const alignInspectionTime = () => {
    if (!inspectionWindow || resourceSelection.invalidContext) return;
    try {
      const utc = !draft && resourceSelection.inspectionUtc
        ? resourceInspectionUtc(resourceSelection.inspectionUtc, inspectionWindow.fromUtc, inspectionWindow.toUtc)
        : resourceInspectionInputUtc(inputValue, inspectionWindow.fromUtc, inspectionWindow.toUtc);
      setInspection(undefined);
      setInspectionDraft({ key: inspectionContext, source: utc, value: inputValue });
      updateSelection({ inspectionUtc: utc });
    } catch (failure) {
      setInspection(undefined);
      setInspectionDraft({ key: inspectionContext, value: inputValue, error: failure instanceof Error ? failure.message : "Choose a valid inspection time in UTC." });
      updateSelection({ inspectionUtc: undefined });
    }
  };
  const fileContext = JSON.stringify([instanceId.toLowerCase(), refresh]);
  const fileContextRef = useRef(fileContext);
  fileContextRef.current = fileContext;
  const [receivedFiles, setFiles] = useState<{ key: string; page: DatabaseFileHealthPage }>();
  const [receivedFileError, setFileError] = useState<{ key: string; message: string }>();
  const [receivedFilesLoading, setFilesLoading] = useState<{ key: string; loading: boolean }>();
  const files = receivedFiles?.key === fileContext ? receivedFiles.page : undefined;
  const fileError = receivedFileError?.key === fileContext ? receivedFileError.message : undefined;
  const filesLoading = receivedFilesLoading?.key === fileContext ? receivedFilesLoading.loading : true;
  const [filePageIndex, setFilePageIndex] = useState(0);
  const [fileCursors, setFileCursors] = useState<readonly (string | undefined)[]>([undefined]);
  const loadedSelectedFile = resourceSelection.file && files?.items.find(file => file.databaseId === resourceSelection.file!.databaseId && file.fileId === resourceSelection.file!.fileId);
  const activeSelectedFile: SelectedDatabaseFile | null = resourceSelection.file
    ? { ...resourceSelection.file, logicalName: loadedSelectedFile?.logicalName ?? `File ID ${resourceSelection.file.fileId} (name unavailable in the loaded snapshot)` } : null;
  const visibleFiles = files?.items.filter(file => scope.databaseId === undefined || file.databaseId === scope.databaseId) ?? [];
  const fileRequest = useRef<AbortController | undefined>(undefined);
  const isRewind = scope.range === "custom";

  useEffect(() => {
    fileRequest.current?.abort();
    const controller = new AbortController();
    fileRequest.current = controller;
    setFiles(undefined);
    setFileError(undefined);
    setFilePageIndex(0);
    setFileCursors([undefined]);
    setFilesLoading({ key: fileContext, loading: true });
    void getDatabaseFileHealth(instanceId, controller.signal)
      .then(page => {
        if (!controller.signal.aborted && fileContextRef.current === fileContext) {
          if (page.instanceId.toLowerCase() !== instanceId.toLowerCase())
            throw new Error("File evidence did not match the selected server.");
          setFiles({ key: fileContext, page });
        }
      })
      .catch((error: unknown) => {
        if (!controller.signal.aborted && fileContextRef.current === fileContext) setFileError({ key: fileContext, message: error instanceof Error ? error.message : "File evidence is unavailable." });
      })
      .finally(() => { if (!controller.signal.aborted && fileContextRef.current === fileContext) { fileRequest.current = undefined; setFilesLoading({ key: fileContext, loading: false }); } });
    return () => { controller.abort(); fileRequest.current?.abort(); fileRequest.current = undefined; };
  }, [instanceId, refresh]);

  async function navigateFilePage(index: number, cursor?: string) {
    if (fileRequest.current || !files) return;
    const controller = new AbortController();
    fileRequest.current = controller;
    setFilesLoading({ key: fileContext, loading: true });
    setFileError(undefined);
    try {
      const page = await getDatabaseFileHealth(instanceId, controller.signal, cursor);
      if (controller.signal.aborted || fileContextRef.current !== fileContext) return;
      if (page.instanceId.toLowerCase() !== instanceId.toLowerCase())
        throw new Error("File evidence did not match the selected server.");
      setFiles({ key: fileContext, page });
      setFilePageIndex(index);
      setFileCursors(previous => [...previous.slice(0, index), cursor]);
    } catch (error: unknown) {
      if (!controller.signal.aborted && fileContextRef.current === fileContext) setFileError({ key: fileContext, message: error instanceof Error ? error.message : "File evidence is unavailable." });
    } finally {
      if (!controller.signal.aborted && fileContextRef.current === fileContext) { fileRequest.current = undefined; setFilesLoading({ key: fileContext, loading: false }); }
    }
  }

  const chart = (title: string, metric: string, note: string) => <section className="panel server-dashboard-chart" key={metric}>
    <h3>{title}</h3>
    <ChartEvidenceIndicator label={title} series={evidence?.series.filter(item => item.metric === metric) ?? []} />
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
    {resourceSelection.issues?.map(issue => <p role="alert" key={issue}>{issue}</p>)}
    {(resourceSelection.file || resourceSelection.numaNodeId !== undefined || resourceSelection.inspectionUtc || resourceSelection.issues?.length) &&
      <button type="button" className="secondary-button" onClick={() => updateSelection({}, true)}>Clear resource selections</button>}
    {inspectionWindow && <p><a href={resourceSelectionHref(chartScope, resourceSelection)}>Open fixed resource investigation</a></p>}
    <section className="panel server-dashboard-chart" aria-label="Resource chart alignment">
      <h3>Inspect resource charts at one time</h3>
      <p id={inspectionDescriptionId} className="activity-evidence">Align loaded CPU, memory, PLE and file-latency charts to one UTC instant. The marker does not add observations or fill gaps. This keeps the workspace window unchanged.</p>
      <label className="scope-control">Resource inspection time (UTC)
        <input type="datetime-local" step="0.001" value={inputValue}
          aria-invalid={Boolean(draft?.error)} aria-describedby={inspectionDescriptionId} disabled={!inspectionWindow || resourceSelection.invalidContext}
          onChange={event => setInspectionDraft({ key: inspectionContext, source: resourceSelection.inspectionUtc, value: event.target.value })} />
      </label>
      <div className="screen-actions">
        <button type="button" className="secondary-button" disabled={!inspectionWindow || resourceSelection.invalidContext} onClick={alignInspectionTime}>Align resource charts</button>
        <button type="button" className="secondary-button" disabled={crosshairUtc === null && !resourceSelection.inspectionUtc} onClick={() => { setInspection(undefined); updateSelection({ inspectionUtc: undefined }); }}>Clear resource alignment</button>
      </div>
      {draft?.error && <p role="alert">{draft.error}</p>}
      {requestedTimeOutside && <p role="status">Requested resource inspection time is outside the loaded shared window. Align another time or clear resource alignment.</p>}
      <p role="status">{crosshairUtc ? <>Resource inspection time: <time dateTime={crosshairUtc}>{crosshairUtc}</time>. Loaded charts share this marker; missing evidence remains unavailable.</>
        : "No resource alignment selected. Use the UTC input or move the pointer over a loaded chart."}</p>
    </section>
    {result.loading && <p role="status">{result.data && evidence
      ? `Refreshing resource history; previously loaded evidence remains visible for ${formatDisplayTime(result.data.fromUtc, mode)} to ${formatDisplayTime(result.data.toUtc, mode)}.`
      : "Loading resource history…"}</p>}
    {result.error && <p role="alert">{result.error}</p>}
    {result.data && !evidence && <p role="status">No authorized resource evidence was returned for this server.</p>}
    {result.data && evidence && <>
      <div className="server-dashboard-charts">
        {chart("SQL scheduler CPU", "engine.sql_scheduler_cpu_percent",
          "Percent of visible SQL scheduler capacity used by non-preemptive work between samples. Host CPU from the collector machine is not treated as target CPU.")}
        {chart("Runnable SQL tasks", "engine.scheduler_runnable_tasks",
          "Tasks waiting for CPU on visible online SQL schedulers at sample time.")}
        {chart("Memory grants pending", "engine.memory_grants_pending",
          "Queries waiting for execution memory grants at sample time.")}
        <section className="panel server-dashboard-chart"><h3>Memory pressure</h3>
          <ChartEvidenceIndicator label="Memory pressure" series={memory(evidence)} />
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
    </>}
    {inspectionWindow && <BufferNodePlePanel instanceId={instanceId} scope={chartScope} refresh={refresh}
      nodeSelectionInvalid={resourceSelection.invalidNode}
      selectedNodeId={resourceSelection.numaNodeId} onSelectNode={nodeId => updateSelection({ numaNodeId: nodeId })}
      crosshairUtc={crosshairUtc} onCrosshairChange={changeInspectionTime}
      onSelectWindow={onSelectWindow} />}
    <SqlVolumePanel instanceId={instanceId} refresh={refresh} isRewind={isRewind} />
    <section className="panel" aria-label="Database file I/O" aria-busy={filesLoading}>
      <h3>Database file I/O</h3>
        <p>Choose a file to chart read and write latency in the selected window. The table is the latest snapshot even in Rewind; its averages use cumulative counters since reset. This is not free disk space.</p>
        {activeSelectedFile && <><p className="activity-evidence">Selected file: database ID {activeSelectedFile.databaseId}, file ID {activeSelectedFile.fileId} · {activeSelectedFile.logicalName}.</p>
          {!loadedSelectedFile && <p role="status">The selected file is not in the loaded snapshot page. Its identity is retained; no other file has been selected.</p>}
          <button type="button" className="secondary-button" onClick={() => updateSelection({ file: undefined })}>Clear selected file</button></>}
        {filesLoading && <p role="status">Loading file snapshot…</p>}
        {fileError && <p role="alert">{fileError} Refresh or retry page navigation.</p>}
        {files && <><p className="table-note">Page {filePageIndex + 1} · {files.items.length} loaded files{files.nextCursor ? " · More files available" : " · End of file snapshot"}.</p>
          <EvidenceDisclosure label={`File snapshot evidence: ${files.collector.state.replaceAll("_", " ")}`} attention={files.collector.state !== "current"}>
            <p>Repository time: {formatDisplayTime(files.repositoryTimeUtc, mode)}. The table shows the latest source snapshot, including in Rewind; it does not follow the chart window.</p>
          </EvidenceDisclosure>
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
                  onClick={() => updateSelection({ file: { databaseId: file.databaseId, fileId: file.fileId } })}>View trend</button></td></tr>)}
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
    {result.data && evidence && <section className="compact-evidence-footer" aria-label="Resources evidence status">
      <p>Selected window: {formatDisplayTime(result.data.fromUtc, mode)} to {formatDisplayTime(result.data.toUtc, mode)}. Refreshed {formatDisplayTime(result.data.refreshedAtUtc, mode)}.</p>
      <p>SQL core status: {evidence.collectionState.replaceAll("_", " ")} · {evidence.gaps.length} reported collection gaps. Independent sources can have different coverage.</p>
      <EvidenceDisclosure label="Collection evidence details" attention={evidence.gaps.length > 0 || evidence.collectionState !== "current"}>
        <p>CPU and memory history follow this loaded window. PLE and selected-file latency state their own coverage; file counters and volume capacity show latest snapshots even in Rewind. Missing observations are never filled with zero.</p>
        <p>Last SQL core observation: {evidence.lastObservedUtc ? formatDisplayTime(evidence.lastObservedUtc, mode) : "unavailable"}. Source status does not establish a health verdict.</p>
      </EvidenceDisclosure>
      <ReferenceEvidenceStatus {...chartReferences} />
    </section>}
  </section>;
}
