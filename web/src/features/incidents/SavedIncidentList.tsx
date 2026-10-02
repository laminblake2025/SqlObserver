import { useEffect, useId, useRef, useState } from "react";
import { IncidentRequestError, listSavedIncidents } from "./incidentApi.ts";
import { savedIncidentHref } from "./incidentModel.ts";
import type { SavedIncidentPage } from "./incidentModel.ts";
import type { OverviewScope } from "../overview/overviewTypes.ts";

interface ListSnapshot {
  readonly key: string; readonly pending: boolean; readonly page?: SavedIncidentPage;
  readonly error?: { message: string; retryable: boolean };
}

export function SavedIncidentList({ targetId, targetRevision, scope, formatTime, compact = false }: {
  readonly targetId: string; readonly targetRevision?: number; readonly scope: OverviewScope;
  readonly formatTime: (value: string) => string; readonly compact?: boolean;
}) {
  const context = JSON.stringify([targetId.toLowerCase(), targetRevision]);
  const [expandedContext, setExpanded] = useState<string>();
  const expanded = expandedContext === context;
  const [priorState, setPrior] = useState<{ context: string; page: SavedIncidentPage }>();
  const prior = priorState?.context === context ? priorState.page : undefined;
  const [reload, setReload] = useState(0);
  const key = JSON.stringify([context, expanded, prior?.nextCursor, reload]);
  const latest = useRef(key); latest.current = key;
  const [snapshot, setSnapshot] = useState<ListSnapshot>();
  const current = snapshot?.key === key ? snapshot : undefined;
  const page = current?.page; const error = current?.error;
  const pending = expanded && (!current || current.pending);
  const contentsId = useId();
  const Heading = compact ? "h2" : "h4";
  useEffect(() => {
    if (!expanded) return;
    const controller = new AbortController(); setSnapshot({ key, pending: true });
    void listSavedIncidents(targetId, controller.signal, prior)
      .then(value => { if (!controller.signal.aborted && latest.current === key) setSnapshot({ key, pending: false, page: value }); })
      .catch(reason => { if (!controller.signal.aborted && latest.current === key) setSnapshot({ key, pending: false, error: {
        message: reason instanceof Error ? reason.message : "Saved investigations are unavailable.",
        retryable: reason instanceof IncidentRequestError && reason.retryable } }); });
    return () => controller.abort();
  }, [targetId, expanded, prior, key]);
  const refresh = () => { setPrior(undefined); setReload(value => value + 1); };
  return <section className="saved-incident-list">
    <button type="button" aria-expanded={expanded} aria-controls={contentsId} onClick={() => setExpanded(expanded ? undefined : context)}>Saved investigations</button>
    <div id={contentsId} hidden={!expanded} className="panel incident-list-content">
      {expanded && <>
        <div className="section-heading"><Heading>Saved investigations</Heading><button type="button" disabled={pending} onClick={refresh}>Refresh investigations</button></div>
        <p>Newest saves first, across this server's saved revisions. Open an investigation to restore its window and evidence.</p>
        {pending && <p role="status">Loading saved investigations…</p>}
        {error && <p role="alert">{error.message}</p>}
        {error?.retryable && <button type="button" onClick={() => setReload(value => value + 1)}>Retry loading investigations</button>}
        {!pending && !error && page?.items.length === 0 && <p>No saved investigations were found.</p>}
        {page && !error && !pending && <ul className="incident-summary-list">{page.items.map(item => <li key={item.threadId}>
          <a href={savedIncidentHref(item, scope)}>{item.title}</a>
          <span>Saved {formatTime(item.savedAtUtc)} · Window {formatTime(item.fromUtc)} to {formatTime(item.toUtc)}</span>
          <small>{item.evidenceCount} evidence packet{item.evidenceCount === 1 ? "" : "s"} · Server revision {item.targetRevision}{item.evidenceTruncated ? " · Capture truncated" : ""}</small>
        </li>)}</ul>}
        {page?.hasMore && !error && !pending && <button type="button" onClick={() => setPrior({ context, page })}>Older investigations</button>}
      </>}
    </div>
  </section>;
}
