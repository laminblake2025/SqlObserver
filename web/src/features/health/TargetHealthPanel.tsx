import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { Tabs } from "../../components/DiagnosticUi";
import { healthSectionHref } from "../../dashboardModel";
import { overviewHref } from "../overview/overviewModel";
import type { OverviewScope } from "../overview/overviewTypes";
import { useHealthPageRequest, canAppendHealthPage } from "./useHealthPageRequest";
import { healthRecoveryAdvice } from "./healthRecoveryModel";
import { ServerDashboard } from "./ServerDashboard";
import { DatabaseOptionChanges } from "./DatabaseOptionChanges";
import { ServerConfigurationChanges } from "./ServerConfigurationChanges";
import { ServerConfigurationSnapshot } from "./ServerConfigurationSnapshot";
import "./healthNavigationFrame.css";

import { getDatabaseHealth, getTargetHealthEvidence, HealthRequestError, subscribeHealthReadDenials } from "./healthApi";
import {
  coreMetricDefinition,
  formatCoreMetricValue,
  healthSummaryMetricDefinitions,
  latestDimensionlessMetric,
} from "./healthDisplayModel";
import type {
  CollectorCircuitState,
  CollectorHealthReason,
  CollectorHealthSummary,
  CoreMetricSummary,
  DatabaseFileHealthPage,
  DatabaseFileHealthSummary,
  DatabaseHealthPage,
  DatabaseHealthSummary,
  TargetHealthReadResult,
  TargetHealthSource,
  TargetHealthState,
} from "./healthTypes";

const stateLabels: Readonly<Record<TargetHealthState, string>> = {
  pending: "No data yet",
  current: "Current evidence",
  degraded: "Degraded visibility",
  unavailable: "Collection unavailable",
  unsupported: "Unsupported",
  stale: "Stale evidence",
  disabled: "Disabled",
};

const reasonLabels: Readonly<Record<CollectorHealthReason, string>> = {
  none: "No visibility gap reported",
  capability_profile_missing: "Capability discovery has not completed",
  capability_profile_stale: "Capability evidence is stale",
  capability_missing: "A required capability is missing",
  permission_denied: "A required read permission is missing",
  version_unsupported: "This SQL Server version is unsupported",
  platform_unsupported: "This SQL Server platform is unsupported",
  edition_unsupported: "This SQL Server edition is unsupported",
  never_collected: "No observation cycle has completed",
  timed_out: "The bounded observation cycle timed out",
  collection_failed: "The observation cycle failed safely",
  output_invalid: "Collector output failed validation",
  sample_loss: "Some samples were not persisted",
  circuit_open: "Collection is paused by the circuit breaker",
  evidence_stale: "The latest successful evidence is overdue",
};

const circuitLabels: Readonly<Record<CollectorCircuitState, string>> = {
  closed: "Closed",
  open: "Open",
  half_open: "Recovery probe",
};

interface RecoveryContext {
  readonly instanceId: string;
  readonly scope: OverviewScope;
  readonly canGeneratePermissions: boolean;
}

interface LatestHealthAccess {
  readonly key: string;
  mounted: boolean;
  denied: boolean;
  generation: number;
  request?: AbortController;
  recoverRequested: boolean;
}
interface LatestHealthRead {
  readonly access: LatestHealthAccess;
  readonly evidence: TargetHealthReadResult;
  readonly loading: boolean;
  readonly pendingSources: readonly TargetHealthSource[];
  readonly message?: string;
}
const latestHealthSources: readonly TargetHealthSource[] = ["target", "databases", "files"];
const latestHealthSourceLabels: Readonly<Record<TargetHealthSource, string>> = {
  target: "Core and collector health", databases: "Database inventory", files: "Logical-file inventory",
};

export interface TargetHealthPanelProps {
  readonly embedded?: boolean;
  readonly instanceId: string;
  readonly displayName: string;
  readonly onClose: () => void;
  readonly scope: OverviewScope;
  readonly refresh: number;
  readonly healthRefresh: number;
  readonly blockingRefresh: number;
  readonly selectedDatabaseId?: number;
  readonly onSelectDatabase: (databaseId: number | undefined) => void;
  readonly canManageConfiguration: boolean;
  readonly canManageLifecycleCollection?: boolean;
  readonly accessContext?: string;
  readonly targetRevision: number;
  readonly selectionHash: string;
  readonly selectedConfigurationRunId?: string;
  readonly selectedSection?: "summary" | "databases" | "collection";
  readonly onSelectSection?: (section: "summary" | "databases" | "collection") => void;
}

