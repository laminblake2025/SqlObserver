import type { LiveRow, QueryDetail } from "./liveActivityApi";
import type { ActivityCapture } from "./activityCaptureModel";
import { buildBlockingTree, rowsForBlockingGroup } from "./blockingModel";
import { BlockingWorkspace } from "./BlockingWorkspace";
import { buildSessionTriage, formatElapsed, formatRate, rowRates, type TriageThresholds } from "./sessionTriageModel";

export type ActivityInspectorTab = "overview" | "chain" | "sql";
export function ActivitySessionInspector({ row, capture, thresholds, tab, onTab, onClose, onInspect, onSelectChain, selectedDatabaseId, filtered, canReadText, detail, detailError, queryLoading, onLoadSql, timeLabel }: {
  row: LiveRow; capture: ActivityCapture; thresholds: TriageThresholds; tab: ActivityInspectorTab; onTab: (tab: ActivityInspectorTab) => void;
  onClose: () => void; onInspect: (row: LiveRow) => void; onSelectChain: (key: string) => void; selectedDatabaseId?: number; filtered: boolean;
  canReadText: boolean; detail?: QueryDetail; detailError?: string; queryLoading: boolean; onLoadSql: () => void; timeLabel: (value: string) => string;
}) {
  const session = buildSessionTriage(capture.rows, thresholds).find(group => group.rows.some(value => value.identity === row.identity));
  const groups = buildBlockingTree(capture.rows);
  const group = groups.find(value => rowsForBlockingGroup(capture.rows, value).some(member => member.sessionId === row.sessionId));
  const rates = rowRates(row);
  const rate = (value: bigint | undefined, unit: string) => value === undefined ? "Rate unavailable" : `${formatRate(value)} ${unit}`;
  return <section id="session-details" className="activity-inspector" aria-label="Session details">
    <div className="health-heading-row"><div><h4>Session {row.sessionId}</h4><p className="activity-inspector-subtitle">{row.isUser ? "User" : "System"} session · {row.requestId === null ? "Idle" : `Request ${row.requestId}`}{session && session.rows.length > 1 ? ` · ${session.rows.length} captured requests` : ""}</p></div><button type="button" onClick={onClose}>Close details</button></div>
    <p className="table-note">Observed {capture.observedUtc ? timeLabel(capture.observedUtc) : "Unavailable"} · capture pinned</p>
    {selectedDatabaseId !== undefined && row.databaseId !== selectedDatabaseId && <p role="status">Context outside the selected database. This session is shown to preserve the captured blocking relationship.</p>}
    <div>{session?.signals.map(signal => <span key={signal.label} className={`activity-signal ${signal.tone}`} title={signal.detail}>{signal.label}</span>)}</div>
    <div className="activity-inspector-tabs" aria-label="Session inspection views">{([['overview', 'Overview'], ['chain', 'Blocking chain'], ['sql', 'Captured SQL']] as const).map(([value, label]) => <button type="button" key={value} aria-pressed={tab === value} onClick={() => onTab(value)}>{label}</button>)}</div>
    {tab === "overview" && <><dl>{[
      ["Database", row.databaseName ?? (row.databaseId === null ? "Unavailable" : `Database ${row.databaseId}`)], ["Application", row.application ?? "Unavailable"], ["Login", row.login ?? "Unavailable"], ["Client", row.clientHost ?? "Unavailable"], ["State / command", `${row.status} / ${row.command ?? "Unavailable"}`], ["Wait", row.waitType ?? "None captured"], ["Request elapsed", row.requestId === null ? "Idle · no active request" : formatElapsed(row.elapsedMs)], ["CPU rate", rate(rates.cpu, "ms/s")], ["Physical I/O rate", rate(rates.io, "ops/s")], ["Logical reads", rate(rates.logical, "reads/s")],
    ].map(([label, value]) => <div className="activity-detail-field" key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl>{session?.signals.some(signal => signal.tone === "workload") && <div><h5>Workload signals</h5><p className="table-note">Signals across this session’s captured requests. These are investigation thresholds.</p><ul>{session.signals.filter(signal => signal.tone === "workload").map(signal => <li key={signal.label}>{signal.label} · {signal.detail}</li>)}</ul></div>}</>}
    {tab === "chain" && <BlockingWorkspace page={capture} groups={group ? [group] : []} selectedGroupKey={group?.key ?? ""} selectedIdentity={row.identity} onSelectGroup={onSelectChain} onInspect={onInspect} selectedDatabaseId={selectedDatabaseId} filtered={filtered} paged={capture.partial} coverage={capture.coverageMessage ?? `${capture.pagesRead} metadata page${capture.pagesRead === 1 ? "" : "s"} · ${capture.rows.length} captured request rows`}/>}
    {tab === "sql" && <div><h5>Captured executing SQL</h5><p role={canReadText && detailError ? "alert" : "status"}>{row.queryId && !canReadText ? "Captured SQL is hidden. Query Text Reader access is required for this server." : <>{row.queryState === "truncated" ? "Statement truncated to 16 KiB. " : ""}{detailError ?? detail?.state ?? (queryLoading ? "Loading protected query…" : row.queryId ? "Captured SQL requires a deliberate request and Query Text Reader access." : row.queryState)}</>}</p>
      {row.queryId && canReadText && <button type="button" disabled={queryLoading} onClick={onLoadSql}>Load captured SQL</button>}
      {detail?.text && <pre className="query-text" tabIndex={0} aria-label="Captured executing SQL">{detail.text}</pre>}
    </div>}
    <details className="supporting-evidence"><summary>Captured metrics & lifetime</summary><p>Engine startup: {row.engineStartup}; session login: {row.sessionLogin}; request start: {row.requestStart ?? "idle"} (SQL Server local timestamps).</p><p>Cumulative CPU {row.cpuMs} ms · memory {row.memoryBytes} bytes · logical reads {row.logicalReads} · reads {row.reads} · writes {row.writes} · elapsed {row.elapsedMs} ms.</p><p>{row.delta ? `Changes over ${row.delta.seconds}s: CPU +${row.delta.cpuMs} ms; reads +${row.delta.reads}; writes +${row.delta.writes}; logical reads +${row.delta.logicalReads}.` : "Interval changes unavailable: no matching complete baseline, restarted lifetime or reset counters."}</p><p>Rates use valid interval changes. Request elapsed is not wait duration. Short requests between samples may not appear.</p></details>
    <p className="table-note">Passive inspection · no session commands are issued.</p>
  </section>;
}
