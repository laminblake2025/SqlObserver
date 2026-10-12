import { useEffect, useState } from "react";
import { activityHistoryHref } from "../../dashboardModel.ts";
import { utcMicroseconds } from "../../utcTimestamp.ts";
import { configurationTimelineEvents, databaseOptionTimelineEvents, planRegressionTimelineEvents, supportsChangeTimelineWindow,
  type TimelineEvent } from "../../components/dashboardTimelineModel.ts";
import { getDeadlocks } from "../deadlocks/deadlockApi";
import { getQueryStorePlanRegressions } from "../queries/queryPerformanceApi";
import { getDatabaseOptionChanges, getServerConfigurationChanges } from "./healthApi";
import type { OverviewScope } from "../overview/overviewTypes";
import type { TargetHealthState } from "./healthTypes";

type Source = "deadlocks" | "options" | "configuration" | "plans";
interface SourceState { readonly events: readonly TimelineEvent[]; readonly note: string; readonly loading?: boolean; }
type Sources = Record<Source, SourceState>;
const utc = (value: string) => utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1"));

export function useDashboardTimeline(targetId: string, targetRevision: number, scope: OverviewScope,
    window: { readonly fromUtc: string; readonly toUtc: string } | undefined,
    refresh: number, configurationCollectorState?: TargetHealthState) {
  const scopeKey = JSON.stringify([targetId.toLowerCase(), targetRevision, window?.fromUtc, window?.toUtc,
    scope.databaseId, configurationCollectorState]);
  const key = JSON.stringify([scopeKey, refresh]);
  const [state, setState] = useState<{ readonly key: string; readonly scopeKey: string; readonly sources: Sources }>();
  useEffect(() => {
    const controller = new AbortController();
    const long = window && !supportsChangeTimelineWindow(window.fromUtc, window.toUtc);
    const sources: Sources = {
      deadlocks: { events: [], loading: Boolean(window), note: "Loading deadlock event markers…" },
      options: { events: [], loading: Boolean(window && !long), note: long ? "Database option markers support windows up to seven days." : "Loading database option change markers…" },
      configuration: { events: [], loading: Boolean(window && !long && configurationCollectorState !== "disabled"), note: configurationCollectorState === "disabled"
        ? "Server configuration collection is disabled; enable its collector to obtain change markers."
        : long ? "Server configuration markers support windows up to seven days." : "Loading server configuration change markers…" },
      plans: { events: [], loading: Boolean(window && !long), note: long ? "Plan comparison markers support windows up to seven days." : "Loading plan comparison markers…" },
    };
    setState(previous => {
      if (previous?.scopeKey === scopeKey) {
        for (const source of Object.keys(sources) as Source[]) {
          if (sources[source].loading && previous.sources[source].events.length) sources[source] = {
            ...sources[source], events: previous.sources[source].events,
            note: `Refreshing ${source}; showing previously loaded markers in this same window.`,
          };
        }
      }
      return { key, scopeKey, sources };
    });
    if (!window) return () => controller.abort();
    const fixedScope: OverviewScope = { ...scope, target: targetId, range: "custom", from: window.fromUtc, to: window.toUtc };
    const publish = (source: Source, value: SourceState) => {
      if (!controller.signal.aborted) setState(previous => previous?.key === key
        ? { key, scopeKey, sources: { ...previous.sources, [source]: value } } : previous);
    };
    const read = (source: Source, promise: Promise<SourceState>, failure: string) => {
      void promise.then(value => publish(source, value)).catch(() => publish(source, { events: [], note: failure }));
    };
    read("deadlocks", getDeadlocks(targetId, controller.signal, undefined, window).then(page => {
      if (page.targetId.toLowerCase() !== targetId.toLowerCase() || !Array.isArray(page.items) || page.items.length > 25)
        throw new Error("Deadlocks did not match the selected server.");
      const events = page.items.map(item => {
        if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu.test(item.eventId) ||
            !Number.isSafeInteger(item.participantCount) || item.participantCount < 0 ||
            utc(item.occurredAtUtc) < utc(window.fromUtc) || utc(item.occurredAtUtc) >= utc(window.toUtc))
          throw new Error("Deadlock event was invalid.");
        utc(item.collectedAtUtc);
        return { id: `deadlock:${item.eventId}`, kind: "deadlock" as const, timeUtc: item.occurredAtUtc,
          title: `Deadlock · ${item.participantCount} sessions`, timeBasis: "occurred" as const,
          detail: `Captured ${item.collectedAtUtc}. ${item.parseTruncated ? "Parsed relationships are truncated. " : ""}Open the activity captured for this event. Deadlocks remain server-wide.`,
          href: activityHistoryHref(targetId, item.occurredAtUtc, item.eventId, fixedScope) };
      });
      return { events, note: `Deadlocks: first ${page.items.length} of up to 25 events loaded${page.nextCursor ? "; additional pages are available in Deadlocks" : ""}. Capture gaps can hide events.` };
    }), "Deadlock event markers are unavailable. Refresh when collection and repository access are available.");
    if (!long) {
      read("options", getDatabaseOptionChanges(targetId, window, controller.signal).then(response => ({
        events: databaseOptionTimelineEvents(targetId, fixedScope, response),
        note: `Database options: up to 20 detected change groups from the first ${response.items.length} of up to 100 rows. Selected database filtering applies; changes occurred between observations.`,
      })), "Database option change markers are unavailable. Refresh when inventory collection and repository access are available.");
      if (configurationCollectorState !== "disabled") read("configuration", getServerConfigurationChanges(targetId, window, controller.signal).then(response => ({
        events: configurationTimelineEvents(targetId, fixedScope, response, targetRevision),
        note: response.coverage === "never_collected" ? "No server configuration change evidence has been published yet. Enable configuration collection to obtain evidence."
          : `Server configuration: up to 20 detected change groups from the first ${response.items.length} of up to 100 rows${response.nextCursor ? "; additional pages are available in configuration history" : ""}. This source remains server-wide.`,
      })), "Server configuration change markers are unavailable. Refresh when configuration collection and repository access are available.");
      read("plans", getQueryStorePlanRegressions(targetId, window.fromUtc, window.toUtc, controller.signal).then(response => ({
        events: planRegressionTimelineEvents(targetId, fixedScope, response),
        note: `Plan comparisons: first ${response.items.length} of up to 25 qualifying comparisons across this and the preceding equal window. Selected database filtering applies. Markers use the last observed group, not a plan-switch instant. Missing Query Store groups can hide regressions.`,
      })), "Plan comparison markers are unavailable. Check Query Store collection and repository access, then refresh.");
    }
    return () => controller.abort();
  }, [key]);
  const current = state?.scopeKey === scopeKey ? state.sources : undefined;
  const refreshing = state?.key !== key;
  return { events: current ? Object.values(current).flatMap(source => source.events) : [],
    loading: Boolean(window && (!current || refreshing || Object.values(current).some(source => source.loading))),
    notes: current ? refreshing ? ["Refreshing timeline sources; showing previously loaded markers in this same window."]
      : Object.values(current).map(source => source.note) : ["Loading timeline sources…"] };
}
