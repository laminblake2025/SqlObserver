import { useLayoutEffect, useRef } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import type { ReplicationRead, ReplicationWindowHistoryRead } from "./useReplicationWindowHistory.ts";
import { validReplicationSelection, type ReplicationSelection, type ReplicationHeader } from "./replicationWindowHistoryModel.ts";
import "./replicationWindowHistory.css";

const notices: Readonly<Record<string, string>> = {
  observed_rows: "The retained rows have complete source accounting. Reported legacy status remains unqualified; this does not establish current replication health.",
  observed_empty: "This complete collection reported no replication rows. Empty source accounting does not establish a configured topology or healthy replication.",
  collection_failed: "This collection attempt failed. No older successful observation is substituted. Check collection health, then refresh this source.",
  collection_loss: "This capture reported source loss. Retained rows do not establish complete replication visibility. Check collection limits and health.",
  collection_gap: "Collection gaps remain. These receipts do not establish continuous replication state. Check collection scheduling and health.",
  source_schema_unsupported: "The retained source schema is unsupported. Check the collector version; no replacement receipt is selected.",
  payload_missing: "The canonical attempt remains but expected source rows are missing. Check retention and collection health; no older success is substituted.",
  stored_count_mismatch: "The retained row count differs from source accounting. Treat this evidence as incomplete and check collection health.",
  source_clock_skew: "Monitor capture time conflicts with repository completion or cutoff. Review clock evidence; timestamps are shown without adjustment.",
  source_partial: "This source reported reduced visibility. Check distribution binding and source permissions; missing queue and latency values are unavailable, not zero.",
  snapshot_not_retained: "This exact run and sample revision is not retained. No replacement is selected. Clear the pin to browse the newest completed attempt in this window.",
  snapshot_outside_window: "This exact receipt is outside the shared completion window. No replacement is selected. Change the shared window or clear the pin.",
};

