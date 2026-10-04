import { useId, useLayoutEffect, useRef } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext.tsx";
import { OverviewChart } from "../overview/OverviewChart.tsx";
import type { OverviewSeries } from "../overview/overviewTypes.ts";
import type { SqlVolumePlanningSelection, SqlVolumePlanningVolume } from "./sqlVolumePlanningModel.ts";
import { maximumSqlVolumePlanningPages, type SqlVolumePlanningRead } from "./useSqlVolumePlanning.ts";
import "./sqlVolumePlanning.css";

const labels: Readonly<Record<string, string>> = {
  observed_volumes: "Retained volume capacities", observed_empty: "Complete empty snapshot", collection_failed: "Collection failed",
  collection_loss: "Source collection loss", payload_missing: "Snapshot payload missing", stored_count_mismatch: "Stored source count differs",
  source_clock_skew: "Source clock skew", source_schema_unsupported: "Unsupported source schema", capacity_unknown: "Capacity unknown",
  collection_gap: "Collection interval gap", snapshot_not_retained: "Exact snapshot not retained", snapshot_outside_window: "Exact snapshot outside the shared window",
  insufficient_history: "Insufficient daily history", not_depleting: "Available capacity is not depleting", unstable_trend: "Unstable daily trend",
  beyond_horizon: "Estimate exceeds the supported horizon", capacity_changed: "Total capacity changed", identity_not_comparable: "Source identity is not comparable",
  visibility_gaps: "Retained source visibility gaps", revision_boundary: "Target revision boundary", lookback_overflow: "Lookback exceeds its evidence bound",
  selected_receipt_unavailable: "Selected receipt does not support planning", retained_lookback: "Complete retained lookback",
};
const reason = (value: string | null) => value === null ? "Not recorded" : labels[value] ?? value.replaceAll("_", " ");
const value = (v: string | number | null) => v === null ? "Not recorded" : String(v);

function PlanningEvidence({ volume, targetId, fromUtc, toUtc }: { readonly volume: SqlVolumePlanningVolume; readonly targetId: string; readonly fromUtc: string; readonly toUtc: string }) {
  const formatTime = useTimeFormatter(), forecast = volume.forecast;
  // The shared plot has a half-open millisecond display window. Include the
  // native inclusive final sample without changing its exact receipt or table.
  const plotToUtc = new Date(Date.parse(toUtc) + 1).toISOString();
  const series: readonly OverviewSeries[] = [{ targetId, label: "Available capacity", metric: "volume.planning.available_bytes", unit: "GiB", state: "available", dimension: volume.volumeKey,
    points: forecast.dailySamples.map(s => ({ timeUtc: s.completedAtUtc, value: Number(s.availableBytes) / 1073741824, samples: 1 })) }];
  return <details><summary>Planning evidence for volume {volume.volumeKey}</summary>
    <p>Daily samples are the last completed retained receipt for each UTC completion date in the inclusive 14-day lookback. Approximate GiB in the plot; exact bytes in the table. Points remain separate across daily intervals. The display endpoint is rounded to the next millisecond to include the final receipt; the exact lookback is unchanged. These samples do not establish physical volume incarnation.</p>
    <dl className="volume-planning-facts"><div><dt>Source identity</dt><dd>{reason(volume.identityKind)} · {volume.volumeKey}</dd></div>
      <div><dt>Mapped files</dt><dd>{volume.mappedFileCount}</dd></div>
      <div><dt>SQL observation time</dt><dd><time dateTime={volume.observedAtUtc}>{formatTime(volume.observedAtUtc)}</time></dd></div>
      <div><dt>Ingestion time</dt><dd><time dateTime={volume.collectedAtUtc}>{formatTime(volume.collectedAtUtc)}</time></dd></div>
      <div><dt>Exact total / available bytes</dt><dd>{value(volume.totalBytes)} / {value(volume.availableBytes)}</dd></div>
      <div><dt>Planning source state</dt><dd>{reason(forecast.state)} · {forecast.sampleDays} received daily samples</dd></div></dl>
    {forecast.dailySamples.length >= 2 && <OverviewChart scrollablePlotLabel={`Volume planning daily capacity plot ${volume.volumeKey}`} series={series} fromUtc={fromUtc} toUtc={plotToUtc} />}
    {forecast.dailySamples.length === 0 ? <p>No qualified daily samples support this proposal. Inspect the source refusal above; missing observations are not zero available bytes.</p> :
      <div className="table-scroll" tabIndex={0} role="region" aria-label={`Qualified daily capacity samples for volume ${volume.volumeKey}`}><table><caption>Received daily source evidence; completion-time axis</caption>
        <thead><tr><th scope="col">Completion</th><th scope="col">Run</th><th scope="col">Total bytes</th><th scope="col">Available bytes</th></tr></thead>
        <tbody>{forecast.dailySamples.map(s => <tr key={s.runId}><td><time dateTime={s.completedAtUtc}>{formatTime(s.completedAtUtc)}</time></td><td>{s.runId}</td><td>{s.totalBytes}</td><td>{s.availableBytes}</td></tr>)}</tbody></table></div>}
  </details>;
}

