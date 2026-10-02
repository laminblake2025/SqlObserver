import { useEffect, useId, useMemo, useRef, useState } from "react";
import { useTimeFormatter } from "../TimeDisplayContext";
import { timelineEventsInWindow, type TimelineEvent } from "./dashboardTimelineModel";
import { matchesDashboardTimelineSelection, type DashboardTimelineSelection } from "./dashboardTimelineSelectionModel.ts";

const categories: readonly { value: "all" | TimelineEvent["kind"]; label: string }[] = [
  { value: "all", label: "All events" },
  { value: "deadlock", label: "Deadlocks" },
  { value: "agent_failure", label: "SQL Agent failures" },
  { value: "database_options", label: "Database options" },
  { value: "configuration", label: "Server configuration" },
  { value: "plan_regression", label: "Plan comparisons" },
  { value: "finding", label: "Snapshot findings" },
];
const timeBasisLabels: Readonly<Record<TimelineEvent["timeBasis"], string>> = {
  occurred: "Event occurred",
  first_observed: "First observed",
  detected: "Change detected",
  last_observed: "Last observed",
  snapshot: "Evidence snapshot",
};

export function DashboardTimeline({ events, fromUtc, toUtc, crosshairUtc, onSelectTime, sourceNotes,
  selection, onSelectionChange, loading = false }: {
  readonly events: readonly TimelineEvent[];
  readonly fromUtc: string;
  readonly toUtc: string;
  readonly crosshairUtc?: string | null;
  readonly onSelectTime: (utc: string | null) => void;
  readonly sourceNotes: readonly string[];
  readonly selection?: DashboardTimelineSelection | null;
  readonly onSelectionChange?: (selection: DashboardTimelineSelection | undefined) => void;
  readonly loading?: boolean;
}) {
  const headingId = useId();
  const timeLabel = useTimeFormatter();
  const [category, setCategory] = useState<"all" | TimelineEvent["kind"]>("all");
  const [localSelection, setLocalSelection] = useState<DashboardTimelineSelection & { readonly windowKey: string }>();
  const callback = useRef(onSelectTime);
  const ownsAlignment = useRef(false);
  const windowKey = JSON.stringify([fromUtc, toUtc]);
  const loaded = useMemo(() => timelineEventsInWindow(events, fromUtc, toUtc), [events, fromUtc, toUtc]);
  const visible = loaded.filter(event => category === "all" || event.kind === category);
  const controlled = onSelectionChange !== undefined;
  const requested = controlled ? selection : localSelection?.windowKey === windowKey ? localSelection : undefined;
  const loadedSelection = requested ? loaded.find(event => matchesDashboardTimelineSelection(requested, event)) : undefined;
  const selected = loadedSelection && (category === "all" || loadedSelection.kind === category) ? loadedSelection : undefined;
  useEffect(() => { callback.current = onSelectTime; }, [onSelectTime]);
  useEffect(() => {
    if (!controlled && localSelection && !selected) {
      setLocalSelection(undefined);
      ownsAlignment.current = false;
      callback.current(null);
    }
  }, [controlled, localSelection, selected]);
  useEffect(() => () => {
    if (ownsAlignment.current) callback.current(null);
  }, []);
  const align = (event: TimelineEvent) => {
    const next = { eventId: event.id, timeUtc: event.timeUtc };
    if (controlled) onSelectionChange(next);
    else setLocalSelection({ ...next, windowKey });
    ownsAlignment.current = !controlled;
    onSelectTime(event.timeUtc);
  };
  const clearAlignment = () => {
    setLocalSelection(undefined);
    onSelectionChange?.(undefined);
    ownsAlignment.current = false;
    onSelectTime(null);
  };
  return <section className="dashboard-timeline panel" aria-labelledby={headingId}>
    <div className="dashboard-timeline-heading">
      <div><h4 id={headingId}>Investigation timeline</h4>
        <p>Loaded evidence from {timeLabel(fromUtc)} to {timeLabel(toUtc)}. Align charts to an event, then open its evidence. Events sharing a time do not establish a common cause.</p>
      </div>
      <label>Timeline event category
        <select value={category} onChange={event => {
          const next = event.target.value as "all" | TimelineEvent["kind"];
          setCategory(next);
          if (selected && next !== "all" && selected.kind !== next) clearAlignment();
        }}>
          {categories.map(item => <option value={item.value} key={item.value}>{item.label}</option>)}
        </select>
      </label>
    </div>
    <p className="dashboard-timeline-status" role="status">
      {visible.length} shown · {loaded.length} loaded events · up to 120 events in this window.
      {selected && <> Selected event: {selected.title}.</>}
      {crosshairUtc && Number.isFinite(Date.parse(crosshairUtc)) && <> Chart crosshair: <time dateTime={crosshairUtc} title={crosshairUtc}>{timeLabel(crosshairUtc)}</time>.</>}
    </p>
    {controlled && selection === null && <p className="empty-state" role="status">The requested timeline selection is invalid for this server and window. No other event has been selected.</p>}
    {controlled && requested && !loadedSelection && <p className="empty-state" role="status">{loading
      ? "Loading the requested timeline event…"
      : "The requested timeline event is not present in the loaded evidence for this window. No other event has been selected."}</p>}
    {controlled && loadedSelection && !selected && <p className="empty-state" role="status">
      The selected timeline event is hidden by this category. Show all events or clear the selection.{" "}
      <button className="secondary-button" type="button" onClick={() => setCategory("all")}>Show selected timeline event</button>
    </p>}
    {(selected || controlled && selection !== undefined) && <button className="secondary-button dashboard-timeline-clear" type="button" onClick={clearAlignment}>Clear timeline alignment</button>}
    {visible.length === 0 ? <p className="empty-state">{loaded.length === 0
      ? loading ? "Timeline sources are loading for this window." : "No timeline events were loaded in this window. Missing or partial sources do not prove there were no events."
      : "No loaded events match this category. Other categories and unloaded sources may contain evidence."}</p>
      : <ol className="dashboard-timeline-events" role="list">{visible.map(event => <li key={event.id} className={selected?.id === event.id ? "dashboard-timeline-event is-selected" : "dashboard-timeline-event"}>
        <div className="dashboard-timeline-event-time">
          <span>{timeBasisLabels[event.timeBasis]}</span>
          <time dateTime={event.timeUtc} title={event.timeUtc}>{timeLabel(event.timeUtc)}</time>
          <small>{categories.find(item => item.value === event.kind)?.label}</small>
        </div>
        <div className="dashboard-timeline-event-evidence">
          <button className="dashboard-timeline-align" type="button" aria-label={`Align charts: ${event.title}`}
            aria-pressed={selected?.id === event.id} onClick={() => align(event)}>{event.title}</button>
          <p>{event.detail}</p>
          <a href={event.href} aria-label={`Open evidence: ${event.title}`}>Open evidence</a>
        </div>
      </li>)}</ol>}
    <details className="dashboard-timeline-coverage">
      <summary>Timeline coverage</summary>
      <p>This timeline combines loaded observations with different time meanings. Snapshot findings and plan comparisons do not supply the exact moment a condition began. Missing observations are not evidence of normal health.</p>
      {sourceNotes.length > 0 ? <ul>{sourceNotes.map((note, index) => <li key={`${index}:${note}`}>{note}</li>)}</ul>
        : <p>Source coverage details were not returned for this window.</p>}
    </details>
  </section>;
}
