import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import type { OverviewScope } from "../overview/overviewTypes";
import { createReport, reportCsvUrl, reportHtmlUrl, type ReportKind, type ReportRun } from "./reportsApi";
import { reportContextKey, resolveReportWindow, type ReportSelection, type ReportWindow } from "./reportsWorkspaceModel";
import { useReportExpiry } from "./useReportExpiry";

interface ReportAttempt {
  readonly contextKey: string;
  readonly targetId: string;
  readonly kind: ReportKind;
  readonly window?: ReportWindow;
  readonly operationId: string;
}
interface ActiveRequest { readonly contextKey: string; readonly controller: AbortController; }
interface ReportView {
  readonly contextKey: string;
  readonly busy: boolean;
  readonly window?: ReportWindow;
  readonly run?: ReportRun;
  readonly message?: string;
}

interface ReportsPanelProps {
  readonly instanceId: string;
  readonly displayName: string;
  readonly onClose: () => void;
  readonly scope: OverviewScope;
  readonly selection: ReportSelection;
  readonly onSelectKind: (kind: ReportKind) => void;
}

export function ReportsPanel(props: ReportsPanelProps) {
  // A returning target/type/window owns a new lifetime, even after selection ABA.
  return <ReportWorkspace key={reportContextKey(props.instanceId, props.selection, props.scope)} {...props} />;
}