export function TargetHealthPanel({ embedded = false, instanceId, displayName, onClose, scope, refresh, healthRefresh, blockingRefresh, selectedDatabaseId, onSelectDatabase, canManageConfiguration, canManageLifecycleCollection = false, accessContext, targetRevision, selectionHash, selectedSection, selectedConfigurationRunId, onSelectSection }: TargetHealthPanelProps) {
  const formatTimestamp = useTimeFormatter();
  const evidenceKey = JSON.stringify([instanceId.toLowerCase(), targetRevision]);
  const access = useMemo<LatestHealthAccess>(() => ({ key: evidenceKey, mounted: false,
    denied: false, generation: 0, recoverRequested: false }), [evidenceKey]);
  const currentAccess = useRef(access); currentAccess.current = access;
  const [received, setReceived] = useState<LatestHealthRead>();
  const read = received?.access === access ? received : undefined;
  const denied = access.denied;
  const evidence = denied ? undefined : read?.evidence;
  const message = read?.message;
  const loading = read?.loading ?? !denied;
  const [retry, setRetry] = useState(0);
  const [localTab, setTab] = useState<"summary" | "databases" | "collection">(selectedSection ?? "summary");
  // Route-owned sections are resolved during render, including an absent URL
  // selection restored by Back. Local state remains available to standalone consumers.
  const tab = onSelectSection ? selectedSection ?? "summary" : localTab;
  useEffect(() => { if (!onSelectSection && selectedSection) setTab(selectedSection); }, [selectedSection, onSelectSection]);
  const snapshot = evidence?.target;
  const recoveryContext: RecoveryContext = { instanceId, scope, canGeneratePermissions: canManageConfiguration };

  const refuseAccess = useCallback((error: HealthRequestError) => {
    if (currentAccess.current !== access || !access.mounted || access.denied && !access.request) return;
    access.denied = true;
    access.generation++;
    const request = access.request; access.request = undefined;
    request?.abort(error);
    setReceived({ access, evidence: { errors: {} }, loading: false, pendingSources: [], message: getSafeMessage(error) });
  }, [access]);
  const dashboardAccessDenied = useCallback((status: 401 | 403) => {
    refuseAccess(new HealthRequestError(status === 401
      ? "Your health read access could not be verified. Restore access and explicitly retry."
      : "You are not authorized to view health for this target.", status));
  }, [refuseAccess]);
  useLayoutEffect(() => {
    access.mounted = true;
    const unsubscribe = subscribeHealthReadDenials(instanceId, refuseAccess);
    return () => {
      unsubscribe(); access.mounted = false; access.generation++;
      access.request?.abort(); access.request = undefined;
    };
  }, [access, refuseAccess]);

  useEffect(() => {
    const recovery = access.recoverRequested; access.recoverRequested = false;
    if (access.request || access.denied && !recovery) return;
    const controller = new AbortController();
    const generation = ++access.generation; access.request = controller;
    const current = () => access.mounted && currentAccess.current === access &&
      access.generation === generation && !controller.signal.aborted;
    setReceived(previous => ({ access, evidence: previous?.access === access ? previous.evidence : { errors: {} },
      loading: true, pendingSources: latestHealthSources, message: undefined }));
    void getTargetHealthEvidence(instanceId, controller.signal, {
      onAccessDenied: refuseAccess,
      onSource: source => {
        if (!current() || recovery || access.denied) return;
        setReceived(previous => {
          if (!current() || access.denied) return previous;
          const before = previous?.access === access ? previous.evidence : { errors: {} };
          const next = source.error === undefined
            ? { ...before, [source.source]: source.value, errors: { ...before.errors, [source.source]: undefined } }
            : { ...before, errors: { ...before.errors, [source.source]: source.error } };
          return { access, evidence: next, loading: true,
            pendingSources: (previous?.access === access ? previous.pendingSources : latestHealthSources).filter(value => value !== source.source) };
        });
      },
    }).then(next => {
      if (!current()) return;
      if (recovery) {
        const verified = Boolean(next.target || next.databases || next.files);
        if (verified) access.denied = false;
        setReceived({ access, evidence: next, loading: false, pendingSources: [],
          message: verified ? undefined : "No latest health source was verified. Restore read access and retry health access." });
      } else setReceived(previous => current() && previous?.access === access
        ? { ...previous, loading: false, pendingSources: [] } : previous);
    }).catch(error => {
      if (!current()) return;
      if (error instanceof HealthRequestError && (error.status === 401 || error.status === 403)) { refuseAccess(error); return; }
      setReceived(previous => ({ access, evidence: previous?.access === access ? previous.evidence : { errors: {} },
        loading: false, pendingSources: [], message: getSafeMessage(error) }));
    }).finally(() => { if (access.request === controller) access.request = undefined; });
  }, [access, healthRefresh, retry]);
  const retryHealth = () => {
    if (!access.mounted || access.request || currentAccess.current !== access) return;
    access.recoverRequested = access.denied; setRetry(value => value + 1);
  };

  return (
    <section className="health-screen" aria-labelledby="health-heading">
      {embedded ? <h2 id="health-heading" className="health-frame-accessible-heading">Server diagnostics for {displayName}</h2> : <div className="health-heading-row">
        <div>
          <p className="eyebrow">Server summary · target-scoped snapshot</p>
          <h2 id="health-heading">Server summary for {displayName}</h2>
        </div>
        <button className="secondary-button" onClick={onClose} type="button">
          Close
        </button>
      </div>}
      {embedded && denied && <p role="alert">Health read access was denied. Protected evidence was removed. <button type="button" disabled={loading} onClick={retryHealth}>Retry health access</button></p>}
      {!denied && <div className="health-dashboard-pane" key={`dashboard:${evidenceKey}`} hidden={tab !== "summary"}>
        <ServerDashboard active={tab === "summary"} instanceId={instanceId} displayName={displayName} scope={scope} refresh={refresh}
          targetRevision={targetRevision} selectionHash={selectionHash} canReadRules={canManageConfiguration}
          canManageLifecycleCollection={canManageLifecycleCollection} accessContext={accessContext}
          onAccessDenied={dashboardAccessDenied}
          blockingRefresh={blockingRefresh}
          configurationCollectorState={snapshot?.collectors.find(item => item.collectorId === "engine.configurations")?.state} />
      </div>}
      <details className="supporting-evidence latest-health-disclosure" open={!embedded||tab!=="summary"||selectedConfigurationRunId!==undefined?true:undefined}><summary>Latest health, databases & configuration</summary>
      <div className="health-navigation-frame">
        <Tabs
            label="Server summary sections"
            tabs={[{ value: "summary", label: "Dashboard" }, { value: "databases", label: "Databases" }, { value: "collection", label: "Collection health" }]}
            value={tab}
            onChange={(value) => { const section = value as "summary" | "databases" | "collection"; if (onSelectSection) onSelectSection(section); else setTab(section); }}
        />
        {loading && <p role="status">Loading health evidence…</p>}
        {denied && <p>Health read access was denied. Protected latest snapshots, dashboard and health-history evidence were removed. Automatic health reads remain stopped until an explicit retry.</p>}
        {message !== undefined && <p role="alert" className="status-message">{message}</p>}
        {!denied && latestHealthSources.map(source => {
          const receipt = evidence?.[source], error = evidence?.errors[source];
          const pending = read?.pendingSources.includes(source) ?? true;
          const label = latestHealthSourceLabels[source];
          return <div key={source} role="group" aria-label={`${label} read status`}>
            {pending && <p role="status">{label}: reading latest evidence…{receipt ? ` The received snapshot checked at ${formatTimestamp(receipt.repositoryTimeUtc)} remains visible.` : " No validated snapshot has been received yet."}</p>}
            {error && <p role="alert">{label}: {error} {receipt
              ? `Previously received evidence checked at ${formatTimestamp(receipt.repositoryTimeUtc)} remains visible; this source was not refreshed.`
              : "No validated snapshot is available for this source. Missing evidence is not an all-clear signal."}</p>}
          </div>;
        })}
        {(!embedded || !denied) && (denied || message || latestHealthSources.some(source => evidence?.errors[source])) && <button type="button" className="secondary-button" disabled={loading} onClick={retryHealth}>{denied ? "Retry health access" : "Retry latest health evidence"}</button>}
        {!denied && !snapshot && !loading && <p role="status">No latest core and collector health snapshot has been received for this server. Independent database, file and history sources can still be available; missing evidence is not an all-clear signal.</p>}
      </div>
      {!denied && <section className="health-latest-pane" key={`latest:${evidenceKey}`} aria-labelledby="health-latest-heading">
        <h3 id="health-latest-heading">{tab === "summary" ? "Latest server snapshots" : tab === "databases" ? "Latest database and logical-file snapshots" : "Latest collection health"}</h3>
        <p className="table-note">{tab === "summary"
          ? "Core metrics and configuration snapshots show their latest evidence; they do not rewind with the dashboard. Configuration changes follow the selected time window."
          : tab === "databases"
            ? "Database and logical-file summaries show their latest snapshots. Database-option changes follow the selected time window."
            : "Collector health shows the latest snapshot, independently of the selected historical window. Source observation and repository times are shown alongside each observation."}</p>
        {snapshot && <>
          <div className="health-summary">
            <span className={`health-state health-${snapshot.state}`}>
              {stateLabels[snapshot.state]}
            </span>
            <p>
              Evaluated using repository time: {formatTimestamp(snapshot.repositoryTimeUtc)}
            </p>
          </div>
          {tab === "summary" ? <>
            <HealthMetricStrip metrics={snapshot.coreMetrics} />
            <p className="detail-note">For unavailable or incomplete core metrics, review <a href={healthSectionHref(scope, instanceId, "collection")}>collection health</a> for source reasons and recovery guidance.</p>
            <details className="supporting-evidence"><summary>All latest core metric samples</summary>
              <CoreMetrics metrics={snapshot.coreMetrics} />
            </details>
            <details className="supporting-evidence"><summary>Collector snapshot summary</summary><div className="collector-grid">{snapshot.collectors.map((collector) => <CollectorHealthCard collector={collector} recoveryContext={recoveryContext} key={collector.collectorId} />)}</div></details>
          </> : null}
          {tab === "collection" ? <div className="collector-grid">{snapshot.collectors.map((collector) => <CollectorHealthCard collector={collector} recoveryContext={recoveryContext} key={collector.collectorId} />)}</div> : null}
        </>}
        {tab === "summary" && <>
          <ServerConfigurationSnapshot instanceId={instanceId} refresh={refresh} canManage={canManageConfiguration} />
          <ServerConfigurationChanges selectedRunId={selectedConfigurationRunId} targetRevision={targetRevision} instanceId={instanceId} scope={scope} refresh={refresh}
            collector={snapshot?.collectors.find(item => item.collectorId === "engine.configurations")} canManage={canManageConfiguration} />
        </>}
        {tab === "databases" && <>
          <DatabaseOptionChanges instanceId={instanceId} scope={scope} refresh={refresh} />
          {evidence?.databases && <DatabaseHealth targetRevision={targetRevision} instanceId={instanceId} firstPage={evidence.databases} recoveryContext={recoveryContext} selectedDatabaseId={selectedDatabaseId} onSelectDatabase={onSelectDatabase} />}
          {evidence?.files && <DatabaseFileHealth page={evidence.files} recoveryContext={recoveryContext} selectedDatabaseId={selectedDatabaseId} />}
        </>}
      </section>}</details>
    </section>
  );
}

