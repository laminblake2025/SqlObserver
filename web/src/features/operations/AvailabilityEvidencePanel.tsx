import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { overviewWindow } from "../overview/overviewModel";
import type { OverviewScope } from "../overview/overviewTypes";
import { assertAvailabilitySnapshot, type AvailabilityPage, type AvailabilityStream } from "./availabilityEvidence.ts";
import { AvailabilityEvidenceError, getAvailabilityEvidence } from "./availabilityEvidenceApi.ts";
import { AvailabilityEvidenceView } from "./AvailabilityEvidenceView";

export function AvailabilityEvidencePanel({ instanceId, scope, refresh }: {
  readonly instanceId: string;
  readonly scope: OverviewScope;
  readonly refresh: number;
}) {
  const formatTime = useTimeFormatter();
  const [restart, setRestart] = useState(0);
  const selection = useMemo(() => {
    try { return { atUtc: overviewWindow(scope, Date.now()).toUtc }; }
    catch { return { error: "Choose a valid time range for AG evidence." }; }
  }, [scope.range, scope.from, scope.to, refresh, restart]);
  const key = `${instanceId}:${selection.atUtc}:${refresh}:${restart}`;
  const [headers, setHeaders] = useState<{ key: string; pages: Partial<Record<AvailabilityStream, AvailabilityPage>> }>({ key: "", pages: {} });
  const published = useCallback((stream: AvailabilityStream, page: AvailabilityPage) => {
    setHeaders(previous => ({ key, pages: { ...(previous.key === key ? previous.pages : {}), [stream]: page } }));
  }, [key]);
  const current = headers.key === key ? headers.pages : {};
  const mismatch = current.replicas && current.databases &&
    (current.replicas.targetRevision !== current.databases.targetRevision || current.replicas.runId !== current.databases.runId);
  const restartBoth = () => setRestart(value => value + 1);
  return <section aria-label="Per-replica availability evidence" className="availability-evidence">
    <h3>Per-replica availability evidence</h3>
    <p>Snapshot at the selected range's end. Each stream reads the latest published v2 run started in the preceding seven days.
      The range's start does not filter snapshot rows. Pause Live to browse a fixed snapshot.</p>
    {selection.error ? <p role="alert">{selection.error}</p> : <>
      <p>Publication cutoff: {formatTime(selection.atUtc)} · <button type="button" onClick={restartBoth}>Restart both AG streams</button></p>
      {mismatch && <p role="alert">The two streams returned different runs or target revisions. Compare them independently and restart both before correlating rows.</p>}
      {(["replicas", "databases"] as const).map(stream => <AvailabilityStreamPanel key={`${key}:${stream}`} requestKey={key}
        instanceId={instanceId} stream={stream} atUtc={selection.atUtc!} onPublished={published} onRestart={restartBoth} />)}
    </>}
  </section>;
}

function AvailabilityStreamPanel({ instanceId, stream, atUtc, requestKey, onPublished, onRestart }: {
  readonly instanceId: string;
  readonly stream: AvailabilityStream;
  readonly atUtc: string;
  readonly requestKey: string;
  readonly onPublished: (stream: AvailabilityStream, page: AvailabilityPage) => void;
  readonly onRestart: () => void;
}) {
  const formatTime = useTimeFormatter();
  const [paging, setPaging] = useState<{ index: number; cursors: readonly (string | undefined)[] }>({ index: 0, cursors: [undefined] });
  const [loaded, setLoaded] = useState<{ index: number; retry: number; page: AvailabilityPage }>();
  const [failed, setFailed] = useState<{ index: number; retry: number; error: AvailabilityEvidenceError }>();
  const [retry, setRetry] = useState(0);
  const page = loaded?.index === paging.index && loaded.retry === retry ? loaded.page : undefined;
  const error = failed?.index === paging.index && failed.retry === retry ? failed.error : undefined;
  const first = useRef<AvailabilityPage | undefined>(undefined);
  const cursor = paging.cursors[paging.index];
  useEffect(() => {
    const controller = new AbortController();
    setLoaded(undefined);
    setFailed(undefined);
    void getAvailabilityEvidence(instanceId, stream, atUtc, controller.signal, cursor)
      .then(next => {
        if (controller.signal.aborted) return;
        if (first.current) assertAvailabilitySnapshot(first.current, next);
        else first.current = next;
        setLoaded({ index: paging.index, retry, page: next });
        onPublished(stream, next);
      })
      .catch((failure: unknown) => {
        if (!controller.signal.aborted) setFailed({ index: paging.index, retry,
          error: failure instanceof AvailabilityEvidenceError ? failure
            : new AvailabilityEvidenceError("The AG snapshot changed. Restart both streams from the first page.", true) });
      });
    return () => controller.abort();
  }, [instanceId, stream, atUtc, requestKey, paging.index, cursor, retry, onPublished]);
  const title = stream === "replicas" ? "AG replicas" : "AG database replicas";
  const loading = !page && !error;
  return <article className="panel" aria-label={title} aria-busy={loading}>
    <h4>{title}</h4>
    {loading && <p role="status">Loading {title.toLowerCase()}…</p>}
    {error && <p role="alert">{error.message}{error.restart ? <button type="button" onClick={onRestart}>Restart AG snapshot</button>
      : error.retryable ? <button type="button" onClick={() => setRetry(value => value + 1)}>Retry {title.toLowerCase()}</button> : null}</p>}
    {page && <AvailabilityEvidenceView page={page} stream={stream} formatTime={formatTime} />}
    <nav className="pager" aria-label={`${title} pages`}>
      <button type="button" disabled={loading || paging.index === 0} onClick={() => setPaging(previous => ({ ...previous, index: 0 }))}>First {title.toLowerCase()} page</button>{" "}
      <button type="button" disabled={loading || paging.index === 0} onClick={() => setPaging(previous => ({ ...previous, index: previous.index - 1 }))}>Previous {title.toLowerCase()} page</button>{" "}
      <span>Page {paging.index + 1}</span>{" "}
      <button type="button" disabled={!page?.nextCursor} onClick={() => {
        if (page?.nextCursor) setPaging(previous => ({ index: previous.index + 1,
          cursors: [...previous.cursors.slice(0, previous.index + 1), page.nextCursor!] }));
      }}>Next {title.toLowerCase()} page</button>
    </nav>
  </article>;
}
