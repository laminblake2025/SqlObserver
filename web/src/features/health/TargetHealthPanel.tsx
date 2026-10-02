import { useEffect, useRef, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { Tabs } from "../../components/DiagnosticUi";
import { healthSectionHref } from "../../dashboardModel";
import { overviewHref } from "../overview/overviewModel";
import type { OverviewScope } from "../overview/overviewTypes";
import { healthRecoveryAdvice } from "./healthRecoveryModel";
import { ServerDashboard } from "./ServerDashboard";
import { DatabaseOptionChanges } from "./DatabaseOptionChanges";
import { ServerConfigurationChanges } from "./ServerConfigurationChanges";
import { ServerConfigurationSnapshot } from "./ServerConfigurationSnapshot";

import { getDatabaseHealth, getTargetHealthEvidence, HealthRequestError } from "./healthApi";
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
  TargetHealthEvidence,
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

export interface TargetHealthPanelProps {
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
  readonly targetRevision: number;
  readonly selectionHash: string;
  readonly selectedConfigurationRunId?: string;
  readonly selectedSection?: "summary" | "databases" | "collection";
  readonly onSelectSection?: (section: "summary" | "databases" | "collection") => void;
}

export function TargetHealthPanel({ instanceId, displayName, onClose, scope, refresh, healthRefresh, blockingRefresh, selectedDatabaseId, onSelectDatabase, canManageConfiguration, targetRevision, selectionHash, selectedSection, selectedConfigurationRunId, onSelectSection }: TargetHealthPanelProps) {
  const formatTimestamp = useTimeFormatter();
  const [receivedEvidence, setEvidence] = useState<TargetHealthEvidence>();
  const evidence = receivedEvidence?.target.instanceId.toLowerCase() === instanceId.toLowerCase()
    ? receivedEvidence : undefined;
  const [message, setMessage] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [tab, setTab] = useState<"summary" | "databases" | "collection">(selectedSection ?? (selectedDatabaseId === undefined ? "summary" : "databases"));
  useEffect(() => { if (selectedSection) setTab(selectedSection); else if (selectedDatabaseId !== undefined) setTab("databases"); }, [selectedDatabaseId, selectedSection]);
  const snapshot = evidence?.target;
  const recoveryContext: RecoveryContext = { instanceId, scope, canGeneratePermissions: canManageConfiguration };

  useEffect(() => {
    let active = true;
    let request: AbortController | undefined;

    async function refresh() {
      const currentRequest = new AbortController();
      request = currentRequest;
      try {
        const next = await getTargetHealthEvidence(instanceId, currentRequest.signal);
        if ([next.target.instanceId, next.databases.instanceId, next.files.instanceId]
          .some(targetId => targetId.toLowerCase() !== instanceId.toLowerCase())) {
          throw new HealthRequestError("Health evidence did not match the selected server. Refresh to retry.");
        }
        if (active) {
          setEvidence(next);
          setMessage(undefined);
        }
      } catch (error: unknown) {
        if (active && currentRequest.signal.aborted === false) {
          setMessage(getSafeMessage(error));
        }
      } finally {
        if (active) setLoading(false);
      }
    }

    setMessage(undefined);
    setLoading(true);
    void refresh();
    return () => {
      active = false;
      request?.abort();
    };
  }, [instanceId, healthRefresh]);

  return (
    <section className="health-screen" aria-labelledby="health-heading">
      <div className="health-heading-row">
        <div>
          <p className="eyebrow">Server summary · target-scoped snapshot</p>
          <h2 id="health-heading">Server summary for {displayName}</h2>
        </div>
        <button className="secondary-button" onClick={onClose} type="button">
          Close
        </button>
      </div>
      <ServerDashboard instanceId={instanceId} displayName={displayName} scope={scope} refresh={refresh}
        targetRevision={targetRevision} selectionHash={selectionHash} canReadRules={canManageConfiguration}
        blockingRefresh={blockingRefresh}
        configurationCollectorState={snapshot?.collectors.find(item => item.collectorId === "engine.configurations")?.state} />
      <p className="table-note">The dashboard follows the selected time window. Database, file, configuration, and collector summaries below show their latest snapshots, with source times alongside each observation.</p>

      {loading && snapshot === undefined ? <p role="status">Loading health evidence…</p> : null}
      {message === undefined ? null : <p role="alert" className="status-message">{message}</p>}
      {evidence === undefined ? null : (
        <>
          <Tabs
            label="Server summary sections"
            tabs={[{ value: "summary", label: "Summary" }, { value: "databases", label: "Databases" }, { value: "collection", label: "Collection health" }]}
            value={tab}
            onChange={(value) => { const section = value as "summary" | "databases" | "collection"; setTab(section); onSelectSection?.(section); }}
          />
          <div className="health-summary">
            <span className={`health-state health-${evidence.target.state}`}>
              {stateLabels[evidence.target.state]}
            </span>
            <p>
              Evaluated using repository time: {formatTimestamp(evidence.target.repositoryTimeUtc)}
            </p>
          </div>
          {tab === "summary" ? <>
            <HealthMetricStrip metrics={evidence.target.coreMetrics} />
            <p className="detail-note">For unavailable or incomplete core metrics, review <a href={healthSectionHref(scope, instanceId, "collection")}>collection health</a> for source reasons and recovery guidance.</p>
            <details className="supporting-evidence"><summary>All latest core metric samples</summary>
              <CoreMetrics metrics={evidence.target.coreMetrics} />
            </details>
            <ServerConfigurationSnapshot instanceId={instanceId} refresh={refresh}
              canManage={canManageConfiguration} />
            <ServerConfigurationChanges selectedRunId={selectedConfigurationRunId} instanceId={instanceId} scope={scope} refresh={refresh}
              collector={evidence.target.collectors.find(item => item.collectorId === "engine.configurations")}
              canManage={canManageConfiguration} />
            <details className="supporting-evidence"><summary>Collector snapshot summary</summary><div className="collector-grid">{evidence.target.collectors.map((collector) => <CollectorHealthCard collector={collector} recoveryContext={recoveryContext} key={collector.collectorId} />)}</div></details>
          </> : null}
          {tab === "databases" ? <><DatabaseOptionChanges instanceId={instanceId} scope={scope} refresh={refresh} /><DatabaseHealth key={instanceId} instanceId={instanceId} firstPage={evidence.databases} recoveryContext={recoveryContext} selectedDatabaseId={selectedDatabaseId} onSelectDatabase={onSelectDatabase} /><DatabaseFileHealth page={evidence.files} recoveryContext={recoveryContext} selectedDatabaseId={selectedDatabaseId} /></> : null}
          {tab === "collection" ? <div className="collector-grid">{evidence.target.collectors.map((collector) => <CollectorHealthCard collector={collector} recoveryContext={recoveryContext} key={collector.collectorId} />)}</div> : null}
        </>
      )}
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

function DatabaseHealth({ instanceId, firstPage, recoveryContext, selectedDatabaseId, onSelectDatabase }: { readonly instanceId: string; readonly firstPage: DatabaseHealthPage; readonly recoveryContext: RecoveryContext; readonly selectedDatabaseId?: number; readonly onSelectDatabase: (databaseId: number | undefined) => void }) {
  const formatTimestamp = useTimeFormatter();
  const [page, setPage] = useState(firstPage);
  const [pageIndex, setPageIndex] = useState(0);
  const [cursors, setCursors] = useState<readonly (string | undefined)[]>([undefined]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(false);
  const request = useRef<AbortController | undefined>(undefined);
  const [selectionMissing, setSelectionMissing] = useState(false);

  useEffect(() => {
    request.current?.abort();
    request.current = undefined;
    setPage(firstPage);
    setPageIndex(0);
    setCursors([undefined]);
    setLoading(false);
    setError(false);
    setSelectionMissing(false);
    return () => request.current?.abort();
  }, [instanceId, firstPage]);

  useEffect(() => {
    if (selectedDatabaseId === undefined) return;
    const controller = new AbortController();
    request.current?.abort();
    request.current = controller;
    setSelectionMissing(false);
    if (firstPage.items.some(item => item.databaseId === selectedDatabaseId)) {
      setPage(firstPage);
      setPageIndex(0);
      setCursors([undefined]);
      setLoading(false);
      request.current = undefined;
      return;
    }
    setLoading(true);
    void (async () => {
      let current = firstPage;
      const visited = new Set<string>();
      const pageCursors: (string | undefined)[] = [undefined];
      for (let index = 1; index < 20 && current.nextCursor; index += 1) {
        if (visited.has(current.nextCursor)) throw new Error("Database paging repeated a cursor.");
        visited.add(current.nextCursor);
        pageCursors.push(current.nextCursor);
        const next = await getDatabaseHealth(instanceId, controller.signal, current.nextCursor);
        if (controller.signal.aborted) return;
        if (next.instanceId.toLowerCase() !== instanceId.toLowerCase()) throw new Error("Database page target mismatch.");
        if (next.items.some(item => item.databaseId === selectedDatabaseId)) {
          setPage(next);
          setPageIndex(index);
          setCursors(pageCursors);
          return;
        }
        current = next;
      }
      setSelectionMissing(true);
    })().catch(() => { if (!controller.signal.aborted) setError(true); }).finally(() => {
      if (!controller.signal.aborted) { request.current = undefined; setLoading(false); }
    });
    return () => controller.abort();
  }, [instanceId, firstPage, selectedDatabaseId]);

  useEffect(() => {
    if (selectedDatabaseId !== undefined && page.items.some(item => item.databaseId === selectedDatabaseId)) {
      document.getElementById(`database-health-${selectedDatabaseId}`)?.scrollIntoView({ block: "center" });
    }
  }, [page, selectedDatabaseId]);

  async function navigate(index: number, cursor?: string) {
    if (request.current) return;
    if (index === 0) {
      setPage(firstPage);
      setPageIndex(0);
      setError(false);
      return;
    }
    const controller = new AbortController();
    request.current = controller;
    setLoading(true);
    setError(false);
    try {
      const next = await getDatabaseHealth(instanceId, controller.signal, cursor);
      if (controller.signal.aborted) return;
      if (next.instanceId.toLowerCase() !== instanceId.toLowerCase()) throw new Error("Database page target mismatch.");
      setPage(next);
      setPageIndex(index);
      setCursors(previous => [...previous.slice(0, index), cursor]);
    } catch {
      if (!controller.signal.aborted) setError(true);
    } finally {
      if (!controller.signal.aborted) {
        request.current = undefined;
        setLoading(false);
      }
    }
  }

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
      {error ? <p role="alert">Database page is unavailable. Try again.</p> : null}
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
        <button type="button" disabled={loading || pageIndex === 0} onClick={() => void navigate(0)}>First database page</button>{" "}
        <button type="button" disabled={loading || pageIndex === 0} onClick={() => void navigate(pageIndex - 1, cursors[pageIndex - 1])}>Previous database page</button>{" "}
        <button type="button" disabled={loading || !page.nextCursor} onClick={() => void navigate(pageIndex + 1, page.nextCursor ?? undefined)}>Next database page</button>
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

function DatabaseFileHealth({ page, recoveryContext, selectedDatabaseId }: { readonly page: DatabaseFileHealthPage; readonly recoveryContext: RecoveryContext; readonly selectedDatabaseId?: number }) {
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