function HealthMetricStrip({ metrics }: { readonly metrics: readonly CoreMetricSummary[] }) {
  const formatTimestamp = useTimeFormatter();
  return <div className="health-metric-strip">{healthSummaryMetricDefinitions.map((definition) => {
    const metric = latestDimensionlessMetric(metrics, definition.metricId);
    return <section className="health-metric" key={definition.metricId}>
      <p>{definition.label}</p>
      <strong>{metric === undefined ? "Unavailable" : formatCoreMetricValue(metric, definition)}</strong>
      <small>{metric === undefined
        ? "No sample available"
        : `Observed ${formatTimestamp(metric.observedAtUtc)}`}</small>
    </section>;
  })}</div>;
}

export function DatabaseHealth({ instanceId, targetRevision, firstPage, recoveryContext, selectedDatabaseId, onSelectDatabase }: { readonly instanceId: string; readonly targetRevision: number; readonly firstPage: DatabaseHealthPage; readonly recoveryContext: RecoveryContext; readonly selectedDatabaseId?: number; readonly onSelectDatabase: (databaseId: number | undefined) => void }) {
  const formatTimestamp = useTimeFormatter();
  const scope = recoveryContext.scope;
  const key = JSON.stringify([instanceId.toLowerCase(), targetRevision, scope.range, scope.from, scope.to, selectedDatabaseId]);
  type Trail = { key: string; pages: readonly DatabaseHealthPage[]; index: number; retained: boolean; searched: boolean; missing: boolean };
  const [trail, setTrail] = useState<Trail | undefined>(() => ({ key, pages: [firstPage], index: 0,
    retained: selectedDatabaseId !== undefined, searched: false, missing: false }));
  const current = trail?.key === key ? trail : undefined;
  const page = current?.pages[current.index];
  const pageIndex = current?.index ?? 0;
  const commands = useHealthPageRequest(key);
  const [failure, setFailure] = useState<string>();
  const error = failure === key;
  const loading = commands.pending || (!page && !error);
  const [limitKey, setLimitKey] = useState<string>();
  const atLimit = limitKey === key;
  const selectionMissing = current?.missing ?? false;

  async function readLatest(initial?: DatabaseHealthPage, explicit = true) {
    const command = commands.begin();
    if (!command) return;
    setFailure(undefined);
    if (current && (explicit || selectedDatabaseId !== undefined)) setTrail({ ...current, retained: true, searched: true });
    try {
      const first = initial ?? await getDatabaseHealth(instanceId, command.signal);
      const pages = [first];
      if (!command.current()) return;
      let missing = selectedDatabaseId !== undefined && !first.items.some(item => item.databaseId === selectedDatabaseId);
      const visited = new Set<string>();
      while (missing && pages.length < 20 && pages.at(-1)?.nextCursor) {
        const cursor = pages.at(-1)!.nextCursor!;
        if (visited.has(cursor)) throw new Error("Database paging repeated a cursor.");
        visited.add(cursor);
        const next = await getDatabaseHealth(instanceId, command.signal, cursor);
        if (!command.current()) return;
        pages.push(next);
        missing = !next.items.some(item => item.databaseId === selectedDatabaseId);
      }
      if (command.current()) {
        setLimitKey(undefined);
        setTrail({ key, pages, index: pages.length - 1, retained: selectedDatabaseId !== undefined, searched: true, missing });
      }
    } catch (error) { if (command.current()) {
      setFailure(key);
      if (error instanceof HealthRequestError && (error.status === 401 || error.status === 403)) setTrail(undefined);
    } }
    finally { command.finish(); }
  }

  useEffect(() => {
    if (!current) { void readLatest(undefined, false); return; }
    if (selectedDatabaseId !== undefined && !current.searched) { void readLatest(firstPage, false); return; }
    // A polled first page belongs only to latest mode. It never replaces a retained cursor trail.
    if (!current.retained && !commands.pending)
      setTrail({ key, pages: [firstPage], index: 0, retained: false, searched: true, missing: false });
  }, [key, firstPage]);

  useEffect(() => {
    if (selectedDatabaseId !== undefined && page?.items.some(item => item.databaseId === selectedDatabaseId))
      document.getElementById(`database-health-${selectedDatabaseId}`)?.scrollIntoView({ block: "center" });
  }, [page, selectedDatabaseId]);

  async function navigate(index: number) {
    if (!current || commands.pending) return;
    if (current.pages[index]) {
      setTrail({ ...current, retained: true, index }); setFailure(undefined); return;
    }
    if (!canAppendHealthPage(current.pages)) { setLimitKey(key); setTrail({ ...current, retained: true }); return; }
    const cursor = page?.nextCursor;
    if (!cursor) return;
    const command = commands.begin();
    if (!command) return;
    setTrail({ ...current, retained: true }); setFailure(undefined);
    try {
      const next = await getDatabaseHealth(instanceId, command.signal, cursor);
      if (!command.current()) return;
      if (!canAppendHealthPage(current.pages, next.items.length)) { setLimitKey(key); return; }
      if (next.nextCursor && current.pages.some(item => item.nextCursor === next.nextCursor))
        throw new Error("Database paging repeated a cursor.");
      setTrail({ ...current, pages: [...current.pages.slice(0, index), next], index, retained: true });
    } catch (error) { if (command.current()) {
      setFailure(key);
      if (error instanceof HealthRequestError && (error.status === 401 || error.status === 403)) setTrail(undefined);
    } }
    finally { command.finish(); }
  }

  if (!page) return <section className="bounded-health-section" aria-labelledby="database-health-heading" aria-busy={loading}>
    <h4 id="database-health-heading">Database health</h4>
    {error ? <p role="alert">Database page is unavailable. <button type="button" onClick={() => void readLatest()}>Return to latest database snapshot</button></p>
      : <p role="status">Loading database page…</p>}
  </section>;
  return (
    <section className="bounded-health-section" aria-labelledby="database-health-heading" aria-busy={loading}>
      <div className="bounded-health-heading">
        <h4 id="database-health-heading">Database health</h4>
        <span>Repository time: {formatTimestamp(page.repositoryTimeUtc)}</span>
      </div>
      <PageCollectorHealth collector={page.collector} recoveryContext={recoveryContext} />
      <p className="table-note">Database context applies to database-scoped views. This inventory remains the loaded server snapshot.</p>
      {selectedDatabaseId !== undefined ? <p className="table-note">Selected database ID {selectedDatabaseId}. <button type="button" className="secondary-button" onClick={() => onSelectDatabase(undefined)}>Clear database context</button></p> : null}
      <p className="page-boundary">Page {pageIndex + 1} · {page.items.length} databases{page.nextCursor ? " · More available" : " · End of snapshot"}</p>
      {loading ? <p role="status">Loading database page…</p> : null}
      {error ? <p role="alert">Database page is unavailable or its snapshot changed. The loaded page remains available; return to latest to retry.</p> : null}
      {current?.retained ? <p className="table-note">Retained database snapshot. Workspace refresh does not replace this investigated page or its cursor trail.</p> : null}
      {atLimit ? <p role="status">Browser investigation limit reached: 400 pages or 10,000 database rows. Return to latest or change context to start a new investigation.</p> : null}
      {selectionMissing ? <p role="status">Database ID {selectedDatabaseId} is not in the first 500 database rows. Browse the remaining pages below.</p> : null}
      {page.items.length === 0 ? (
        <p className="empty-state">
          {isCurrentLossFree(page.collector)
            ? "No database rows were reported by a current, loss-free collection."
            : "Database rows cannot be treated as complete for this snapshot."}
        </p>
      ) : (
        <div className="bounded-health-grid">
          {page.items.map((database) => (
            <div id={`database-health-${database.databaseId}`} key={database.databaseId}><DatabaseHealthCard database={database} selected={database.databaseId === selectedDatabaseId} onSelect={() => onSelectDatabase(database.databaseId)} /></div>
          ))}
        </div>
      )}
      <nav aria-label="Database health pages" className="pager">
        <button type="button" disabled={commands.pending} onClick={() => void readLatest()}>Return to latest database snapshot</button>
        <button type="button" disabled={loading || pageIndex === 0} onClick={() => void navigate(0)}>First database page</button>{" "}
        <button type="button" disabled={loading || pageIndex === 0} onClick={() => void navigate(pageIndex - 1)}>Previous database page</button>{" "}
        <button type="button" disabled={loading || !page.nextCursor || atLimit} onClick={() => void navigate(pageIndex + 1)}>Next database page</button>
      </nav>
    </section>
  );
}

