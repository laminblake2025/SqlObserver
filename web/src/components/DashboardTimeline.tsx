import { useEffect, useId, useMemo, useRef, useState, type PointerEvent } from "react";
import { useTimeFormatter } from "../TimeDisplayContext";
import { timelineEventsInWindow, type TimelineEvent } from "./dashboardTimelineModel";
import { matchesDashboardTimelineSelection, type DashboardTimelineSelection } from "./dashboardTimelineSelectionModel.ts";
import { chartSelection, chartX } from "../features/overview/chartTimeModel.ts";
import { canonicalUtcTimestamp, utcMicroseconds } from "../utcTimestamp.ts";
import "./dashboardTimelineBrowser.css";

const categories: readonly { value: "all" | TimelineEvent["kind"]; label: string }[] = [
  { value: "all", label: "All events" },
  { value: "deadlock", label: "Deadlocks" },
  { value: "alert", label: "Alerts (retained lifecycle)" },
  { value: "agent_failure", label: "SQL Agent failures" },
  { value: "database_options", label: "Database options" },
  { value: "configuration", label: "Server configuration" },
  { value: "plan_regression", label: "Plan comparisons" },
  { value: "finding", label: "Snapshot findings" },
];
const timeBasisLabels: Readonly<Record<TimelineEvent["timeBasis"], string>> = {
  occurred: "Event occurred",
  fired: "Alert fired",
  first_observed: "First observed",
  detected: "Change detected",
  last_observed: "Last observed",
  snapshot: "Evidence snapshot",
  observed: "Evaluator observation",
  applied: "Operation applied",
};

