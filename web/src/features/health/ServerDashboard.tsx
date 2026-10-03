import { useId, useMemo, useRef, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { formatDisplayTime } from "../../timeDisplay";
import { utcMicroseconds } from "../../utcTimestamp.ts";
import { OverviewChart } from "../overview/OverviewChart";
import { issueHref, overviewEvidenceFooter, overviewHref, overviewWindow, rankedIssues } from "../overview/overviewModel";
import type { OverviewScope, OverviewValue } from "../overview/overviewTypes";
import { useOverviewAnalytics } from "../overview/useOverviewAnalytics";
import { CurrentBlockingPanel } from "./CurrentBlockingPanel";
import { useCurrentBlockingEvidence } from "./useCurrentBlockingEvidence.ts";
import { ServerWaitTrendPanel } from "./ServerWaitTrendPanel";
import type { TargetHealthState } from "./healthTypes";
import { DashboardTimeline } from "../../components/DashboardTimeline";
import { timelineEventsInWindow, type TimelineEvent } from "../../components/dashboardTimelineModel";
import { useDashboardTimeline } from "./useDashboardTimeline";
import { useAlertLifecycleHistory } from "./useAlertLifecycleHistory.ts";
import { useServerLifecycleHistory } from "./useServerLifecycleHistory.ts";
import { ServerLifecycleHistoryPanel } from "./ServerLifecycleHistoryPanel.tsx";
import { serverLifecycleTimelineEvents, serverLifecycleEventIdentity, matchesServerLifecycleSelection, type ServerLifecycleSelection } from "./serverLifecycleHistoryModel.ts";
import { AlertLifecycleHistoryPanel } from "./AlertLifecycleHistoryPanel.tsx";
import { alertLifecycleTimelineEvents, lifecycleEventIdentity, matchesLifecycleSelection, type AlertLifecycleSelection } from "./alertLifecycleHistoryModel.ts";
import { useAgentHistory } from "../operations/useAgentHistory.ts";
import { healthCheckHref, validateAgentWindow } from "../operations/agentHistoryModel.ts";
import { AgentFailureEvidence } from "../operations/AgentFailureHistoryPanel.tsx";
import { useChartReferences } from "../overview/useChartReferences.ts";
import { dashboardTimelineSelectionHref, matchesDashboardTimelineSelection, readDashboardTimelineSelection, readAlertLifecycleTimelineIdentity, readServerLifecycleTimelineIdentity,
  type DashboardTimelineSelection } from "../../components/dashboardTimelineSelectionModel.ts";
import { ChartAlignmentControls } from "../../components/ChartAlignmentControls.tsx";
import { chartInspectionHref, readChartInspection } from "../../components/chartInspectionModel.ts";
import { useChartInspection } from "../../components/useChartInspection.ts";
import { DashboardBaselineCards } from "./DashboardBaselineCards.tsx";
import { DashboardInvestigationSummary } from "./DashboardInvestigationSummary.tsx";
import { ChartEvidenceIndicator, EvidenceDisclosure, ReferenceEvidenceStatus } from "../../components/CompactEvidenceStatus.tsx";
import "./serverHistoryDisclosure.css";

export function ServerDashboard({ instanceId, displayName, scope, refresh, blockingRefresh, configurationCollectorState, targetRevision, selectionHash, canReadRules, active = true }: {
  readonly instanceId: string;
  readonly displayName: string;
  readonly scope: OverviewScope;
  readonly refresh: number;
  readonly blockingRefresh: number;
  readonly configurationCollectorState?: TargetHealthState;
  readonly targetRevision: number;
  readonly selectionHash: string;
  readonly canReadRules: boolean;
  readonly active?: boolean;
}) {
  const { mode } = useTimeDisplay();
  const timeLabel = (value: string) => formatDisplayTime(value, mode);
  const inspectionKey = JSON.stringify([instanceId.toLowerCase(), targetRevision, scope.range, scope.from, scope.to, scope.databaseId, scope.compare]);
  const timelineContextKey = JSON.stringify([instanceId.toLowerCase(), targetRevision, scope.databaseId, scope.compare]);
  const requestedChartAt = readChartInspection(selectionHash);
  const requestedSelection = readDashboardTimelineSelection(selectionHash, instanceId);
  const selectedScope = { ...scope, target: instanceId };
  const result = useOverviewAnalytics(selectedScope, refresh, targetRevision);
  const data = result.data;
  const evidence = data?.evidence.find(item => item.targetId === instanceId);
  const blockingEvidence = useCurrentBlockingEvidence(instanceId, targetRevision, blockingRefresh, Boolean(data && evidence));
  const [blockerSelection, setBlockerSelection] = useState<{ key: string; sessionId: number }>();
  const blockerKey = JSON.stringify([instanceId.toLowerCase(), targetRevision, blockingEvidence.page?.evidence?.runId]);
  const inspectBlocker = (sessionId: number) => {
    if (!active || location.hash !== selectionHash) return;
    setBlockerSelection({ key: blockerKey, sessionId });
    document.getElementById("server-blocking-heading")?.focus();
    document.getElementById("server-blocking-heading")?.scrollIntoView({ block: "start" });
  };
  const previous = result.previous?.evidence.find(item => item.targetId === instanceId);
  const issues = data ? rankedIssues(data).filter(issue => issue.targetId.toLowerCase() === instanceId.toLowerCase()) : [];
  const agentHistory = useAgentHistory(instanceId, targetRevision, data && evidence ? data : undefined, refresh);
  const agentDisclosureKey = JSON.stringify([timelineContextKey, ...(agentHistory.page
    ? [agentHistory.page.window.fromUtc, agentHistory.page.window.toUtc].map(value => utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1")).toString())
    : [])]);
  const chartReferences = useChartReferences(instanceId, targetRevision, data && evidence ? data : undefined, canReadRules, refresh, evidence?.series.map(item => item.metric));
  const fallbackWindow = useMemo(()=>{try{return overviewWindow(selectedScope,Date.now());}catch{return undefined;}},[instanceId,targetRevision,scope.range,scope.from,scope.to,refresh]);
  const analyticsWindow = data && evidence ? { fromUtc:data.fromUtc,toUtc:data.toUtc } : undefined;
  const alertEvidence = useRef<HTMLDivElement>(null), alertEvidenceId = useId();
  const serverLifecycleEvidence = useRef<HTMLDivElement>(null), serverLifecycleEvidenceId = useId();
  const agentWindowAvailable = (() => {
    if (!analyticsWindow) return false;
    try { validateAgentWindow(analyticsWindow); return true; } catch { return false; }
  })();
  const timelineWindow = analyticsWindow ?? fallbackWindow;
  const timeline = useDashboardTimeline(instanceId, targetRevision, selectedScope, analyticsWindow, refresh, configurationCollectorState);
  const requestedLifecycleIdentity = requestedSelection ? readAlertLifecycleTimelineIdentity(requestedSelection.eventId) : undefined;
  const selectedLifecycleEvent: AlertLifecycleSelection|null|undefined = requestedSelection === null ? null : requestedLifecycleIdentity && requestedSelection ? {...requestedLifecycleIdentity,eventAtUtc:requestedSelection.timeUtc} : undefined;
  const alerts = useAlertLifecycleHistory(timelineWindow ? {targetId:instanceId,targetRevision,...timelineWindow}:undefined,selectedLifecycleEvent);
  const requestedServerLifecycleIdentity = requestedSelection ? readServerLifecycleTimelineIdentity(requestedSelection.eventId) : undefined;
  const selectedServerLifecycleEvent: ServerLifecycleSelection|null|undefined = requestedSelection === null ? null : requestedServerLifecycleIdentity && requestedSelection
    ? {...requestedServerLifecycleIdentity,detectedAtUtc:requestedSelection.timeUtc} : undefined;
  const serverLifecycle = useServerLifecycleHistory(timelineWindow ? {targetId:instanceId,targetRevision,...timelineWindow}:undefined,selectedServerLifecycleEvent);
  const fixedTimelineScope: OverviewScope = timelineWindow ? { ...selectedScope, range: "custom", from: timelineWindow.fromUtc, to: timelineWindow.toUtc } : selectedScope;
  const lifecycleEvents = timelineWindow ? alertLifecycleTimelineEvents(alerts.list.pages.flatMap(page=>page.items),fixedTimelineScope) : [];
  const serverLifecycleEvents = timelineWindow ? serverLifecycleTimelineEvents(serverLifecycle.list.pages.flatMap(page=>page.items),fixedTimelineScope) : [];
  const pinnedExactEvent = serverLifecycle.detail.receipt?.event && timelineWindow
    ? serverLifecycleTimelineEvents([serverLifecycle.detail.receipt.event],fixedTimelineScope)[0]
    : alerts.detail.receipt?.event && timelineWindow ? alertLifecycleTimelineEvents([alerts.detail.receipt.event],fixedTimelineScope)[0] : undefined;
  const timelineEvents: readonly TimelineEvent[] = timelineWindow ? timelineEventsInWindow([
    ...(pinnedExactEvent ? [pinnedExactEvent] : []),
    ...timeline.events,
    ...lifecycleEvents,
    ...serverLifecycleEvents,
    ...(agentHistory.page?.rows.filter(row => row.countsAsJobFailure).map(row => ({
      id: `agent:${row.jobId}:${row.historyInstanceId}`, kind: "agent_failure" as const,
      timeUtc: row.firstObservedAtUtc, title: `SQL Agent job failure · ${row.jobId} · history ${row.historyInstanceId}`,
      timeBasis: "first_observed" as const,
      detail: `First observed by collection. Server-local job start: ${row.sourceLocalStart ?? "unavailable"}; no UTC execution time is inferred.`,
      href: healthCheckHref(fixedTimelineScope, instanceId, "agent"),
    })) ?? []),
    ...issues.filter(issue => issue.observedAtUtc && issue.title !== "SQL Agent failures").map(issue => ({
      id: `finding:${issue.destination}:${encodeURIComponent(issue.title)}:${issue.observedAtUtc}`, kind: "finding" as const,
      timeUtc: issue.observedAtUtc!, title: issue.title, timeBasis: "snapshot" as const,
      detail: `${issue.detail} This is an evidence snapshot, not a condition-start or alert-trigger timestamp.`,
      href: issueHref(fixedTimelineScope, instanceId, issue.destination, issue.observedAtUtc, issue.title),
    })),
  ], timelineWindow.fromUtc, timelineWindow.toUtc,pinnedExactEvent) : [];
  const restoredEvent = requestedSelection && timelineEvents.find(event => matchesDashboardTimelineSelection(requestedSelection, event));
  const alignmentContext = JSON.stringify([inspectionKey, selectionHash]);
  const inspection = useChartInspection(alignmentContext, timelineWindow?.fromUtc, timelineWindow?.toUtc, requestedChartAt, restoredEvent?.timeUtc);
  const crosshairUtc = inspection.crosshairUtc;
  const setCrosshairUtc = (utc: string | null) => { if (active && location.hash === selectionHash) inspection.setCrosshairUtc(utc); };
  const selectTimeline = (selection: DashboardTimelineSelection | undefined) => {
    if (!active || location.hash !== selectionHash) return;
    setCrosshairUtc(null);
    const current = readDashboardTimelineSelection(location.hash, instanceId);
    if (selection && !timelineEvents.some(event => matchesDashboardTimelineSelection(selection, event))) {
      const identity=readAlertLifecycleTimelineIdentity(selection.eventId);
      const serverIdentity=readServerLifecycleTimelineIdentity(selection.eventId);
      if (!(identity && alerts.list.pages.flatMap(page=>page.items).some(event=>matchesLifecycleSelection(event,{...identity,eventAtUtc:selection.timeUtc})))
        && !(serverIdentity && serverLifecycle.list.pages.flatMap(page=>page.items).some(event=>matchesServerLifecycleSelection(event,{...serverIdentity,detectedAtUtc:selection.timeUtc})))) return;
    }
    if (selection && current && matchesDashboardTimelineSelection(current, { id: selection.eventId, timeUtc: selection.timeUtc })) return;
    const href = dashboardTimelineSelectionHref(selection ? fixedTimelineScope : selectedScope, selection);
    location.hash = selection ? href : `${href}&healthSection=summary`;
  };
  const markers = timelineEvents.map(event => ({ timeUtc: event.timeUtc, label: `${event.title} · ${event.timeBasis.replaceAll("_", " ")}` }));
  const timelineNotes = [
    ...timeline.notes,
    alerts.denied ? "Alert lifecycle history access was denied; protected list and exact event evidence were removed. Restore target read access and explicitly retry history access."
      : alerts.list.error ? `Alert lifecycle history read: ${alerts.list.error}`
        : alerts.list.pages.length ? `Retained alert lifecycle: ${alerts.list.pages.reduce((sum,page)=>sum+page.items.length,0)} received of ${alerts.list.pages[0]!.coverage.eventCount} events. ${alerts.list.pages[0]!.coverage.legacyPartialEventCount} legacy partial events; missing metadata stays unknown. Browse explicit history pages and provenance below; current inbox state is separate evidence.`
          : "Retained alert lifecycle has no received coverage yet. Check the shared window and read status below; missing history is not all clear.",
    agentHistory.error ? `SQL Agent first-observed markers are unavailable: ${agentHistory.error}`
      : agentHistory.refreshing ? `Refreshing SQL Agent markers; showing the previous first-page snapshot from ${agentHistory.page!.observedAtUtc} in this same window.`
      : agentHistory.loading ? "Loading SQL Agent first-observed markers…"
      : agentHistory.page ? `SQL Agent: first ${agentHistory.page.rows.length} of up to 50 history rows${agentHistory.page.hasMore ? "; additional pages are available in SQL Agent history" : ""}${agentHistory.page.truncated ? "; capture was truncated" : ""}. Only failed job-outcome rows become markers.`
        : "SQL Agent has no received first-observed history coverage for this dashboard window. Check its read status below or open SQL Agent history independently; missing evidence is not all clear.",
    "The timeline retains the latest 120 loaded events. Database options and plan comparisons follow the selected database; alerts, deadlocks, job failures, configuration and snapshot findings remain server-wide.",
    serverLifecycle.denied ? "Server lifecycle access was denied; protected pages and exact proof were removed. Restore same-target read access and explicitly retry."
      : serverLifecycle.list.error ? `Server lifecycle history: ${serverLifecycle.list.error}`
        : serverLifecycle.list.pages.length ? `Sampled server lifecycle: ${serverLifecycle.list.pages.reduce((sum,page)=>sum+page.items.length,0)} received of ${serverLifecycle.list.pages[0]!.coverage.eventCount} detections. ${serverLifecycle.list.pages[0]!.coverage.hasVisibilityGap ? "Source visibility is incomplete; changes may be missing." : "Retained source coverage does not establish health."}`
          : "Server lifecycle has no received source coverage. Verify collector availability and choose a retained window; missing evidence is not all clear.",
    "Server lifecycle markers show detected changes between observations. Exact occurrence times, cause, number of changes between samples and physical AG failover are unqualified. An empty timeline is not an all-clear signal.",
  ];
  const memorySeries = (source: typeof evidence) => source?.series
    .filter(item => item.metric === "engine.process_physical_memory_bytes" || item.metric === "engine.os_available_memory_bytes" || item.metric === "host.memory.available_bytes")
    .map(item => ({ ...item, dimension: item.metric === "engine.process_physical_memory_bytes" ? "SQL process used" : item.metric === "engine.os_available_memory_bytes" ? "OS available via SQL" : "Host available" })) ?? [];
  const change = (next: Partial<OverviewScope>) => {
    if (!active || location.hash !== selectionHash) return;
    const nextScope = { ...selectedScope, ...next };
    try { location.hash = dashboardTimelineSelectionHref(nextScope, requestedSelection ?? undefined); }
    catch { location.hash = overviewHref(nextScope, "health", instanceId); }
  };
  const selectLifecycleEvent = (event:AlertLifecycleSelection|undefined) => selectTimeline(event ? {eventId:lifecycleEventIdentity(event),timeUtc:event.eventAtUtc}:undefined);
  const selectServerLifecycleEvent = (event:ServerLifecycleSelection|undefined) => selectTimeline(event ? {eventId:serverLifecycleEventIdentity(event),timeUtc:event.detectedAtUtc}:undefined);
  const chart = (metric: string, label: string, note: string) => <section className="panel server-dashboard-chart" key={metric}>
    <h4>{label}</h4>
    <ChartEvidenceIndicator label={label} series={evidence?.series.filter(series => series.metric === metric) ?? []} />
    <OverviewChart
      interactionKey={String(active)}
      scrollablePlotLabel={`${label} plot`}
      series={evidence?.series.filter(series => series.metric === metric) ?? []}
      previous={previous?.series.filter(series => series.metric === metric)}
      fromUtc={data!.fromUtc}
      toUtc={data!.toUtc}
      markers={markers}
      references={chartReferences.references}
      crosshairUtc={crosshairUtc}
      onCrosshairChange={setCrosshairUtc}
      onSelectWindow={window => { setCrosshairUtc(null); change({ range: "custom", from: window.fromUtc, to: window.toUtc }); }}
    />
    <small>{note}</small>
  </section>;

  return <section className="server-dashboard" aria-labelledby="server-dashboard-heading">
    <div className="section-heading"><div><p className="eyebrow">Server dashboard</p><h3 id="server-dashboard-heading">Investigate {displayName}</h3></div></div>
    <div className="overview-controls server-dashboard-controls">
      <label className="overview-check"><input type="checkbox" checked={scope.compare} onChange={event => change({ compare: event.target.checked })} />Compare previous period</label>
      <span className="server-dashboard-mode">Drag a chart or the timeline rail to select a shared time window. Live ranges refresh every 60 seconds.</span>
    </div>
    {result.loading && <p role="status">{data
      ? `Refreshing server timeline… Showing the previous snapshot window ${timeLabel(data.fromUtc)} to ${timeLabel(data.toUtc)}, refreshed ${timeLabel(data.refreshedAtUtc)}.`
      : "Loading server timeline…"}</p>}
    {result.error && <p role="alert" className="status-message">{result.error}</p>}
    {data && !evidence && <p className="empty-state">No authorized dashboard evidence was returned for this server.</p>}
    {data && evidence && <>
      <div className="kpi-grid server-dashboard-kpis">
        <DashboardCount label="Active alerts now" value={evidence.activeAlerts} />
        <DashboardCount label="Blocked sessions now" value={evidence.blockedSessions} />
        <DashboardCount label="Deadlocks in window" value={evidence.deadlocks} />
      </div>
      <DashboardBaselineCards targetId={instanceId} series={evidence.series} bands={chartReferences.references.bands}
        fromUtc={data.fromUtc} toUtc={data.toUtc} formatTime={timeLabel} baselineStatus={chartReferences.baselineStatus} />
      <DashboardInvestigationSummary targetId={instanceId} scope={fixedTimelineScope} fromUtc={data.fromUtc} toUtc={data.toUtc}
        issues={issues} events={timelineEvents} formatTime={timeLabel} loading={result.loading || timeline.loading || alerts.list.loading || alerts.detail.loading || serverLifecycle.list.loading || serverLifecycle.detail.loading || agentHistory.loading}
        sourceNotes={timelineNotes} blocking={{ evidence: blockingEvidence, targetRevision, onInspect: inspectBlocker }} />
    </>}
    {timelineWindow&&<><ChartAlignmentControls key={inspectionKey} context={inspectionKey} label="Dashboard" headingLevel={4} {...timelineWindow}
      requestedUtc={requestedChartAt} crosshairUtc={crosshairUtc}
      onAlign={utc=>{if(!active||location.hash!==selectionHash)return;setCrosshairUtc(null);location.hash=chartInspectionHref(selectionHash,utc);}}
      onClear={()=>{if(!active||location.hash!==selectionHash)return;setCrosshairUtc(null);location.hash=chartInspectionHref(selectionHash);}} />
      <DashboardTimeline interactionEnabled={active} contextKey={timelineContextKey} events={timelineEvents} {...timelineWindow} crosshairUtc={crosshairUtc}
      selection={requestedSelection} onSelectionChange={selectTimeline} pinnedExactEvent={pinnedExactEvent}
      loading={timeline.loading||alerts.list.loading||alerts.detail.loading||serverLifecycle.list.loading||serverLifecycle.detail.loading}
      onSelectWindow={window=>{setCrosshairUtc(null);change({range:"custom",from:window.fromUtc,to:window.toUtc});}}
      onSelectTime={setCrosshairUtc} sourceNotes={timelineNotes}/>
      {selectedLifecycleEvent!==undefined&&<button type="button" className="secondary-button server-history-action" aria-controls={alertEvidenceId} onClick={()=>{
        if(!active||location.hash!==selectionHash)return;
        alertEvidence.current?.focus();alertEvidence.current?.scrollIntoView({block:"start"});
      }}>Review selected alert event evidence</button>}</>}
    {selectedServerLifecycleEvent!==undefined&&<button type="button" className="secondary-button server-history-action" aria-controls={serverLifecycleEvidenceId} onClick={()=>{
      if(!active||location.hash!==selectionHash)return;
      serverLifecycleEvidence.current?.focus();serverLifecycleEvidence.current?.scrollIntoView({block:"start"});
    }}>Review selected server lifecycle evidence</button>}
    {data && evidence && <>
      <div className="server-dashboard-charts">
        {chart("engine.batch_requests_per_second", "SQL workload", "Batch requests per second from comparable SQL samples; missing intervals remain gaps.")}
        {chart("blocking.sessions", "Blocked sessions", "Peak distinct blocked sessions per observed bucket; this is not a continuous count.")}
        {chart("engine.sql_scheduler_cpu_percent", "SQL scheduler CPU", "Percent of online SQL scheduler capacity used by non-preemptive workers between samples. This excludes preemptive and hidden-scheduler work; it is not total SQL process CPU.")}
        <ServerWaitTrendPanel interactionKey={String(active)} instanceId={instanceId} fromUtc={data.fromUtc} toUtc={data.toUtc} refresh={refresh}
          markers={markers}
          crosshairUtc={crosshairUtc} onCrosshairChange={setCrosshairUtc}
          onSelectWindow={window => { setCrosshairUtc(null); change({ range: "custom", from: window.fromUtc, to: window.toUtc }); }} />
        {chart("engine.scheduler_runnable_tasks", "SQL scheduler pressure", "Runnable tasks waiting for CPU across visible online SQL schedulers. This is a point-in-time queue, not SQL process CPU percent.")}
        {chart("engine.memory_grants_pending", "SQL memory grant pressure", "Queries waiting for execution memory grants. This is a point-in-time count; a zero means none were waiting at the sample time.")}
        <section className="panel server-dashboard-chart">
          <h4>Memory pressure</h4>
          <ChartEvidenceIndicator label="Memory pressure" series={memorySeries(evidence)} />
          <OverviewChart
      interactionKey={String(active)}
            scrollablePlotLabel="Memory pressure plot"
            series={memorySeries(evidence)}
            previous={memorySeries(previous)}
            references={chartReferences.references}
            fromUtc={data.fromUtc}
            toUtc={data.toUtc}
            markers={markers}
            crosshairUtc={crosshairUtc}
            onCrosshairChange={setCrosshairUtc}
            onSelectWindow={window => { setCrosshairUtc(null); change({ range: "custom", from: window.fromUtc, to: window.toUtc }); }}
          />
          <small>GiB. SQL process physical memory is used memory; OS available memory via SQL and host available memory are free memory. They are separate measurements and need not add up to total host memory.</small>
        </section>
      </div>
      <CurrentBlockingPanel instanceId={instanceId} scope={selectedScope} evidence={blockingEvidence} targetRevision={targetRevision}
        selectedRoot={blockerSelection?.key === blockerKey ? blockerSelection.sessionId : undefined} />
      {evidence.gaps.length > 0 && <details className="panel server-dashboard-gaps"><summary>Collection gaps ({evidence.gaps.length})</summary><ul>{evidence.gaps.map((gap, index) => <li key={index}>{gap}</li>)}</ul></details>}
      <section className="compact-evidence-footer" aria-label="Dashboard evidence status">
        <p>Selected window: {timeLabel(data.fromUtc)} to {timeLabel(data.toUtc)}. Refreshed {timeLabel(data.refreshedAtUtc)}.</p>
        <p>SQL core status: {evidence.collectionState.replaceAll("_", " ")} · {evidence.gaps.length} reported collection gaps. Independent sources can have different coverage.</p>
        <EvidenceDisclosure label="Collection evidence details" attention={evidence.gaps.length > 0 || evidence.collectionState !== "current"}>
          <p>{overviewEvidenceFooter(data, timeLabel)}</p>
          <p>Last SQL core observation: {evidence.lastObservedUtc ? timeLabel(evidence.lastObservedUtc) : "unavailable"}. Source status does not establish a health verdict.</p>
        </EvidenceDisclosure>
        <ReferenceEvidenceStatus {...chartReferences} />
      </section>
      {result.comparisonError && <p role="status">{result.comparisonError}</p>}
    </>}
    {timelineWindow&&<div ref={alertEvidence} id={alertEvidenceId} tabIndex={-1} role="region" aria-label="Alert history evidence" className="server-history-evidence-location">
      <AlertLifecycleHistoryPanel history={alerts} scope={fixedTimelineScope} selectedEvent={selectedLifecycleEvent} onSelectEvent={selectLifecycleEvent}
        onRetainWindow={()=>{if(scope.range!=="custom")change({range:"custom",from:timelineWindow.fromUtc,to:timelineWindow.toUtc});}}/>
    </div>}
    {timelineWindow&&<div ref={serverLifecycleEvidence} id={serverLifecycleEvidenceId} tabIndex={-1} role="region" aria-label="Server lifecycle evidence" className="server-history-evidence-location">
      <ServerLifecycleHistoryPanel history={serverLifecycle} selectedEvent={selectedServerLifecycleEvent} onSelectEvent={selectServerLifecycleEvent}
        onRetainWindow={()=>{if(scope.range!=="custom")change({range:"custom",from:timelineWindow.fromUtc,to:timelineWindow.toUtc});}}/>
    </div>}
    <section className="panel server-agent-evidence" aria-label="SQL Agent first-observed evidence" aria-busy={agentHistory.loading}>
      <h4>SQL Agent failures first observed in this window</h4>
      {agentHistory.loading&&<p role="status">{agentHistory.refreshing&&agentHistory.page
        ? `Refreshing SQL Agent evidence; showing the previously loaded first-page snapshot from ${timeLabel(agentHistory.page.observedAtUtc)} in this same window.`
        : "Loading SQL Agent first-observed evidence for the received dashboard window…"}</p>}
      {agentHistory.error&&<p role="alert">{agentHistory.error}{agentWindowAvailable&&<> <button type="button" disabled={agentHistory.loading} onClick={agentHistory.retry}>Retry SQL Agent history</button></>}</p>}
      {agentHistory.page?<>
        <p className="activity-evidence">SQL Agent evidence: {agentHistory.page.state} · {agentHistory.page.rows.filter(row=>row.countsAsJobFailure).length} job-outcome failures in {agentHistory.page.rows.length} received history rows.
          {agentHistory.page.hasMore?" Additional history pages are available.":" No further history pages are reported by this received snapshot."}
          {agentHistory.page.truncated?" Source capture was truncated; failures may be missing.":""}</p>
        <p className="table-note">First observed by monitoring, not job execution time. Snapshot {timeLabel(agentHistory.page.observedAtUtc)}; received window {timeLabel(agentHistory.page.window.fromUtc)} to {timeLabel(agentHistory.page.window.toUtc)}. Missing collection coverage can hide failures.</p>
        {agentHistory.page.state==="PermissionDenied"&&<p role="status">The collector cannot read SQL Agent history. Have a Target Administrator review the server permission script; refreshing this read does not repair collector permissions.</p>}
        {agentHistory.page.state==="Unsupported"&&<p role="status">SQL Agent history is unsupported for this target. Check the SQL Server edition and Agent availability.</p>}
        {agentHistory.page.state==="NoData"&&<p role="status">No SQL Agent snapshot has been captured. Check that the Agent collector is enabled and can connect.</p>}
        {["Partial","Degraded"].includes(agentHistory.page.state)&&<p role="status">SQL Agent collection coverage is incomplete. Review the collector diagnostics before treating missing rows as evidence that no jobs failed.</p>}
        <details key={agentDisclosureKey} className="server-agent-evidence-browser"><summary>Browse SQL Agent failure evidence</summary>
          <AgentFailureEvidence page={agentHistory.page} compact />
        </details>
        <button type="button" className="secondary-button server-history-action" disabled={agentHistory.loading||!agentWindowAvailable} onClick={agentHistory.returnLatest}>Refresh SQL Agent evidence</button>
      </>:!agentHistory.loading&&!agentHistory.error&&<p role="status">SQL Agent history has no received evidence for this dashboard window. {analyticsWindow
        ? "Verify collector availability and choose a retained range; missing evidence is not an all-clear signal."
        : "A comparable dashboard source window has not been received. Open SQL Agent history to read its retained evidence independently."}</p>}
      {timelineWindow&&<p><a href={healthCheckHref(fixedTimelineScope,instanceId,"agent")}>Open SQL Agent history for this window →</a></p>}
    </section>
  </section>;
}

function DashboardCount({ label, value }: { readonly label: string; readonly value: OverviewValue }) {
  const { mode } = useTimeDisplay();
  const known = value.value !== null && !["stale", "unavailable", "unsupported", "disabled"].includes(value.state);
  return <section className="kpi"><p>{label}</p><strong>{known ? `${value.value!.toLocaleString()}${value.state === "partial" ? "+" : ""}` : "—"}</strong><small>{value.state.replaceAll("_", " ")}{value.observedAtUtc ? ` · ${formatDisplayTime(value.observedAtUtc, mode)}` : ""}</small></section>;
}