function DatabaseHealthCard({ database, selected, onSelect }: { readonly database: DatabaseHealthSummary; readonly selected: boolean; readonly onSelect: () => void }) {
  const formatTimestamp = useTimeFormatter();
  return (
    <article className={selected ? "bounded-health-card selected-row" : "bounded-health-card"}>
      <div className="collector-title-row" style={{ flexWrap: "wrap" }}>
        <h5 style={{ minWidth: 0, overflowWrap: "anywhere", flex: "1 1 160px" }}>{database.name}</h5>
        <span className={`health-state health-${database.collector.state}`}>
          {stateLabels[database.collector.state]}
        </span>
      </div>
      <VisibilityGap collector={database.collector} />
      <button type="button" className="secondary-button" aria-pressed={selected} onClick={onSelect}>Use {database.name} as database context</button>
      <dl className="health-facts">
        <HealthFact label="Database ID" value={String(database.databaseId)} />
        <HealthFact label="State" value={formatToken(database.state)} />
        <HealthFact label="Recovery" value={formatToken(database.recoveryModel)} />
        <HealthFact label="Access" value={formatToken(database.userAccess)} />
        <HealthFact label="Read only" value={database.isReadOnly ? "Yes" : "No"} />
        <HealthFact label="Compatibility" value={String(database.compatibilityLevel)} />
        <HealthFact label="Log reuse wait" value={database.logReuseWaitDescription === null
          ? "Not collected" : formatToken(database.logReuseWaitDescription)} />
        <HealthFact label="Observed" value={formatTimestamp(database.observedAtUtc)} />
      </dl>
    </article>
  );
}

