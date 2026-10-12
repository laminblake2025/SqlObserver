import { useEffect, useId, useState } from "react";
import { useTimeDisplay } from "../../TimeDisplayContext";
import { displayRangeInput, editedRangeUtc } from "../../components/timeRangeEditorModel";

const calendarTime = (minute: number | undefined, mode: "utc" | "local") => minute === undefined ? "" : displayRangeInput(new Date(minute * 60000).toISOString(), mode).slice(0, 16);

export function SnapshotCalendar({ minute, earliestMinute, latestMinute, onSelect }: {
  readonly minute: number | undefined;
  readonly earliestMinute: number;
  readonly latestMinute: number;
  readonly onSelect: (minute: number) => void;
}) {
  const id = useId();
  const { mode } = useTimeDisplay();
  const timezone = mode === "utc" ? "UTC" : Intl.DateTimeFormat().resolvedOptions().timeZone;
  const [draft, setDraft] = useState(() => calendarTime(minute, mode));
  const [error, setError] = useState("");
  useEffect(() => { setDraft(calendarTime(minute, mode)); setError(""); }, [minute, earliestMinute, latestMinute, mode]);
  return <div className="snapshot-calendar">
    <label htmlFor={id}>Exact snapshot ({timezone})</label>
    <input id={id} type="datetime-local" step={60} required value={draft}
      min={mode === "utc" ? calendarTime(earliestMinute, mode) : undefined} max={mode === "utc" ? calendarTime(latestMinute, mode) : undefined}
      aria-describedby={`${id}-help${error ? ` ${id}-error` : ""}`} aria-invalid={Boolean(error)}
      onChange={event => {
        const value = event.currentTarget.value;
        setDraft(value);
        let selected: number;
        try { selected = Date.parse(`${editedRangeUtc(value, mode)}Z`) / 60000; }
        catch (failure) { setError(failure instanceof Error ? failure.message : "Choose a valid time in the display timezone."); return; }
        if (!event.currentTarget.validity.valid || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/u.test(value)
          || !Number.isSafeInteger(selected) || selected < earliestMinute || selected > latestMinute) {
          setError("Choose a date and minute within the selected history window. The selected capture has not changed.");
          return;
        }
        setError("");
        onSelect(selected);
      }} />
    <small id={`${id}-help`}>Choose a minute in {timezone}. Minutes without a retained capture are shown as gaps. The selected capture retains its exact observation timestamp.</small>
    {error && <small id={`${id}-error`} role="alert">{error}</small>}
  </div>;
}
