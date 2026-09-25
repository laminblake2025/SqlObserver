import { useEffect, useReducer, useRef, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { AnalyticsSurfacePanel } from "../analytics/AnalyticsSurfacePanel";
import { EvidenceStatus, Tabs } from "../../components/DiagnosticUi";
import { EvidenceTable } from "../../components/EvidenceTable";
import { getOperationalPage, OperationalApiError } from "./operationsApi";
import { initialOperationsState, operationKinds, operationsReducer } from "./operationsState";
import { buildOperationsRenderModel, type OperationsRenderCard } from "./operationsRenderModel";
import type { OperationalKind, OperationalPage } from "./operationsTypes";

type OperationsTab = "backups" | "agent" | "tempdb" | "availability" | "replication";

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

export function OperationsPanel({ instanceId, refresh }: { readonly instanceId: string; readonly refresh: number }) {
  const [cards, dispatch] = useReducer(operationsReducer, undefined, initialOperationsState);
  const [tab, setTab] = useState<OperationsTab>("backups");
  const controllers = useRef(new Map<OperationalKind, AbortController>());

  useEffect(() => {
    controllers.current.forEach((controller) => controller.abort());
    controllers.current.clear();
    dispatch({ type: "reset" });
    let disposed = false;
    const isCurrent = (kind: OperationalKind, controller: AbortController) => !disposed && !controller.signal.aborted && controllers.current.get(kind) === controller;
    const load = async (kind: OperationalKind, cursor?: string | null) => {
      const controller = new AbortController();
      controllers.current.set(kind, controller);
      dispatch({ type: "start", kind, append: cursor !== undefined });
      try {
        const page = await getOperationalPage(instanceId, kind, { cursor, limit: 50, signal: controller.signal });
        if (isCurrent(kind, controller)) dispatch({ type: "success", kind, page, append: cursor !== undefined });
      } catch (error: unknown) {
        if (isCurrent(kind, controller) && !(error instanceof DOMException && error.name === "AbortError")) {
          const api = error instanceof OperationalApiError ? error : undefined;
          dispatch({ type: "failure", kind, error: api?.message ?? "Operational health is temporarily unavailable.", retryable: api?.retryable ?? true });
        }
      }
    };
    operationKinds.forEach((kind) => { void load(kind); });
    return () => {
      disposed = true;
      controllers.current.forEach((controller) => controller.abort());
      controllers.current.clear();
    };
  }, [instanceId, refresh]);

  const retry = (kind: OperationalKind) => {
    controllers.current.get(kind)?.abort();
    const controller = new AbortController();
    controllers.current.set(kind, controller);
    dispatch({ type: "start", kind, append: false });
    void getOperationalPage(instanceId, kind, { limit: 50, signal: controller.signal })
      .then((page) => { if (!controller.signal.aborted && controllers.current.get(kind) === controller) dispatch({ type: "success", kind, page, append: false }); })
      .catch((error: unknown) => { if (!controller.signal.aborted && controllers.current.get(kind) === controller && !(error instanceof DOMException && error.name === "AbortError")) dispatch({ type: "failure", kind, error: error instanceof OperationalApiError ? error.message : "Operational health is temporarily unavailable.", retryable: true }); });
  };

  const more = (kind: OperationalKind, cursor: string) => {
    controllers.current.get(kind)?.abort();
    const controller = new AbortController();
    controllers.current.set(kind, controller);
    dispatch({ type: "start", kind, append: true });
    void getOperationalPage(instanceId, kind, { limit: 50, cursor, signal: controller.signal })
      .then((page) => { if (!controller.signal.aborted && controllers.current.get(kind) === controller) dispatch({ type: "success", kind, page, append: true }); })
      .catch((error: unknown) => { if (!controller.signal.aborted && controllers.current.get(kind) === controller && !(error instanceof DOMException && error.name === "AbortError")) dispatch({ type: "failure", kind, error: error instanceof OperationalApiError ? error.message : "Operational health is temporarily unavailable.", retryable: true }); });
  };

  const cancel = (kind: OperationalKind) => {
    controllers.current.get(kind)?.abort();
    controllers.current.delete(kind);
    dispatch({ type: "cancel", kind });
  };

  const model = buildOperationsRenderModel(cards);
  const activeKinds = tab === "replication" ? [] : tabKinds[tab];
  return <section className="operations-screen" aria-label="Operational health">
    <div className="screen-intro"><div><p className="eyebrow">Operations · target-scoped projections</p><h2>Operational health</h2><p>Daily checks for recovery, jobs, TempDB, availability, and replication visibility. Each category retains its own loading, failure, retry, cancellation, and paging state.</p></div></div>
    <Tabs label="Operational health categories" tabs={tabs} value={tab} onChange={(value) => setTab(value as OperationsTab)} />
    {tab === "replication" ? <ReplicationTab instanceId={instanceId} refresh={refresh} /> : <div className="operations-stack">{activeKinds.map((kind) => <OperationCard card={model.cards[kind]} key={kind} onCancel={cancel} onLoadMore={more} onRetry={retry} />)}</div>}
  </section>;
}

function OperationCard({ card, onRetry, onLoadMore, onCancel }: { readonly card: OperationsRenderCard; readonly onRetry: (kind: OperationalKind) => void; readonly onLoadMore: (kind: OperationalKind, cursor: string) => void; readonly onCancel: (kind: OperationalKind) => void }) {
  const formatTime = useTimeFormatter();
  const page = card.page;
  const tone = card.status === "Complete" ? "current" : card.status === "Error" || card.status === "PermissionDenied" ? "critical" : card.status === "Partial" || card.status === "Degraded" ? "warning" : "unavailable";
  return <article className="operation-card panel" aria-label={card.label}>
    <div className="operation-card-heading"><div><p className="eyebrow">{card.kind === "agent" ? "SQL AGENT" : card.kind.toUpperCase()}</p><h3>{card.label}</h3></div><EvidenceStatus label={card.loading ? "Loading" : page?.state ?? card.status} detail={page ? `Observed ${formatTime(page.observedAtUtc)}` : undefined} tone={tone} /></div>
    {card.status === "Loading" ? <div className="operation-state"><p>Loading bounded evidence…</p><button type="button" onClick={() => onCancel(card.kind)}>Cancel</button></div> : null}
    {card.status === "Error" && page === undefined ? <div className="operation-state"><p role="alert">{card.error ?? "Operational health is temporarily unavailable."}</p>{card.canRetry ? <button type="button" onClick={() => onRetry(card.kind)}>Retry</button> : null}</div> : null}
    {page ? <>
      <OperationCoverage page={page} kind={card.kind} />
      {card.kind === "tempdb-summary" ? <TempDbSummary page={page} /> : null}
      {page.state === "NoData" ? <p className="empty-state">No data is available for this target.</p> : null}
      {page.state === "Unsupported" ? <p className="empty-state">Unsupported by this target.</p> : null}
      {page.state === "PermissionDenied" ? <p className="empty-state">Permission denied.</p> : null}
      {page.items.length ? <NamedOperationEvidence kind={card.kind} page={page} /> : card.kind !== "tempdb-summary" && page.state === "Complete" ? <p className="empty-state">No bounded rows were reported by this observation.</p> : null}
      {page.items.length ? <details className="evidence-disclosure"><summary>Secondary projection fields</summary><EvidenceTable label={`${card.label} secondary evidence`} rows={card.items} /></details> : null}
      {card.error ? <div className="operation-state"><p role="alert">{card.error}</p>{card.canRetry ? <button type="button" onClick={() => onRetry(card.kind)}>Retry</button> : null}</div> : null}
      {card.canLoadMore && page.nextCursor ? <button type="button" disabled={card.loading} onClick={() => onLoadMore(card.kind, page.nextCursor!)}>Load more bounded rows</button> : null}
    </> : null}
  </article>;
}

function OperationCoverage({ page, kind }: { readonly page: OperationalPage; readonly kind: OperationalKind }) {
  const formatTime = useTimeFormatter();
  return <div className="operation-coverage"><span>Snapshot observed {formatTime(page.observedAtUtc)}</span>{page.truncated ? <strong>Partial capture · additional rows may be missing</strong> : null}{page.visibilityScope ? <span>Visibility: {page.visibilityScope}</span> : null}{page.coverageFromUtc || page.coverageToUtc ? <span>SQL Agent coverage: {formatTime(page.coverageFromUtc)} to {formatTime(page.coverageToUtc)}</span> : null}{kind === "agent" ? <small>Execution start uses the SQL Server local clock without a time-zone offset. Job names and messages are unavailable.</small> : null}</div>;
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

function ReplicationTab({ instanceId, refresh }: { readonly instanceId: string; readonly refresh: number }) {
  return <div className="replication-tab"><div className="evidence-callout">Replication retains the existing analytics client and rendering behavior. Status and historical evidence have independent snapshot and visibility states.</div><div className="replication-grid"><AnalyticsSurfacePanel targetId={instanceId} surface="replication/status" refresh={refresh} /><AnalyticsSurfacePanel targetId={instanceId} surface="replication/evidence" refresh={refresh} /></div></div>;
}

function formatValue(value: unknown): string {
  if (value === null || value === undefined) return "Unavailable";
  if (typeof value === "boolean") return value ? "Yes" : "No";
  if (typeof value === "string" || typeof value === "number") return String(value);
  try { return JSON.stringify(value); } catch { return "Unavailable"; }
}
