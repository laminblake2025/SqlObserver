import { useLayoutEffect, useMemo, useRef } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { overviewWindow } from "../overview/overviewModel";
import type { OverviewScope } from "../overview/overviewTypes";
import { availabilityUtcKey, type AvailabilityStream } from "./availabilityEvidence.ts";
import { AvailabilityEvidenceView } from "./AvailabilityEvidenceView";
import { useAvailabilityEvidence, type AvailabilityEvidenceAccessOwner, type AvailabilityEvidenceRead } from "./useAvailabilityEvidence";
import "./availabilityEvidence.css";

export function AvailabilityEvidencePanel({ instanceId, targetRevision, scope, refresh, accessContext = "", accessOwner }: {
  readonly instanceId: string; readonly targetRevision: number; readonly scope: OverviewScope; readonly refresh: number;
  readonly accessContext?: string;
  readonly accessOwner?: AvailabilityEvidenceAccessOwner;
}) {
  const selection = useMemo(() => {
    try { return { atUtc: overviewWindow(scope, Date.now()).toUtc }; }
    catch { return { error: "Choose a valid time range for AG evidence." }; }
  }, [scope.range, scope.from, scope.to, refresh]);
  const history = useAvailabilityEvidence(instanceId, targetRevision, scope, selection.atUtc, refresh, accessContext, accessOwner);
  const { replicas, databases } = history;
  const section = useRef<HTMLElement>(null), retryAccess = useRef<HTMLButtonElement>(null);
  const cancel = useRef<HTMLButtonElement>(null), latest = useRef<HTMLButtonElement>(null);
  const lastFocus = useRef<HTMLElement | null>(null), handoff = useRef<string | undefined>(undefined);
  const loading = replicas.loading || databases.loading;
  const focusContext = useRef(history.contextKey);
  useLayoutEffect(() => {
    if (focusContext.current !== history.contextKey) {
      focusContext.current = history.contextKey; handoff.current = undefined; lastFocus.current = null; return;
    }
    const active = document.activeElement;
    const ownedFocus = section.current?.contains(active) || active === document.body &&
      (handoff.current === history.contextKey || lastFocus.current !== null && !lastFocus.current.isConnected);
    if (!ownedFocus) { handoff.current = undefined; return; }
    if (history.denied) {
      handoff.current = undefined; retryAccess.current?.focus();
    } else if (handoff.current === history.contextKey) {
      const destination = loading ? cancel.current : latest.current;
      destination?.focus();
      if (!loading) handoff.current = undefined;
    }
  }, [history.contextKey, history.denied, loading]);
  function withFocus(action: () => void) { handoff.current = history.contextKey; action(); }
  const a = replicas.page, b = databases.page;
  const mismatch = a && b && (a.targetRevision !== b.targetRevision || a.runId !== b.runId || availabilityUtcKey(a.atUtc) !== availabilityUtcKey(b.atUtc));
  return <section ref={section} aria-label="Per-replica availability evidence" className="availability-evidence"
    onFocusCapture={event => { lastFocus.current = event.target as HTMLElement; }}>
    <h3>Per-replica availability evidence</h3>
    <p>Each stream reads the latest published v2 run started in the seven days before its declared cutoff.
      The range's start does not filter snapshot rows. Investigated pages keep their cutoff until you return to latest.</p>
    {history.denied && <div role="group" aria-label="AG evidence access recovery">
      <p role="alert">A Viewer, Operator or TargetAdministrator role scoped to this server is required. Previously received AG evidence has been cleared.</p>
      <button ref={retryAccess} type="button" disabled={selection.atUtc === undefined} onClick={() => withFocus(history.retryAccess)}>Retry AG evidence access</button>
    </div>}
    {selection.error && <p role="alert">{selection.error}</p>}
    {!selection.error && !history.denied && <>
      <button ref={latest} type="button" disabled={loading} onClick={() => { replicas.returnLatest(); databases.returnLatest(); }}>Return both AG streams to latest</button>
      {loading && <button ref={cancel} type="button" onClick={() => withFocus(history.cancel)}>Cancel AG evidence read</button>}
      {mismatch && <p role="alert">The two streams returned different cutoffs, runs or target revisions. Compare them independently; return both to latest before correlating rows.</p>}
      <AvailabilityStreamPanel stream="replicas" result={replicas} onRetry={() => withFocus(replicas.retry)} />
      <AvailabilityStreamPanel stream="databases" result={databases} onRetry={() => withFocus(databases.retry)} />
    </>}
  </section>;
}
function AvailabilityStreamPanel({ stream, result, onRetry }: {
  readonly stream: AvailabilityStream; readonly result: AvailabilityEvidenceRead["replicas"]; readonly onRetry: () => void;
}) {
  const formatTime = useTimeFormatter();
  const title = stream === "replicas" ? "AG replicas" : "AG database replicas";
  const error = result.error instanceof Error ? result.error.message : result.error ? "AG evidence is unavailable. Retry this page." : undefined;
  return <article className="panel" aria-label={title} aria-busy={result.loading}>
    <h4>{title}</h4>
    {result.page && <p className="activity-evidence">Publication cutoff: {formatTime(result.page.atUtc)} · Run: {result.page.runId ?? "None"} · Target revision: {result.page.targetRevision}</p>}
    {result.held && <p className="table-note">Retained investigation page. Workspace polling does not replace this stream.</p>}
    {result.loading && <p role="status">{result.page ? "Reading AG evidence; the received page remains visible…" : `Loading ${title.toLowerCase()}…`}</p>}
    {error && <p role="alert">{error} <button type="button" disabled={result.loading} onClick={onRetry}>Retry {title.toLowerCase()}</button></p>}
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