export function DatabaseFileHealth({ page, recoveryContext, selectedDatabaseId }: { readonly page: DatabaseFileHealthPage; readonly recoveryContext: RecoveryContext; readonly selectedDatabaseId?: number }) {
  const formatTimestamp = useTimeFormatter();
  const visibleFiles = selectedDatabaseId === undefined ? page.items : page.items.filter(file => file.databaseId === selectedDatabaseId);
  return (
    <section className="bounded-health-section" aria-labelledby="file-health-heading">
      <div className="bounded-health-heading">
        <h4 id="file-health-heading">Logical-file health</h4>
        <span>Repository time: {formatTimestamp(page.repositoryTimeUtc)}</span>
      </div>
      <PageCollectorHealth collector={page.collector} recoveryContext={recoveryContext} />
      {selectedDatabaseId !== undefined ? <p className="table-note">Showing {visibleFiles.length} of {page.items.length} loaded logical files for database ID {selectedDatabaseId}. Other files may be on another page.</p> : null}
      {visibleFiles.length === 0 ? (
        <p className="empty-state">
          {selectedDatabaseId !== undefined ? "No logical-file rows for this database are present in the bounded first page. Open Resources to browse the file pages." : isCurrentLossFree(page.collector)
            ? "No logical-file rows were reported by a current, loss-free collection."
            : "Logical-file rows cannot be treated as complete for this snapshot."}
        </p>
      ) : (
        <div className="bounded-health-grid">
          {visibleFiles.map((file) => (
            <DatabaseFileHealthCard
              file={file}
              key={`${String(file.databaseId)}:${String(file.fileId)}`}
            />
          ))}
        </div>
      )}
      {page.nextCursor === null ? null : (
        <p className="page-boundary">Additional logical files exist; this view shows the bounded first page.</p>
      )}
    </section>
  );
}