function ReportWorkspace({ instanceId, displayName, onClose, scope, selection, onSelectKind }: ReportsPanelProps) {
  const formatTime = useTimeFormatter();
  const contextKey = reportContextKey(instanceId, selection, scope);
  const latestContext = useRef(contextKey);
  latestContext.current = contextKey;
  const [view, setView] = useState<ReportView>({ contextKey, busy: false });
  const activeRequest = useRef<ActiveRequest | null>(null);
  const attempt = useRef<ReportAttempt | null>(null);
  const mounted = useRef(false);
  useLayoutEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; activeRequest.current?.controller.abort(); activeRequest.current = null; };
  }, []);

  useEffect(() => {
    if (activeRequest.current && activeRequest.current.contextKey !== contextKey) {
      activeRequest.current.controller.abort();
      activeRequest.current = null;
    }
    if (attempt.current?.contextKey !== contextKey) attempt.current = null;
    setView(previous => previous.contextKey === contextKey ? previous : { contextKey, busy: false });
    return () => {
      // Cleanup belongs to its captured context and cannot cancel a newer submission.
      if (activeRequest.current?.contextKey === contextKey) {
        activeRequest.current.controller.abort();
        activeRequest.current = null;
      }
      if (attempt.current?.contextKey === contextKey) attempt.current = null;
    };
  }, [contextKey]);

  function changeKind(kind: ReportKind) {
    activeRequest.current?.controller.abort();
    activeRequest.current = null;
    attempt.current = null;
    setView({ contextKey, busy: false });
    onSelectKind(kind);
  }

  // A changed context never displays old exports, errors or pending state while effects catch up.
  const visibleView = view.contextKey === contextKey ? view : undefined;
  const kind = selection.state === "valid" ? selection.kind : undefined;
  const needsWindow = kind !== undefined && kind !== "instance-health";
  const maximumDays = kind === "capacity-readiness" ? 31 : 7;
  let selectedWindow: ReportWindow | undefined;
  let windowError: string | undefined;
  if (kind !== undefined && visibleView?.window === undefined) {
    try { selectedWindow = resolveReportWindow(scope, kind, Date.now()); }
    catch (error) { windowError = error instanceof Error ? error.message : "Update the shared time range to generate this report."; }
  }
  const displayWindow = visibleView?.window ?? selectedWindow;
  const message = selection.state === "invalid" ? selection.message : visibleView?.message ?? windowError;
  const run = visibleView?.run;
  const expiry = useReportExpiry(run);
  const exportSection = kind === "instance-health" ? "health"
    : kind === "performance-window" ? "performance"
    : kind === "incident-evidence" ? "incidents" : "capacity";

  async function generate() {
    if (!mounted.current || selection.state !== "valid") return;
    if (activeRequest.current?.contextKey === contextKey) return;
    activeRequest.current?.controller.abort();
    const controller = new AbortController();
    activeRequest.current = { contextKey, controller };
    setView({ contextKey, busy: true, window: visibleView?.window });
    const isCurrent = () => mounted.current && latestContext.current === contextKey
      && activeRequest.current?.controller === controller && !controller.signal.aborted;
    let immutableAttempt: ReportAttempt | undefined;

    try {
      // Live time is resolved once per attempt. A retry preserves its exact window and payload.
      immutableAttempt = attempt.current?.contextKey === contextKey ? attempt.current : {
        contextKey, targetId: instanceId, kind: selection.kind,
        window: resolveReportWindow(scope, selection.kind, Date.now()), operationId: globalThis.crypto.randomUUID(),
      };
      attempt.current = immutableAttempt;
      setView({ contextKey, busy: true, window: immutableAttempt.window });
      const result = await createReport(immutableAttempt.targetId, immutableAttempt.kind, controller.signal,
        immutableAttempt.window?.fromUtc, immutableAttempt.window?.toUtc, immutableAttempt.operationId);
      if (isCurrent()) {
        attempt.current = null;
        setView({ contextKey, busy: false, run: result, window: immutableAttempt.window });
      }
    } catch (error: unknown) {
      if (isCurrent()) setView({ contextKey, busy: false, window: immutableAttempt?.window,
        message: error instanceof Error ? error.message : "The report request failed." });
    } finally {
      if (isCurrent()) activeRequest.current = null;
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
        <select id="report-kind" value={kind ?? ""} onChange={event => {
          if (event.target.value) changeKind(event.target.value as ReportKind);
        }}>
          {kind === undefined ? <option value="" disabled>Choose report type</option> : null}
          <option value="instance-health">Instance Health</option>
          <option value="performance-window">Performance Window</option>
          <option value="incident-evidence">Incident Evidence</option>
          <option value="capacity-readiness">Capacity/Readiness</option>
        </select>
      </div>

      {needsWindow ? <fieldset className="report-window" aria-describedby="report-window-help">
        <legend>UTC window (maximum {maximumDays} days)</legend>
        {displayWindow === undefined ? null : <dl className="report-window-summary report-window-fields">
          <div className="report-field">
            <dt>From</dt>
            <dd><time dateTime={displayWindow.fromUtc}>{formatTime(displayWindow.fromUtc)}</time></dd>
          </div>
          <div className="report-field">
            <dt>To (exclusive)</dt>
            <dd><time dateTime={displayWindow.toUtc}>{formatTime(displayWindow.toUtc)}</time></dd>
          </div>
        </dl>}
        <p id="report-window-help">Uses the shared time bar. The end is exclusive.</p>
        <p className="report-window-note">{displayWindow === undefined
          ? "Choose a supported range in the shared time bar before generating this report."
          : visibleView?.window !== undefined
          ? run === undefined ? "This report attempt uses the frozen window shown here. Retries keep these bounds."
            : "This generated report uses the window shown here. Generate again to use the current shared range."
          : "The selected report window is shown here. Live bounds freeze when you generate."}</p>
      </fieldset> : kind === "instance-health"
        ? <p className="empty-state">Instance Health is a current snapshot and does not use the shared time window.</p> : null}

      {scope.databaseId === undefined ? null : <p className="report-window-note">Reports use server-wide evidence; database context is retained for navigation.</p>}

      <div className="report-actions">
        <button disabled={kind === undefined || (visibleView?.busy ?? false) || windowError !== undefined} type="submit">
          {visibleView?.busy ? "Generating…" : expiry.expired ? "Generate new report" : "Generate report"}
        </button>
      </div>
    </form>

    {message === undefined ? null : <p className="status-message" role="alert">{message}</p>}
    {run === undefined || run.targetId.toLowerCase() !== instanceId.toLowerCase()
      ? <p className="empty-state">Reports use authorized, materialized projection evidence.</p>
      : expiry.expired ? <p className="status-message" role="status">This report expired. Generate a new report using {kind === "instance-health" ? "current snapshot evidence" : "the current shared range"}.</p>
      : <div className="report-result" aria-live="polite">
        <p>Ready through {formatTime(run.expiresAtUtc)}.</p>
        <div className="report-export-links">
          <a href={reportHtmlUrl(instanceId, run.runId)} target="_blank" rel="noreferrer" onClick={event => { if (!mounted.current || expiry.check()) event.preventDefault(); }} onAuxClick={event => { if (!mounted.current || expiry.check()) event.preventDefault(); }}>Open printable HTML</a>
          <a href={reportCsvUrl(instanceId, run.runId, exportSection)} download onClick={event => { if (!mounted.current || expiry.check()) event.preventDefault(); }} onAuxClick={event => { if (!mounted.current || expiry.check()) event.preventDefault(); }}>Download section CSV</a>
        </div>
      </div>}
  </section>;
}
