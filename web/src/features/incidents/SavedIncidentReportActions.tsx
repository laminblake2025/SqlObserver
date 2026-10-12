import { useEffect, useRef, useState } from "react";
import { IncidentRequestError } from "./incidentApi.ts";
import { createSavedInvestigationReport } from "./savedIncidentReportApi.ts";
import type { SavedInvestigationReportRun } from "./savedIncidentReportApi.ts";
import { incidentUtc, type SavedIncident } from "./incidentModel.ts";
import { reportCsvUrl, reportHtmlUrl } from "../reports/reportsApi.ts";

interface Props {
  readonly incident: SavedIncident;
  readonly canRead: boolean;
  readonly formatTime: (value: string) => string;
  readonly compact?: boolean;
}

export function SavedIncidentReportActions(props: Props) {
  return <ReportActions key={JSON.stringify([props.incident.targetId.toLowerCase(), props.incident.threadId.toLowerCase(), props.incident.targetRevision])} {...props} />;
}

function ReportActions({ incident, canRead, formatTime, compact = false }: Props) {
  const Heading = compact ? "h3" : "h5";
  const operation = useRef<string | undefined>(undefined);
  const active = useRef<{ controller: AbortController; epoch: number } | undefined>(undefined);
  const authority = useRef({ allowed: canRead, epoch: 0 });
  if (authority.current.allowed !== canRead) authority.current = { allowed: canRead, epoch: authority.current.epoch + 1 };
  const [pending, setPending] = useState(false);
  const [run, setRun] = useState<SavedInvestigationReportRun>();
  const [error, setError] = useState<{ message: string; retryable: boolean }>();
  const [now, setNow] = useState(Date.now);
  // The receipt expires at a half-open UTC boundary. Wake/focus checks also
  // handle browser suspension and wall-clock changes without regenerating it.
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    const tick = () => {
      clearTimeout(timer);
      const current = Date.now(); setNow(current);
      if (!run) return;
      const end = incidentUtc(run.expiresAtUtc);
      if (BigInt(current) * 1000n < end) {
        timer = setTimeout(tick, Math.max(1, Math.min(2_147_483_647, Math.ceil(Number(end) / 1000) - current)));
      }
    };
    tick();
    window.addEventListener("focus", tick);
    document.addEventListener("visibilitychange", tick);
    return () => { clearTimeout(timer); window.removeEventListener("focus", tick); document.removeEventListener("visibilitychange", tick); };
  }, [run]);
  useEffect(() => () => active.current?.controller.abort(), []);
  useEffect(() => {
    if (active.current && active.current.epoch !== authority.current.epoch) {
      active.current.controller.abort(); active.current = undefined; setPending(false);
    }
    if (!canRead) { setRun(undefined); setError(undefined); }
  }, [canRead]);
  const expired = run !== undefined && BigInt(Math.max(now, Date.now())) * 1000n >= incidentUtc(run.expiresAtUtc);
  const generate = async () => {
    if (!authority.current.allowed || active.current) return;
    if (run) {
      if (BigInt(Date.now()) * 1000n < incidentUtc(run.expiresAtUtc)) return;
      // A verified expired result can start a new operation. Failed attempts
      // have no result and retain their identity for an explicit retry.
      operation.current = undefined; setRun(undefined);
    }
    const request = { controller: new AbortController(), epoch: authority.current.epoch };
    active.current = request;
    operation.current ??= crypto.randomUUID(); setPending(true); setError(undefined);
    const current = () => active.current === request && !request.controller.signal.aborted
      && request.epoch === authority.current.epoch && authority.current.allowed;
    try {
      const result = await createSavedInvestigationReport(incident, operation.current, request.controller.signal);
      if (current()) { setRun(result); setNow(Date.now()); }
    } catch (reason) {
      if (current()) setError({ message: reason instanceof Error ? reason.message : "The report is unavailable.",
        retryable: reason instanceof IncidentRequestError && reason.retryable });
    } finally {
      if (active.current === request) { active.current = undefined; setPending(false); }
    }
  };
  if (!canRead) return <p role="status">Viewer, Operator or Target Administrator access is required to read and export this saved investigation.</p>;
  return <div className="saved-incident-report">
    <Heading>Report and exports</Heading>
    {expired && <p role="status">This report expired. Generate a new report from the same saved investigation.</p>}
    {(!run || expired) && <button type="button" disabled={pending || error !== undefined && !error.retryable} onClick={() => void generate()}>
      {pending ? "Generating report…" : expired ? "Generate new investigation report" : operation.current ? "Retry report generation" : "Generate investigation report"}
    </button>}
    {error && <p role="alert">{error.message}</p>}
    {run && !expired && <div className="toolbar" aria-live="polite">
      <span>Report ready through {formatTime(run.expiresAtUtc)}.</span>
      <a href={reportHtmlUrl(incident.targetId, run.runId)} target="_blank" rel="noreferrer">Open printable investigation</a>
      <a href={reportCsvUrl(incident.targetId, run.runId, "investigation")} download>Download saved details CSV</a>
      <a href={reportCsvUrl(incident.targetId, run.runId, "saved-evidence")} download>Download saved evidence CSV</a>
    </div>}
    <small>Reports preserve the saved notes and captured evidence. Large captures may exceed printable-view limits; use the CSV sections to export all retained content.</small>
  </div>;
}
