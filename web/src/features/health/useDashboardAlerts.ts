import { useEffect, useRef, useState } from "react";
import { alertFiredTimelineEvents, loadAlertTimelineInbox, type TimelineEvent } from "../../components/dashboardTimelineModel.ts";
import { getActiveAlerts } from "../alerts/alertApi.ts";
import type { OverviewScope } from "../overview/overviewTypes";

interface AlertSourceState { readonly context: string; readonly key: string; readonly events: readonly TimelineEvent[]; readonly note: string; readonly publishedNote?: string; readonly loading: boolean; }

export function useDashboardAlerts(targetId: string, targetRevision: number, scope: OverviewScope,
    window: { readonly fromUtc: string; readonly toUtc: string } | undefined, refresh: number) {
  const context = JSON.stringify([targetId.toLowerCase(), targetRevision, scope.range, scope.from, scope.to,
    scope.databaseId, scope.compare, window?.fromUtc, window?.toUtc]);
  const key = JSON.stringify([context, refresh]);
  const latest = useRef(key); latest.current = key;
  const [state, setState] = useState<AlertSourceState>();
  useEffect(() => {
    const controller = new AbortController();
    setState(previous => ({ context, key, events: previous?.context === context ? previous.events : [], loading: Boolean(window),
      publishedNote: previous?.context === context ? previous.publishedNote : undefined,
      note: previous?.context === context && previous.publishedNote ? `Refreshing current alert inbox; showing previously loaded fired markers in this same window. ${previous.publishedNote}` : "Loading current-inbox alert fired markers…" }));
    if (!window) return () => controller.abort();
    void loadAlertTimelineInbox(targetId, cursor => getActiveAlerts(targetId, controller.signal, 100, cursor), controller.signal)
      .then(inbox => {
        const events = alertFiredTimelineEvents(targetId, scope, window.fromUtc, window.toUtc, inbox);
        const note = `Current alert inbox: ${inbox.items.length} rows from ${inbox.pagesRead} of at most five pages / 500 rows${inbox.hasMore ? "; additional inbox rows are not loaded" : ""}. ${events.length} native fired markers in this window. Snapshot ${inbox.snapshotUtc ?? "not reported"}. Only current firing or acknowledged alerts with reported fired UTC become markers; resolved cycles and missing fired times are not reconstructed. Alerts remain server-wide.`;
        if (!controller.signal.aborted && latest.current === key) setState({ context, key, events, loading: false, note, publishedNote: note });
      })
      .catch(() => { if (!controller.signal.aborted && latest.current === key) setState({ context, key, events: [], loading: false,
        note: "Current-inbox alert markers are unavailable. Refresh when Viewer access and repository reads are available; no alert history or all-clear state is inferred." }); });
    return () => controller.abort();
  }, [key]);
  const current = state?.context === context ? state : undefined;
  return { events: current?.events ?? [], loading: Boolean(window && (!current || current.key !== key || current.loading)),
    note: !window ? "Alert fired markers need a valid loaded dashboard window."
      : !current ? "Loading current-inbox alert fired markers…"
      : current.key !== key ? current.publishedNote ? `Refreshing current alert inbox; showing previously loaded fired markers in this same window. ${current.publishedNote}` : "Loading current-inbox alert fired markers…" : current.note };
}
