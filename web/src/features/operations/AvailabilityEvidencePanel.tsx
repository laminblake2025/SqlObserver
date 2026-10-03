import { useMemo } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { overviewWindow } from "../overview/overviewModel";
import type { OverviewScope } from "../overview/overviewTypes";
import { assertAvailabilitySnapshot, availabilityUtcKey, type AvailabilityPage, type AvailabilityStream } from "./availabilityEvidence.ts";
import { AvailabilityEvidenceError, getAvailabilityEvidence } from "./availabilityEvidenceApi.ts";
import { AvailabilityEvidenceView } from "./AvailabilityEvidenceView";
import { useRetainedOperationsPage } from "./useRetainedOperationsPage";

export function AvailabilityEvidencePanel({ instanceId, scope, refresh }: {
  readonly instanceId: string; readonly scope: OverviewScope; readonly refresh: number;
}) {
  const selection = useMemo(() => {
    try { return { atUtc: overviewWindow(scope, Date.now()).toUtc }; }
    catch { return { error: "Choose a valid time range for AG evidence." }; }
  }, [scope.range, scope.from, scope.to, refresh]);
  const context = JSON.stringify([instanceId.toLowerCase(), scope.range, scope.from, scope.to]);
  const replicas = useAvailabilityStream(instanceId, "replicas", selection.atUtc, context, refresh);
  const databases = useAvailabilityStream(instanceId, "databases", selection.atUtc, context, refresh);
  const a = replicas.page, b = databases.page;
  const mismatch = a && b && (a.targetRevision !== b.targetRevision || a.runId !== b.runId || availabilityUtcKey(a.atUtc) !== availabilityUtcKey(b.atUtc));
  return <section aria-label="Per-replica availability evidence" className="availability-evidence">
    <h3>Per-replica availability evidence</h3>
    <p>Each stream reads the latest published v2 run started in the seven days before its declared cutoff.
      The range's start does not filter snapshot rows. Investigated pages keep their cutoff until you return to latest.</p>
    {selection.error ? <p role="alert">{selection.error}</p> : <>
      <button type="button" disabled={replicas.loading || databases.loading} onClick={() => { replicas.returnLatest(); databases.returnLatest(); }}>Return both AG streams to latest</button>
      {mismatch && <p role="alert">The two streams returned different cutoffs, runs or target revisions. Compare them independently; return both to latest before correlating rows.</p>}
      <AvailabilityStreamPanel stream="replicas" result={replicas} />
      <AvailabilityStreamPanel stream="databases" result={databases} />
    </>}
  </section>;
}
function useAvailabilityStream(instanceId: string, stream: AvailabilityStream, atUtc: string | undefined, context: string, refresh: number) {
  return useRetainedOperationsPage<string, AvailabilityPage>({ context, input: atUtc, inputKey: atUtc ?? "invalid", refresh,
    read: (cutoff, signal, cursor) => getAvailabilityEvidence(instanceId, stream, cutoff, signal, cursor),
    validate: (first, next) => assertAvailabilitySnapshot(first, next),
    discardOnError: error => error instanceof AvailabilityEvidenceError && !error.retryable && !error.restart });
}
function AvailabilityStreamPanel({ stream, result }: {
  readonly stream: AvailabilityStream; readonly result: ReturnType<typeof useAvailabilityStream>;
}) {
  const formatTime = useTimeFormatter();
  const title = stream === "replicas" ? "AG replicas" : "AG database replicas";
  const error = result.error instanceof Error ? result.error.message : result.error ? "AG evidence is unavailable. Retry this page." : undefined;
  return <article className="panel" aria-label={title} aria-busy={result.loading}>
    <h4>{title}</h4>
    {result.page && <p className="activity-evidence">Publication cutoff: {formatTime(result.page.atUtc)} · Run: {result.page.runId ?? "None"} · Target revision: {result.page.targetRevision}</p>}
    {result.held && <p className="table-note">Retained investigation page. Workspace polling does not replace this stream.</p>}
    {result.loading && <p role="status">{result.page ? "Reading AG evidence; the received page remains visible…" : `Loading ${title.toLowerCase()}…`}</p>}
    {error && <p role="alert">{error} <button type="button" disabled={result.loading} onClick={result.retry}>Retry {title.toLowerCase()}</button></p>}
    {result.page && <AvailabilityEvidenceView page={result.page} stream={stream} formatTime={formatTime} />}
    <nav className="pager" aria-label={`${title} pages`}>
      <button type="button" disabled={result.loading || result.index === 0} onClick={result.first}>First {title.toLowerCase()} page</button>{" "}
      <button type="button" disabled={result.loading || result.index === 0} onClick={result.previous}>Previous {title.toLowerCase()} page</button>{" "}
      <span>Page {result.index + 1}</span>{" "}
      <button type="button" disabled={result.loading || !result.page?.nextCursor} onClick={() => result.page?.nextCursor && result.next(result.page.nextCursor)}>Next {title.toLowerCase()} page</button>{" "}
      <button type="button" disabled={result.loading} onClick={result.returnLatest}>Return {title.toLowerCase()} to latest</button>
    </nav>
  </article>;
}
