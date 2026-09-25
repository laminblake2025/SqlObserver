import { useEffect, useRef, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { createReport, localDateTimeToUtc, reportCsvUrl, reportHtmlUrl, type ReportKind, type ReportRun } from "./reportsApi";

export function ReportsPanel({ instanceId, displayName, onClose }: { readonly instanceId: string; readonly displayName: string; readonly onClose: () => void }) {
  const formatTime = useTimeFormatter();
  const [kind, setKind] = useState<ReportKind>("instance-health"); const [fromUtc, setFromUtc] = useState(""); const [toUtc, setToUtc] = useState(""); const [run, setRun] = useState<ReportRun>(); const [message, setMessage] = useState<string>(); const [busy, setBusy] = useState(false);
  const activeRequest = useRef<AbortController | null>(null);
  useEffect(() => () => activeRequest.current?.abort(), [instanceId]);
  const resetRequest = () => { activeRequest.current?.abort(); activeRequest.current = null; setRun(undefined); setMessage(undefined); setBusy(false); };
  const needsWindow = kind !== "instance-health"; const maximumDays = kind === "capacity-readiness" ? 31 : 7;
  async function generate() {
    if (activeRequest.current) return;
    const controller = new AbortController(); activeRequest.current = controller;
    setBusy(true); setRun(undefined); setMessage(undefined);
    try { const result = await createReport(instanceId, kind, controller.signal, needsWindow ? localDateTimeToUtc(fromUtc) : undefined, needsWindow ? localDateTimeToUtc(toUtc) : undefined); if (!controller.signal.aborted) setRun(result); }
    catch (error: unknown) { if (!controller.signal.aborted) setMessage(error instanceof Error ? error.message : "The report request failed."); }
    finally { if (!controller.signal.aborted) { activeRequest.current = null; setBusy(false); } }
  }

  return <section className="reports-panel" aria-labelledby="reports-heading"><div className="section-heading"><div><p className="eyebrow">M12</p><h2 id="reports-heading">Reports for {displayName}</h2></div><button className="secondary-button" onClick={onClose} type="button">Close</button></div><label htmlFor="report-kind">Report type</label><select id="report-kind" value={kind} onChange={(event) => { resetRequest(); setKind(event.target.value as ReportKind); }}><option value="instance-health">Instance Health</option><option value="performance-window">Performance Window</option><option value="incident-evidence">Incident Evidence</option><option value="capacity-readiness">Capacity/Readiness</option></select>{needsWindow ? <fieldset><legend>UTC window (maximum {maximumDays} days)</legend><label htmlFor="report-from">From (UTC)</label><input id="report-from" aria-describedby="report-window-help" required type="datetime-local" value={fromUtc} onChange={(event) => { resetRequest(); setFromUtc(event.target.value); }} /><label htmlFor="report-to">To (UTC, exclusive)</label><input id="report-to" aria-describedby="report-window-help" required type="datetime-local" value={toUtc} onChange={(event) => { resetRequest(); setToUtc(event.target.value); }} /><p id="report-window-help">The end is exclusive. Times are submitted as UTC.</p></fieldset> : <p className="empty-state">Instance Health is a current snapshot and does not use a time window.</p>}<button disabled={busy || (needsWindow && (!fromUtc || !toUtc))} onClick={() => void generate()} type="button">{busy ? "Generating…" : "Generate report"}</button>{message === undefined ? null : <p className="status-message" role="alert">{message}</p>}{run === undefined ? <p className="empty-state">Reports use authorized, materialized projection evidence.</p> : <div className="report-result" aria-live="polite"><p>Ready through {formatTime(run.expiresAtUtc)}.</p><a href={reportHtmlUrl(instanceId, run.runId)} target="_blank" rel="noreferrer">Open printable HTML</a><a href={reportCsvUrl(instanceId, run.runId, kind === "instance-health" ? "health" : kind === "performance-window" ? "performance" : kind === "incident-evidence" ? "incidents" : "capacity")} download>Download section CSV</a></div>}</section>;
}
