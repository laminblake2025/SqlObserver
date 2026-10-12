import { useRef } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext.tsx";
import { EvidenceDisclosure } from "../../components/CompactEvidenceStatus.tsx";
import { matchesServerLifecycleSelection, type ServerLifecycleEngineProof, type ServerLifecycleEvent, type ServerLifecycleSelection } from "./serverLifecycleHistoryModel.ts";
import type { ServerLifecycleHistoryRead } from "./useServerLifecycleHistory.ts";
import "./serverLifecycleHistory.css";

const title = (event: ServerLifecycleEvent) => event.kind === "restart_detected" ? "Sampled restart detected" : "Responding node changed";
const selection = (event: ServerLifecycleEvent): ServerLifecycleSelection => ({eventId:event.eventId,sampleTargetRevision:event.sampleTargetRevision,afterRunId:event.afterRunId,detectedAtUtc:event.detectedAtUtc});

function EngineProof({label,proof}: {readonly label:string;readonly proof:ServerLifecycleEngineProof}) {
  return <section className="server-lifecycle-engine-proof" aria-label={`${label} immutable engine evidence`}>
    <h6>{label} engine evidence</h6>
    <dl className="server-lifecycle-facts">
      <dt>Physical node fingerprint</dt><dd>{proof.physicalNodeFingerprint}</dd>
      <dt>Logical instance fingerprint</dt><dd>{proof.logicalInstanceFingerprint}</dd>
      <dt>Startup incarnation fingerprint</dt><dd>{proof.startupIncarnationFingerprint}</dd>
      <dt>Process ID</dt><dd>{proof.processId}</dd>
      <dt>Source local startup</dt><dd>{proof.sourceStartLocal} <span className="table-note">(offset unavailable; not an occurrence UTC)</span></dd>
      <dt>Startup milliseconds counter</dt><dd>{proof.startMsTicks}</dd>
      <dt>Current milliseconds counter</dt><dd>{proof.currentMsTicks}</dd>
    </dl>
  </section>;
}

