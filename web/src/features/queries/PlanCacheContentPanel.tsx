import { useEffect, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { getPlanCacheContent } from "./queryPerformanceApi";
import type { PlanCacheContent, PlanCacheContentKind, PlanCacheContentReason, PlanCacheHistorySelection, PlanCacheIntervalHistoryItem } from "./queryPerformanceTypes";

const missing: Record<Exclude<PlanCacheContentReason, "available">, string> = {
  evidence_unavailable: "This exact observation is no longer available under the current server configuration. Select a retained sample from the current configuration.",
  capture_missing: "This sample was collected without query content. Update the query collector and select a newer sample with content capture enabled.",
  capture_disabled: "Content capture was disabled for this sample. Enable it for future collections.",
  not_selected: "This cache entry was outside the bounded content capture. Select another sample; collection keeps at most eight entries per capture.",
  source_unavailable: "SQL Server did not supply content for this exact entry. It may have been evicted or changed during capture.",
  oversized: "The content exceeded the capture size limit and was not retained.",
  response_byte_limit: "The capture reached its content byte limit before retaining this entry. Select another sample.",
  protection_unavailable: "A protection key was unavailable. Check the server and collector key configuration; new captures require a working key.",
  protection_failed: "The protected content could not be opened. Check the configured protection key and inspect the collection diagnostics.",
};

// Parent keys this component by time/access context and exact run/entry/kind.
export function PlanCacheContentPanel({ selection, row, kind, onClose }: {
  readonly selection: PlanCacheHistorySelection; readonly row: PlanCacheIntervalHistoryItem;
  readonly kind: PlanCacheContentKind; readonly onClose: () => void;
}) {
  const timeLabel = useTimeFormatter();
  const [state, setState] = useState<{ readonly result?: PlanCacheContent; readonly error?: string }>();
  useEffect(() => {
    const controller = new AbortController();
    void getPlanCacheContent(selection, row.collectionRunId, row.cacheEntryFingerprint, kind, controller.signal)
      .then(result => { if (!controller.signal.aborted) setState({ result }); })
      .catch(() => { if (!controller.signal.aborted) setState({ error: "Content could not be opened. Check your QueryTextReader access and repository availability, then reopen the sample." }); });
    return () => controller.abort();
  }, [selection.targetId, selection.databaseId, selection.databaseIncarnation, selection.queryFingerprint,
    selection.planFingerprint, row.collectionRunId, row.cacheEntryFingerprint, kind]);
  return <section className="panel" aria-label="Cache observation content">
    <div className="toolbar"><h5>{kind === "text" ? "Cache query text" : "Cache plan XML"}</h5><button type="button" onClick={onClose}>Close cache content</button></div>
    <p>Capture {timeLabel(row.observedAtUtc)} · entry {row.cacheEntryFingerprint.slice(0,12)}… · access is audited.</p>
    {!state ? <p role="status">Opening protected cache content…</p> : null}
    {state?.error ? <p role="alert">{state.error}</p> : null}
    {state?.result?.status === "unavailable" ? <p role="status">{missing[state.result.reason as Exclude<PlanCacheContentReason, "available">]}</p> : null}
    {state?.result?.status === "available" ? <pre className="table-scroll" tabIndex={0} aria-label={kind === "text" ? "Captured query text" : "Captured plan XML"}>{state.result.content}</pre> : null}
  </section>;
}
