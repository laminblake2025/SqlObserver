import { useId, useLayoutEffect, useRef } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext.tsx";
import type { CpuCaptureCounter, CpuCaptureDiagnostics, CpuCaptureSelection } from "./cpuCaptureWindowHistoryModel.ts";
import type { CpuCaptureWindowHistoryRead } from "./useCpuCaptureWindowHistory.ts";
import { maximumCpuCapturePages } from "./useCpuCaptureWindowHistory.ts";
import "./cpuCaptureWindowHistory.css";

const labels: Readonly<Record<string, string>> = {
  source_qualification_absent: "Source admission absent", host_binding_unavailable: "Host binding unavailable",
  host_binding_denied: "Host binding refused", host_binding_boundary: "Host binding changed", host_cpu_capability_unavailable: "Host CPU capability unavailable",
  native_capture_incomplete: "Native capture incomplete", candidate_association_rejected: "Candidate association not established",
  numerical_evidence_incomplete: "Numerical evidence incomplete", collection_failed: "Collection failed", payload_missing: "Capture payload unavailable",
  capture_not_retained: "Exact capture is not retained", capture_outside_window: "Exact capture is outside the shared window",
};
const reason = (value: string) => labels[value] ?? value.replace(/([a-z])([A-Z])/gu, "$1 $2").replaceAll("_", " ");
const exact = (value: string | number | boolean | null | undefined) => value === null || value === undefined ? "Not recorded" : typeof value === "boolean" ? value ? "Yes" : "No" : String(value);
function Counter({ value, label }: { readonly value: CpuCaptureCounter | null; readonly label: string }) {
  return <section aria-label={label}><h6>{label}</h6>{value ? <dl className="cpu-capture-facts">
    <div><dt>Type / counter status / API status</dt><dd>{value.counterType} / {value.cStatus} / {value.apiStatus}</dd></div>
    <div><dt>Raw first / second value</dt><dd>{value.firstValue} / {value.secondValue}</dd></div>
    <div><dt>Provider-local FILETIME (100 ns)</dt><dd>{value.collectedLocalFileTime100ns}</dd></div>
    <div><dt>Time base / multicount</dt><dd>{value.timeBase} / {value.multiCount}</dd></div>
  </dl> : <p>Counter evidence was not recorded. No zero value is substituted.</p>}</section>;
}
function Diagnostics({ capture }: { readonly capture: CpuCaptureDiagnostics }) {
  const eligibility = capture.numericalEligibility;
  return <details className="cpu-capture-diagnostics"><summary>Captured CPU source diagnostics</summary>
    <p>These are whitelisted facts from the exact retained capture. Socket addresses, identities, connection identifiers and process names are withheld. Retained diagnostics cannot independently re-attest discarded socket ownership.</p>
    <dl className="cpu-capture-facts">
      <div><dt>Candidate association at capture</dt><dd>{capture.candidateLocalAssociation ? "Source-derived candidate only" : "Not established"} · {reason(capture.candidateReason)}</dd></div>
      <div><dt>Source frames</dt><dd>{capture.sourceFrames} of 6</dd></div>
      <div><dt>Source qualification / target admission</dt><dd>Absent / No</dd></div>
      <div><dt>SQL process / OS numerical eligibility</dt><dd>{eligibility ? `${exact(eligibility.sqlProcessEligible)} / ${exact(eligibility.operatingSystemEligible)}` : "Not evaluated"}</dd></div>
      <div><dt>Same native counter interval</dt><dd>{eligibility ? exact(eligibility.sameNativeInterval) : "Not evaluated"}</dd></div>
      <div><dt>Numerical refusal reasons</dt><dd>{eligibility ? `${reason(eligibility.sqlProcessReason)} / ${reason(eligibility.operatingSystemReason)}` : "Candidate association is required before evaluating numerical evidence."}</dd></div>
      <div><dt>SQL counter anchors (100 ns)</dt><dd>{exact(eligibility?.sqlFromNative100ns)} to {exact(eligibility?.sqlToNative100ns)}</dd></div>
      <div><dt>OS counter anchors (100 ns)</dt><dd>{exact(eligibility?.osFromNative100ns)} to {exact(eligibility?.osToNative100ns)}</dd></div>
    </dl>
    {(["before", "after"] as const).map(side => {
      const sql = side === "before" ? capture.sqlBefore : capture.sqlAfter, tcp = side === "before" ? capture.tcpBefore : capture.tcpAfter;
      const cpu = side === "before" ? capture.cpuBefore : capture.cpuAfter, frame = side === "before" ? capture.frameBefore : capture.frameAfter;
      const id = side === "before" ? capture.idProcessBefore : capture.idProcessAfter, title = side === "before" ? "Before" : "After";
      return <section key={side} aria-label={`${title} captured CPU facts`}><h5>{title} captured CPU facts</h5>
        <p>Exact seven-digit UTC source clocks below are distinct from collection completion. SQL startup is an offset-free local source value; no UTC startup is inferred.</p>
        <dl className="cpu-capture-facts">
          <div><dt>SQL source UTC</dt><dd>{exact(sql?.sourceObservedAtUtc)}</dd></div>
          <div><dt>SQL source schema / ordinal</dt><dd>{exact(sql?.sourceSchemaVersion)} / {exact(sql?.ordinal)}</dd></div>
          <div><dt>SQL PID / startup local</dt><dd>{exact(sql?.processId)} / {exact(sql?.sourceStartLocal)}</dd></div>
          <div><dt>SQL start / current millisecond ticks</dt><dd>{exact(sql?.startMsTicks)} / {exact(sql?.currentMsTicks)}</dd></div>
          <div><dt>SQL cumulative user / kernel milliseconds</dt><dd>{exact(sql?.processUserTimeMs)} / {exact(sql?.processKernelTimeMs)}</dd></div>
          <div><dt>SQL-reported logical processors</dt><dd>{exact(sql?.sqlReportedLogicalProcessors)}; separate from the native denominator</dd></div>
          <div><dt>Native socket source UTC / monotonic 100 ns</dt><dd>{exact(tcp?.observedAtUtc)} / {exact(tcp?.monotonic100ns)}</dd></div>
          <div><dt>AF4 / AF6 API status and enumerated rows</dt><dd>{tcp ? `${tcp.af4Status} / ${tcp.af6Status}; ${tcp.rowCount4} / ${tcp.rowCount6} rows` : "Not recorded"}</dd></div>
          <div><dt>Retained exact-tuple row count</dt><dd>{exact(tcp?.matchingRowCount)}. Intentional filtering is not claimed as source loss.</dd></div>
          <div><dt>Native SQL process creation before / after (100 ns)</dt><dd>{exact(tcp?.sqlProcess.creationBefore100ns)} / {exact(tcp?.sqlProcess.creationAfter100ns)}</dd></div>
          <div><dt>SQL process handle wait / API status</dt><dd>{exact(tcp?.sqlProcess.waitResultAfter)} / {exact(tcp?.sqlProcess.apiStatus)}</dd></div>
          <div><dt>Native collector process creation before / after (100 ns)</dt><dd>{exact(tcp?.collectorProcess.creationBefore100ns)} / {exact(tcp?.collectorProcess.creationAfter100ns)}</dd></div>
          <div><dt>Collector process handle wait / API status</dt><dd>{exact(tcp?.collectorProcess.waitResultAfter)} / {exact(tcp?.collectorProcess.apiStatus)}</dd></div>
          <div><dt>CPU source UTC</dt><dd>{exact(cpu?.observedAtUtc)}</dd></div>
          <div><dt>CPU monotonic ticks / frequency</dt><dd>{exact(cpu?.monotonicTicks)} / {exact(cpu?.monotonicFrequency)}</dd></div>
          <div><dt>Native all-group logical processor denominator</dt><dd>{exact(cpu?.logicalProcessorCount)}</dd></div>
          <div><dt>Within-frame processor counts before / after</dt><dd>{exact(frame?.logicalProcessorsBefore)} / {exact(frame?.logicalProcessorsAfter)}</dd></div>
          <div><dt>Process mapping ambiguity / enumerated instances</dt><dd>{exact(frame?.processMappingAmbiguous)} / {exact(frame?.processInstances)}</dd></div>
          <div><dt>CPU held-process wait / exit raw FILETIME</dt><dd>{exact(frame?.processWaitResultAfter)} / {exact(frame?.processExitAfter100ns)}. A raw exit value is not liveness proof.</dd></div>
        </dl>
        <Counter value={id} label={`${title} ID Process raw counter`} />
        <Counter value={cpu?.processCounter ?? null} label={`${title} SQL process raw counter`} />
        <Counter value={cpu?.osCounter ?? null} label={`${title} OS raw counter`} />
      </section>;
    })}
    <details><summary>Source refusal and byte accounting</summary><dl className="cpu-capture-facts">
      <div><dt>Socket before / CPU pair / socket after reasons</dt><dd>{capture.tcpBeforeReadReason ? reason(capture.tcpBeforeReadReason) : "Not recorded"} / {capture.cpuPairReadReason ? reason(capture.cpuPairReadReason) : "Not recorded"} / {capture.tcpAfterReadReason ? reason(capture.tcpAfterReadReason) : "Not recorded"}</dd></div>
      <div><dt>Process read reason</dt><dd>{capture.processReadReason ? reason(capture.processReadReason) : "Not recorded"}</dd></div>
      <div><dt>Actual parsed helper bytes before / pair / after</dt><dd>{exact(capture.bytes.tcpBeforeHelperBytes)} / {exact(capture.bytes.cpuPairHelperBytes)} / {exact(capture.bytes.tcpAfterHelperBytes)}</dd></div>
      <div><dt>SQL logical response upper bound (bytes)</dt><dd>{capture.bytes.sqlLogicalResponseUpperBoundBytes}; this is not measured TDS wire bytes</dd></div>
    </dl></details>
  </details>;
}