function DatabaseFileHealthCard({ file }: { readonly file: DatabaseFileHealthSummary }) {
  const formatTimestamp = useTimeFormatter();
  return (
    <article className="bounded-health-card">
      <div className="collector-title-row" style={{ flexWrap: "wrap" }}>
        <h5 style={{ minWidth: 0, overflowWrap: "anywhere", flex: "1 1 160px" }}>{file.logicalName}</h5>
        <span className={`health-state health-${file.collector.state}`}>
          {stateLabels[file.collector.state]}
        </span>
      </div>
      <VisibilityGap collector={file.collector} />
      <dl className="health-facts">
        <HealthFact label="Database / file ID" value={`${String(file.databaseId)} / ${String(file.fileId)}`} />
        <HealthFact label="Type" value={formatToken(file.fileType)} />
        <HealthFact label="State" value={formatToken(file.state)} />
        <HealthFact label="Size bytes" value={formatExactInteger(file.sizeBytes)} />
        <HealthFact
          label="Maximum bytes"
          value={file.maximumSizeBytes === null ? "Not reported" : formatExactInteger(file.maximumSizeBytes)}
        />
        <HealthFact label="Growth bytes" value={formatExactInteger(file.growthBytes)} />
        <HealthFact label="Growth percent" value={`${String(file.growthPercent)}%`} />
        <HealthFact label="Reads" value={formatExactInteger(file.readCount)} />
        <HealthFact label="Writes" value={formatExactInteger(file.writeCount)} />
        <HealthFact label="Bytes read" value={formatExactInteger(file.bytesRead)} />
        <HealthFact label="Bytes written" value={formatExactInteger(file.bytesWritten)} />
        <HealthFact label="I/O stall ms" value={formatExactInteger(file.ioStallMilliseconds)} />
        <HealthFact label="Read stall ms (total)" value={file.readStallMilliseconds === null ? "Not reported" : formatExactInteger(file.readStallMilliseconds)} />
        <HealthFact label="Write stall ms (total)" value={file.writeStallMilliseconds === null ? "Not reported" : formatExactInteger(file.writeStallMilliseconds)} />
        <HealthFact label="Observed" value={formatTimestamp(file.observedAtUtc)} />
      </dl>
    </article>
  );
}

