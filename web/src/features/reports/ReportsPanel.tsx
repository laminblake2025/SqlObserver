import { useEffect, useRef, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import {
  createReport, localDateTimeToUtc, reportCsvUrl, reportHtmlUrl, validateReportWindow,
  type ReportKind, type ReportRun,
} from "./reportsApi";

interface ReportAttempt {
  readonly targetId: string;
  readonly kind: ReportKind;
  readonly fromUtc?: string;
  readonly toUtc?: string;
  readonly operationId: string;
}

export function ReportsPanel({ instanceId, displayName, onClose }: {
  readonly instanceId: string;
  readonly displayName: string;
  readonly onClose: () => void;
}) {
  const formatTime = useTimeFormatter();
  const [kind, setKind] = useState<ReportKind>("instance-health");
  const [fromUtc, setFromUtc] = useState("");
  const [toUtc, setToUtc] = useState("");
  const [run, setRun] = useState<ReportRun>();
  const [message, setMessage] = useState<string>();
  const [busy, setBusy] = useState(false);
  const activeRequest = useRef<AbortController | null>(null);
  const attempt = useRef<ReportAttempt | null>(null);

  useEffect(() => {
    activeRequest.current?.abort();
    activeRequest.current = null;
    attempt.current = null;
    setRun(undefined);
    setMessage(undefined);
    setBusy(false);
    return () => {
      activeRequest.current?.abort();
      activeRequest.current = null;
      attempt.current = null;
    };
  }, [instanceId]);

  function resetRequest() {
    activeRequest.current?.abort();
    activeRequest.current = null;
    attempt.current = null;
    setRun(undefined);
    setMessage(undefined);
    setBusy(false);
  }

  const needsWindow = kind !== "instance-health";
  const maximumDays = kind === "capacity-readiness" ? 31 : 7;
  const exportSection = kind === "instance-health" ? "health"
    : kind === "performance-window" ? "performance"
    : kind === "incident-evidence" ? "incidents" : "capacity";

  async function generate() {
    if (activeRequest.current) return;
    const controller = new AbortController();
    activeRequest.current = controller;
    setBusy(true);
    setRun(undefined);
    setMessage(undefined);
    const isCurrent = () => activeRequest.current === controller && !controller.signal.aborted;

    try {
      const start = needsWindow ? localDateTimeToUtc(fromUtc) : undefined;
      const end = needsWindow ? localDateTimeToUtc(toUtc) : undefined;
      validateReportWindow(kind, start, end);
      const previous = attempt.current;
      const immutableAttempt = previous?.targetId === instanceId && previous.kind === kind
        && previous.fromUtc === start && previous.toUtc === end ? previous
        : { targetId: instanceId, kind, fromUtc: start, toUtc: end, operationId: globalThis.crypto.randomUUID() };
      attempt.current = immutableAttempt;
      const result = await createReport(immutableAttempt.targetId, immutableAttempt.kind, controller.signal,
        immutableAttempt.fromUtc, immutableAttempt.toUtc, immutableAttempt.operationId);
      if (isCurrent()) {
        attempt.current = null;
        setRun(result);
      }
    } catch (error: unknown) {
      if (isCurrent()) setMessage(error instanceof Error ? error.message : "The report request failed.");
    } finally {
      if (isCurrent()) {
        activeRequest.current = null;
        setBusy(false);
      }
    }
  }

  return <section className="reports-panel" aria-labelledby="reports-heading">
    <div className="section-heading">
      <div>
        <p className="eyebrow">REPORTS</p>
        <h2 id="reports-heading">Reports for {displayName}</h2>
      </div>
      <button className="secondary-button" onClick={onClose} type="button">Close</button>
    </div>

    <form className="report-form" aria-label="Generate server report" onSubmit={event => {
      event.preventDefault();
      void generate();
    }}>
      <div className="report-field">
        <label htmlFor="report-kind">Report type</label>
        <select id="report-kind" value={kind} onChange={event => {
          resetRequest();
          setKind(event.target.value as ReportKind);
        }}>
          <option value="instance-health">Instance Health</option>
          <option value="performance-window">Performance Window</option>
          <option value="incident-evidence">Incident Evidence</option>
          <option value="capacity-readiness">Capacity/Readiness</option>
        </select>
      </div>

      {needsWindow ? <fieldset className="report-window">
        <legend>UTC window (maximum {maximumDays} days)</legend>
        <div className="report-window-fields">
          <div className="report-field">
            <label htmlFor="report-from">From (UTC)</label>
            <input id="report-from" aria-describedby="report-window-help" required type="datetime-local"
              value={fromUtc} onChange={event => {
                resetRequest();
                setFromUtc(event.target.value);
              }} />
          </div>
          <div className="report-field">
            <label htmlFor="report-to">To (UTC, exclusive)</label>
            <input id="report-to" aria-describedby="report-window-help" required type="datetime-local"
              value={toUtc} onChange={event => {
                resetRequest();
                setToUtc(event.target.value);
              }} />
          </div>
        </div>
        <p id="report-window-help">The end is exclusive. Times are submitted as UTC.</p>
      </fieldset> : <p className="empty-state">Instance Health is a current snapshot and does not use a time window.</p>}

      <div className="report-actions">
        <button disabled={busy || (needsWindow && (!fromUtc || !toUtc))} type="submit">
          {busy ? "Generating…" : "Generate report"}
        </button>
      </div>
    </form>

    {message === undefined ? null : <p className="status-message" role="alert">{message}</p>}
    {run === undefined || run.targetId.toLowerCase() !== instanceId.toLowerCase()
      ? <p className="empty-state">Reports use authorized, materialized projection evidence.</p>
      : <div className="report-result" aria-live="polite">
        <p>Ready through {formatTime(run.expiresAtUtc)}.</p>
        <div className="report-export-links">
          <a href={reportHtmlUrl(instanceId, run.runId)} target="_blank" rel="noreferrer">Open printable HTML</a>
          <a href={reportCsvUrl(instanceId, run.runId, exportSection)} download>Download section CSV</a>
        </div>
      </div>}
  </section>;
}