export function ServerLifecycleHistoryPanel({history,selectedEvent,onSelectEvent,onRetainWindow}: {
  readonly history:ServerLifecycleHistoryRead;
  readonly selectedEvent?:ServerLifecycleSelection|null;
  readonly onSelectEvent:(event:ServerLifecycleSelection|undefined)=>void;
  readonly onRetainWindow?:()=>void;
}) {
  const format = useTimeFormatter(), {list,detail} = history, page = list.pages[list.index], event = detail.receipt?.event;
  const received = list.pages.reduce((sum,item)=>sum+item.items.length,0), browser = useRef<HTMLDetailsElement>(null);
  return <section className="panel server-lifecycle-history" aria-label="Server lifecycle history" aria-busy={list.loading||detail.loading}>
    <h4>Server lifecycle history</h4>
    <p className="table-note">Sampled engine changes follow the shared window. Detection is a completed collection time, not an exact occurrence time or cause. Availability-group failover evidence is unqualified.</p>
    {history.denied ? <div className="server-lifecycle-access">
      <p role="alert">Server lifecycle history access was denied. Protected pages and selected evidence were removed. Restore read access for this server, then explicitly retry.</p>
      <button type="button" onClick={history.retryAccess}>Retry server lifecycle history access</button>
    </div> : <>
      {selectedEvent===null && <p role="status">The requested server lifecycle event is invalid for this server and window. No other event is selected.</p>}
      {selectedEvent!==undefined && <button type="button" className="secondary-button" onClick={()=>onSelectEvent(undefined)}>Clear selected server lifecycle event</button>}
      {selectedEvent && <section className="server-lifecycle-selected" aria-label="Selected server lifecycle event" aria-busy={detail.loading}>
        <h5>Selected server lifecycle event</h5>
        <p className="table-note">Requested event {selectedEvent.eventId} · sample target revision {selectedEvent.sampleTargetRevision} · after-run {selectedEvent.afterRunId}. Exact lookup does not substitute a newer event or scan list pages.</p>
        {detail.loading && <p role="status">{detail.receipt ? "Refreshing this exact event; its previously received receipt remains evidence." : "Loading the exact selected server lifecycle event…"}</p>}
        {detail.error && <p role="alert">{detail.error}</p>}
        {detail.receipt && <>
          <p className="activity-evidence">Exact event read checked {format(detail.receipt.repositoryTimeUtc)} · cutoff {format(detail.receipt.cutoffUtc)}. This separate lookup does not establish complete window coverage.</p>
          {event ? <>
            <h6>{title(event)}</h6>
            <dl className="server-lifecycle-facts">
              <dt>Detected at completion</dt><dd><time dateTime={event.detectedAtUtc} title={event.detectedAtUtc}>{format(event.detectedAtUtc)}</time></dd>
              <dt>Sampled observation bracket</dt><dd><time dateTime={event.beforeObservedAtUtc} title={event.beforeObservedAtUtc}>{format(event.beforeObservedAtUtc)}</time> to <time dateTime={event.afterObservedAtUtc} title={event.afterObservedAtUtc}>{format(event.afterObservedAtUtc)}</time></dd>
              <dt>Exact occurrence UTC</dt><dd>Unknown. The observation bracket does not establish the exact occurrence or cause.</dd>
              <dt>Source target revision</dt><dd>{event.sampleTargetRevision}; current read revision fence {detail.receipt.targetRevision}</dd>
            </dl>
            <EvidenceDisclosure label="Immutable before and after engine evidence">
              <p>These proofs belong to this event's two retained source runs. They are not filled from the latest server state. Fingerprints are opaque identities, not server names.</p>
              <div className="server-lifecycle-proof-pair">
                {detail.receipt.beforeEngine && <EngineProof label="Before" proof={detail.receipt.beforeEngine}/>}
                {detail.receipt.afterEngine && <EngineProof label="After" proof={detail.receipt.afterEngine}/>}
              </div>
              <dl className="server-lifecycle-facts"><dt>Before run</dt><dd>{event.beforeRunId}</dd><dt>After run</dt><dd>{event.afterRunId}</dd><dt>Affected identity fingerprint</dt><dd>{event.affectedIdentityFingerprint}</dd><dt>Source schema</dt><dd>{event.sourceSchemaVersion}; availability-group consistency remains unqualified</dd></dl>
            </EvidenceDisclosure>
          </> : <p role="status">This exact event is not retained in the requested server, revision and window. Verify its identity or choose a retained range; no other event is selected.</p>}
        </>}
        <div className="form-actions" role="group" aria-label="Selected server lifecycle event controls">
          <button type="button" disabled={detail.loading} onClick={()=>void history.readDetail()}>Refresh selected server lifecycle event</button>
          {detail.error && <button type="button" disabled={detail.loading} onClick={()=>void history.readDetail()}>Retry selected server lifecycle event read</button>}
          {detail.loading && <button type="button" onClick={()=>history.cancel("detail")}>Cancel selected server lifecycle event read</button>}
        </div>
      </section>}

      <section className="server-lifecycle-source-read" aria-label="Server lifecycle source read" aria-busy={list.loading}>
        {list.loading && <p role="status">{page ? "Reading server lifecycle history; same-window received pages remain evidence." : "Loading retained server lifecycle events…"}</p>}
        {list.error && <p role="alert">{list.error}</p>}
        <div className="form-actions" role="group" aria-label="Server lifecycle source controls">
          <button type="button" disabled={list.loading} onClick={history.refreshList}>Refresh server lifecycle history</button>
          {list.error && <button type="button" disabled={list.loading} onClick={history.retryList}>Retry server lifecycle history read</button>}
          {list.loading && <button type="button" onClick={()=>history.cancel("list")}>Cancel server lifecycle history read</button>}
        </div>
      </section>

      <section className="compact-evidence-footer" aria-label="Server lifecycle evidence status">
        <p>{page ? `${received} received of ${page.coverage.eventCount} retained events · ${page.coverage.completeRunCount} engine-qualified of ${page.coverage.runCount} completed source attempts · ${page.coverage.gapRunCount} source gaps.` : "No received server lifecycle window coverage."}</p>
        {page?.coverage.hasVisibilityGap && <p className="server-lifecycle-gap">Source or comparison continuity is incomplete. Missing events do not establish normal health.</p>}
        {page?.coverage.retainedSnapshotCount===0 && <p>Lifecycle snapshots are unavailable in this window. Check that lifecycle collection is supported and enabled and can connect. A Target Administrator can review collection settings and source read permissions. Choose a window with retained observations; refreshing cannot recreate missing history.</p>}
        <EvidenceDisclosure label="Server lifecycle source coverage details" attention={!page||page.state!=="available"}>
          {page ? <>
            <p>{page.coverage.retainedSnapshotCount} retained snapshots · {page.coverage.failedRunCount} failed attempts · {page.coverage.lostRunCount} attempts with recorded source loss · {page.coverage.retiredRunCount} proven retired receipts · {page.coverage.comparisonBoundaryCount} comparison boundaries. Loss can overlap engine-qualified reads; these counts are not additive. A first baseline is a comparison boundary, not a restart.</p>
            <p>Capture boundary {page.coverage.captureBoundaryUtc ? format(page.coverage.captureBoundaryUtc) : "not established"}. Source attempts and cadence describe received coverage, not a health verdict. Availability-group source qualification: {page.coverage.availabilityQualification}; no physical failover is inferred.</p>
            <p>Received window {format(page.fromUtc)} to {format(page.toUtc)} (exclusive) · cutoff {format(page.cutoffUtc)} · repository checked {format(page.repositoryTimeUtc)}. Read epoch {page.readEpoch}. A changed or expired unread continuation requires explicit refresh; visited pages remain evidence.</p>
          </> : <p>Retry a failed read, verify server read access or choose another retained window. Missing history is not an all-clear signal.</p>}
        </EvidenceDisclosure>
      </section>

      <details ref={browser} className="server-lifecycle-browser">
        <summary onClick={()=>{if(!browser.current?.open)onRetainWindow?.();}}>Browse server lifecycle events</summary>
        <section aria-label="Received server lifecycle events" aria-busy={list.loading}>
          <h5>Received server lifecycle events</h5>
          {page && (page.items.length ? <div className="table-scroll" role="region" aria-label="Received server lifecycle event table" tabIndex={0}>
            <table><caption>Retained sampled engine changes, ordered by detection completion UTC and event identity</caption><thead><tr><th scope="col">Sampled change</th><th scope="col">Detection / observation bracket</th><th scope="col">Source identity</th></tr></thead>
              <tbody>{page.items.map(item=><tr key={item.eventId}>
                <td><button type="button" className="secondary-button" aria-label={`Inspect server lifecycle event ${item.eventId}: ${title(item)}`} aria-pressed={Boolean(selectedEvent&&matchesServerLifecycleSelection(item,selectedEvent))} onClick={()=>onSelectEvent(selection(item))}>{title(item)}</button><small>{item.eventId}</small></td>
                <td><time dateTime={item.detectedAtUtc} title={item.detectedAtUtc}>{format(item.detectedAtUtc)}</time><small>Observed {format(item.beforeObservedAtUtc)} to {format(item.afterObservedAtUtc)}</small><small>Exact occurrence UTC unknown</small></td>
                <td>Sample target revision {item.sampleTargetRevision}<small>Before run {item.beforeRunId}</small><small>After run {item.afterRunId}</small></td>
              </tr>)}</tbody>
            </table>
          </div> : <p>No sampled lifecycle events were retained in this window. Check source coverage and comparison boundaries; zero events do not establish normal health.</p>)}
          <p className="table-note">{received} received events in {list.pages.length} pages{page ? ` · page ${list.index+1}` : ""}. Read bound: 100 pages / 10,000 events and completed source attempts. {page?.nextCursor ? "Unread native pages remain." : page ? "End of this received read." : "No receipt yet."} Cached visited pages restore without another request. Browsing retains the effective shared window in Rewind.</p>
          <nav className="form-actions" aria-label="Server lifecycle history pages">
            <button type="button" disabled={list.loading||list.index===0} onClick={()=>history.navigate(0)}>First server lifecycle history page</button>
            <button type="button" disabled={list.loading||list.index===0} onClick={()=>history.navigate(list.index-1)}>Previous server lifecycle history page</button>
            <button type="button" disabled={list.loading||!list.pages[list.index+1]&&!page?.nextCursor||list.index+1>=100} onClick={()=>{onRetainWindow?.();history.navigate(list.index+1);}}>Next server lifecycle history page</button>
          </nav>
        </section>
      </details>
    </>}
  </section>;
}
