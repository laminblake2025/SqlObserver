import { useEffect, useRef, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { Tabs } from "../../components/DiagnosticUi";
import type { OverviewScope } from "../overview/overviewTypes";
import { ServerDashboard } from "./ServerDashboard";

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

export interface TargetHealthPanelProps {
  readonly instanceId: string;
  readonly displayName: string;
  readonly onClose: () => void;
  readonly scope: OverviewScope;
  readonly refresh: number;
  readonly healthRefresh: number;
  readonly blockingRefresh: number;
}

export function TargetHealthPanel({ instanceId, displayName, onClose, scope, refresh, healthRefresh, blockingRefresh }: TargetHealthPanelProps) {
  const formatTimestamp = useTimeFormatter();
  const [evidence, setEvidence] = useState<TargetHealthEvidence>();
  const [message, setMessage] = useState<string>();
  const [loading, setLoading] = useState(true);
  const [tab, setTab] = useState<"summary" | "databases" | "collection">("summary");
  const snapshot = evidence?.target;

  useEffect(() => {
    let active = true;
    let request: AbortController | undefined;

    async function refresh() {
      const currentRequest = new AbortController();
      request = currentRequest;
      try {
        const next = await getTargetHealthEvidence(instanceId, currentRequest.signal);
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
      <ServerDashboard instanceId={instanceId} displayName={displayName} scope={scope} refresh={refresh} blockingRefresh={blockingRefresh} />
      <div className="health-heading-row">
        <div>
          <p className="eyebrow">Server summary · target-scoped snapshot</p>
          <h3 id="health-heading">Health evidence for {displayName}</h3>
        </div>
        <button className="secondary-button" onClick={onClose} type="button">
          Close
        </button>
      </div>

      {loading && snapshot === undefined ? <p role="status">Loading health evidence…</p> : null}
      {message === undefined ? null : <p role="alert" className="status-message">{message}</p>}
      {evidence === undefined ? null : (
        <>
          <Tabs
            label="Server summary sections"
            tabs={[{ value: "summary", label: "Summary" }, { value: "databases", label: "Databases" }, { value: "collection", label: "Collection health" }]}
            value={tab}
            onChange={(value) => setTab(value as "summary" | "databases" | "collection")}
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
            <CoreMetrics metrics={evidence.target.coreMetrics} />
            <details className="supporting-evidence"><summary>Collector snapshot summary</summary><div className="collector-grid">{evidence.target.collectors.map((collector) => <CollectorHealthCard collector={collector} key={collector.collectorId} />)}</div></details>
          </> : null}
          {tab === "databases" ? <><DatabaseHealth instanceId={instanceId} firstPage={evidence.databases} /><DatabaseFileHealth page={evidence.files} /></> : null}
          {tab === "collection" ? <div className="collector-grid">{evidence.target.collectors.map((collector) => <CollectorHealthCard collector={collector} key={collector.collectorId} />)}</div> : null}
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

function DatabaseHealth({ instanceId, firstPage }: { readonly instanceId: string; readonly firstPage: DatabaseHealthPage }) {
  const formatTimestamp = useTimeFormatter();
  const [page, setPage] = useState(firstPage);
  const [pageIndex, setPageIndex] = useState(0);
  const [cursors, setCursors] = useState<readonly (string | undefined)[]>([undefined]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(false);
  const request = useRef<AbortController | undefined>(undefined);

  useEffect(() => {
    request.current?.abort();
    request.current = undefined;
    setPage(firstPage);
    setPageIndex(0);
    setCursors([undefined]);
    setLoading(false);
    setError(false);
    return () => request.current?.abort();
  }, [instanceId, firstPage]);

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
      <PageCollectorHealth collector={page.collector} />
      <p className="page-boundary">Page {pageIndex + 1} · {page.items.length} databases{page.nextCursor ? " · More available" : " · End of snapshot"}</p>
      {loading ? <p role="status">Loading database page…</p> : null}
      {error ? <p role="alert">Database page is unavailable. Try again.</p> : null}
      {page.items.length === 0 ? (
        <p className="empty-state">
          {isCurrentLossFree(page.collector)
            ? "No database rows were reported by a current, loss-free collection."
            : "Database rows cannot be treated as complete for this snapshot."}
        </p>
      ) : (
        <div className="bounded-health-grid">
          {page.items.map((database) => (
            <DatabaseHealthCard database={database} key={database.databaseId} />
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

function DatabaseHealthCard({ database }: { readonly database: DatabaseHealthSummary }) {
  const formatTimestamp = useTimeFormatter();
  return (
    <article className="bounded-health-card">
      <div className="collector-title-row">
        <h5>{database.name}</h5>
        <span className={`health-state health-${database.collector.state}`}>
          {stateLabels[database.collector.state]}
        </span>
      </div>
      <VisibilityGap collector={database.collector} />
      <dl className="health-facts">
        <HealthFact label="Database ID" value={String(database.databaseId)} />
        <HealthFact label="State" value={formatToken(database.state)} />
        <HealthFact label="Recovery" value={formatToken(database.recoveryModel)} />
        <HealthFact label="Access" value={formatToken(database.userAccess)} />
        <HealthFact label="Read only" value={database.isReadOnly ? "Yes" : "No"} />
        <HealthFact label="Compatibility" value={String(database.compatibilityLevel)} />
        <HealthFact label="Observed" value={formatTimestamp(database.observedAtUtc)} />
      </dl>
    </article>
  );
}

function DatabaseFileHealth({ page }: { readonly page: DatabaseFileHealthPage }) {
  const formatTimestamp = useTimeFormatter();
  return (
    <section className="bounded-health-section" aria-labelledby="file-health-heading">
      <div className="bounded-health-heading">
        <h4 id="file-health-heading">Logical-file health</h4>
        <span>Repository time: {formatTimestamp(page.repositoryTimeUtc)}</span>
      </div>
      <PageCollectorHealth collector={page.collector} />
      {page.items.length === 0 ? (
        <p className="empty-state">
          {isCurrentLossFree(page.collector)
            ? "No logical-file rows were reported by a current, loss-free collection."
            : "Logical-file rows cannot be treated as complete for this snapshot."}
        </p>
      ) : (
        <div className="bounded-health-grid">
          {page.items.map((file) => (
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
      <div className="collector-title-row">
        <h5>{file.logicalName}</h5>
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

function PageCollectorHealth({ collector }: { readonly collector: CollectorHealthSummary }) {
  return (
    <div className="page-collector-health">
      <div className="collector-title-row">
        <strong>{collector.collectorId}</strong>
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

function CollectorHealthCard({ collector }: { readonly collector: CollectorHealthSummary }) {
  const formatTimestamp = useTimeFormatter();
  const formatOptionalTimestamp = (value: string | null) => formatTimestamp(value);
  return (
    <article className="collector-health-card">
      <div className="collector-title-row">
        <h4>{collector.collectorId}</h4>
        <span className={`health-state health-${collector.state}`}>
          {stateLabels[collector.state]}
        </span>
      </div>
      <VisibilityGap collector={collector} />
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
