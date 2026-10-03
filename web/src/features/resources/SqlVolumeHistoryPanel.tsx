import { useTimeDisplay } from "../../TimeDisplayContext.tsx";
import { formatDisplayTime } from "../../timeDisplay.ts";
import { useSqlVolumeHistory, type VolumeHistoryReadState, type VolumeHistorySource } from "./useSqlVolumeHistory.ts";
import { comparisonEligibility, identityKey, validVolumeHistoryIdentity, type VolumeHistoryIdentity, type VolumeHistoryCatalogPage, type VolumeHistoryPage, type VolumeHistoryReadPage } from "./sqlVolumeHistoryModel.ts";
import { SqlVolumeHistoryChart } from "./SqlVolumeHistoryChart.tsx";
import "./sqlVolumeHistory.css";
export interface SqlVolumeHistoryPanelProps {
  readonly instanceId: string; readonly targetRevision: number; readonly fromUtc: string; readonly toUtc: string; readonly compare: boolean; readonly refresh: number;
  readonly selectedVolume?: VolumeHistoryIdentity | null; readonly onSelectVolume: (volume: VolumeHistoryIdentity | undefined) => void;
  readonly crosshairUtc?: string | null; readonly onCrosshairChange?: (utc: string | null) => void;
  readonly onSelectWindow?: (window: { fromUtc: string; toUtc: string }) => void;
}
const sourceLabels = { catalog: "volume identities", selected: "selected volume history", previous: "previous volume history" };
function readNotice(page: VolumeHistoryReadPage) {
  switch (page.reason) { case "no_completed_runs": return "No completed volume collections were retained in this window. Choose a retained window or check the volume collection schedule.";
    case "retained_evidence_unavailable": return "Retained volume evidence is unavailable. Check retention and collection status, or choose a narrower retained window.";
    case "visibility_gaps": return "This received window has visibility gaps. Missing volume rows, failed or lossy scans and unknown capacity are not zero space.";
    case "revision_boundary": return "sampleTargetRevision" in page
      ? "This window crosses target revisions. Only the exact selected sample revision is shown; comparison is unavailable across this boundary."
      : "This window crosses target revisions. The catalog lists received revision and volume-key identities; comparisons are unavailable across this boundary.";
    default: return "Received volume evidence from retained completed collections; this does not prove every scheduled collection occurred."; }
}
export function SqlVolumeHistoryPanel(props: SqlVolumeHistoryPanelProps) {
  const { instanceId, targetRevision, fromUtc, toUtc, compare, selectedVolume, onSelectVolume, crosshairUtc, onCrosshairChange, onSelectWindow } = props;
  const { mode } = useTimeDisplay(), validSelection = selectedVolume && validVolumeHistoryIdentity(selectedVolume) ? selectedVolume : selectedVolume === undefined ? undefined : null;
  const investigation = useSqlVolumeHistory(instanceId, targetRevision, fromUtc, toUtc, compare, validSelection), { reads } = investigation;
  const current = reads.selected.pages as readonly VolumeHistoryPage[], previous = reads.previous.pages as readonly VolumeHistoryPage[];
  const comparisonIssue = compare ? comparisonEligibility(current, previous) : undefined;
  const evidence = (state: VolumeHistoryReadState) => {
    const page = state.pages[state.index]; if (!page) return null;
    return <><p className="activity-evidence">Received window {formatDisplayTime(page.fromUtc,mode)} to {formatDisplayTime(page.toUtc,mode)} (exclusive). Completion cutoff {formatDisplayTime(page.cutoffUtc,mode)}; repository checked {formatDisplayTime(page.repositoryTimeUtc,mode)}.</p>
      <p>{readNotice(page)}</p><p className="table-note">{page.coverage.runCount} retained runs · {page.coverage.completeRunCount} complete · {page.coverage.gapRunCount} gap runs · {page.coverage.retainedObservationCount} retained observations. Retention {page.coverage.retentionDays === null ? "unavailable" : page.coverage.retentionDays + " days"}.</p>
      <details><summary>Read provenance</summary><p>Current target revision fence {page.targetRevision} · read epoch {page.readEpoch}. Already-read pages remain received evidence even if unread continuations expire.</p></details></>;
  };
  const controls = (source: VolumeHistorySource) => {
    const state = reads[source], label = sourceLabels[source], page = state.pages[state.index], last = state.pages.at(-1);
    return <><p className="table-note">{state.pages.reduce((count,p) => count + p.items.length,0)} received rows in {state.pages.length} pages · page {state.pages.length ? state.index + 1 : 0}. Bound: 40 pages / {source === "catalog" ? "1,000 identities" : "10,000 events"}. {last?.nextCursor ? "More unread evidence is available." : state.pages.length ? "End of received read." : "No evidence received."}</p>
      {state.loading && <p role="status">Loading {label}; same-context received evidence remains visible.</p>}
      {state.error && <p role="alert">{state.error}</p>}
      <div className="form-actions"><button type="button" disabled={state.loading || !state.pages.length || state.index === 0} onClick={() => investigation.navigate(source,0)}>First {label} page</button>
        <button type="button" disabled={state.loading || state.index === 0} onClick={() => investigation.navigate(source,state.index-1)}>Previous {label} page</button>
        <button type="button" disabled={state.loading || !page || state.index + 1 >= state.pages.length && (!last?.nextCursor || state.pages.length >= 40)} onClick={() => investigation.navigate(source,state.index+1)}>Next {label} page</button>
        <button type="button" disabled={state.loading} onClick={() => investigation.refresh(source)}>Refresh {label}</button>
        {state.error && state.attempt && <button type="button" disabled={state.loading} onClick={() => investigation.retry(source)}>Retry {label} read</button>}
        {state.loading && <button type="button" onClick={() => investigation.cancel(source)}>Cancel {label} read</button>}</div></>;
  };
  function samples(source: "selected" | "previous", name: string) {
    const state = reads[source], page = state.pages[state.index] as VolumeHistoryPage | undefined;
    return <section aria-label={name} className="panel volume-history-subpanel" aria-busy={state.loading}><h4>{name}</h4>{evidence(state)}
      {page && <><p className="table-note">Exact sample revision {page.sampleTargetRevision}, key {page.volumeKey} · {page.identityKind?.replaceAll("_"," ") ?? "identity evidence unavailable"}. A repeated source key does not prove physical volume incarnation.</p>
        <div className="table-scroll" role="region" tabIndex={0} aria-label={name + " received events"}><table className="evidence-table"><caption>{name}: exact received bytes and source times</caption><thead><tr><th scope="col">Collection completed</th><th scope="col">Actual SQL observation / ingestion</th><th scope="col">Total / available bytes</th><th scope="col">Evidence / mapped files</th></tr></thead>
          <tbody>{page.items.map(row => <tr key={row.runId}><td><time dateTime={row.completedAtUtc}>{formatDisplayTime(row.completedAtUtc,mode)}</time><small>{row.runId}</small></td>
            <td>{row.observedAtUtc ? <time dateTime={row.observedAtUtc}>{formatDisplayTime(row.observedAtUtc,mode)}</time> : "No SQL observation"}<small>{row.collectedAtUtc ? "Ingested " + formatDisplayTime(row.collectedAtUtc,mode) : "No ingestion observation"}</small></td>
            <td>{row.totalBytes ?? "Unknown"}<small>{row.availableBytes ?? "Unknown"} available</small></td><td>{row.state} · {row.reason.replaceAll("_"," ")}<small>{row.outcome.replaceAll("_"," ")} · {row.mappedFileCount ?? "unknown"} mapped files · loss {row.lossKind?.replaceAll("_"," ") ?? "unknown"}{row.minimumLostItems === null ? "" : " / at least " + row.minimumLostItems}</small></td></tr>)}</tbody>
        </table></div>{!page.items.length && <p>No events were retained for this exact volume identity. No other volume has been substituted.</p>}</>}{controls(source)}</section>;
  }
  if (investigation.denied) return <section className="panel volume-window-history" aria-label="SQL volume window history"><h3>SQL volume window history</h3><p role="alert">Volume history access was denied. Protected identities, selected-window and comparison evidence were removed. Restore read access to this server, then explicitly retry.</p><button type="button" onClick={investigation.retryAccess}>Retry volume history access</button></section>;
  const catalog = reads.catalog.pages[reads.catalog.index] as VolumeHistoryCatalogPage | undefined;
  return <section className="panel volume-window-history" aria-label="SQL volume window history"><h3>SQL volume window history</h3>
    <p>History follows the shared Live or Rewind window by collection completion UTC. Actual SQL observation and ingestion times remain distinct. The latest snapshot and its forecast are separate evidence; no historical forecast is inferred.</p>
    <section aria-label="Received volume identities" className="volume-history-subpanel" aria-busy={reads.catalog.loading}><h4>Received volume identities</h4>{evidence(reads.catalog)}
      {catalog && <div className="table-scroll" role="region" tabIndex={0} aria-label="Volume identities in the selected window"><table className="evidence-table"><caption>Retained window identities, including older sample revisions</caption><thead><tr><th scope="col">Volume / revision</th><th scope="col">Observed evidence</th><th scope="col">Last total / available bytes</th><th scope="col">Window change</th></tr></thead><tbody>
        {catalog.items.map(item => <tr key={identityKey(item)}><td><button type="button" className="table-link" aria-label={`Inspect volume revision ${item.sampleTargetRevision} key ${item.volumeKey}`} aria-pressed={validSelection ? identityKey(validSelection) === identityKey(item) : false} onClick={() => onSelectVolume({sampleTargetRevision:item.sampleTargetRevision,volumeKey:item.volumeKey})}>Volume {item.volumeKey.slice(0,8)} · revision {item.sampleTargetRevision}</button><small>{item.identityKind.replaceAll("_"," ")}</small></td>
          <td>{item.observationCount} observations<small>{formatDisplayTime(item.firstCompletedAtUtc,mode)} to {formatDisplayTime(item.lastCompletedAtUtc,mode)}</small></td><td>{item.lastTotalBytes ?? "Unknown"}<small>{item.lastAvailableBytes ?? "Unknown"} available</small></td><td>{item.comparisonState.replaceAll("_"," ")}<small>{item.availableBytesDelta === null ? "No comparable delta" : item.availableBytesDelta + " available bytes"}</small></td></tr>)}
      </tbody></table></div>}{catalog && !catalog.items.length && <p>No volume identities were retained in this window. Choose a retained range or check collection and retention.</p>}{controls("catalog")}</section>
    {validSelection === null && <p role="alert">The requested volume identity is invalid. Clear it or choose an exact received revision/key; no other volume was selected.</p>}
    {validSelection && <p className="activity-evidence">Requested volume: sample revision {validSelection.sampleTargetRevision}, key {validSelection.volumeKey}. Its identity remains selected even when absent from the latest snapshot or this catalog page.</p>}
    {selectedVolume !== undefined && <button type="button" onClick={() => onSelectVolume(undefined)}>Clear volume selection</button>}
    {!validSelection && validSelection !== null && <p>Select a received volume identity to inspect its history. No default volume is chosen.</p>}
    {validSelection && <>{current.length > 0 && <><h4>Available capacity over the loaded window</h4><p className="table-note">Chart uses {current.reduce((n,p)=>n+p.items.length,0)} received events{current.at(-1)?.nextCursor ? "; read remaining pages for complete received coverage" : ""}. Plot values are approximate GiB; tables preserve exact decimal bytes.</p>
      <SqlVolumeHistoryChart current={current} previous={compare && !comparisonIssue ? previous : undefined} fromUtc={fromUtc} toUtc={toUtc} crosshairUtc={crosshairUtc} onCrosshairChange={onCrosshairChange} onSelectWindow={onSelectWindow} />
      {compare && <p role="status">{comparisonIssue ?? "Eligible comparison: same volume-ID source key and sample revision, complete received windows and unchanged known total capacity. This does not establish physical incarnation continuity."}</p>}</>}
      {samples("selected","Selected volume history")}{compare && samples("previous","Previous volume history")}</>}
  </section>;
}