function VisibilityGap({ collector }: { readonly collector: CollectorHealthSummary }) {
  return collector.hasVisibilityGap ? (
    <p className="visibility-gap">
      <strong>Visibility gap:</strong> {reasonLabels[collector.reason]}
    </p>
  ) : null;
}

function PageCollectorHealth({ collector, recoveryContext }: { readonly collector: CollectorHealthSummary; readonly recoveryContext: RecoveryContext }) {
  return (
    <div className="page-collector-health">
      <div className="collector-title-row" style={{ flexWrap: "wrap" }}>
        <strong style={{ minWidth: 0, overflowWrap: "anywhere", flex: "1 1 160px" }}>{collector.collectorId}</strong>
        <span className={`health-state health-${collector.state}`}>
          {stateLabels[collector.state]}
        </span>
      </div>
      <p>
        <strong>Collector reason:</strong> {reasonLabels[collector.reason]}
      </p>
      <p>
        <strong>Sample loss:</strong> {formatLoss(collector)}
      </p>
      <CollectorRecovery collector={collector} context={recoveryContext} />
    </div>
  );
}

function CoreMetrics({ metrics }: { readonly metrics: readonly CoreMetricSummary[] }) {
  const formatTimestamp = useTimeFormatter();
  return (
    <section className="core-metrics" aria-labelledby="core-metrics-heading">
      <h4 id="core-metrics-heading">Latest core metrics</h4>
      {metrics.length === 0 ? (
        <p className="empty-state">No core metric samples are available.</p>
      ) : (
        <dl className="core-metric-grid">
          {metrics.map((metric) => (
            <div key={metric.sampleId}>
              <dt>{coreMetricDefinition(metric.metricId).label}</dt>
              <dd>{formatCoreMetricValue(metric)}</dd>
              <dd className="metric-observed">Metric ID: {metric.metricId} · Observed {formatTimestamp(metric.observedAtUtc)}</dd>
              {metric.dimensions.length === 0 ? null : (
                <dd className="metric-dimensions">
                  {metric.dimensions.map((dimension) => (
                    <span key={dimension.key}>
                      {dimension.key}={dimension.value}
                    </span>
                  ))}
                </dd>
              )}
            </div>
          ))}
        </dl>
      )}
    </section>
  );
}

