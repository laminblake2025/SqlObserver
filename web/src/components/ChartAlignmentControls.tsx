import { useId, useState } from "react";
import { chartInspectionInWindow, chartInspectionUtc } from "./chartInspectionModel.ts";
import "./chartAlignment.css";

export function ChartAlignmentControls({ context, label, headingLevel = 2, fromUtc, toUtc, requestedUtc, crosshairUtc, onAlign, onClear,
  clearFallbackNote = "An independently selected timeline event can still align charts." }: {
  readonly context: string; readonly label: string; readonly fromUtc: string; readonly toUtc: string;
  readonly requestedUtc: string | null | undefined; readonly crosshairUtc: string | null;
  readonly headingLevel?: 2 | 4;
  readonly clearFallbackNote?: string;
  readonly onAlign: (utc: string) => void; readonly onClear: () => void;
}) {
  const description = useId(), source = requestedUtc ?? "", sourceIdentity = requestedUtc === null ? "invalid" : requestedUtc ?? "absent";
  const [draft, setDraft] = useState<{ context: string; source: string; value: string; error?: string }>();
  const current = draft?.context === context && draft.source === sourceIdentity ? draft : undefined;
  const Heading = headingLevel === 4 ? "h4" : "h2";
  return <section className="panel chart-alignment" aria-label={`${label} chart alignment`}>
    <Heading>Inspect {label.toLowerCase()} charts at one time</Heading>
    <p id={description}>Align loaded charts to one UTC instant. This keeps the workspace window unchanged and does not add observations or select a timeline event.</p>
    <form onSubmit={event => {
      event.preventDefault();
      try {
        const utc = chartInspectionUtc(current?.value ?? source);
        if (!chartInspectionInWindow(utc, fromUtc, toUtc)) throw new Error("Choose an inspection time inside the loaded window, before its end.");
        onAlign(utc); setDraft(undefined);
      } catch (failure) { setDraft({ context, source: sourceIdentity, value: current?.value ?? source, error: failure instanceof Error ? failure.message : "Choose a valid inspection time." }); }
    }}>
      <label>{label} inspection time (UTC)<input type="text" value={current?.value ?? source}
        placeholder="2026-09-26T10:00:00.123456Z" spellCheck={false} autoComplete="off"
        aria-invalid={Boolean(current?.error)} aria-describedby={description}
        onChange={event => setDraft({ context, source: sourceIdentity, value: event.target.value })} /></label>
      <div className="screen-actions"><button type="submit" className="secondary-button">Align {label.toLowerCase()} charts</button>
        <button type="button" className="secondary-button" disabled={requestedUtc === undefined}
          onClick={() => { setDraft(undefined); onClear(); }}>Clear {label.toLowerCase()} alignment</button></div>
    </form>
    {current?.error && <p role="alert">{current.error}</p>}
    {requestedUtc === null && <p role="alert">The requested chart inspection time is invalid or repeated. Enter a valid UTC time or clear alignment.</p>}
    {requestedUtc && !chartInspectionInWindow(requestedUtc, fromUtc, toUtc) && <p role="status">Requested chart inspection time is outside the loaded window. Align another time or clear alignment.</p>}
    <p role="status">{crosshairUtc ? <>Chart inspection time: <time dateTime={crosshairUtc}>{crosshairUtc}</time>. Missing evidence remains unavailable.</> : "No chart alignment selected. Use the UTC input or move the pointer over a loaded chart."}</p>
    <small>Loaded window: {fromUtc} to {toUtc}, excluding the end.</small>
    <small>Clear removes the UTC selection.{clearFallbackNote ? ` ${clearFallbackNote}` : ""}</small>
  </section>;
}