export function DashboardTimeline({ events, fromUtc, toUtc, crosshairUtc, onSelectTime, sourceNotes,
  selection, onSelectionChange, onSelectWindow, loading = false, pinnedExactEvent, contextKey, interactionEnabled = true }: {
  readonly events: readonly TimelineEvent[];
  readonly fromUtc: string;
  readonly toUtc: string;
  readonly crosshairUtc?: string | null;
  readonly onSelectTime: (utc: string | null) => void;
  readonly sourceNotes: readonly string[];
  readonly selection?: DashboardTimelineSelection | null;
  readonly onSelectionChange?: (selection: DashboardTimelineSelection | undefined) => void;
  readonly onSelectWindow?: (window: { readonly fromUtc: string; readonly toUtc: string }) => void;
  readonly loading?: boolean;
  readonly pinnedExactEvent?: TimelineEvent;
  readonly contextKey?: string;
  readonly interactionEnabled?: boolean;
}) {
  const headingId = useId();
  const timeLabel = useTimeFormatter();
  const [category, setCategory] = useState<"all" | TimelineEvent["kind"]>("all");
  const [localSelection, setLocalSelection] = useState<DashboardTimelineSelection & { readonly windowKey: string }>();
  const callback = useRef(onSelectTime);
  const ownsAlignment = useRef(false);
  const windowInstant = (value: string) => {
    try { return utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1")).toString(); }
    catch { return value; }
  };
  const windowKey = JSON.stringify([contextKey, windowInstant(fromUtc), windowInstant(toUtc)]);
  const gestureKey = JSON.stringify([windowKey, interactionEnabled]);
  const currentGesture = useRef(gestureKey); currentGesture.current = gestureKey;
  const currentWindow = useRef(windowKey);
  currentWindow.current = windowKey;
  const [eventBrowser, setEventBrowser] = useState<{ key: string; open: boolean }>();
  const [focusRequest, setFocusRequest] = useState<{ key: string; eventId: string }>();
  const eventButtons = useRef(new Map<string, HTMLButtonElement>());
  const rail = useRef<SVGSVGElement | null>(null);
  const gesture = useRef<{ key: string; pointer: number; start: number; end: number } | undefined>(undefined);
  const [preview, setPreview] = useState<{ key: string; start: number; end: number }>();
  const [draft, setDraft] = useState<{ key: string; from: string; to: string }>();
  const [rangeError, setRangeError] = useState<{ key: string; message: string }>();
  const range = draft?.key === windowKey ? draft : { key: windowKey, from: fromUtc, to: toUtc };
  const cancelGesture = () => {
    const active = gesture.current;
    gesture.current = undefined;
    if (active && rail.current?.hasPointerCapture(active.pointer)) rail.current.releasePointerCapture(active.pointer);
    setPreview(undefined);
  };
  useEffect(() => {
    cancelGesture();
    return () => {
      const active = gesture.current;
      gesture.current = undefined;
      if (active && rail.current?.hasPointerCapture(active.pointer)) rail.current.releasePointerCapture(active.pointer);
    };
  }, [gestureKey]);
  const position = (event: PointerEvent<SVGSVGElement>) => {
    const bounds = event.currentTarget.getBoundingClientRect();
    return chartX(event.clientX, bounds.left + event.currentTarget.clientLeft, event.currentTarget.clientWidth);
  };
  const beginRange = (event: PointerEvent<SVGSVGElement>) => {
    if (!interactionEnabled || !onSelectWindow || event.button !== 0 || !event.isPrimary || gesture.current) return;
    const start = position(event);
    gesture.current = { key: gestureKey, pointer: event.pointerId, start, end: start };
    event.currentTarget.setPointerCapture(event.pointerId);
    setPreview({ key: windowKey, start, end: start });
  };
  const moveRange = (event: PointerEvent<SVGSVGElement>) => {
    const active = gesture.current;
    if (!active || active.pointer !== event.pointerId || active.key !== currentGesture.current) return;
    active.end = position(event);
    setPreview({ key: windowKey, start: active.start, end: active.end });
  };
  const endRange = (event: PointerEvent<SVGSVGElement>) => {
    const active = gesture.current;
    if (!active || active.pointer !== event.pointerId) return;
    const selected = interactionEnabled && active.key === currentGesture.current
      ? chartSelection(active.start, position(event), fromUtc, toUtc) : undefined;
    cancelGesture();
    if (selected) onSelectWindow?.(selected);
  };
  const instant = (value: string) => utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1"));
  const applyRange = () => {
    if (!interactionEnabled || !onSelectWindow || currentWindow.current !== windowKey) return;
    try {
      const start = instant(range.from), end = instant(range.to);
      if (start < instant(fromUtc) || end > instant(toUtc) || end - start < 60_000_000n)
        throw new Error("Choose at least one minute within the loaded timeline window.");
      setRangeError(undefined);
      onSelectWindow({ fromUtc: canonicalUtcTimestamp(range.from.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1")),
        toUtc: canonicalUtcTimestamp(range.to.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1")) });
    } catch {
      setRangeError({ key: windowKey, message: "Use valid UTC timestamps and choose at least one minute within the loaded timeline window." });
    }
  };
  const loaded = useMemo(() => timelineEventsInWindow(events, fromUtc, toUtc,pinnedExactEvent), [events, fromUtc, toUtc,pinnedExactEvent]);
  const visible = loaded.filter(event => category === "all" || event.kind === category);
  const controlled = onSelectionChange !== undefined;
  const requested = controlled ? selection : localSelection?.windowKey === windowKey ? localSelection : undefined;
  const loadedSelection = requested ? loaded.find(event => matchesDashboardTimelineSelection(requested, event)) : undefined;
  const selected = loadedSelection && (category === "all" || loadedSelection.kind === category) ? loadedSelection : undefined;
  const drag = preview?.key === windowKey ? preview : undefined;
  const previewWindow = drag ? chartSelection(drag.start, drag.end, fromUtc, toUtc) : undefined;
  const eventX = (value: string) => 48 + Number(instant(value) - instant(fromUtc)) / Number(instant(toUtc) - instant(fromUtc)) * 572;
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
    if (!interactionEnabled) return;
    const next = { eventId: event.id, timeUtc: event.timeUtc };
    if (controlled) onSelectionChange(next);
    else setLocalSelection({ ...next, windowKey });
    ownsAlignment.current = !controlled;
    onSelectTime(event.timeUtc);
  };
  const clearAlignment = () => {
    if (!interactionEnabled) return;
    setLocalSelection(undefined);
    onSelectionChange?.(undefined);
    ownsAlignment.current = false;
    onSelectTime(null);
  };
  const showSelectedEvent = () => {
    if (!interactionEnabled || !loadedSelection || currentWindow.current !== windowKey) return;
    setCategory("all");
    setEventBrowser({ key: windowKey, open: true });
    setFocusRequest({ key: windowKey, eventId: loadedSelection.id });
  };
  useEffect(() => {
    if (!focusRequest) return;
    if (interactionEnabled && focusRequest.key === windowKey) eventButtons.current.get(focusRequest.eventId)?.focus();
    setFocusRequest(undefined);
  }, [focusRequest, windowKey, interactionEnabled]);
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
    {onSelectWindow && <div className="dashboard-timeline-range">
      <p id={`${headingId}-range-help`}>Drag across the time rail to select a shared window of at least one minute, or enter its UTC bounds below. The end time is exclusive.</p>
      <svg ref={rail} className="dashboard-timeline-rail" viewBox="0 0 650 64" preserveAspectRatio="none" role="img"
        aria-label="Timeline range selection with loaded event markers" aria-describedby={`${headingId}-range-help`}
        onPointerDown={beginRange} onPointerMove={moveRange} onPointerUp={endRange}
        onPointerCancel={event => { if (gesture.current?.pointer === event.pointerId) cancelGesture(); }}
        onLostPointerCapture={event => { if (gesture.current?.pointer === event.pointerId) cancelGesture(); }}>
        <line className="dashboard-timeline-rail-axis" x1="48" x2="620" y1="32" y2="32" />
        {drag && <rect className="dashboard-timeline-range-preview" x={Math.min(drag.start, drag.end)} y="12"
          width={Math.abs(drag.end - drag.start)} height="40" />}
        {visible.map(event => <line key={event.id} className="dashboard-timeline-rail-marker"
          x1={eventX(event.timeUtc)} x2={eventX(event.timeUtc)} y1="22" y2="42"><title>{event.title} · {timeLabel(event.timeUtc)}</title></line>)}
        <line className="dashboard-timeline-rail-axis" x1="48" x2="48" y1="25" y2="39" />
        <line className="dashboard-timeline-rail-axis" x1="620" x2="620" y1="25" y2="39" />
      </svg>
      <div className="dashboard-timeline-rail-bounds"><span>{timeLabel(fromUtc)}</span><span>{timeLabel(toUtc)}</span></div>
      <p className="dashboard-timeline-range-status" role="status">{previewWindow
        ? `Selected preview: ${timeLabel(previewWindow.fromUtc)} to ${timeLabel(previewWindow.toUtc)}.`
        : drag ? "Drag farther to select at least one minute." : "The rail shows loaded events; empty intervals do not establish normal health."}</p>
      <form className="dashboard-timeline-range-form" aria-label="Select timeline window" onSubmit={event => { event.preventDefault(); applyRange(); }}>
        <label>Timeline from UTC<input value={range.from} onChange={event => setDraft({ ...range, from: event.target.value })} /></label>
        <label>Timeline to UTC<input value={range.to} onChange={event => setDraft({ ...range, to: event.target.value })} /></label>
        <button type="submit">Apply timeline window</button>
        {rangeError?.key === windowKey && <p role="alert">{rangeError.message}</p>}
      </form>
    </div>}
    <p className="dashboard-timeline-status" role="status">
      {visible.length} matching · {loaded.length} loaded events · up to 120 events in this window.
      {pinnedExactEvent && <> The validated exact selected event is included alongside up to 119 other latest loaded events.</>}
      {selected && <> Selected event: {selected.title}.</>}
      {crosshairUtc && Number.isFinite(Date.parse(crosshairUtc)) && <> Chart crosshair: <time dateTime={crosshairUtc} title={crosshairUtc}>{timeLabel(crosshairUtc)}</time>.</>}
    </p>
    {controlled && selection === null && <p className="empty-state" role="status">The requested timeline selection is invalid for this server and window. No other event has been selected.</p>}
    {controlled && requested && !loadedSelection && <p className="empty-state" role="status">{loading
      ? "Loading the requested timeline event…"
      : "The requested timeline event is not present in the loaded evidence for this window. No other event has been selected."}</p>}
    {controlled && loadedSelection && !selected && <p className="empty-state" role="status">
      The selected timeline event is hidden by this category. Show all events or clear the selection.{" "}
      <button className="secondary-button" type="button" onClick={showSelectedEvent}>Show selected timeline event</button>
    </p>}
    {loadedSelection && <div className="dashboard-timeline-selected-evidence">
      <p><strong>{loadedSelection.title}</strong> · {timeBasisLabels[loadedSelection.timeBasis]}:{" "}
        <time dateTime={loadedSelection.timeUtc} title={loadedSelection.timeUtc}>{timeLabel(loadedSelection.timeUtc)}</time></p>
      <p>{loadedSelection.detail}</p>
      <div className="dashboard-timeline-selected-actions">
        <a href={loadedSelection.href} aria-label="Open selected event evidence">Open selected event evidence</a>
        {selected && <button className="secondary-button" type="button" onClick={showSelectedEvent}>Show selected event in timeline list</button>}
      </div>
    </div>}
    {(selected || controlled && selection !== undefined) && <button className="secondary-button dashboard-timeline-clear" type="button" onClick={clearAlignment}>Clear timeline alignment</button>}
    {visible.length === 0 ? <p className="empty-state">{loaded.length === 0
      ? loading ? "Timeline sources are loading for this window." : "No timeline events were loaded in this window. Missing or partial sources do not prove there were no events."
      : "No loaded events match this category. Other categories and unloaded sources may contain evidence."}</p>
      : <details className="dashboard-timeline-browser" open={eventBrowser?.key === windowKey && eventBrowser.open}
        onToggle={event => {
          if (currentWindow.current === windowKey) setEventBrowser({ key: windowKey, open: event.currentTarget.open });
        }}>
        <summary>Browse loaded timeline events ({visible.length})</summary>
        <div className="dashboard-timeline-browser-scroll" role="region" aria-label="Loaded timeline event list" tabIndex={0}>
        <ol className="dashboard-timeline-events" role="list">{visible.map(event => <li key={event.id} className={selected?.id === event.id ? "dashboard-timeline-event is-selected" : "dashboard-timeline-event"}>
        <div className="dashboard-timeline-event-time">
          <span>{timeBasisLabels[event.timeBasis]}</span>
          <time dateTime={event.timeUtc} title={event.timeUtc}>{timeLabel(event.timeUtc)}</time>
          <small>{categories.find(item => item.value === event.kind)?.label}</small>
        </div>
        <div className="dashboard-timeline-event-evidence">
          <button className="dashboard-timeline-align" type="button" aria-label={`Align charts: ${event.title}`}
            ref={element => { if (element) eventButtons.current.set(event.id, element); else eventButtons.current.delete(event.id); }}
            aria-pressed={selected?.id === event.id} onClick={() => align(event)}>{event.title}</button>
          <p>{event.detail}</p>
          <a href={event.href} aria-label={`Open evidence: ${event.title}`}>Open evidence</a>
        </div>
      </li>)}</ol></div></details>}
    <details className="dashboard-timeline-coverage">
      <summary>Timeline coverage</summary>
      <p>This timeline combines loaded observations with different time meanings. Snapshot findings and plan comparisons do not supply the exact moment a condition began. Missing observations are not evidence of normal health.</p>
      {sourceNotes.length > 0 ? <ul>{sourceNotes.map((note, index) => <li key={`${index}:${note}`}>{note}</li>)}</ul>
        : <p>Source coverage details were not returned for this window.</p>}
    </details>
  </section>;
}