function CollectorHealthCard({ collector, recoveryContext }: { readonly collector: CollectorHealthSummary; readonly recoveryContext: RecoveryContext }) {
  const formatTimestamp = useTimeFormatter();
  const formatOptionalTimestamp = (value: string | null) => formatTimestamp(value);
  return (
    <article className="collector-health-card">
      <div className="collector-title-row" style={{ flexWrap: "wrap" }}>
        <h4 style={{ minWidth: 0, overflowWrap: "anywhere", flex: "1 1 160px" }}>{collector.collectorId}</h4>
        <span className={`health-state health-${collector.state}`}>
          {stateLabels[collector.state]}
        </span>
      </div>
      <VisibilityGap collector={collector} />
      <CollectorRecovery collector={collector} context={recoveryContext} />
      <dl className="health-facts">
        <HealthFact label="Last success" value={formatOptionalTimestamp(collector.lastSuccessAtUtc)} />
        <HealthFact label="Last attempt" value={formatOptionalTimestamp(collector.lastAttemptAtUtc)} />
        <HealthFact label="Scheduled" value={formatTimestamp(collector.scheduledAtUtc)} />
        <HealthFact label="Next due" value={formatTimestamp(collector.nextDueAtUtc)} />
        <HealthFact label="Last outcome" value={collector.lastExecutionOutcome ?? "Not observed"} />
        <HealthFact label="Circuit" value={circuitLabels[collector.circuitState]} />
        <HealthFact
          label="Duration"
          value={
            collector.durationMilliseconds === null
              ? "Not observed"
              : `${String(collector.durationMilliseconds)} ms`
          }
        />
        <HealthFact label="Retries" value={String(collector.retryCount)} />
        <HealthFact label="Source rows" value={String(collector.sourceRows)} />
        <HealthFact label="Output rows" value={String(collector.outputRows)} />
        <HealthFact label="Inserted rows" value={String(collector.insertedRows)} />
        <HealthFact label="Duplicate rows" value={String(collector.duplicateRows)} />
        <HealthFact label="Rejected rows" value={String(collector.rejectedRows)} />
        <HealthFact label="Response bytes" value={String(collector.responseBytes)} />
        <HealthFact label="Persisted bytes" value={String(collector.persistedBytes)} />
        <HealthFact label="Sample loss" value={formatLoss(collector)} />
      </dl>
    </article>
  );
}

function CollectorRecovery({ collector, context }: { readonly collector: CollectorHealthSummary; readonly context: RecoveryContext }) {
  const advice = healthRecoveryAdvice(collector);
  if (advice === undefined) return null;
  return <div className="health-recovery">
    <p className="detail-note" style={{ fontSize: "14px", lineHeight: 1.5 }}><strong>Recovery:</strong> {advice.message}</p>
    <p className="detail-note" style={{ fontSize: "14px", lineHeight: 1.5 }}>
      <a href={healthSectionHref(context.scope, context.instanceId, "collection")}>Review collection health</a>
      {advice.permissions ? <> · {context.canGeneratePermissions
        ? <a href={overviewHref(context.scope, "permissions", context.instanceId)}>Generate permission script</a>
        : <span>Target Administrator access unlocks the permission script generator.</span>}</> : null}
    </p>
  </div>;
}

function HealthFact({ label, value }: { readonly label: string; readonly value: string }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd>{value}</dd>
    </div>
  );
}

function formatToken(value: string): string {
  return value
    .split("_")
    .map((part) => `${part.charAt(0).toUpperCase()}${part.slice(1)}`)
    .join(" ");
}

function formatExactInteger(value: string): string {
  try {
    return BigInt(value).toLocaleString();
  } catch {
    return "Invalid counter";
  }
}

function formatLoss(collector: CollectorHealthSummary): string {
  if (collector.sampleLossKind === null) {
    return "None reported";
  }

  const itemQualifier = collector.lossCountIsExact === true ? "" : "at least ";
  const lostItems = collector.minimumLostItems ?? 0;
  const lostBytes = collector.minimumLostBytes ?? 0;
  return `${collector.sampleLossKind}: ${itemQualifier}${String(lostItems)} items, ${String(lostBytes)} bytes`;
}

function isCurrentLossFree(collector: CollectorHealthSummary): boolean {
  return (
    collector.state === "current" &&
    collector.reason === "none" &&
    collector.lastExecutionOutcome === "succeeded" &&
    collector.hasVisibilityGap === false &&
    collector.sampleLossKind === null &&
    collector.minimumLostItems === null &&
    collector.lossCountIsExact === null &&
    collector.minimumLostBytes === null &&
    collector.rejectedRows === 0
  );
}

function getSafeMessage(error: unknown): string {
  return error instanceof HealthRequestError
    ? error.message
    : "The health request failed safely.";
}
