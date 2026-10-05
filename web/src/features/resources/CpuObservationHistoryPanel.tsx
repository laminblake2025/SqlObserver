import { useId } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext.tsx";
import { CpuObservationChart } from "./CpuObservationChart.tsx";
import type { CpuObservationSelection, CpuObservationValue } from "./cpuObservationHistoryModel.ts";
import type { CpuObservationHistoryRead } from "./useCpuObservationHistory.ts";
import "./cpuObservationHistory.css";

const label = (value: string) => value.replace(/([a-z])([A-Z])/gu, "$1 $2").replaceAll("_", " ");
const quality = (value: CpuObservationValue) => value.quality === "provider_adjusted" ? "ProviderAdjusted" : value.quality === "available" ? "Available" : "Unavailable";
const percent = (value: CpuObservationValue | undefined) => value?.percent == null ? "Unavailable" : `${value.percent.toLocaleString(undefined, { maximumFractionDigits: 3 })}%`;
function ComponentEvidence({ value, title }: { readonly value: CpuObservationValue; readonly title: string }) {
  return <section aria-label={`${title} exact evidence`}><h5>{title}: {percent(value)} · {quality(value)}</h5>
    <details><summary>Source intervals and calculation details</summary><dl className="cpu-observation-facts">
      <div><dt>Quality reason</dt><dd>{label(value.reason)}</dd></div>
      <div><dt>Native denominator interval (100 ns)</dt><dd>{value.fromNative100ns ?? "Unavailable"} to {value.toNative100ns ?? "Unavailable"}</dd></div>
      <div><dt>Observation bracket (UTC)</dt><dd>{value.observedFromUtc ?? "Unavailable"} to {value.observedToUtc ?? "Unavailable"}</dd></div>
      <div><dt>Normalization logical processors</dt><dd>{value.normalizationLogicalProcessors ?? "Unavailable"}</dd></div>
      <div><dt>Raw formula percent / eligibility</dt><dd>{value.rawPercent ?? "Unavailable"} / {value.rawFormulaEligible ? "Eligible" : "Ineligible"} · {label(value.rawFormulaReason)}</dd></div>
      <div><dt>Provider raw percent / API status / counter status</dt><dd>{value.providerRawPercent ?? "Unavailable"} / {value.providerApiStatus ?? "Unavailable"} / {value.providerCStatus ?? "Unavailable"}</dd></div>
      <div><dt>Method</dt><dd>PDH calculation of the exact saved raw pair</dd></div>
    </dl></details>{value.quality === "provider_adjusted" && <p>ProviderAdjusted: the native provider returned 0% while the raw target-host CPU formula was below 0%. The raw calculation remains ineligible.</p>}
  </section>;
}
export function CpuObservationHistoryPanel({ history, comparison, fromUtc, toUtc, windowError, selectionInvalid, explicitSelection, onSelectObservation, onRetainWindow, crosshairUtc, onCrosshairChange, onSelectWindow }: {
  readonly history: CpuObservationHistoryRead; readonly comparison?: boolean; readonly fromUtc?: string; readonly toUtc?: string;
  readonly windowError?: string; readonly selectionInvalid?: boolean; readonly explicitSelection?: CpuObservationSelection;
  readonly onSelectObservation: (selection?: CpuObservationSelection) => void; readonly onRetainWindow: () => void;
  readonly crosshairUtc?: string | null; readonly onCrosshairChange?: (utc: string | null) => void;
  readonly onSelectWindow?: (window: { fromUtc: string; toUtc: string }) => void;
}) {
  const id = useId(), formatTime = useTimeFormatter(), exact = history.detail.pages[0], selected = explicitSelection ?? history.selected;
  function controls(source: "list" | "comparison" | "detail", title: string) {
    const read = history[source];
    return <><div className="screen-actions">
      <button className="secondary-button" type="button" disabled={read.loading || Boolean(windowError)} onClick={() => history.refresh(source)}>Refresh {title}</button>
      {read.loading && <button className="secondary-button" type="button" onClick={() => history.cancel(source)}>Cancel {title} wait</button>}
      {read.error && <button className="secondary-button" type="button" onClick={() => history.retry(source)}>Retry {title} read</button>}
    </div>{read.loading && <p role="status">Loading {title}; received evidence remains visible.</p>}{read.error && <p role="alert">{read.error}</p>}</>;
  }
  function receipts(source: "list" | "comparison", title: string) {
    const read = history[source], page = read.pages[read.index], first = read.pages[0], received = read.pages.reduce((sum, value) => sum + value.items.length, 0);
    return <section aria-label={title}>{controls(source, title)}
      {first && <p>{received} received observations of {first.coverage.runCount} completed runs · {first.coverage.completeRunCount} Available · {first.coverage.gapRunCount} with component gaps or adjusted quality. {first.reason === "revision_boundary" ? "Target revision boundary is present." : ""}</p>}
      <details onToggle={event => { if (event.currentTarget.open) onRetainWindow(); }}><summary>Browse {title}</summary>
        {page && <><p>Page {read.index + 1} · cutoff {formatTime(page.cutoffUtc)} · {label(page.reason)}</p>
          <div className="table-scroll" tabIndex={0} role="region" aria-label={`${title} table`}><table><thead><tr><th>Completed</th><th>Run / sample revision</th><th>SQL-process CPU</th><th>Target-host CPU</th><th>Evidence</th></tr></thead>
            <tbody>{page.items.map(row => <tr key={row.runId}><td>{formatTime(row.completedAtUtc)}</td><td>{row.runId}<br/>Revision {row.sampleTargetRevision}</td>
              <td>{percent(row.observation?.sqlProcess)} · {row.observation ? quality(row.observation.sqlProcess) : label(row.reason)}</td>
              <td>{percent(row.observation?.operatingSystem)} · {row.observation ? quality(row.observation.operatingSystem) : label(row.reason)}</td>
              <td>{source === "list" ? <button className="secondary-button" type="button" onClick={() => onSelectObservation(row)} aria-label={`Inspect CPU observation ${row.runId}`}>Inspect</button> : label(row.reason)}
                <details><summary>Component intervals and quality</summary>{row.observation ? <><ComponentEvidence value={row.observation.sqlProcess} title="SQL-process CPU"/><ComponentEvidence value={row.observation.operatingSystem} title="Target-host CPU"/></> : <p>{label(row.reason)}. Missing evidence is not zero.</p>}</details></td>
            </tr>)}</tbody></table></div>
        </>}{!page && !read.loading && <p>No received observations for this source.</p>}
      </details>
      <div className="screen-actions">
        <button className="secondary-button" type="button" disabled={read.loading || read.index === 0} onClick={() => { onRetainWindow(); history.navigate(0, source); }}>First {title} page</button>
        <button className="secondary-button" type="button" disabled={read.loading || read.index === 0} onClick={() => { onRetainWindow(); history.navigate(read.index - 1, source); }}>Previous {title} page</button>
        <button className="secondary-button" type="button" disabled={read.loading || !page || !read.pages[read.index + 1] && !page.nextCursor} onClick={() => { onRetainWindow(); history.navigate(read.index + 1, source); }}>Next {title} page</button>
      </div>
    </section>;
  }
  return <section className="panel cpu-observation-history" aria-labelledby={id}><h3 id={id}>CPU component observations</h3>
    <p>SQL-process CPU and target-host CPU are independent measurements with their own native intervals. They do not form a stacked breakdown; no other-process percentage is inferred.</p>
    {windowError && <p role="alert">{windowError}</p>}
    {history.denied ? <><p role="alert">CPU observation access was denied. All received current, comparison and exact evidence was removed.</p><button className="secondary-button" type="button" onClick={history.retryAccess}>Retry CPU observation access</button></> : <>
      {fromUtc && toUtc && <div className="cpu-observation-charts">{(["sqlProcess", "operatingSystem"] as const).map(component => <CpuObservationChart key={component} component={component} current={history.list.pages} previous={comparison ? history.comparison.pages : undefined} fromUtc={fromUtc} toUtc={toUtc} crosshairUtc={crosshairUtc} onCrosshairChange={onCrosshairChange} onSelectWindow={onSelectWindow}/>)}</div>}
      {receipts("list", "CPU observations")}
      {comparison && receipts("comparison", "CPU comparison observations")}
      <section aria-label="Selected CPU observation"><h4>Selected CPU observation</h4>
        {(selected || selectionInvalid) && <button className="secondary-button" type="button" onClick={() => onSelectObservation()}>Clear CPU observation selection</button>}
        {selectionInvalid ? <p role="alert">The requested CPU observation identity is invalid. Clear the selection or choose an exact received run; no substitute is selected.</p> : <>
          {selected && <p>Run {selected.runId} · sample revision {selected.sampleTargetRevision}</p>}
          {controls("detail", "CPU observation")}
          {exact && <><p>{label(exact.reason)}. Completed {exact.receipt ? formatTime(exact.receipt.completedAtUtc) : "outside retained evidence"}.</p>
            {exact.receipt?.observation ? <><p>{exact.receipt.observation.sourceAssociationVerified ? "Target association verified for this observation." : `Target association unavailable: ${label(exact.receipt.observation.associationReason)}.`} Strict paired calculation eligible: {exact.receipt.observation.exactPairEligible ? "Yes" : "No"}.</p>
              {exact.receipt.observation.associationReason === "NativeProcessAccessDenied" && <p>The monitor account could not read the required native process evidence. Ask the server administrator to review its SQL-process query and synchronization access; enabling collection does not grant operating-system permissions.</p>}
              <ComponentEvidence value={exact.receipt.observation.sqlProcess} title="SQL-process CPU"/><ComponentEvidence value={exact.receipt.observation.operatingSystem} title="Target-host CPU"/>
            </> : <p>No measured components are available for this exact run. Refresh does not substitute another run.</p>}</>}
          {!selected && !history.list.loading && <p>No observation is selected.</p>}
        </>}
      </section>
    </>}
  </section>;
}