export function CpuCaptureWindowHistoryPanel({ history, windowError, selectionInvalid = false, explicitSelection, onSelectCapture, onRetainWindow }:
  { readonly history: CpuCaptureWindowHistoryRead; readonly windowError?: string; readonly selectionInvalid?: boolean;
    readonly explicitSelection?: CpuCaptureSelection; readonly onSelectCapture: (selection: CpuCaptureSelection | undefined) => void;
    readonly onRetainWindow?: () => void }) {
  const id = useId(), formatTime = useTimeFormatter(), root = useRef<HTMLElement | null>(null);
  const list = history.list, page = list.pages[list.index], detail = history.detail, selected = detail.pages[0];
  const receipt = selected?.receipt, capture = selected?.capture;
  const recovery = useRef<{ source: "list" | "detail"; context: string; selection: string; waiting: boolean } | undefined>(undefined);
  function recover(source: "list" | "detail", action: () => void) {
    recovery.current = { source, context: history.contextKey, selection: history.selectionIdentity, waiting: false }; action();
  }
  useLayoutEffect(() => {
    const intent = recovery.current; if (!intent) return;
    if (intent.context !== history.contextKey || intent.source === "detail" && intent.selection !== history.selectionIdentity) { recovery.current = undefined; return; }
    if (history.denied) { root.current?.querySelector<HTMLButtonElement>("[data-cpu-access-retry]")?.focus(); recovery.current = undefined; return; }
    const read = intent.source === "list" ? list : detail, cancel = root.current?.querySelector<HTMLButtonElement>(`[data-cpu-cancel="${intent.source}"]`);
    if (!intent.waiting && read.loading) { cancel?.focus(); intent.waiting = true; }
    else if (!read.loading) {
      if (!intent.waiting || document.activeElement === cancel || document.activeElement === document.body)
        root.current?.querySelector<HTMLButtonElement>(`[data-cpu-refresh="${intent.source}"]`)?.focus();
      recovery.current = undefined;
    }
  }, [list.loading, detail.loading, history.contextKey, history.selectionIdentity, history.denied]);
  function controls(source: "list" | "detail", name: string) {
    const read = source === "list" ? list : detail;
    return <div className="cpu-capture-actions" role="group" aria-label={`${name} read controls`}>
      <button type="button" data-cpu-refresh={source} disabled={read.loading || Boolean(windowError) || source === "detail" && selectionInvalid} onClick={() => history.refresh(source)}>Refresh {name}</button>
      <button type="button" data-cpu-cancel={source} disabled={!read.loading} onClick={() => recover(source, () => history.cancel(source))}>Cancel {name} read</button>
      {read.error && <button type="button" disabled={read.loading} onClick={() => recover(source, () => history.retry(source))}>Retry {name} read</button>}
      {source === "list" && <>
        <button type="button" disabled={read.loading || read.index === 0} onClick={() => { onRetainWindow?.(); history.navigate(0); }}>First CPU receipts page</button>
        <button type="button" disabled={read.loading || read.index === 0} onClick={() => { onRetainWindow?.(); history.navigate(read.index - 1); }}>Previous CPU receipts page</button>
        <button type="button" disabled={read.loading || !page || read.index + 1 >= read.pages.length && (!page.nextCursor || read.pages.length >= maximumCpuCapturePages)} onClick={() => { onRetainWindow?.(); history.navigate(read.index + 1); }}>Next CPU receipts page</button>
      </>}
    </div>;
  }
  return <section ref={root} className="panel cpu-capture-window-history" aria-labelledby={id}>
    <h3 id={id}>CPU paired capture history</h3>
    <p>Collection receipts follow completion in the shared window, in Live or Rewind. This evidence is unqualified: no admitted SQL-process or OS CPU percentage, trend, health rating or target comparison is available.</p>
    <p>Existing SQL scheduler utilization and collector-host CPU series use different sources. They are not a replacement for this paired target CPU evidence.</p>
    {windowError && <p role="alert">{windowError}</p>}
    {history.denied ? <section aria-label="CPU capture evidence access"><p role="alert">CPU capture evidence access was denied. Received receipts, diagnostics and cached pages were removed.</p>
      <button type="button" data-cpu-access-retry onClick={() => recover("list", history.retryAccess)}>Retry CPU capture access</button></section> : <>
      <section aria-label="CPU capture receipts" aria-busy={list.loading}><h4>CPU capture receipts</h4>
        {list.loading && <p role="status">{page ? "Refreshing CPU receipts; received evidence remains until a validated replacement arrives…" : "Loading CPU capture receipts…"}</p>}
        {list.error && <p role="alert">{list.error}</p>}
        {controls("list", "CPU receipts")}
        {page && <>
          <p className="cpu-capture-receipt">Received completion window {formatTime(page.fromUtc)} to {formatTime(page.toUtc)} · cutoff {formatTime(page.cutoffUtc)} · checked {formatTime(page.repositoryTimeUtc)}. Page {list.index + 1}; {list.pages.reduce((count, value) => count + value.items.length, 0)} received receipts.</p>
          <p role="status">{page.coverage.runCount} completed attempts; {page.coverage.retainedReceiptCount} retained receipts; {page.coverage.completeRunCount} complete six-frame captures; {page.coverage.gapRunCount} gaps. Complete source accounting is not CPU source admission or a health verdict.</p>
          {page.coverage.hasVisibilityGap && <p>{page.reason === "revision_boundary" ? "This window crosses target revisions. Each receipt retains its source revision." : "Failed, missing, incomplete or timed collection evidence prevents a complete retained view."}</p>}
          {page.items.length === 0 ? <p role="status">No completed paired CPU capture attempts are retained in this window. Choose another completion window or ask the application administrator whether capture is provisioned. Refresh only rereads retained history.</p> :
            <details onToggle={event => { if (event.currentTarget.open) onRetainWindow?.(); }}><summary>Browse CPU capture receipts ({page.items.length} on this page)</summary>
              <div className="table-scroll" tabIndex={0} role="region" aria-label="CPU capture receipt table"><table><caption>Actual attempts, newest completion first; failures are never skipped</caption>
                <thead><tr><th scope="col">Completion</th><th scope="col">Source revision</th><th scope="col">Collection</th><th scope="col">Capture</th><th scope="col">Inspect</th></tr></thead>
                <tbody>{page.items.map(item => <tr key={item.runId}><td><time dateTime={item.completedAtUtc}>{formatTime(item.completedAtUtc)}</time><small>{item.runId}</small></td><td>{item.sampleTargetRevision}</td><td>{reason(item.outcome)}<small>Loss: {reason(item.lossKind)}; minimum lost: {item.minimumLostItems}</small></td><td>{reason(item.reason)}<small>{item.capturePresent ? `${item.sourceFrames} of 6 source frames` : "No capture payload"}; unqualified</small></td><td><button type="button" aria-pressed={history.selected?.runId === item.runId && history.selected?.sampleTargetRevision === item.sampleTargetRevision} onClick={() => onSelectCapture(item)}>Inspect CPU receipt {item.runId}</button></td></tr>)}</tbody>
              </table></div>
            </details>}
        </>}
      </section>
      <section aria-label="Selected CPU capture receipt" aria-busy={detail.loading}><h4>Selected CPU capture receipt</h4>
        {selectionInvalid && <p role="alert">The requested CPU capture identity is invalid. No other receipt has been selected.</p>}
        {(explicitSelection || selectionInvalid) && <button type="button" onClick={() => onSelectCapture(undefined)}>Clear CPU capture selection</button>}
        {history.selected && <p className="cpu-capture-receipt">Exact run {history.selected.runId} · sample revision {history.selected.sampleTargetRevision}. {explicitSelection ? "Pinned investigation; polling does not choose another receipt." : "Default: the most recent received attempt, without skipping failures."}</p>}
        {detail.loading && <p role="status">{selected ? "Reading the exact CPU capture; received diagnostics remain until a validated response arrives…" : "Loading exact CPU capture…"}</p>}
        {detail.error && <p role="alert">{detail.error}</p>}
        {history.selected && controls("detail", "CPU capture")}
        {!history.selected && !selectionInvalid && !list.loading && <p role="status">No received attempt is available for exact CPU diagnostics in this window.</p>}
        {selected && <>
          <p role="status">{reason(selected.reason)}. {receipt ? `Collection ${reason(receipt.outcome)} completed ${formatTime(receipt.completedAtUtc)}; source revision ${receipt.sampleTargetRevision}.` : "No exact receipt is retained; the requested identity remains selected without replacement."}</p>
          <p>Native qualification: absent. Target comparison admitted: no. Hardware identity verified: no. SQL-process and OS CPU percentages are not available.</p>
          {!capture && <p>{["capture_not_retained", "capture_outside_window", "payload_missing"].includes(selected.reason) ? "Verify the exact run, sample revision and retained completion window. Refresh cannot recreate missing historical capture evidence." : "Review target connectivity, validated TLS, monitoring permissions and source availability. Refresh reads retained history; it does not repair this past collection attempt."}</p>}
          {capture && <>
            {capture.reason.startsWith("host_") && <p>Ask the application administrator to review the exact target-revision host binding and CPU profile. No collector-host identity is substituted.</p>}
            {capture.reason === "candidate_association_rejected" && <p>The source could not establish local process ownership for this interval. Review native socket/process access and captured identity boundaries; the receipt does not authorize a target comparison.</p>}
            {capture.reason === "numerical_evidence_incomplete" && <p>Inspect individual counter status, types, epochs, denominator and interval anchors in source diagnostics. A valid independent component does not establish a paired comparison.</p>}
            {capture.reason === "source_qualification_absent" && <p>Source admission has not been established. A source-derived candidate and numerical eligibility are diagnostic evidence only.</p>}
            {capture.reason === "native_capture_incomplete" && <p>Review native counter, socket and held-process access in source diagnostics. Missing frames remain unavailable; refresh cannot complete this past capture.</p>}
            <Diagnostics capture={capture} />
          </>}
          <details><summary>CPU receipt provenance</summary><p className="cpu-capture-receipt">Completion cutoff {formatTime(selected.cutoffUtc)} · checked {formatTime(selected.repositoryTimeUtc)} · retained content epoch {selected.readEpoch}. List and exact-run sources have independent epochs; this does not prove physical continuity or reopen source admission.</p></details>
        </>}
      </section>
    </>}
  </section>;
}
