import { useEffect, useId, useMemo, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { OverviewChart } from "../overview/OverviewChart";
import type { OverviewScope, OverviewSeries } from "../overview/overviewTypes";
import { customUtcInputValue, overviewWindow, readOverviewScope } from "../overview/overviewModel.ts";
import { useOverviewAnalytics } from "../overview/useOverviewAnalytics";

import { DatabaseFileIoTrendPanel, type SelectedDatabaseFile } from "./DatabaseFileIoTrendPanel";
import { BufferNodePlePanel } from "./BufferNodePlePanel";
import { SqlVolumePlanningPanel } from "./SqlVolumePlanningPanel.tsx";
import { useSqlVolumePlanning } from "./useSqlVolumePlanning.ts";
import { sqlVolumePlanningWindow, type SqlVolumePlanningSelection } from "./sqlVolumePlanningModel.ts";
import { SqlVolumeHistoryPanel } from "./SqlVolumeHistoryPanel";
import { useChartReferences } from "../overview/useChartReferences.ts";
import { resourceInspectionInputUtc, resourceInspectionUtc } from "./resourceInspectionModel.ts";
import { readResourceSelection, resourceSelectionHref, preserveInvalidFileSnapshotHref, preserveInvalidCpuCaptureHref, preserveInvalidCpuObservationHref, preserveInvalidVolumePlanningHref, resourceWindowMatches, type ResourceSelection } from "./resourceSelectionModel.ts";
import { ChartEvidenceIndicator, EvidenceDisclosure, ReferenceEvidenceStatus } from "../../components/CompactEvidenceStatus.tsx";

import { useDatabaseFileWindowHistory } from "./useDatabaseFileWindowHistory.ts";
import { DatabaseFileWindowHistoryPanel } from "./DatabaseFileWindowHistoryPanel.tsx";
import { fileHistoryWindow, type FileSnapshotSelection } from "./databaseFileWindowHistoryModel.ts";
import { useCpuCaptureWindowHistory } from "./useCpuCaptureWindowHistory.ts";
import { CpuCaptureWindowHistoryPanel } from "./CpuCaptureWindowHistoryPanel.tsx";
import { cpuCaptureWindow, type CpuCaptureSelection } from "./cpuCaptureWindowHistoryModel.ts";

import { useCpuObservationHistory } from "./useCpuObservationHistory.ts";
import { CpuCollectionControl } from "./CpuCollectionControl.tsx";
import { CpuObservationHistoryPanel } from "./CpuObservationHistoryPanel.tsx";
import { previousCpuObservationWindow, type CpuObservationSelection } from "./cpuObservationHistoryModel.ts";

const memoryMetrics = new Set([
  "engine.process_physical_memory_bytes", "engine.os_available_memory_bytes",
  "host.memory.available_bytes",
]);

export function TargetResourcesPanel({ instanceId, displayName, onClose, scope, refresh, manualRefresh=0, accessContext="", onSelectWindow, targetRevision, canReadRules, canManageCpuCollection = false }: {
  readonly instanceId: string;
  readonly displayName: string;
  readonly onClose: () => void;
  readonly scope: OverviewScope;
  readonly refresh: number;
  readonly manualRefresh?: number;
  readonly accessContext?: string;
  readonly targetRevision: number;
  readonly canReadRules: boolean;
  readonly canManageCpuCollection?: boolean;
  readonly onSelectWindow?: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
}) {
  const { mode } = useTimeDisplay();
  const result = useOverviewAnalytics({ ...scope, target: instanceId }, refresh, targetRevision);
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
  const fallbackWindow = useMemo(() => { try { return overviewWindow(scope, Date.now()); } catch { return undefined; } },
    [instanceId, targetRevision, scope.range, scope.from, scope.to, refresh]);
  const analyticsWindow = result.data && evidence ? { fromUtc: result.data.fromUtc, toUtc: result.data.toUtc } : undefined;
  const inspectionWindow = analyticsWindow ?? fallbackWindow;
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
    const href = resourceSelectionHref(latestScope, clearAll ? {} : { ...readResourceSelection(location.hash, instanceId), ...change });
    const withFilePin = clearAll || Object.hasOwn(change,"fileSnapshot") ? href : preserveInvalidFileSnapshotHref(href,location.hash);
    const withCpuPin = clearAll || Object.hasOwn(change,"cpuCapture") ? withFilePin : preserveInvalidCpuCaptureHref(withFilePin,location.hash);
    const withObservationPin = clearAll || Object.hasOwn(change,"cpuObservation") ? withCpuPin : preserveInvalidCpuObservationHref(withCpuPin,location.hash);
    location.hash = clearAll || Object.hasOwn(change,"volumePlanning") ? withObservationPin : preserveInvalidVolumePlanningHref(withObservationPin,location.hash);
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
  const fileWindow = (() => {
    try { return inspectionWindow ? { window: fileHistoryWindow(inspectionWindow.fromUtc, inspectionWindow.toUtc), error: undefined } : { window: undefined, error: "Choose a valid shared time window for file evidence." }; }
    catch (failure) { return { window: undefined, error: failure instanceof Error ? failure.message : "Choose a valid file history window." }; }
  })();
  const fileHistory = useDatabaseFileWindowHistory(fileWindow.window ? { targetId: instanceId, targetRevision, ...fileWindow.window } : undefined,
    resourceSelection.invalidFileSnapshot || resourceSelection.invalidContext ? null : resourceSelection.fileSnapshot,manualRefresh,accessContext);
  const loadedSelectedFile = resourceSelection.file && fileHistory.snapshot.pages.flatMap(page => page.items).find(file => file.databaseId === resourceSelection.file!.databaseId && file.fileId === resourceSelection.file!.fileId);
  const activeSelectedFile: SelectedDatabaseFile | null = resourceSelection.file
    ? { ...resourceSelection.file, logicalName: loadedSelectedFile?.logicalName ?? `File ID ${resourceSelection.file.fileId} (name unavailable in the received exact snapshot)` } : null;
  const beginFileInvestigation = (snapshot?: FileSnapshotSelection, file?: ResourceSelection["file"], clearSnapshot = false) => {
    const latestScope=readOverviewScope(location.hash);
    if (!inspectionWindow || !/^#\/?resources(?:\?|$)/u.test(location.hash) || latestScope.target.toLowerCase() !== instanceId.toLowerCase()) return;
    if(latestScope.databaseId!==scope.databaseId||latestScope.range==="custom"&&!resourceWindowMatches(latestScope.from??"",latestScope.to??"",inspectionWindow.fromUtc,inspectionWindow.toUtc)||latestScope.range!=="custom"&&latestScope.range!==scope.range)return;
    const selection = { ...readResourceSelection(location.hash,instanceId), ...(file ? {file} : {}), ...(clearSnapshot ? {fileSnapshot:undefined} : snapshot ? {fileSnapshot:snapshot} : {}) };
    const href=resourceSelectionHref({...latestScope,range:"custom",from:inspectionWindow.fromUtc,to:inspectionWindow.toUtc},selection);
    location.hash=preserveInvalidCpuObservationHref(preserveInvalidVolumePlanningHref(preserveInvalidCpuCaptureHref(clearSnapshot || snapshot ? href : preserveInvalidFileSnapshotHref(href,location.hash),location.hash),location.hash),location.hash);
  };
  const cpuWindow = (() => {
    if (resourceSelection.invalidContext) return { window: undefined, error: "The requested CPU capture has ambiguous shared context. Clear resource selections before choosing evidence." };
    try { return inspectionWindow ? { window: cpuCaptureWindow(inspectionWindow.fromUtc, inspectionWindow.toUtc), error: undefined }
      : { window: undefined, error: "Choose a valid shared time window for CPU capture evidence." }; }
    catch (failure) { return { window: undefined, error: failure instanceof Error ? failure.message : "Choose a valid CPU capture history window." }; }
  })();
  const cpuHistory = useCpuCaptureWindowHistory(cpuWindow.window ? { targetId: instanceId, targetRevision, ...cpuWindow.window } : undefined,
    resourceSelection.invalidCpuCapture || resourceSelection.invalidContext ? null : resourceSelection.cpuCapture, manualRefresh, accessContext);
  const comparisonCpuWindow = (() => { try { return scope.compare && cpuWindow.window ? previousCpuObservationWindow(cpuWindow.window.fromUtc, cpuWindow.window.toUtc) : undefined; } catch { return undefined; } })();
  const cpuObservations = useCpuObservationHistory(cpuWindow.window ? { targetId: instanceId, targetRevision, ...cpuWindow.window } : undefined,
    resourceSelection.invalidCpuObservation || resourceSelection.invalidContext ? null : resourceSelection.cpuObservation, manualRefresh, accessContext,
    comparisonCpuWindow ? { targetId: instanceId, targetRevision, ...comparisonCpuWindow } : undefined);
  const beginCpuObservation = (observation?: CpuObservationSelection, retainWindow = false) => {
    const latestScope = readOverviewScope(location.hash), latestSelection = readResourceSelection(location.hash, instanceId);
    if (!inspectionWindow || latestSelection.invalidContext || !/^#\/?resources(?:\?|$)/u.test(location.hash) || latestScope.target.toLowerCase() !== instanceId.toLowerCase()) return;
    if (latestScope.databaseId !== scope.databaseId || latestScope.range === "custom" && !resourceWindowMatches(latestScope.from ?? "", latestScope.to ?? "", inspectionWindow.fromUtc, inspectionWindow.toUtc)
      || latestScope.range !== "custom" && latestScope.range !== scope.range) return;
    const pinnedScope = observation || retainWindow ? { ...latestScope, range: "custom" as const, from: inspectionWindow.fromUtc, to: inspectionWindow.toUtc } : latestScope;
    const href = resourceSelectionHref(pinnedScope, { ...latestSelection, cpuObservation: observation });
    const preserve = preserveInvalidVolumePlanningHref(preserveInvalidCpuCaptureHref(preserveInvalidFileSnapshotHref(href, location.hash), location.hash), location.hash);
    location.hash = retainWindow && latestSelection.invalidCpuObservation ? preserveInvalidCpuObservationHref(preserve, location.hash) : preserve;
  };
  const beginCpuInvestigation = (capture?: CpuCaptureSelection, retainWindow = false) => {
    const latestScope = readOverviewScope(location.hash), latestSelection = readResourceSelection(location.hash, instanceId);
    if (!inspectionWindow || latestSelection.invalidContext || !/^#\/?resources(?:\?|$)/u.test(location.hash) || latestScope.target.toLowerCase() !== instanceId.toLowerCase()) return;
    if (latestScope.databaseId !== scope.databaseId || latestScope.range === "custom" && !resourceWindowMatches(latestScope.from ?? "", latestScope.to ?? "", inspectionWindow.fromUtc, inspectionWindow.toUtc)
      || latestScope.range !== "custom" && latestScope.range !== scope.range) return;
    const selected = { ...latestSelection, cpuCapture: capture };
    const pinnedScope = capture || retainWindow ? { ...latestScope, range: "custom" as const, from: inspectionWindow.fromUtc, to: inspectionWindow.toUtc } : latestScope;
    const href = preserveInvalidFileSnapshotHref(resourceSelectionHref(pinnedScope, selected), location.hash);
    location.hash = preserveInvalidCpuObservationHref(preserveInvalidVolumePlanningHref(retainWindow && latestSelection.invalidCpuCapture ? preserveInvalidCpuCaptureHref(href, location.hash) : href, location.hash), location.hash);
  };
  const planningWindow = (() => {
    if (resourceSelection.invalidContext) return { window: undefined, error: "The requested volume planning receipt has ambiguous shared context. Clear resource selections before choosing evidence." };
    try { return inspectionWindow ? { window: sqlVolumePlanningWindow(inspectionWindow.fromUtc, inspectionWindow.toUtc), error: undefined }
      : { window: undefined, error: "Choose a valid shared time window for volume planning." }; }
    catch (failure) { return { window: undefined, error: failure instanceof Error ? failure.message : "Choose a valid volume planning window." }; }
  })();
  const volumePlanning = useSqlVolumePlanning(planningWindow.window ? { targetId: instanceId, targetRevision, ...planningWindow.window } : undefined,
    resourceSelection.invalidVolumePlanning || resourceSelection.invalidContext ? null : resourceSelection.volumePlanning, manualRefresh, accessContext);
  const beginVolumePlanning = (receipt?: SqlVolumePlanningSelection, retainWindow = false) => {
    const latestScope = readOverviewScope(location.hash), latestSelection = readResourceSelection(location.hash, instanceId);
    const receivedWindow = volumePlanning.snapshot.pages[0] ?? volumePlanning.list.pages[0] ?? inspectionWindow;
    if (!receivedWindow || !inspectionWindow || latestSelection.invalidContext || !/^#\/?resources(?:\?|$)/u.test(location.hash) || latestScope.target.toLowerCase() !== instanceId.toLowerCase()) return;
    if (latestScope.databaseId !== scope.databaseId || latestScope.range === "custom" && !resourceWindowMatches(latestScope.from ?? "", latestScope.to ?? "", receivedWindow.fromUtc, receivedWindow.toUtc)
      || latestScope.range !== "custom" && latestScope.range !== scope.range || !resourceWindowMatches(receivedWindow.fromUtc, receivedWindow.toUtc, inspectionWindow.fromUtc, inspectionWindow.toUtc)) return;
    const selected = { ...latestSelection, volumePlanning: receipt };
    const pinnedScope = receipt || retainWindow ? { ...latestScope, range: "custom" as const, from: receivedWindow.fromUtc, to: receivedWindow.toUtc } : latestScope;
    const href = preserveInvalidCpuCaptureHref(preserveInvalidFileSnapshotHref(resourceSelectionHref(pinnedScope, selected), location.hash), location.hash);
    location.hash = preserveInvalidCpuObservationHref(retainWindow && latestSelection.invalidVolumePlanning ? preserveInvalidVolumePlanningHref(href, location.hash) : href, location.hash);
  };

  const chart = (title: string, metric: string, note: string) => <section className="panel server-dashboard-chart" key={metric}>
    <h3>{title}</h3>
    <ChartEvidenceIndicator label={title} series={evidence?.series.filter(item => item.metric === metric) ?? []} />
    <OverviewChart scrollablePlotLabel={`${title} plot`} series={evidence?.series.filter(item => item.metric === metric) ?? []}
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
    <div className="screen-intro"><div><p className="eyebrow">Server Â· resources</p>
      <h2 id="resources-heading">Resources for {displayName}</h2>
      <p>SQL Server CPU, memory, per-NUMA page life expectancy, and selected-file latency follow the shared time window. File allocation and autogrowth follow actual collection completions inside this window; cumulative counters remain separate from window latency.</p>
    </div><button className="secondary-button" onClick={onClose} type="button">Close</button></div>
    <p className="table-note">CPU, memory, page life expectancy, and volume capacity remain server-wide. Database file rows are filtered only within the loaded page.</p>
    {resourceSelection.issues?.map(issue => <p role="alert" key={issue}>{issue}</p>)}
    {(resourceSelection.cpuObservation || resourceSelection.invalidCpuObservation || resourceSelection.volumePlanning || resourceSelection.invalidVolumePlanning || resourceSelection.cpuCapture || resourceSelection.invalidCpuCapture || resourceSelection.file || resourceSelection.fileSnapshot || resourceSelection.invalidFileSnapshot || resourceSelection.volume || resourceSelection.numaNodeId !== undefined || resourceSelection.inspectionUtc || resourceSelection.issues?.length) &&
      <button type="button" className="secondary-button" onClick={() => updateSelection({}, true)}>Clear resource selections</button>}
    {inspectionWindow && <p><a href={preserveInvalidCpuObservationHref(preserveInvalidVolumePlanningHref(preserveInvalidCpuCaptureHref(preserveInvalidFileSnapshotHref(resourceSelectionHref(chartScope, resourceSelection),location.hash),location.hash),location.hash),location.hash)}>Open fixed resource investigation</a></p>}
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
      : "Loading resource historyâ€¦"}</p>}
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
          <OverviewChart scrollablePlotLabel="Memory pressure plot" series={memory(evidence)} previous={memory(previous)}
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
    {analyticsWindow && <BufferNodePlePanel instanceId={instanceId} targetRevision={targetRevision} scope={chartScope} refresh={refresh}
      nodeSelectionInvalid={resourceSelection.invalidNode}
      selectedNodeId={resourceSelection.numaNodeId} onSelectNode={nodeId => updateSelection({ numaNodeId: nodeId })}
      crosshairUtc={crosshairUtc} onCrosshairChange={changeInspectionTime}
      onSelectWindow={onSelectWindow} />}
    <SqlVolumePlanningPanel history={volumePlanning} windowError={planningWindow.error}
      selectionInvalid={resourceSelection.invalidVolumePlanning || resourceSelection.invalidContext} explicitSelection={resourceSelection.volumePlanning}
      onSelectReceipt={receipt => beginVolumePlanning(receipt)} onRetainWindow={() => beginVolumePlanning(volumePlanning.selected ?? undefined, true)} />
    {inspectionWindow && <SqlVolumeHistoryPanel instanceId={instanceId} targetRevision={targetRevision} fromUtc={inspectionWindow.fromUtc} toUtc={inspectionWindow.toUtc}
      compare={scope.compare} refresh={refresh} selectedVolume={resourceSelection.invalidVolume || resourceSelection.invalidContext ? null : resourceSelection.volume}
      onSelectVolume={volume => updateSelection({ volume })} crosshairUtc={crosshairUtc} onCrosshairChange={changeInspectionTime} onSelectWindow={onSelectWindow} />}
    <CpuCollectionControl targetId={instanceId} targetRevision={targetRevision} canManage={canManageCpuCollection} accessContext={accessContext}/>
    <CpuObservationHistoryPanel history={cpuObservations} comparison={scope.compare} fromUtc={cpuWindow.window?.fromUtc} toUtc={cpuWindow.window?.toUtc}
      windowError={cpuWindow.error} selectionInvalid={resourceSelection.invalidCpuObservation || resourceSelection.invalidContext} explicitSelection={resourceSelection.cpuObservation}
      onSelectObservation={observation => beginCpuObservation(observation)} onRetainWindow={() => beginCpuObservation(cpuObservations.selected ?? undefined, true)}
      crosshairUtc={crosshairUtc} onCrosshairChange={changeInspectionTime} onSelectWindow={onSelectWindow} />
    <CpuCaptureWindowHistoryPanel history={cpuHistory} windowError={cpuWindow.error}
      selectionInvalid={resourceSelection.invalidCpuCapture || resourceSelection.invalidContext} explicitSelection={resourceSelection.cpuCapture}
      onSelectCapture={capture => beginCpuInvestigation(capture)} onRetainWindow={() => beginCpuInvestigation(cpuHistory.selected ?? undefined, true)} />
    <DatabaseFileWindowHistoryPanel history={fileHistory} windowError={fileWindow.error}
      selectionInvalid={resourceSelection.invalidFileSnapshot || resourceSelection.invalidContext}
      explicitSelection={resourceSelection.fileSnapshot} selectedFile={resourceSelection.file} databaseId={scope.databaseId}
      onSelectReceipt={snapshot => beginFileInvestigation(snapshot, undefined, snapshot === undefined)}
      onStartInvestigation={snapshot => beginFileInvestigation(snapshot)}
      onSelectFile={(file, snapshot) => beginFileInvestigation(snapshot, {databaseId:file.databaseId,fileId:file.fileId})} />
    {activeSelectedFile && <button type="button" className="secondary-button" onClick={()=>updateSelection({file:undefined})}>Clear selected file</button>}
    {inspectionWindow && !fileHistory.denied && <DatabaseFileIoTrendPanel instanceId={instanceId} targetRevision={targetRevision} file={activeSelectedFile} scope={chartScope}
      crosshairUtc={crosshairUtc} onCrosshairChange={changeInspectionTime}
      refresh={refresh} onSelectWindow={onSelectWindow} />}
    {result.data && evidence && <section className="compact-evidence-footer" aria-label="Resources evidence status">
      <p>Selected window: {formatDisplayTime(result.data.fromUtc, mode)} to {formatDisplayTime(result.data.toUtc, mode)}. Refreshed {formatDisplayTime(result.data.refreshedAtUtc, mode)}.</p>
      <p>SQL core status: {evidence.collectionState.replaceAll("_", " ")} Â· {evidence.gaps.length} reported collection gaps. Independent sources can have different coverage.</p>
      <EvidenceDisclosure label="Collection evidence details" attention={evidence.gaps.length > 0 || evidence.collectionState !== "current"}>
        <p>CPU, memory and volume history follow this loaded window. PLE and selected-file latency state their own coverage. File counters and volume capacities describe exact retained receipts in the shared completion window. Volume planning uses the 14-day lookback anchored to its selected receipt. Missing observations are never filled with zero.</p>
        <p>Last SQL core observation: {evidence.lastObservedUtc ? formatDisplayTime(evidence.lastObservedUtc, mode) : "unavailable"}. Source status does not establish a health verdict.</p>
      </EvidenceDisclosure>
      <ReferenceEvidenceStatus {...chartReferences} />
    </section>}
  </section>;
}
