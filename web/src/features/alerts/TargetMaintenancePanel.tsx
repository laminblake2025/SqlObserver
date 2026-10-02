import { useState, type FormEvent } from "react";
import { useTimeDisplay, useTimeFormatter } from "../../TimeDisplayContext";
import { DetailPane } from "../../components/DiagnosticUi";
import { getMaintenanceForAdmin, type MaintenanceAdminItem } from "./alertAdminListsApi";
import { cancelMaintenanceWindow, createMaintenanceWindow, MaintenanceAttempts, prepareMaintenanceWindow } from "./maintenanceAdminActions";
import { useAlertAdminList } from "./useAlertAdminList";
import { adminSelectionMessage } from "./adminSelectionModel.ts";
import { maintenanceCalendarMonth, maintenanceMonthAt, shiftMaintenanceMonth } from "./maintenanceCalendarModel.ts";
import "./maintenanceCalendar.css";

const windowId = (item: MaintenanceAdminItem) => item.windowId;
const attempts = new MaintenanceAttempts();

export function TargetMaintenancePanel({ instanceId, displayName, onClose, refresh, canManage, selectedId, onSelect }: {
  readonly instanceId: string; readonly displayName: string; readonly onClose: () => void; readonly refresh: number; readonly canManage: boolean; readonly selectedId?: string | null; readonly onSelect: (id: string) => void;
}) {
  const formatTime = useTimeFormatter();
  const { mode } = useTimeDisplay();
  const list = useAlertAdminList(instanceId, refresh, getMaintenanceForAdmin, windowId, selectedId);
  const [startsLocal, setStartsLocal] = useState("");
  const [endsLocal, setEndsLocal] = useState("");
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState<string>();
  const [actionMessage, setActionMessage] = useState<string>();
  const [actionError, setActionError] = useState<string>();
  const selected = selectedId === undefined ? list.items[0] : list.items.find(item => item.windowId.toLowerCase() === selectedId?.toLowerCase());
  const selectionMessage = adminSelectionMessage(selectedId, Boolean(selected), list.findingSelection, Boolean(list.nextCursor));
  const calendarContext = JSON.stringify([instanceId.toLowerCase(), mode]);
  const [calendarView, setCalendarView] = useState<{ context: string; month: string; day: number }>();
  const now = new Date();
  const currentMonth = maintenanceMonthAt(now.toISOString(), mode)!;
  const today = mode === "utc" ? now.getUTCDate() : now.getDate();
  const selectedMonth = selected && maintenanceMonthAt(selected.startsAtUtc, mode);
  const view = calendarView?.context === calendarContext ? calendarView : undefined;
  const calendar = maintenanceCalendarMonth(view?.month ?? selectedMonth ?? currentMonth, list.items, mode);
  const selectedDay = calendar.days.find(day => day.windows.some(item => item.windowId.toLowerCase() === selected?.windowId.toLowerCase()));
  const day = calendar.days.find(item => item.day === view?.day) ?? selectedDay ?? calendar.days[0]!;
  const selectedOutsideMonth = Boolean(selected && !selectedDay);
  const previousMonth = shiftMaintenanceMonth(calendar.month, -1), nextMonth = shiftMaintenanceMonth(calendar.month, 1);
  const cells = [...Array.from({ length: calendar.offset }, () => undefined), ...calendar.days];
  while (cells.length % 7) cells.push(undefined);
  const zone = mode === "utc" ? "UTC" : Intl.DateTimeFormat().resolvedOptions().timeZone;
  function chooseMonth(month: string | undefined, selectedWindow = false) {
    if (!month || !/^(?!0000)\d{4}-(?:0[1-9]|1[0-2])$/u.test(month)) return;
    const next = maintenanceCalendarMonth(month, list.items, mode);
    const first = selectedWindow ? next.days.find(item => item.windows.some(window => window.windowId.toLowerCase() === selected?.windowId.toLowerCase()))
      : next.days.find(item => item.windows.length > 0);
    setCalendarView({ context: calendarContext, month, day: first?.day ?? 1 });
  }

  async function create(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!canManage || busy) return;
    setActionError(undefined);
    setActionMessage(undefined);
    try {
      const window = prepareMaintenanceWindow(startsLocal, endsLocal, reason);
      const attempt = attempts.createFor(instanceId, window);
      setBusy("create");
      await createMaintenanceWindow(instanceId, window, attempt, new AbortController().signal);
      attempts.completeCreate(instanceId, window);
      onSelect(attempt.windowId);
      setStartsLocal(""); setEndsLocal(""); setReason("");
      setActionMessage("Maintenance window created. The list is refreshing.");
      list.retry();
    } catch (failure) {
      setActionError(failure instanceof Error ? failure.message : "The maintenance window could not be created.");
    } finally { setBusy(undefined); }
  }

  async function cancel(window: MaintenanceAdminItem) {
    if (!canManage || busy || window.cancelledAtUtc) return;
    setActionError(undefined);
    setActionMessage(undefined);
    const operationId = attempts.cancellationFor(instanceId, window.windowId, window.revision);
    setBusy(window.windowId);
    try {
      await cancelMaintenanceWindow(instanceId, window.windowId, window.revision, operationId,
        new AbortController().signal);
      attempts.completeCancellation(instanceId, window.windowId, window.revision);
      setActionMessage("Maintenance window cancelled. The list is refreshing.");
      list.retry();
    } catch (failure) {
      setActionError(failure instanceof Error ? failure.message : "The maintenance window could not be cancelled.");
    } finally { setBusy(undefined); }
  }

  return <section className="alerts-screen" aria-labelledby="maintenance-heading">
    <div className="screen-intro"><div><p className="eyebrow">Alerts · {displayName}</p><h2 id="maintenance-heading">Maintenance windows</h2><p>Review scheduled and cancelled one-off windows. These windows suppress delivery, while collection and alert evaluation continue.</p></div><button className="secondary-button" onClick={onClose} type="button">Back to alerts</button></div>
    {list.loading ? <p role="status" className="status-message">Loading maintenance windows…</p> : null}
    {selectionMessage ? <p role="status" className="status-message">{selectionMessage}</p> : null}
    {list.error ? <div role="alert" className="status-message">{list.error} <button className="secondary-button" type="button" onClick={list.retry}>Retry</button></div> : null}
    {actionError ? <p role="alert" className="status-message">{actionError}</p> : null}
    {actionMessage ? <p role="status" className="status-message">{actionMessage}</p> : null}
    {canManage ? <form className="panel maintenance-form" onSubmit={(event) => void create(event)}>
      <div><h3>Schedule a window</h3><p>Times use your computer’s local time zone. Alert evaluation and collection continue during the window.</p></div>
      <div className="maintenance-fields"><label>Starts (local time)<input type="datetime-local" required value={startsLocal} onChange={event => setStartsLocal(event.target.value)} disabled={busy === "create"} /></label><label>Ends (local time)<input type="datetime-local" required value={endsLocal} onChange={event => setEndsLocal(event.target.value)} disabled={busy === "create"} /></label><label>Reason<textarea required maxLength={512} value={reason} onChange={event => setReason(event.target.value)} disabled={busy === "create"} /></label></div>
      <button className="primary" type="submit" disabled={Boolean(busy)}>{busy === "create" ? "Scheduling…" : "Schedule window"}</button>
    </form> : null}
    <section className="panel maintenance-calendar" aria-label="Maintenance calendar" aria-busy={list.loading || list.loadingMore}>
      <h3>Maintenance calendar</h3>
      <p className="table-note">{zone} display · {list.items.length} loaded one-off windows · {calendar.overlappingWindows} overlap this month. Cancelled windows remain visible. {list.nextCursor ? "More listing pages are available below." : "End of the loaded listing."} The calendar uses only loaded pages; an empty date does not establish that no maintenance was configured. Recurrence is not supplied by this source.</p>
      <p className="table-note">Numbers beneath dates count overlapping loaded windows, including cancelled windows. Choose a date to review its agenda.</p>
      <div className="maintenance-calendar-controls">
        <label>Calendar month<input type="month" min="0001-01" max="9999-12" value={calendar.month} onChange={event => chooseMonth(event.target.value)} /></label>
        <button className="secondary-button" type="button" disabled={!previousMonth} onClick={() => chooseMonth(previousMonth)}>Previous calendar month</button>
        <button className="secondary-button" type="button" disabled={!nextMonth} onClick={() => chooseMonth(nextMonth)}>Next calendar month</button>
        <button className="secondary-button" type="button" onClick={() => chooseMonth(currentMonth)}>Current calendar month</button>
      </div>
      {selectedOutsideMonth && <p role="status">The selected window does not overlap the displayed calendar month. Its exact configuration remains selected. <button type="button" className="secondary-button" onClick={() => chooseMonth(selectedMonth, true)}>Show selected window in calendar</button></p>}
      {calendar.unplaced > 0 && <p role="alert">{calendar.unplaced} loaded windows have invalid time bounds and cannot be placed in the calendar. Their list entries remain available for inspection.</p>}
      <div className="maintenance-calendar-layout">
        <table className="maintenance-month-table"><caption>{calendar.title}</caption>
          <thead><tr>{["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"].map(label => <th scope="col" key={label}><abbr title={label}>{label.slice(0, 3)}</abbr></th>)}</tr></thead>
          <tbody>{Array.from({ length: cells.length / 7 }, (_, row) => <tr key={row}>{cells.slice(row * 7, row * 7 + 7).map((cell, index) => <td key={index}>{cell && <button type="button" className="maintenance-calendar-day"
            aria-label={`${cell.label}: ${cell.windows.length} loaded windows, ${cell.cancelled} cancelled`}
            aria-pressed={cell.day === day.day} aria-current={currentMonth === calendar.month && today === cell.day ? "date" : undefined}
            onClick={() => setCalendarView({ context: calendarContext, month: calendar.month, day: cell.day })}>
            <span>{cell.day}</span><span className="maintenance-calendar-day-count" aria-hidden="true">{cell.windows.length || "—"}</span>
          </button>}</td>)}</tr>)}</tbody>
        </table>
        <section className="maintenance-agenda" aria-label="Maintenance date agenda">
          <h4>Agenda for {day.label}</h4>
          <p className="table-note">Windows overlap this civil day in {zone}, including windows starting on an earlier date. End times are exclusive; cancelled entries do not suppress delivery.</p>
          {day.windows.length ? <ul className="maintenance-agenda-list">{day.windows.map(item => <li key={item.windowId}>
            <button className="table-link" type="button" aria-label={`Select maintenance window ${item.reason} starting ${formatTime(item.startsAtUtc)}`} aria-pressed={item.windowId.toLowerCase() === selected?.windowId.toLowerCase()} onClick={() => { setCalendarView({ context: calendarContext, month: calendar.month, day: day.day }); onSelect(item.windowId); }}>{item.reason}</button>
            <p><span className="state-pill">{item.cancelledAtUtc ? "Cancelled" : "Configured"}</span> · {formatTime(item.startsAtUtc)} to {formatTime(item.endsAtUtc)}</p>
            <p>Window identifier: {item.windowId}</p>
          </li>)}</ul> : <p className="empty-state">No loaded windows overlap this date. Load additional pages when available to continue reviewing the bounded listing.</p>}
        </section>
      </div>
    </section>
    <div className="alerts-layout">
      <section className="alert-list panel" aria-label="Configured maintenance windows"><div className="table-card-heading"><div><h3>Windows ({list.items.length} loaded)</h3><p>Newest start time first. Cancelled windows remain visible for review.</p></div></div><div className="table-scroll"><table><caption>Maintenance windows for {displayName}</caption><thead><tr><th scope="col">State</th><th scope="col">Starts</th><th scope="col">Ends</th><th scope="col">Reason</th></tr></thead><tbody>{list.items.map(item => <tr key={item.windowId} className={item.windowId === selected?.windowId ? "selected-row" : ""}><td><span className="state-pill">{item.cancelledAtUtc ? "Cancelled" : "Configured"}</span></td><td><button className="table-link" type="button" aria-pressed={item.windowId === selected?.windowId} onClick={() => onSelect(item.windowId)}>{formatTime(item.startsAtUtc)}</button></td><td>{formatTime(item.endsAtUtc)}</td><td>{item.reason}</td></tr>)}</tbody></table></div>{!list.loading && !list.error && list.items.length === 0 ? <p className="empty-state">No maintenance windows are configured for this server.</p> : null}<div className="pager"><span>{list.nextCursor ? "More windows are available." : "End of loaded windows."}</span><button className="secondary-button" type="button" disabled={!list.nextCursor || list.loadingMore} onClick={() => void list.loadMore()}>{list.loadingMore ? "Loading…" : "Load more"}</button></div></section>
      {selected ? <DetailPane title="Maintenance window" subtitle={`Revision ${selected.revision} · ${displayName}`} actions={canManage && !selected.cancelledAtUtc ? <button className="secondary-button" type="button" disabled={Boolean(busy)} onClick={() => void cancel(selected)}>{busy === selected.windowId ? "Cancelling…" : "Cancel window"}</button> : undefined}><dl className="rule-details"><div><dt>Starts</dt><dd>{formatTime(selected.startsAtUtc)}</dd></div><div><dt>Ends</dt><dd>{formatTime(selected.endsAtUtc)}</dd></div><div><dt>Reason</dt><dd>{selected.reason}</dd></div><div><dt>Cancelled</dt><dd>{selected.cancelledAtUtc ? formatTime(selected.cancelledAtUtc) : "No"}</dd></div></dl><p className="table-note">Window identifier: {selected.windowId}</p></DetailPane> : <div className="detail-pane empty-state">Select a window to inspect its configuration.</div>}
    </div>
  </section>;
}
