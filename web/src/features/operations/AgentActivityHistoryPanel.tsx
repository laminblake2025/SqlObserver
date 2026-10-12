import { useId, useLayoutEffect, useRef } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext.tsx";
import { validAgentActivitySelection, type AgentActivitySelection, type AgentActivitySourceHeader } from "./agentActivityHistoryModel.ts";
import type { AgentActivityHistoryRead, AgentActivityRead } from "./useAgentActivityHistory.ts";
import "./agentActivityHistory.css";

const label = (value: string) => value.replace(/([a-z])([A-Z])/gu, "$1 $2").replaceAll("_", " ");
const notices: Readonly<Record<string, string>> = {
  source_complete: "The retained source rows have complete accounting. Candidate state describes source columns at capture time.",
  source_truncated: "The source exceeded its row bound. Retained jobs are incomplete; at least one additional row was lost.",
  collection_failed: "This collection attempt failed. No older successful observation is substituted.",
  payload_missing: "The canonical attempt remains, but its expected source capture is missing. Review retention and collection health.",
  observation_not_retained: "This exact run and sample revision is not retained. No replacement is selected.",
  observation_outside_window: "This exact observation is outside the shared completion window. Adjust the window or clear the selection.",
};
function SourceHeader({ value, title }: { readonly value: AgentActivitySourceHeader; readonly title: string }) {
  return <section aria-label={`${title} Agent source header`}><h5>{title}</h5><dl className="agent-activity-facts">
    <div><dt>Source observation (UTC)</dt><dd>{value.sourceObservedAtUtc}</dd></div>
    <div><dt>Matching services / reported status</dt><dd>{value.service.matchingServiceCount} / {value.service.status ?? "Unavailable"}</dd></div>
    <div><dt>Process ID / startup (UTC)</dt><dd>{value.service.processId ?? "Unavailable"} / {value.service.startupAtUtc ?? "Unavailable"}</dd></div>
    <div><dt>Latest session / Agent start (source local)</dt><dd>{value.session.sessionId ?? "Unavailable"} / {value.session.agentStartLocal ?? "Unavailable"}</dd></div>
  </dl></section>;
}
export function AgentActivityHistoryPanel({ history, selected, onSelectReceipt, onRetainWindow }: {
  readonly history: AgentActivityHistoryRead; readonly selected?: AgentActivitySelection | null;
  readonly onSelectReceipt: (selection: AgentActivitySelection | undefined) => void; readonly onRetainWindow?: () => void;
}) {
  const id = useId(), format = useTimeFormatter(), root = useRef<HTMLElement | null>(null);
  const list = history.list.pages[history.list.index], detail = history.detail.pages[0];
  const recovery = useRef<{ source: "list" | "detail"; context: string; selection: string; focusOwner: Element | null; waiting: boolean } | undefined>(undefined);
  function recover(source: "list" | "detail", action: () => void) {
    recovery.current = { source, context: history.contextKey, selection: history.selectionIdentity, focusOwner: document.activeElement, waiting: false }; action();
  }
  useLayoutEffect(() => {
    const intent = recovery.current;
    if (!intent) return;
    if (intent.context !== history.contextKey || intent.source === "detail" && intent.selection !== history.selectionIdentity || document.activeElement !== intent.focusOwner && document.activeElement !== document.body) { recovery.current = undefined; return; }
    if (history.denied) { root.current?.querySelector<HTMLButtonElement>("[data-agent-access]")?.focus(); recovery.current = undefined; return; }
    const read = history[intent.source], cancel = root.current?.querySelector<HTMLButtonElement>(`[data-agent-cancel="${intent.source}"]`);
    if (read.loading) { if (!intent.waiting && cancel) { cancel.focus(); intent.focusOwner = cancel; intent.waiting = true; } }
    else { root.current?.querySelector<HTMLButtonElement>(`[data-agent-refresh="${intent.source}"]`)?.focus(); recovery.current = undefined; }
  }, [history.list.loading, history.detail.loading, history.contextKey, history.selectionIdentity, history.denied]);
  function retain() { history.retain(); onRetainWindow?.(); }
  return <section ref={root} className="panel agent-activity-history" aria-labelledby={id}><header><p className="eyebrow">Retained collection evidence</p><h3 id={id}>SQL Agent activity observations</h3>
    <p>The shared half-open window selects repository completion times. Job dates retain their source local spelling. Database filters do not narrow these server-wide observations.</p></header>
    <p className="agent-activity-qualification">Source-column candidates remain unqualified for live Agent state. Running counts, elapsed duration, current step and long-running alerts are unavailable. Existing job failure history uses its own first-observed window.</p>
    {history.denied ? <div><p role="alert">Agent activity access was denied. Received summaries and exact source evidence were removed. Restore target access, then explicitly retry access.</p><button type="button" data-agent-access onClick={() => recover("list", history.retryAccess)}>Retry Agent activity access</button></div> : <>
      <ReadStatus source="list" read={history.list} history={history} retain={retain} recover={recover}/>
      {list && <><p>Received completion window: {format(list.fromUtc)} to {format(list.toUtc)} (end excluded). Original cutoff: {format(list.cutoffUtc)}.</p>
        <details><summary>Activity history receipt identity</summary><p>Current target revision {list.targetRevision} · received epoch {list.readEpoch} · repository read time {format(list.repositoryTimeUtc)}. Native qualification: Absent.</p></details>
        <section aria-label="Agent activity history coverage"><h4>Source accounting</h4><p>{list.coverage.runCount} completed attempts · {list.coverage.retainedReceiptCount} retained receipts · {list.coverage.retainedCaptureCount} retained captures · {list.coverage.completeSourceRunCount} complete source captures.</p><p>{list.coverage.gapRunCount} incomplete attempts · {list.coverage.failedRunCount} failed · {list.coverage.lostRunCount} with source loss · {list.coverage.missingPayloadRunCount} missing expected captures. These counts can overlap.</p>
          {list.reason === "revision_boundary" && <p>Target revisions differ within this window. Each receipt keeps its sampled revision.</p>}
          {list.coverage.runCount === 0 && <p>No completed Agent activity attempts are retained in this window. Review collection health or adjust the range.</p>}</section></>}
      <details key={history.contextKey} onToggle={event => { if (event.currentTarget.open) retain(); }}><summary>Browse Agent activity collection receipts</summary><p>Received pages restore locally. Unread continuations keep their original cutoff; up to 100 pages and 10,000 attempts can be inspected. Job rows are fetched only for an exact selected run.</p>
        {list && <><div className="table-scroll" tabIndex={0} role="region" aria-label="Received Agent activity collection receipts"><table><caption>Actual collection attempts, newest completion first</caption><thead><tr><th scope="col">Repository completion</th><th scope="col">Outcome / source accounting</th><th scope="col">Retained jobs / source rows</th><th scope="col">Run / sample revision</th><th scope="col">Inspect</th></tr></thead><tbody>{list.items.map(item => <tr key={item.runId}><td><time dateTime={item.completedAtUtc}>{format(item.completedAtUtc)}</time></td><td>{label(item.outcome)}<small>{label(item.reason)} · loss {label(item.lossKind)} · minimum lost {item.minimumLostItems} ({item.countIsExact ? "exact" : "inexact"})</small></td><td>{item.retainedJobCount ?? "Unavailable"} / {item.sourceRowsRead ?? "Unavailable"}</td><td>{item.runId}<small>Sample revision {item.sampleTargetRevision}</small></td><td><button type="button" aria-label={`Inspect Agent activity receipt ${item.runId}`} onClick={() => { history.retain(); onSelectReceipt(item); }}>Inspect receipt</button></td></tr>)}</tbody></table></div>
          <nav className="agent-activity-actions" aria-label="Agent activity receipt pages"><span>Page {history.list.index + 1} · {history.list.pages.length} received</span>{["First", "Previous", "Next"].map(action => <button type="button" key={action} disabled={history.list.loading || (action === "Next" ? !history.list.pages[history.list.index + 1] && !list.nextCursor : history.list.index === 0)} onClick={() => { retain(); history.navigate(action === "First" ? 0 : history.list.index + (action === "Next" ? 1 : -1)); }}>{action} Agent activity receipts page</button>)}</nav></>}
      </details>
      <section aria-label="Selected Agent activity receipt"><h4>Selected Agent activity receipt</h4><p>{selected === undefined ? "Default: newest completed attempt in this window, including failed attempts." : selected === null ? "The Agent activity URL selection is invalid. Clear it or inspect a received receipt; no replacement is selected." : `Pinned run ${selected.runId} · sample revision ${selected.sampleTargetRevision}. Paging and refresh preserve this exact identity.`}</p>
        {selected !== undefined && <button type="button" onClick={() => onSelectReceipt(undefined)}>Clear selected Agent activity receipt</button>}
        <ReadStatus source="detail" read={history.detail} history={history} retain={retain} recover={recover}/>
        {detail && <><p>{notices[detail.reason] ?? label(detail.reason)}</p>{detail.receipt && <p>Run {detail.runId} · sample revision {detail.sampleTargetRevision} · completed {format(detail.receipt.completedAtUtc)} · {label(detail.receipt.outcome)}.</p>}
          {detail.capture ? <><p>Source context: {label(detail.capture.gap)}. {detail.capture.gap === "ServiceNotRunning" ? "Agent service was not running in this source bracket; every job candidate is Unknown." : "Candidate states describe retained source columns and cannot establish current Agent liveness."}</p>
            <details><summary>Exact Agent source headers</summary><SourceHeader value={detail.capture.before} title="Before"/><SourceHeader value={detail.capture.after} title="After"/><p>Enrollment revision {detail.capture.enrollmentRevision}. Production admission: false; service/session association qualification: false; native qualification: Absent.</p></details>
            <details key={history.selectionIdentity} onToggle={event => { if (event.currentTarget.open) retain(); }}><summary>Browse jobs from selected Agent activity receipt</summary><p>{detail.capture.jobs.length} retained jobs · {detail.capture.sourceRowsRead} source rows · {detail.capture.truncated ? "source truncated" : "complete source accounting"}. Job dates are offset-free local source values; no UTC conversion or elapsed-time calculation is applied.</p>
              <div className="table-scroll" tabIndex={0} role="region" aria-label="Jobs from selected Agent activity receipt"><table><caption>Bounded source rows from one exact run</caption><thead><tr><th scope="col">Job / session</th><th scope="col">Source-column candidate</th><th scope="col">Requested / queued (local)</th><th scope="col">Start / stop execution (local)</th><th scope="col">Last executed step / date (local)</th></tr></thead><tbody>{detail.capture.jobs.map(job => <tr key={job.jobId}><td>{job.jobId}<small>Session {job.sessionId}</small></td><td>{job.candidateState}<small>Live state unqualified</small></td><td>{job.requestedLocal ?? "Unavailable"}<small>{job.queuedLocal ?? "Unavailable"}</small></td><td>{job.startExecutionLocal ?? "Unavailable"}<small>{job.stopExecutionLocal ?? "Unavailable"}</small></td><td>{job.lastExecutedStepId ?? "Unavailable"}<small>{job.lastExecutedStepLocal ?? "Unavailable"}</small></td></tr>)}</tbody></table></div>
              {detail.capture.jobs.length === 0 && <p>This exact capture reported zero jobs. Empty accounting does not establish Agent liveness.</p>}</details></> : <p>No source capture is available for this exact run. Refresh does not select an older success.</p>}</>}
        {!history.selected && selected !== null && !history.list.loading && <p>No completed attempt is selected.</p>}
      </section>
    </>}
  </section>;
}
function ReadStatus({ source, read, history, retain, recover }: { readonly source: "list" | "detail"; readonly read: AgentActivityRead<unknown>; readonly history: AgentActivityHistoryRead; readonly retain: () => void; readonly recover: (source: "list" | "detail", action: () => void) => void }) {
  const list = source === "list", title = list ? "Agent activity history" : "selected Agent activity receipt";
  return <div role="group" aria-label={`${title} source read`}>{read.loading && <p role="status">Reading {title}; received evidence remains visible.</p>}{read.error && <p role="alert">{read.error}</p>}<div className="agent-activity-actions"><button type="button" data-agent-refresh={source} disabled={read.loading || !list && !validAgentActivitySelection(history.selected)} onClick={() => { retain(); history.refresh(source); }}>{list ? "Refresh Agent activity history" : "Refresh selected Agent activity receipt"}</button>{read.error && read.attempt && <button type="button" disabled={read.loading} onClick={() => recover(source, () => history.retry(source))}>{list ? "Retry Agent activity history read" : "Retry selected Agent activity receipt read"}</button>}{read.loading && <button type="button" data-agent-cancel={source} onClick={() => recover(source, () => history.cancel(source))}>{list ? "Cancel Agent activity history read" : "Cancel selected Agent activity receipt read"}</button>}</div></div>;
}