export function ReplicationWindowHistoryPanel({ history, selected, onSelectReceipt, onRetainWindow }: {
  readonly history: ReplicationWindowHistoryRead; readonly selected?: ReplicationSelection | null;
  readonly onSelectReceipt: (selection: ReplicationSelection | undefined) => void; readonly onRetainWindow?: () => void;
}) {
  const format = useTimeFormatter(), list = history.list.pages[history.list.index], detail = history.detail.pages[history.detail.index];
  const root = useRef<HTMLElement | null>(null);
  const recovery = useRef<{ source: "list" | "detail"; context: string; selection: string; focusOwner: Element | null; waiting: boolean } | undefined>(undefined);
  function recover(source: "list" | "detail", action: () => void) {
    recovery.current = { source, context: history.contextKey, selection: history.selectionIdentity, focusOwner: document.activeElement, waiting: false };
    action();
  }
  useLayoutEffect(() => {
    const intent = recovery.current;
    if (!intent) return;
    if (intent.context !== history.contextKey || intent.source === "detail" && intent.selection !== history.selectionIdentity) { recovery.current = undefined; return; }
    // A user who moved focus elsewhere owns it; a retired read cannot reclaim it.
    if (document.activeElement !== intent.focusOwner && document.activeElement !== document.body) { recovery.current = undefined; return; }
    if (history.denied) { root.current?.querySelector<HTMLButtonElement>("[data-replication-access]")?.focus(); recovery.current = undefined; return; }
    const read = history[intent.source], cancel = root.current?.querySelector<HTMLButtonElement>(`[data-replication-cancel="${intent.source}"]`);
    if (read.loading) {
      if (!intent.waiting && cancel) { cancel.focus(); intent.focusOwner = cancel; intent.waiting = true; }
    } else {
      root.current?.querySelector<HTMLButtonElement>(`[data-replication-refresh="${intent.source}"]`)?.focus();
      recovery.current = undefined;
    }
  }, [history.list.loading, history.detail.loading, history.contextKey, history.selectionIdentity, history.denied]);
  function retain() { history.retain(); onRetainWindow?.(); }
  return <section ref={root} className="replication-window-history panel" aria-label="Replication window history">
    <header><p className="eyebrow">Retained collection evidence</p><h3>Replication window history</h3><p>The shared half-open window selects repository completion times. Each exact receipt keeps its run and sampled target revision. Database filters do not narrow this server-wide source.</p></header>
    <p className="replication-qualification">Reported legacy status is unqualified. Complete source accounting does not establish replication health or current Agent liveness. Topology, delivery rate and last-success age were not retained and cannot be reconstructed.</p>
    {history.denied ? <div><p role="alert">Replication evidence access was denied. Received receipts and observations were removed. Restore target access, then explicitly retry access.</p><button type="button" data-replication-access onClick={() => recover("list", history.retryAccess)}>Retry replication history access</button></div> : <>
      <ReadStatus source="list" read={history.list} history={history} retain={retain} recover={recover} />
      {list && <><ReceiptHeader page={list} /><section aria-label="Replication history coverage"><h4>Source accounting</h4><p>{list.coverage.runCount} completed attempts · {list.coverage.completeRunCount} complete source receipts · {list.coverage.gapRunCount} incomplete attempts · {list.coverage.retainedRowCount} retained rows bound to these attempts.</p><p>{list.coverage.failedRunCount} failed · {list.coverage.lostRunCount} with source loss · {list.coverage.missingPayloadRunCount} missing expected payload · {list.coverage.emptyRunCount} empty snapshots. These counts can overlap.</p>
        {list.coverage.unboundSourceRowCount > 0 && <p>{list.coverage.unboundSourceRowCount} retained source rows lack canonical completion identity. Their ingestion times locate this source gap; they are not counted as completed attempts or exposed as invented receipts.</p>}
        {list.reason === "revision_boundary" && <p>Source revisions differ within this window. Each receipt keeps its sampled revision; current revision is not substituted.</p>}
        {list.coverage.hasVisibilityGap && <p>Visibility gaps remain. No continuous replication state or health rating is inferred.</p>}
        {list.coverage.runCount === 0 && <p>No completed replication collection attempts are retained in this window. Check collection health or change the shared range.</p>}
      </section></>}
      <details key={history.contextKey} className="replication-receipt-browser" onToggle={event => { if (event.currentTarget.open) retain(); }}><summary>Browse replication collection receipts</summary><p>Browsing fixes the received shared window when supported. Received pages restore locally; unread continuations keep their original cutoff and expire after 15 minutes. Up to 205 receipt pages and 20,480 attempts can be inspected.</p>
        {list && <><div className="table-scroll" role="region" aria-label="Received replication collection receipts" tabIndex={0}><table><caption>Actual collection attempts, newest completion first</caption><thead><tr><th scope="col">Repository completion</th><th scope="col">Collection outcome</th><th scope="col">Source accounting</th><th scope="col">Rows retained / expected</th><th scope="col">Run / sampled revision</th><th scope="col">Inspect</th></tr></thead><tbody>{list.items.map(item => <tr key={item.runId}><td><time dateTime={item.completedAtUtc}>{format(item.completedAtUtc)}</time></td><td>{item.outcome}<small>Loss: {item.lossKind ?? "Not recorded"}; minimum lost: {item.minimumLostItems ?? "Not recorded"}</small></td><td>{item.state}<small>{item.reason.replaceAll("_", " ")}</small></td><td>{item.storedRowCount} / {item.expectedRowCount}</td><td>{item.runId}<small>Sample revision {item.sampleTargetRevision}</small></td><td><button type="button" aria-label={`Inspect replication receipt ${item.runId}`} onClick={() => { history.retain(); onSelectReceipt(item); }}>Inspect replication receipt</button></td></tr>)}</tbody></table></div><Pager source="list" history={history} retain={retain} /></>}
      </details>
      <section aria-label="Selected replication receipt"><h4>Selected replication receipt</h4><p>{selected === undefined ? "Default: newest completed attempt in this window, including failed attempts." : selected === null ? "The replication URL selection is invalid. Clear it or explicitly inspect a valid receipt; no replacement is selected." : `Pinned run ${selected.runId} · sample revision ${selected.sampleTargetRevision}. Paging and refresh do not replace this exact identity.`}</p>
        {selected !== undefined && <button type="button" onClick={() => onSelectReceipt(undefined)}>Clear selected replication receipt</button>}
        <button type="button" disabled={history.list.loading} onClick={() => { onSelectReceipt(undefined); history.refresh("list"); }}>Return replication to latest in window</button>
        <ReadStatus source="detail" read={history.detail} history={history} retain={retain} recover={recover} />
        {detail && <><ReceiptHeader page={detail} /><p>{notices[detail.reason] ?? "Retained source evidence is incomplete. Check collection health and refresh this exact source."}</p>
          {detail.receipt && <><dl className="replication-facts"><div><dt>Run / sampled revision</dt><dd>{detail.runId} / {detail.sampleTargetRevision}</dd></div><div><dt>Collection outcome</dt><dd>{detail.receipt.outcome}</dd></div><div><dt>Repository completion time</dt><dd><time dateTime={detail.receipt.completedAtUtc}>{format(detail.receipt.completedAtUtc)}</time></dd></div><div><dt>Monitor capture time</dt><dd>{detail.receipt.monitorObservedAtUtc === null ? "Not recorded as one shared row timestamp" : <time dateTime={detail.receipt.monitorObservedAtUtc}>{format(detail.receipt.monitorObservedAtUtc)}</time>}</dd></div><div><dt>Retained / expected source rows</dt><dd>{detail.receipt.storedRowCount} / {detail.receipt.expectedRowCount}</dd></div><div><dt>Source loss / minimum lost items</dt><dd>{detail.receipt.lossKind ?? "Not recorded"} / {detail.receipt.minimumLostItems ?? "Not recorded"}</dd></div></dl><p>Monitor capture time was stamped by the monitoring worker. Ingestion time is a separate repository timestamp. Neither is a native SQL event time or an inferred replication delivery time.</p></>}
          {detail.receipt && <details key={history.selectionIdentity} className="replication-observations-browser" onToggle={event => { if (event.currentTarget.open) retain(); }}><summary>Browse replication observations from selected receipt</summary><p>Reported status is retained verbatim as legacy evidence. A reported healthy value is not a qualified health verdict. Pending commands are command counts; latency is a reported delivery statistic in seconds, not a continuously increasing queue age.</p>
            <div className="table-scroll" role="region" aria-label="Received replication observations from selected receipt" tabIndex={0}><table><caption>Retained observations from one exact run and sample revision</caption><thead><tr><th scope="col">Opaque row / subscription identity</th><th scope="col">Reported role / legacy status</th><th scope="col">Pending commands</th><th scope="col">Reported latency (seconds)</th><th scope="col">Source visibility</th><th scope="col">Monitor capture time</th><th scope="col">Ingestion time</th></tr></thead><tbody>{detail.items.map(row => <tr key={row.rowIdentity}><td>{row.rowIdentity}<small>Subscription: {row.subscriptionFingerprint ?? "Not retained"}</small></td><td>{row.role}<small>Reported legacy status: {row.reportedStatus}</small></td><td>{row.pendingCommands ?? "Unavailable"}</td><td>{row.latencySeconds === null ? "Unavailable" : String(row.latencySeconds)}</td><td>{row.sourceCoverage.replaceAll("_", " ")}<small>Source state available: {row.stateAvailable ? "Yes" : "No"}</small></td><td><time dateTime={row.monitorObservedAtUtc}>{format(row.monitorObservedAtUtc)}</time></td><td><time dateTime={row.collectedAtUtc}>{format(row.collectedAtUtc)}</time></td></tr>)}</tbody></table></div>
            {!detail.items.length && <p>{detail.receipt.reason === "observed_empty" ? "This exact collection reported no replication rows." : "No observation rows are retained for this exact receipt. No older snapshot is substituted."}</p>}
            <Pager source="detail" history={history} retain={retain} />
          </details>}
        </>}
      </section>
    </>}
  </section>;
}
function ReceiptHeader({ page }: { readonly page: ReplicationHeader }) { const format = useTimeFormatter(); return <div className="replication-receipt"><p>Received completion window: {format(page.fromUtc)} to {format(page.toUtc)} (end excluded). Original cutoff: {format(page.cutoffUtc)} · repository read time: {format(page.repositoryTimeUtc)}. Source accounting: {page.state}.</p><details><summary>Receipt identity</summary><p>Current target revision {page.targetRevision} · received epoch {page.readEpoch}. Status qualification: legacy unqualified; observation time basis: monitor capture.</p></details></div>; }
function ReadStatus({ source, read, history, retain, recover }: { readonly source: "list" | "detail"; readonly read: ReplicationRead<unknown>; readonly history: ReplicationWindowHistoryRead; readonly retain: () => void; readonly recover: (source: "list" | "detail", action: () => void) => void }) {
  const list = source === "list", label = list ? "replication history" : "selected replication receipt";
  return <div role="group" aria-label={list ? "Replication history source read" : "Selected replication receipt source read"}>{read.loading && <p role="status">Reading {label}; received evidence remains visible.</p>}{read.error && <p role="alert">{read.error}</p>}{(read.loading || read.error) && read.pages.length > 0 && <p>Showing previously received evidence. This read does not establish current replication state.</p>}<div className="replication-history-actions"><button type="button" data-replication-refresh={source} disabled={read.loading || !list && !validReplicationSelection(history.selected)} onClick={() => { retain(); history.refresh(source); }}>{list ? "Refresh replication window history" : "Refresh selected replication receipt"}</button>{read.error && read.attempt && <button type="button" disabled={read.loading} onClick={() => recover(source, () => history.retry(source))}>{list ? "Retry replication history read" : "Retry selected replication receipt read"}</button>}{read.loading && <button type="button" data-replication-cancel={source} onClick={() => recover(source, () => history.cancel(source))}>{list ? "Cancel replication history read" : "Cancel selected replication receipt read"}</button>}</div></div>;
}
function Pager({ source, history, retain }: { readonly source: "list" | "detail"; readonly history: ReplicationWindowHistoryRead; readonly retain: () => void }) { const read = history[source], page = read.pages[read.index], name = source === "list" ? "replication receipts" : "replication observations"; return <nav className="replication-history-actions" aria-label={`${name} pages`}><span>Page {read.index + 1} · {read.pages.length} received</span>{["First", "Previous", "Next"].map(action => <button type="button" key={action} disabled={read.loading || (action === "Next" ? !read.pages[read.index + 1] && !page?.nextCursor : read.index === 0)} onClick={() => { retain(); history.navigate(action === "First" ? 0 : read.index + (action === "Next" ? 1 : -1), source); }}>{action} {name} page</button>)}</nav>; }
