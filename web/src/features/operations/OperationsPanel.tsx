import { useMemo, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { AnalyticsSurfacePanel } from "../analytics/AnalyticsSurfacePanel";
import { EvidenceStatus, Tabs } from "../../components/DiagnosticUi";
import { EvidenceTable } from "../../components/EvidenceTable";
import { healthSectionHref } from "../../dashboardModel";
import type { OperationsState } from "./operationsState";
import { useOperationalProjection } from "./useOperationalProjection.ts";
import "./operationsProjection.css";
import { buildOperationsRenderModel, type OperationsRenderCard } from "./operationsRenderModel";
import type { OperationalKind, OperationalPage } from "./operationsTypes";
import type { OverviewScope } from "../overview/overviewTypes";
import { overviewWindow } from "../overview/overviewModel";
import { AvailabilityEvidencePanel } from "./AvailabilityEvidencePanel";
import { BackupAssessmentPanel } from "./BackupAssessmentPanel";
import { AgentFailureHistoryPanel } from "./AgentFailureHistoryPanel";
import { healthCheckHref, type HealthCheckTab } from "./agentHistoryModel.ts";

type OperationsTab = HealthCheckTab;

// Existing projection labels remain visible within the grouped tabs: Backups, SQL Agent failures, TempDB summary, TempDB files, Availability Group replicas, and Availability Group databases.

const tabs: readonly { readonly value: OperationsTab; readonly label: string }[] = [
  { value: "backups", label: "Backups" },
  { value: "agent", label: "SQL Agent" },
  { value: "tempdb", label: "TempDB" },
  { value: "availability", label: "Availability groups" },
  { value: "replication", label: "Replication" },
];

const tabKinds: Readonly<Record<Exclude<OperationsTab, "replication">, readonly OperationalKind[]>> = {
  backups: ["backups"],
  agent: ["agent"],
  tempdb: ["tempdb-summary", "tempdb-files"],
  availability: ["ag-replicas", "ag-databases"],
};

export function OperationsPanel({ instanceId, targetRevision, initialTab = "backups", refresh, scope, canManageBackupPolicies = false }: { readonly instanceId: string; readonly targetRevision: number; readonly initialTab?: OperationsTab; readonly refresh: number; readonly scope: OverviewScope; readonly canManageBackupPolicies?: boolean }) {
  const tabKey = JSON.stringify([instanceId.toLowerCase(), initialTab]);
  const [selectedTab, setSelectedTab] = useState<{ key: string; value: OperationsTab }>();
  const tab = selectedTab?.key === tabKey ? selectedTab.value : initialTab;
  const context = JSON.stringify([instanceId.toLowerCase(), targetRevision, scope.range, scope.from, scope.to, scope.databaseId]);
  const projections = {
    backups: useOperationalProjection("backups", instanceId, context, refresh),
    "tempdb-summary": useOperationalProjection("tempdb-summary", instanceId, context, refresh),
    "tempdb-files": useOperationalProjection("tempdb-files", instanceId, context, refresh),
    "ag-replicas": useOperationalProjection("ag-replicas", instanceId, context, refresh),
    "ag-databases": useOperationalProjection("ag-databases", instanceId, context, refresh),
  };
  const cards: OperationsState = { agent: { loading: false }, backups: projections.backups.card,
    "tempdb-summary": projections["tempdb-summary"].card, "tempdb-files": projections["tempdb-files"].card,
    "ag-replicas": projections["ag-replicas"].card, "ag-databases": projections["ag-databases"].card };
  const model = buildOperationsRenderModel(cards);
  const renderCard = (kind: OperationalKind) => kind === "agent" ? null :
    <OperationCard card={model.cards[kind]} key={kind} actions={projections[kind]} />;
  const activeKinds = tab === "replication" ? [] : tabKinds[tab];
  return <section className="operations-screen" aria-label="Health checks">
    <div className="screen-intro"><div><p className="eyebrow">Database operations</p><h2>Health checks</h2><p>Investigate backup freshness, job failures, TempDB pressure, availability, and replication from collected evidence.</p></div></div>
    <Tabs label="Health check categories" tabs={tabs} value={tab} revealFocusedTab onChange={(value) => { setSelectedTab({ key: tabKey, value: value as OperationsTab }); location.hash = healthCheckHref(scope, instanceId, value as OperationsTab); }} />
    <nav className="toolbar" aria-label="Additional health checks">
      <a className="secondary-button" href={healthSectionHref(scope, instanceId, "summary")}>Configuration drift</a>
      <a className="secondary-button" href={healthSectionHref(scope, instanceId, "databases")}>Database options</a>
    </nav>
    <p className="table-note">Configuration drift and database options open their existing Server summary sections with this workspace time and database context. Change history follows the selected window when supported; current settings and inventory show their latest source snapshots. Server configuration remains server-wide.</p>
    {scope.range === "custom" && <p className="table-note">Rewind applies to backup assessments, per-replica AG evidence and SQL Agent first-observed history. Earlier projection cards read the latest source when requested and can retain an investigated receipt.</p>}
    {tab === "agent" ? <AgentFailureHistoryPanel instanceId={instanceId} targetRevision={targetRevision} scope={scope} refresh={refresh} />
      : tab === "availability" ? <><AvailabilityEvidencePanel instanceId={instanceId} scope={scope} refresh={refresh} />
      <details className="supporting-evidence"><summary>Earlier availability summary (received v1 snapshots)</summary>
        <div className="operations-stack">{activeKinds.map(renderCard)}</div>
      </details></> : tab === "backups" ? <><BackupAssessmentPanel instanceId={instanceId} scope={scope} refresh={refresh} canManagePolicies={canManageBackupPolicies} />
        <details className="supporting-evidence"><summary>Earlier backup observations (received v1 snapshots)</summary>
          <div className="operations-stack">{activeKinds.map(renderCard)}</div>
        </details></> : tab === "replication" ? <ReplicationTab instanceId={instanceId} refresh={refresh} scope={scope} />
        : <div className="operations-stack">{activeKinds.map(renderCard)}</div>}
  </section>;
}

function OperationCard({ card, actions }: { readonly card: OperationsRenderCard; readonly actions: ReturnType<typeof useOperationalProjection> }) {
  const formatTime = useTimeFormatter();
  const page = card.page;
  const tone = card.status === "Complete" ? "current" : card.status === "Error" || card.status === "PermissionDenied" ? "critical" : card.status === "Partial" || card.status === "Degraded" ? "warning" : "unavailable";
  return <article className="operation-card operation-projection-card panel" aria-label={card.label} aria-busy={card.loading}>
    <div className="operation-card-heading"><div><p className="eyebrow">{card.kind.toUpperCase()}</p><h3>{card.label}</h3></div><EvidenceStatus label={card.loading ? "Loading" : page?.state ?? card.status} detail={page ? `${page.state === "NoData" ? "Checked" : "Snapshot receipt"} ${formatTime(page.observedAtUtc)}` : undefined} tone={tone} /></div>
    <p className="operation-projection-note">This projection reads the latest source snapshot when requested. Rewind and database selection do not filter these server-wide rows. Each card has an independent source receipt.</p>
    {card.retained && <p className="operation-projection-retained">Retained projection investigation. Workspace polling does not replace this card. Previously read pages restore locally; unread pages need an unexpired native continuation.</p>}
    {card.loading && <div className="operation-state"><p role="status">{page ? "Reading bounded evidence; the received page remains visible…" : "Loading bounded evidence…"}</p><button type="button" onClick={actions.cancel}>Cancel {card.label} read</button></div>}
    {card.error && <div className="operation-state"><p role="alert">{card.error}</p>{card.canRetry && <button type="button" disabled={card.loading} onClick={actions.retry}>Retry {card.label} read</button>}</div>}
    {page && <>
      {(card.loading || card.error) && <p className="operation-projection-note">Showing the previously received page while this read is pending or unavailable. The receipt below describes that evidence; it does not establish current state.</p>}
      <p className="operation-projection-receipt">{page.runId ? `Received source run: ${page.runId}` : "No source run"} · {page.runId ? "Source target revision" : "Reported target revision"}: {page.targetRevision}. This receipt does not certify current configuration or health.</p>
      <OperationCoverage page={page} kind={card.kind} />
      {card.kind === "tempdb-summary" && <><TempDbSummary page={page} /><p className="operation-projection-note">Summary totals belong to this source run. Browse TempDB files independently; another card can have a different receipt.</p></>}
      {page.state === "NoData" && <p className="empty-state">No observation is available for this target. Check the collector status in Server summary.</p>}
      {page.state === "Unsupported" && <p className="empty-state">This collector is unsupported by the target. Check the discovered platform and version in Server summary.</p>}
      {page.state === "PermissionDenied" && <p className="empty-state">The collector could not read this evidence. Ask a Target Administrator to check the monitored SQL Server read permissions.</p>}
      {page.items.length ? card.kind === "tempdb-files"
        ? <details className="evidence-disclosure" key={`${page.targetId}:${page.targetRevision}`}><summary>Browse TempDB file evidence ({page.items.length} rows on this page)</summary><NamedOperationEvidence kind={card.kind} page={page} /></details>
        : <NamedOperationEvidence kind={card.kind} page={page} />
        : card.kind !== "tempdb-summary" && page.state === "Complete" ? <p className="empty-state">No bounded rows were reported by this observation.</p> : null}
      {page.items.length > 0 && <details className="evidence-disclosure"><summary>Secondary projection fields</summary><EvidenceTable label={`${card.label} secondary evidence`} rows={card.items} /></details>}
    </>}
    <nav className="operation-projection-controls operation-pagination pager" aria-label={`${card.label} pages`}>
      <span>Page {card.pageIndex + 1}</span>
      {card.kind !== "tempdb-summary" && <>
        <button type="button" disabled={card.loading || !page || card.pageIndex === 0} onClick={actions.first}>First {card.label} page</button>
        <button type="button" disabled={card.loading || !page || card.pageIndex === 0} onClick={actions.previous}>Previous {card.label} page</button>
        <button type="button" disabled={!card.canLoadMore} onClick={actions.next}>Next {card.label} page</button>
      </>}
      <button type="button" disabled={card.loading} onClick={actions.latest}>Return {card.label} to latest</button>
    </nav>
  </article>;
}

function OperationCoverage({ page, kind }: { readonly page: OperationalPage; readonly kind: OperationalKind }) {
  const formatTime = useTimeFormatter();
  const availability = kind === "ag-replicas" || kind === "ag-databases";
  return <div className="operation-coverage">
    <span>{page.state === "NoData" ? "Repository no-data check time" : "Snapshot receipt time"}: {formatTime(page.observedAtUtc)}</span>
    {page.state === "Partial" && <strong>Partial source observation · source coverage is incomplete</strong>}
    {page.truncated && <strong>{availability ? "Additional snapshot rows exist beyond this page" : "Partial capture · additional rows may be missing"}</strong>}
    {page.visibilityScope && <span>{availability ? "First row visibility on this page" : "Visibility"}: {page.visibilityScope}</span>}
    {page.coverageFromUtc || page.coverageToUtc ? <span>SQL Agent coverage: {formatTime(page.coverageFromUtc)} to {formatTime(page.coverageToUtc)}</span> : null}
    {kind === "agent" && <small>Execution start uses the SQL Server local clock without a time-zone offset. Job names and messages are unavailable.</small>}
  </div>;
}

function TempDbSummary({ page }: { readonly page: OperationalPage }) {
  const values: readonly [string, unknown][] = [["Total bytes", page.totalBytes], ["Used bytes", page.usedBytes], ["Log total bytes", page.logTotalBytes], ["Log used bytes", page.logUsedBytes]];
  return <div className="operation-metrics">{values.map(([label, value]) => <div key={label}><span>{label}</span><strong>{formatValue(value)}</strong></div>)}</div>;
}

function NamedOperationEvidence({ kind, page }: { readonly kind: OperationalKind; readonly page: OperationalPage }) {
  const formatTime = useTimeFormatter();
  const rows = namedRows(kind, page.items, formatTime);
  return <div className="named-evidence"><h4>Available fields</h4><EvidenceTable label={`${kind} named evidence`} rows={rows} /></div>;
}

function namedRows(kind: OperationalKind, items: readonly Record<string, unknown>[], formatTime: (value: string | null | undefined) => string): readonly Record<string, unknown>[] {
  return items.map((item) => {
    if (kind === "backups") return {
      "Database fingerprint": item.databaseFingerprint,
      "Backup kind": item.kind,
      "Last finish": typeof item.lastFinishUtc === "string" ? formatTime(item.lastFinishUtc) : "Unavailable",
      "Size bytes": item.sizeBytes,
      "Copy only": item.copyOnly,
      "Checksum": item.hasChecksum,
      "Damaged": item.isDamaged,
      Coverage: item.coverage,
    };
    if (kind === "agent") return {
      "Job identifier": item.jobId,
      "Record scope": item.isJobOutcome === true ? "Job outcome" : item.isJobOutcome === false ? "Job step" : "Not reported",
      "Counts as failed job": typeof item.countsAsJobFailure === "boolean" ? item.countsAsJobFailure : "Not reported",
      "History instance": item.historyInstanceId,
      "Step": item.stepId,
      "Run status": item.runStatus,
      "Failure kind": item.failureKind,
      Severity: item.severity,
      "Retry attempt": item.retryAttempt,
      "Duration seconds": item.durationSeconds,
      "Execution started (server local)": item.sourceLocalStart ?? item.SourceLocalStart ?? "Not reported",
      "First observed": typeof item.firstObservedAtUtc === "string" ? formatTime(item.firstObservedAtUtc) : "Unavailable",
      "Failure fingerprint": item.failureFingerprint,
    };
    if (kind === "tempdb-files") return { "File identifier": item.fileId, "Size bytes": item.sizeBytes, "Used bytes": item.usedBytes, "Free bytes": item.freeBytes, State: item.state };
    if (kind === "ag-replicas") return { "Group fingerprint": item.groupFingerprint, "Replica fingerprint": item.replicaFingerprint, Role: item.role, "Operational state": item.operationalState, "Connected state": item.connectedState, Visibility: item.visibilityScope, "State available": item.stateAvailable };
    return { "Group fingerprint": item.groupFingerprint, "Database fingerprint": item.databaseFingerprint, "Synchronization state": item.synchronizationState, "Database state": item.databaseState, Visibility: item.visibilityScope, "State available": item.stateAvailable };
  });
}

function ReplicationTab({ instanceId, refresh, scope }: { readonly instanceId: string; readonly refresh: number; readonly scope: OverviewScope }) {
  const window = useMemo(() => {
    try {
      const selected = overviewWindow(scope, Date.now());
      return Date.parse(selected.toUtc) - Date.parse(selected.fromUtc) <= 7 * 86400000 ? selected : undefined;
    } catch { return undefined; }
  }, [scope.range, scope.from, scope.to, refresh]);
  if (!window) return <p role="status">Replication evidence supports windows of up to seven days. Choose a shorter valid window in the time bar.</p>;
  return <div className="replication-tab"><div className="evidence-callout">Replication observations follow the selected time window. Source timestamps and coverage remain explicit; missing observations do not establish healthy replication.</div><div className="replication-grid"><AnalyticsSurfacePanel targetId={instanceId} surface="replication/status" timeWindow={window} refresh={refresh} /><AnalyticsSurfacePanel targetId={instanceId} surface="replication/evidence" timeWindow={window} refresh={refresh} /></div></div>;
}

function formatValue(value: unknown): string {
  if (value === null || value === undefined) return "Unavailable";
  if (typeof value === "boolean") return value ? "Yes" : "No";
  if (typeof value === "string" || typeof value === "number") return String(value);
  try { return JSON.stringify(value); } catch { return "Unavailable"; }
}