export function SqlVolumePlanningPanel({ history, explicitSelection, selected, selectionInvalid = false, windowError, onSelectReceipt, onRetainWindow }:
  { readonly history: SqlVolumePlanningRead; readonly explicitSelection?: SqlVolumePlanningSelection; readonly selected?: SqlVolumePlanningSelection | null;
    readonly selectionInvalid?: boolean; readonly windowError?: string; readonly onSelectReceipt: (selection: SqlVolumePlanningSelection | undefined) => void; readonly onRetainWindow?: () => void }) {
  const id = useId(), formatTime = useTimeFormatter(), root = useRef<HTMLElement | null>(null);
  const list = history.list, page = list.pages[list.index], snapshot = history.snapshot, exact = snapshot.pages[0], capacityPage = snapshot.pages[snapshot.index];
  const pin = explicitSelection ?? selected ?? undefined, invalidSelection = selectionInvalid || selected === null;
  const recovery = useRef<{ source: "list" | "snapshot"; context: string; selection: string; waiting: boolean } | undefined>(undefined);
  function recover(source: "list" | "snapshot", action: () => void) { recovery.current = { source, context: history.contextKey, selection: history.selectionIdentity, waiting: false }; action(); }
  useLayoutEffect(() => {
    const intent = recovery.current; if (!intent) return;
    if (intent.context !== history.contextKey || intent.source === "snapshot" && intent.selection !== history.selectionIdentity) { recovery.current = undefined; return; }
    if (history.denied) { root.current?.querySelector<HTMLButtonElement>("[data-volume-planning-access]")?.focus(); recovery.current = undefined; return; }
    const read = intent.source === "list" ? list : snapshot, cancel = root.current?.querySelector<HTMLButtonElement>(`[data-volume-planning-cancel="${intent.source}"]`);
    if (!intent.waiting && read.loading) { cancel?.focus(); intent.waiting = true; }
    else if (!read.loading) { if (!intent.waiting || document.activeElement === cancel || document.activeElement === document.body) root.current?.querySelector<HTMLButtonElement>(`[data-volume-planning-refresh="${intent.source}"]`)?.focus(); recovery.current = undefined; }
  }, [list.loading, snapshot.loading, history.contextKey, history.selectionIdentity, history.denied]);
  function retain() { history.retain(); onRetainWindow?.(); }
  function controls(source: "list" | "snapshot") {
    const read = source === "list" ? list : snapshot, label = source === "list" ? "volume planning history" : "selected volume planning receipt";
    return <div className="volume-planning-actions" role="group" aria-label={`${label} read controls`}>
      <button type="button" data-volume-planning-refresh={source} disabled={read.loading || Boolean(windowError) || source === "snapshot" && invalidSelection} onClick={() => { if (source === "snapshot") retain(); history.refresh(source); }}>Refresh {label}</button>
      {read.loading && <button type="button" data-volume-planning-cancel={source} onClick={() => recover(source, () => history.cancel(source))}>Cancel {label} read</button>}
      {read.error && <button type="button" disabled={read.loading} onClick={() => recover(source, () => history.retry(source))}>Retry {label} read</button>}
    </div>;
  }
  function pager(source: "list" | "snapshot") {
    const read = source === "list" ? list : snapshot, receivedPage = read.pages[read.index], label = source === "list" ? "volume receipts" : "volume capacities";
    const maximum = source === "list" ? maximumSqlVolumePlanningPages : 10;
    return <div className="volume-planning-actions" role="group" aria-label={`${label} page navigation`}>
      <button type="button" disabled={read.loading || read.index === 0} onClick={() => { retain(); history.navigate(0, source); }}>First {label} page</button>
      <button type="button" disabled={read.loading || read.index === 0} onClick={() => { retain(); history.navigate(read.index - 1, source); }}>Previous {label} page</button>
      <button type="button" disabled={read.loading || !receivedPage || read.index + 1 >= read.pages.length && (!receivedPage.nextCursor || read.pages.length >= maximum)} onClick={() => { retain(); history.navigate(read.index + 1, source); }}>Next {label} page</button>
      <p>Page {read.index + 1}; {read.pages.reduce((n, p) => n + p.items.length, 0)} received {source === "list" ? "receipts" : "volumes"}. {receivedPage?.nextCursor ? "More retained rows are unread." : "End of received source."}</p>
    </div>;
  }
  return <section id="sql-volume-capacity-planning" ref={root} className="panel sql-volume-planning" aria-labelledby={id}>
    <h3 id={id}>SQL volume capacity planning</h3>
    <p>Capacities follow actual collection completions in the shared Live or Rewind window. Planning is as of the exact selected receipt, using its preceding 14 days of retained source evidence. Failures are never skipped for an older successful snapshot.</p>
    {windowError && <p role="alert">{windowError}</p>}
    {history.denied ? <><p role="alert">Volume planning access was denied. Received capacities, estimates and cached pages were removed.</p><button type="button" data-volume-planning-access onClick={() => recover("list", history.retryAccess)}>Retry volume planning access</button></> : <>
      <section aria-label="Volume planning source receipts" aria-busy={list.loading}><h4>Volume collection receipts</h4>
        {list.loading && <p role="status">{page ? "Refreshing volume planning history; received evidence remains until a validated replacement arrives…" : "Loading volume planning history…"}</p>}
        {list.error && <p role="alert">{list.error}</p>}{controls("list")}
        {page && <><p className="volume-planning-identity">Completion window {formatTime(page.fromUtc)} to {formatTime(page.toUtc)} · cutoff {formatTime(page.cutoffUtc)}.</p>
          <p role="status">{page.coverage.runCount} completed attempts; {page.coverage.completeRunCount} complete snapshots; {page.coverage.gapRunCount} gaps; {page.coverage.retainedVolumeCount} retained volume rows. Complete source accounting does not establish volume health.</p>
          {page.coverage.hasVisibilityGap && <p>{page.reason === "revision_boundary" ? "The window crosses target revisions; each receipt retains its source revision." : "Failed, lossy, missing or timed source evidence prevents a complete retained view."}</p>}
          {page.items.length === 0 ? <p role="status">No completed volume collection attempts are retained in this window. Choose another window or review collection and retention status; refresh cannot reconstruct missing history.</p> :
            <details onToggle={event => { if (event.currentTarget.open) retain(); }}><summary>Browse volume collection receipts ({page.items.length} on this page)</summary>{pager("list")}
              <div className="table-scroll" tabIndex={0} role="region" aria-label="Received volume collection receipts"><table><caption>Actual attempts, newest completion first</caption><thead><tr><th scope="col">Completion / run</th><th scope="col">Source revision</th><th scope="col">Collection</th><th scope="col">Retained evidence</th><th scope="col">Inspect</th></tr></thead>
                <tbody>{page.items.map(r => <tr key={r.runId}><td><time dateTime={r.completedAtUtc}>{formatTime(r.completedAtUtc)}</time><small>{r.runId}</small></td><td>{r.sampleTargetRevision}</td><td>{reason(r.outcome)}<small>Loss: {reason(r.lossKind)}; minimum lost: {value(r.minimumLostItems)}</small></td><td>{reason(r.reason)}<small>{r.storedVolumeCount} stored / {r.expectedVolumeCount} expected volumes</small></td><td><button type="button" aria-pressed={history.selected?.runId === r.runId && history.selected?.sampleTargetRevision === r.sampleTargetRevision} onClick={() => onSelectReceipt(r)}>Inspect volume receipt {r.runId}</button></td></tr>)}</tbody></table></div>
            </details>}</>}
      </section>
      <section aria-label="Selected volume planning receipt" aria-busy={snapshot.loading}><h4>Selected volume planning receipt</h4>
        {invalidSelection && <p role="alert">The requested volume planning identity is invalid. No other receipt has been selected.</p>}
        {(pin || invalidSelection) && <button type="button" onClick={() => onSelectReceipt(undefined)}>Clear selected volume planning receipt</button>}
        {pin && <button type="button" disabled={list.loading} onClick={() => { onSelectReceipt(undefined); history.refresh("list"); }}>Return volume planning to latest in window</button>}
        {history.selected && <p className="volume-planning-identity">Exact run {history.selected.runId} · sample revision {history.selected.sampleTargetRevision}. {pin ? "Pinned receipt; polling does not choose another attempt." : "Default: most recent received completion, including failures."}</p>}
        {snapshot.loading && <p role="status">{exact ? "Reading selected volume capacities; received evidence remains until a validated replacement arrives…" : "Loading selected volume planning receipt…"}</p>}
        {snapshot.error && <p role="alert">{snapshot.error}</p>}{history.selected && controls("snapshot")}
        {!history.selected && !invalidSelection && !list.loading && <p role="status">No received receipt is available for exact capacity planning.</p>}
        {exact && <><p role="status">{reason(exact.reason)}. {exact.receipt ? `Collection ${reason(exact.receipt.outcome)} completed ${formatTime(exact.receipt.completedAtUtc)}.` : "The exact receipt is unavailable; its identity remains requested without replacement."}</p>
          {exact.receipt?.state !== "available" && <p>Verify the selected run, sample revision, retained window and collection status. Received source gaps cannot support an estimate; refreshing does not repair a past attempt.</p>}
          {exact.lookback && <p>Planning lookback: {formatTime(exact.lookback.fromUtc)} through {formatTime(exact.lookback.asOfUtc)}, inclusive, anchored to this receipt. {reason(exact.lookback.reason)}{exact.lookback.coverage ? `; ${exact.lookback.coverage.runCount} attempts, ${exact.lookback.coverage.gapRunCount} gaps.` : "; bounded source coverage is unavailable."}</p>}
          {exact.receipt?.reason === "observed_empty" && <p>This complete snapshot contains no mapped volumes. Empty source accounting does not establish free space or health.</p>}
          {capacityPage && capacityPage.items.length > 0 && <details onToggle={event => { if (event.currentTarget.open) retain(); }}><summary>Browse volume capacities from selected receipt ({capacityPage.items.length} on this page)</summary>{pager("snapshot")}
            <div className="table-scroll" tabIndex={0} role="region" aria-label="Received volume capacities from selected receipt"><table><caption>Exact capacities from one run and source revision</caption><thead><tr><th scope="col">Volume identity</th><th scope="col">Total bytes</th><th scope="col">Available bytes</th><th scope="col">Planning as of receipt</th><th scope="col">Source times</th></tr></thead><tbody>{capacityPage.items.map(v => <tr key={v.volumeKey}><td>{v.volumeKey}<small>{reason(v.identityKind)} · {v.mappedFileCount} mapped files</small></td><td>{value(v.totalBytes)}</td><td>{value(v.availableBytes)}</td><td>{v.forecast.state === "estimated" ? `Estimated days until full: ${v.forecast.daysUntilFull!.toLocaleString(undefined, { maximumFractionDigits: 1 })}` : v.forecast.state === "full" ? "Full at selected receipt" : `No qualified estimate: ${reason(v.forecast.state)}`}</td><td>Observed {formatTime(v.observedAtUtc)}<small>Ingested {formatTime(v.collectedAtUtc)}</small></td></tr>)}</tbody></table></div>
            {capacityPage.items.map(v => <PlanningEvidence key={v.volumeKey} volume={v} targetId={exact.targetId} fromUtc={exact.lookback!.fromUtc} toUtc={exact.lookback!.asOfUtc} />)}
          </details>}
          <details><summary>Volume planning receipt provenance</summary><p className="volume-planning-identity">Exact source revision {exact.sampleTargetRevision}; current target revision {exact.targetRevision}. Independent snapshot epoch {exact.readEpoch}; cutoff {formatTime(exact.cutoffUtc)}; checked {formatTime(exact.repositoryTimeUtc)}.</p>
            {exact.receipt && <><p>Collection outcome: {reason(exact.receipt.outcome)}. Loss: {reason(exact.receipt.lossKind)}; minimum lost: {value(exact.receipt.minimumLostItems)}.</p><p>{value(exact.receipt.storedVolumeCount)} stored / {value(exact.receipt.expectedVolumeCount)} expected source volumes.</p></>}
            <p>A volume-ID source key is required for an estimate; matching keys do not prove physical incarnation. Estimates assume the qualified recent depletion trend continues. They are planning evidence, not a guarantee or a health rating.</p></details>
        </>}
      </section>
    </>}
  </section>;
}
