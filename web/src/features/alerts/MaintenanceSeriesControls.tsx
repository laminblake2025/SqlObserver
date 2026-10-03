import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { ApiRequestError } from "../../apiClient.ts";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { changeMaintenanceSeries, getMaintenanceSeries, getMaintenanceSeriesStatus, getMaintenanceSeriesOccurrences, previewMaintenanceSeries } from "./maintenanceSeriesApi.ts";
import { parseSeriesDefinition, seriesCommandId, validSeriesDate, MaintenanceMutationLane, type MaintenanceSeriesDefinition,
  type MaintenanceSeriesStatus, type MaintenanceSeriesPreview, type MaintenanceSeriesCommand, type MaintenanceSeriesReceipt,
  type MaintenanceSeriesPage, type MaintenanceSeriesOccurrencePage } from "./maintenanceSeriesModel.ts";
import { validNotificationReason } from "./alertSnoozeModel.ts";
import "./maintenanceSeries.css";
interface Props {
  readonly instanceId: string; readonly allowed: boolean; readonly mutationLane: MaintenanceMutationLane; readonly mutationKey: string;
  readonly selectedSeriesId?: string | null; readonly onSelectSeries?: (id: string) => void; readonly onSelectWindow: (id: string) => void;
  readonly onCommitted: () => void; readonly onDenied: () => void;
}
interface Owner { readonly controller: AbortController; readonly kind: "preview" | "status" | "mutation"; readonly seriesId: string;
  readonly command?: MaintenanceSeriesCommand; readonly newIntent?: boolean; readonly readback?: boolean; }
interface View { readonly current?: MaintenanceSeriesStatus; readonly receivedAtUtc?: string; readonly preview?: { readonly key: string; readonly value: MaintenanceSeriesPreview };
  readonly receipt?: MaintenanceSeriesReceipt; readonly busy?: Owner["kind"]; readonly uncertain?: MaintenanceSeriesCommand; readonly conflict?: boolean; readonly definiteRefusal?: boolean;
  readonly message?: string; readonly retryRead?: { readonly id: string; readonly newIntent: boolean; readonly readback: boolean }; }
interface ListView<T> { readonly pages: readonly T[]; readonly loading?: boolean; readonly receivedAtUtc?: string;
  readonly error?: string; readonly attempt?: { readonly before?: string; readonly replace: boolean }; }
interface Draft { readonly id: string; readonly definition: MaintenanceSeriesDefinition; readonly editRevision?: number; readonly effective: string; }
const emptyDefinition = (): MaintenanceSeriesDefinition => ({ frequency: "weekly", interval: 1, startDate: "", endDate: "", localStartTime: "02:00",
  durationMinutes: 60, timeZoneId: "America/New_York", weekdays: [0], dayOfMonth: null, gapPolicy: "skip", foldPolicy: "earlier", reason: "" });
export function MaintenanceSeriesControls({ instanceId, allowed, mutationLane, mutationKey, selectedSeriesId, onSelectSeries, onSelectWindow, onCommitted, onDenied }: Props) {
  const time = useTimeFormatter(), mounted = useRef(false), authority = useRef(allowed), epoch = useRef(0);
  authority.current = allowed;
  const [localSelection, setLocalSelection] = useState<string>(), activeId = selectedSeriesId === undefined ? localSelection : selectedSeriesId;
  const selection = useRef(activeId); selection.current = activeId;
  const owner = useRef<Owner | undefined>(undefined), listOwner = useRef<AbortController | undefined>(undefined), occurrenceOwner = useRef<AbortController | undefined>(undefined);
  const [view, setViewState] = useState<View>({}), viewRef = useRef<View>({});
  const update = (change: (previous: View) => View) => { const next = change(viewRef.current); viewRef.current = next; setViewState(next); };
  const [list, setList] = useState<ListView<MaintenanceSeriesPage>>({ pages: [] });
  const [occurrences, setOccurrences] = useState<ListView<MaintenanceSeriesOccurrencePage>>({ pages: [] });
  const [draft, setDraft] = useState<Draft>(() => ({ id: crypto.randomUUID(), definition: emptyDefinition(), effective: "" }));
  const [cancelReason, setCancelReason] = useState(""), [cancelDate, setCancelDate] = useState("");
  const [cancellationProposal, setCancellationProposal] = useState<MaintenanceSeriesCommand>();
  const draftKey = JSON.stringify(draft), preview = view.preview?.key === draftKey ? view.preview.value : undefined;
  function stop() {
    epoch.current++; owner.current?.controller.abort(); if (owner.current) mutationLane.release(owner.current); owner.current = undefined;
    if (listOwner.current) {
      listOwner.current.abort(); listOwner.current = undefined;
      if (mounted.current) setList(previous => ({ ...previous, loading: false,
        error: "The series listing read was cancelled. Received pages remain visible; retry keeps the same anchor." }));
    }
    occurrenceOwner.current?.abort(); occurrenceOwner.current = undefined;
  }
  useLayoutEffect(() => { mounted.current = true; return () => { mounted.current = false; stop(); }; }, []);
  useLayoutEffect(() => {
    if (allowed) return;
    const request = owner.current; stop();
    update(previous => ({ uncertain: request?.command ?? previous.uncertain, conflict: previous.conflict }));
    setList({ pages: [] }); setOccurrences({ pages: [] });
  }, [allowed]);
  const previousSelection = useRef(activeId);
  useLayoutEffect(() => {
    if (previousSelection.current === activeId) return; previousSelection.current = activeId;
    const acceptedCreate = viewRef.current.current?.seriesId === activeId && viewRef.current.receipt?.seriesId === activeId;
    stop(); if (!acceptedCreate) { update(() => ({})); setDraft({ id: crypto.randomUUID(), definition: emptyDefinition(), effective: "" }); }
    setOccurrences({ pages: [] }); setCancelDate(""); setCancelReason(""); setCancellationProposal(undefined);
  }, [activeId]);
  useEffect(() => { if (allowed) void readList(undefined, true); }, [allowed]);
  useEffect(() => { if (allowed && activeId && viewRef.current.current?.seriesId !== activeId) void readStatus(activeId); }, [activeId, allowed]);
  const current = (request: Owner, capturedEpoch: number) => mounted.current && authority.current && epoch.current === capturedEpoch
    && owner.current === request && !request.controller.signal.aborted;
  function denied(failure: unknown): boolean {
    if (!(failure instanceof ApiRequestError) || ![401, 403].includes(failure.status)) return false;
    stop(); update(() => ({})); setList({ pages: [] }); setOccurrences({ pages: [] }); onDenied(); return true;
  }
  async function readStatus(id: string, readback = false, newIntent = false) {
    if (!mounted.current || !authority.current || owner.current) return;
    const request: Owner = { kind: "status", seriesId: id, controller: new AbortController(), readback, newIntent };
    const captured = epoch.current; owner.current = request;
    update(previous => ({ ...previous, busy: "status", message: undefined, retryRead: undefined }));
    try {
      const status = await getMaintenanceSeriesStatus(instanceId, id, request.controller.signal);
      if (!current(request, captured)) return;
      update(previous => ({ ...previous, current: status, receivedAtUtc: new Date().toISOString(), busy: undefined, retryRead: undefined,
        ...(newIntent && (!previous.uncertain || seriesCommandId(previous.uncertain) === id)
          ? { uncertain: undefined, conflict: false, definiteRefusal: false, preview: undefined } : {}),
        message: readback ? "Current series read back from the server. Occurrence and calendar receipts remain as received." : undefined }));
      if (newIntent) setCancellationProposal(undefined);
      if (readback && viewRef.current.receipt?.seriesId === id && selectedSeriesId !== id) {
        setLocalSelection(id); onSelectSeries?.(id);
      }
    } catch (failure) {
      if (!current(request, captured) || denied(failure)) return;
      update(previous => ({ ...previous, busy: undefined, retryRead: { id, readback, newIntent },
        message: readback ? "Series operation recorded, but current readback is unavailable. Retry status only; the committed command will not be sent again."
          : "Current series status is unavailable. Retry this read before a new intention." }));
    } finally { if (owner.current === request) owner.current = undefined; }
  }
  async function readList(before?: string, replace = false) {
    if (!mounted.current || !authority.current || listOwner.current || !replace && list.pages.length >= 20) return;
    const controller = new AbortController(), captured = epoch.current; listOwner.current = controller;
    setList(previous => ({ ...previous, loading: true, error: undefined, attempt: { before, replace } }));
    try {
      const page = await getMaintenanceSeries(instanceId, controller.signal, replace ? [] : list.pages, before);
      if (!mounted.current || !authority.current || epoch.current !== captured || controller.signal.aborted || listOwner.current !== controller) return;
      setList(previous => ({ pages: replace ? [page] : [...previous.pages, page], receivedAtUtc: new Date().toISOString() }));
      if (selectedSeriesId === undefined && !selection.current && page.items[0]) setLocalSelection(page.items[0].seriesId);
    } catch (failure) {
      if (!mounted.current || controller.signal.aborted || epoch.current !== captured || denied(failure)) return;
      setList(previous => ({ ...previous, loading: false, error: "The series listing is unavailable. Received pages remain visible; retry keeps the same anchor." }));
    } finally { if (listOwner.current === controller) listOwner.current = undefined; }
  }
  async function readOccurrences(before?: string, replace = false) {
    if (!mounted.current || !authority.current || !activeId || occurrenceOwner.current || !replace && occurrences.pages.length >= 41) return;
    const controller = new AbortController(), captured = epoch.current, id = activeId; occurrenceOwner.current = controller;
    setOccurrences(previous => ({ ...previous, loading: true, error: undefined, attempt: { before, replace } }));
    try {
      const page = await getMaintenanceSeriesOccurrences(instanceId, id, controller.signal, replace ? [] : occurrences.pages, before);
      if (!mounted.current || !authority.current || controller.signal.aborted || epoch.current !== captured || occurrenceOwner.current !== controller || selection.current !== id) return;
      setOccurrences(previous => ({ pages: replace ? [page] : [...previous.pages, page], receivedAtUtc: new Date().toISOString() }));
    } catch (failure) {
      if (!mounted.current || controller.signal.aborted || epoch.current !== captured || denied(failure)) return;
      setOccurrences(previous => ({ ...previous, loading: false, error: "Series occurrences are unavailable. Received pages remain visible; retry keeps the same window anchor." }));
    } finally { if (occurrenceOwner.current === controller) occurrenceOwner.current = undefined; }
  }
  function select(id: string) { if (owner.current) cancelRequest(); setLocalSelection(id); onSelectSeries?.(id); }
  async function previewDraft() {
    if (!mounted.current || !authority.current || owner.current) return;
    let definition: MaintenanceSeriesDefinition;
    try { definition = parseSeriesDefinition(draft.definition); if (draft.editRevision && !validSeriesDate(draft.effective)) throw new Error("Choose an effective civil date for future changes."); }
    catch (failure) { update(previous => ({ ...previous, message: failure instanceof Error ? failure.message : "Review the finite schedule." })); return; }
    const request: Owner = { kind: "preview", seriesId: draft.id, controller: new AbortController() }, captured = epoch.current, key = draftKey;
    owner.current = request; update(previous => ({ ...previous, busy: "preview", message: undefined }));
    try {
      const value = await previewMaintenanceSeries(instanceId, draft.id, definition, draft.editRevision ? draft.effective : null, request.controller.signal);
      if (!current(request, captured)) return;
      update(previous => ({ ...previous, busy: undefined, preview: { key, value }, message: "Server preview received. Preview counts are not a guarantee of the later applied result." }));
    } catch (failure) {
      if (!current(request, captured) || denied(failure)) return;
      update(previous => ({ ...previous, busy: undefined, message: failure instanceof ApiRequestError ? failure.message : "The server preview could not be verified. Review the schedule and try again." }));
    } finally { if (owner.current === request) owner.current = undefined; }
  }
  async function send(command: MaintenanceSeriesCommand) {
    if (!mounted.current || !authority.current || owner.current) return;
    const request: Owner = { kind: "mutation", seriesId: seriesCommandId(command), command, controller: new AbortController() }, captured = epoch.current;
    if (!mutationLane.claim(mutationKey, request)) return;
    owner.current = request; update(previous => ({ ...previous, busy: "mutation", uncertain: command, message: undefined, conflict: false, definiteRefusal: false }));
    try {
      const receipt = await changeMaintenanceSeries(instanceId, command, request.controller.signal);
      if (!current(request, captured)) return;
      mutationLane.release(request); owner.current = undefined;
      update(previous => ({ ...previous, busy: undefined, uncertain: undefined, receipt, preview: undefined,
        message: "Series operation recorded. Reading current status; immutable counts describe this operation only." }));
      setCancellationProposal(undefined);
      onCommitted(); await readStatus(receipt.seriesId, true);
    } catch (failure) {
      if (!current(request, captured) || denied(failure)) return;
      const conflict = failure instanceof ApiRequestError && [400, 409, 422].includes(failure.status);
      update(previous => ({ ...previous, busy: undefined, uncertain: command, conflict, definiteRefusal: failure instanceof ApiRequestError && [400, 422].includes(failure.status),
        message: conflict ? "The server refused this future change. Refresh current series status, review the definition and preview a new intention."
          : "Series completion is uncertain. Retry preserves the exact operation, definition, cutoff and cancellation reason." }));
    } finally { mutationLane.release(request); if (owner.current === request) owner.current = undefined; }
  }
  function cancelRequest() {
    const request = owner.current; if (!request) return; request.controller.abort(); mutationLane.release(request); owner.current = undefined;
    update(previous => ({ ...previous, busy: undefined, uncertain: request.command ?? previous.uncertain,
      retryRead: request.kind === "status" ? { id: request.seriesId, readback: Boolean(request.readback), newIntent: Boolean(request.newIntent) } : previous.retryRead,
      message: request.command ? "Series request cancelled; server completion is uncertain. Retry keeps the exact original command."
        : "Series read cancelled. Received evidence remains available; no change was applied by this read." }));
  }
  function patch(value: Partial<MaintenanceSeriesDefinition>) { setDraft(previous => ({ ...previous, definition: { ...previous.definition, ...value } })); }
  const statuses = list.pages.flatMap(page => page.items), seriesNext = list.pages.at(-1)?.nextBeforeSeriesId;
  const rows = occurrences.pages.flatMap(page => page.items), next = occurrences.pages.at(-1)?.nextBeforeWindowId;
  const status = view.current?.seriesId === activeId ? view.current : undefined;
  const busy = Boolean(view.busy), writeBlocked = busy || mutationLane.busy(mutationKey) || Boolean(view.uncertain) || Boolean(view.retryRead);
  const currentRevisionMatches = !draft.editRevision || status?.seriesId === draft.id && status.revision === draft.editRevision;
  const identityAvailable = Boolean(draft.editRevision) || view.current?.seriesId !== draft.id;
  const canApply = allowed && !writeBlocked && currentRevisionMatches && identityAvailable && preview
    && (Boolean(draft.editRevision) || preview.occurrences.length > 0);
  const refreshId = view.uncertain ? seriesCommandId(view.uncertain) : status?.seriesId;
  if (!allowed) return <section className="panel maintenance-series" aria-label="Recurring maintenance"><h3>Recurring maintenance</h3><p>A Target Administrator role on this server is required to read or change maintenance series.</p></section>;
  return <section className="panel maintenance-series" aria-label="Recurring maintenance">
    <h3>Recurring maintenance</h3><p>Finite daily, weekly and monthly schedules create real maintenance windows. Notifications are suppressed while collection and alert evaluation continue.</p>
    <p className="table-note">Series reads are current server configuration, independent of the diagnostic time range. Refresh is explicit; polling does not discard an uncertain future change.</p>
    <form aria-label="Maintenance series definition" onSubmit={event => { event.preventDefault(); void previewDraft(); }}>
      <div className="maintenance-series-fields">
        <label>Recurrence frequency<select value={draft.definition.frequency} disabled={busy} onChange={event => patch({ frequency: event.target.value as MaintenanceSeriesDefinition["frequency"], weekdays: event.target.value === "weekly" ? [0] : [], dayOfMonth: event.target.value === "monthly" ? 1 : null })}><option value="daily">Daily</option><option value="weekly">Weekly</option><option value="monthly">Monthly</option></select></label>
        <label>Recurrence interval<input type="number" min="1" max={draft.definition.frequency === "daily" ? 365 : draft.definition.frequency === "weekly" ? 52 : 12} value={draft.definition.interval} disabled={busy} onChange={event => patch({ interval: Number(event.target.value) })} /></label>
        <label>Series start date<input type="date" required value={draft.definition.startDate} disabled={busy} onChange={event => patch({ startDate: event.target.value })} /></label>
        <label>Series end date<input type="date" required value={draft.definition.endDate} disabled={busy} onChange={event => patch({ endDate: event.target.value })} /></label>
        <label>Local start time<input type="time" required value={draft.definition.localStartTime} disabled={busy} onChange={event => patch({ localStartTime: event.target.value })} /></label>
        <label>Elapsed duration (minutes)<input type="number" min="1" max="10080" required value={draft.definition.durationMinutes} disabled={busy} onChange={event => patch({ durationMinutes: Number(event.target.value) })} /></label>
        <label>IANA time zone<input required value={draft.definition.timeZoneId} maxLength={128} disabled={busy} onChange={event => patch({ timeZoneId: event.target.value })} /></label>
        <label>Repeated local time<select value={draft.definition.foldPolicy} disabled={busy} onChange={event => patch({ foldPolicy: event.target.value as "earlier" | "later" })}><option value="earlier">Earlier UTC occurrence</option><option value="later">Later UTC occurrence</option></select></label>
        {draft.definition.frequency === "monthly" && <label>Day of month<input type="number" min="1" max="31" required value={draft.definition.dayOfMonth ?? ""} disabled={busy} onChange={event => patch({ dayOfMonth: Number(event.target.value) })} /></label>}
        {draft.editRevision && <label>Effective from date<input type="date" required value={draft.effective} disabled={busy} onChange={event => setDraft({ ...draft, effective: event.target.value })} /></label>}
        <label className="maintenance-series-reason">Series reason<textarea required maxLength={512} value={draft.definition.reason} disabled={busy} onChange={event => patch({ reason: event.target.value })} /></label>
      </div>
      {draft.definition.frequency === "weekly" && <fieldset className="maintenance-series-weekdays"><legend>Weekdays</legend>{["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"].map((name, index) => <label key={name}><input type="checkbox" disabled={busy} checked={draft.definition.weekdays.includes(index)} onChange={event => patch({ weekdays: event.target.checked ? [...draft.definition.weekdays, index] : draft.definition.weekdays.filter(day => day !== index) })} />{name}</label>)}</fieldset>}
      <p className="table-note">Dates are inclusive civil dates in the named zone; an end date within five years is required. At most 2,048 occurrences. Weekly intervals start in the Sunday week containing the anchor. Missing monthly days and nonexistent daylight-saving starts are skipped. Duration is elapsed UTC time, so a local end may cross a clock change. The server resolves time-zone rules; the browser does not expand this schedule.</p>
      {!currentRevisionMatches && <p role="alert">This draft no longer matches the selected series revision. Open Edit future occurrences again to capture current status before previewing a new intention.</p>}
      {!identityAvailable && <p className="table-note">This series identity already exists. Use Edit future occurrences or open a New series draft to create another schedule.</p>}
      <div className="maintenance-calendar-controls"><button type="submit" disabled={busy}>Preview maintenance series</button>
        <button type="button" disabled={!canApply} onClick={() => {
          if (!canApply) return;
          const definition = parseSeriesDefinition(draft.definition), operationId = crypto.randomUUID();
          void send(draft.editRevision ? { kind: "edit", seriesId: draft.id, body: { operationId, expectedRevision: draft.editRevision, effectiveFromDate: draft.effective, definition } }
            : { kind: "create", body: { operationId, seriesId: draft.id, definition } });
        }}>{draft.editRevision ? "Save future changes" : "Create maintenance series"}</button>
        <button type="button" disabled={writeBlocked} onClick={() => setDraft({ id: crypto.randomUUID(), definition: emptyDefinition(), effective: "" })}>New series draft</button></div>
    </form>
    {preview && <section aria-label="Server maintenance preview"><h4>Server maintenance preview</h4><p>Server as of {time(preview.serverNowUtc)} · {preview.occurrences.length} proposed future occurrences · {preview.skippedDates.length} skipped dates. Re-preview after a time-zone or policy change; this read is not an applied receipt.</p>
      {draft.editRevision && preview.occurrences.length === 0 && <p>Saving this empty future preview can cancel obsolete ordinary future windows from the effective date. Started windows and individual exceptions remain. Only the applied receipt reports actual cancellations.</p>}
      <div className="table-scroll" tabIndex={0} role="region" aria-label="Proposed UTC maintenance bounds"><table><caption>Server-computed future UTC bounds</caption><thead><tr><th scope="col">Civil date</th><th scope="col">UTC start / end</th><th scope="col">Offset / repeated time</th></tr></thead><tbody>{preview.occurrences.map(item => <tr key={item.windowId}><td>{item.localDate}</td><td><time dateTime={item.startsAtUtc}>{item.startsAtUtc}</time><br /><time dateTime={item.endsAtUtc}>{item.endsAtUtc}</time></td><td>{item.utcOffsetMinutes} minutes · {item.ambiguous ? "Repeated time" : "Single occurrence"}</td></tr>)}</tbody></table></div>
      {preview.skippedDates.length > 0 && <ul>{preview.skippedDates.map((item, index) => <li key={index}>Requested {item.localDate}: {item.reason === "missing_month_day" ? "day does not exist in this month" : "local time does not exist during daylight-saving change"}; skipped.</li>)}</ul>}
    </section>}
    {view.busy && <p role="status">{view.busy === "mutation" ? "Recording series operation…" : view.busy === "preview" ? "Reading server preview…" : "Reading current series…"}</p>}
    {view.message && <p role="status">{view.message}</p>}
    <div className="maintenance-calendar-controls">
      {busy && <button type="button" onClick={cancelRequest}>Cancel series request</button>}
      {view.retryRead && <button type="button" disabled={busy} onClick={() => void readStatus(view.retryRead!.id, view.retryRead!.readback, view.retryRead!.newIntent)}>Retry series status</button>}
      {view.uncertain && !view.conflict && <button type="button" disabled={busy || mutationLane.busy(mutationKey)} onClick={() => void send(view.uncertain!)}>Retry series operation</button>}
      {view.definiteRefusal && view.uncertain?.kind === "create" && <button type="button" disabled={busy || mutationLane.busy(mutationKey)} onClick={() => update(previous => ({ ...previous, uncertain: undefined, conflict: false, definiteRefusal: false, preview: undefined, message: "Rejected creation abandoned explicitly. Review the draft and obtain a new preview before a new operation." }))}>Review rejected series draft</button>}
      {refreshId && <button type="button" disabled={busy} onClick={() => void readStatus(refreshId, false, true)}>Refresh series status</button>}
    </div>
    {view.receipt && <p className="maintenance-series-receipt">Immutable series receipt: {view.receipt.createdOccurrences} created, {view.receipt.updatedOccurrences} updated, {view.receipt.cancelledOccurrences} cancelled, {view.receipt.preservedExceptions} exceptions preserved. Applied {time(view.receipt.appliedAtUtc)} · operation {view.receipt.operationId}. These historical counts do not establish current window state.</p>}
    <section aria-label="Maintenance series listing"><h4>Maintenance series</h4><p className="table-note">{statuses.length} received series in {list.pages.length} page(s); at most 20 pages / 1,000 series. Mutable rows are as of {list.pages.at(-1)?.serverNowUtc ? time(list.pages.at(-1)!.serverNowUtc) : "not received"}.</p>
      {selectedSeriesId === null && <p role="alert">The series URL selection is invalid. No other series was selected.</p>}
      {activeId && !status && !busy && <p role="status">Selected series status has not been received. No other series was substituted.</p>}
      <div className="table-scroll" role="region" tabIndex={0} aria-label="Received maintenance series"><table><caption>Received series configuration</caption><thead><tr><th scope="col">Series</th><th scope="col">Schedule</th><th scope="col">Future cancellation fence</th></tr></thead><tbody>{statuses.map(item => <tr key={item.seriesId}><td><button type="button" className="table-link" aria-label={"Select series " + item.seriesId} aria-pressed={item.seriesId === activeId} onClick={() => select(item.seriesId)}>{item.definition.reason}</button></td><td>{item.definition.frequency} · {item.definition.timeZoneId}<small>{item.definition.startDate} to {item.definition.endDate}</small></td><td>{item.cancelledFromDate ? "Cancelled from " + item.cancelledFromDate : "No fence received"}</td></tr>)}</tbody></table></div>
      {list.error && <p role="alert">{list.error}<button type="button" disabled={list.loading} onClick={() => void readList(list.attempt?.before, list.attempt?.replace)}>Retry maintenance series list</button></p>}
      <div className="maintenance-calendar-controls"><button type="button" disabled={list.loading} onClick={() => void readList(undefined, true)}>Refresh maintenance series</button><button type="button" disabled={list.loading || !seriesNext || list.pages.length >= 20} onClick={() => void readList(seriesNext!)}>Load more maintenance series</button></div>
    </section>
    {status && <section aria-label="Current maintenance series"><h4>Current maintenance series</h4><p>{status.definition.reason} · revision {status.revision} · {status.definition.frequency} · {status.definition.timeZoneId}</p><p>{status.definition.startDate} to {status.definition.endDate}, starting {status.definition.localStartTime}; {status.definition.durationMinutes} elapsed minutes. {status.occurrenceCount} recorded occurrences.</p>
      <p className="maintenance-series-receipt">Server as of {time(status.serverNowUtc)}; received {time(view.receivedAtUtc!)}. Current configuration is separate from retained preview, listing and occurrence evidence.</p>
      {status.cancelledFromDate && <p>Cancelled from {status.cancelledFromDate}. This civil fence is irreversible. Future dates before the fence may still be edited; there is no reactivation action.</p>}
      <button type="button" disabled={writeBlocked} onClick={() => setDraft({ id: status.seriesId, definition: status.definition, editRevision: status.revision, effective: "" })}>Edit future occurrences</button>
      <div className="maintenance-series-fields"><label>Cancellation effective from date<input type="date" value={cancelDate} disabled={busy} onChange={event => setCancelDate(event.target.value)} /></label><label>Series cancellation reason<textarea maxLength={512} value={cancelReason} disabled={busy} onChange={event => setCancelReason(event.target.value)} /></label></div>
      <p className="table-note">Cancel only future, unstarted occurrences from this civil date. Past, started, individually edited and already-cancelled windows stay recorded. Removed or cancelled dates cannot be reintroduced.</p>
      <button type="button" disabled={writeBlocked || !validSeriesDate(cancelDate) || !validNotificationReason(cancelReason)} onClick={() => {
        if (writeBlocked || !validSeriesDate(cancelDate) || !validNotificationReason(cancelReason)) return;
        setCancellationProposal({ kind: "cancel", seriesId: status.seriesId, body: { operationId: crypto.randomUUID(), expectedRevision: status.revision, effectiveFromDate: cancelDate, reason: cancelReason } });
      }}>Cancel future occurrences</button>
      {cancellationProposal?.kind === "cancel" && <section aria-label="Future cancellation confirmation"><p>Cancel eligible future occurrences from {cancellationProposal.body.effectiveFromDate} using series revision {cancellationProposal.body.expectedRevision}. Reason: {cancellationProposal.body.reason}. Past, started and individual exceptions remain; this civil fence cannot be reactivated.</p><button type="button" disabled={writeBlocked} onClick={() => { if (!writeBlocked) void send(cancellationProposal); }}>Confirm future cancellation</button><button type="button" disabled={busy} onClick={() => setCancellationProposal(undefined)}>Close cancellation confirmation</button></section>}
    </section>}
    {activeId && <section aria-label="Series occurrences"><h4>Series occurrences</h4><p className="table-note">{rows.length} received occurrences in {occurrences.pages.length} page(s); at most 41 pages / 2,048 occurrences. Server as of {occurrences.pages.at(-1)?.serverNowUtc ? time(occurrences.pages.at(-1)!.serverNowUtc) : "not received"}. Individual edits and cancellation are exceptions retained by future series changes. Actual UTC bounds describe the current window; original schedule offset and source revision remain materialization provenance after an individual edit. Select an occurrence to use the existing window editor.</p>
      {rows.length > 0 && <div className="table-scroll" role="region" tabIndex={0} aria-label="Received series occurrences"><table><caption>Received generated windows and exceptions</caption><thead><tr><th scope="col">Civil date / state</th><th scope="col">UTC bounds</th><th scope="col">Reason / provenance</th></tr></thead><tbody>{rows.map(item => <tr key={item.windowId}><td><button className="table-link" type="button" aria-label={"Select occurrence " + item.windowId} onClick={() => onSelectWindow(item.windowId)}>{item.localDate}</button><small>{item.cancelledAtUtc ? "Cancelled" : "Configured"}{item.exception ? " · Individual exception" : ""}</small></td><td>{item.startsAtUtc}<br />{item.endsAtUtc}</td><td>{item.reason}<small>Series revision {item.seriesRevision} · window revision {item.revision} · {item.utcOffsetMinutes} minutes offset{item.ambiguous ? " · Repeated time" : ""}</small></td></tr>)}</tbody></table></div>}
      {occurrences.error && <p role="alert">{occurrences.error}<button type="button" disabled={occurrences.loading} onClick={() => void readOccurrences(occurrences.attempt?.before, occurrences.attempt?.replace)}>Retry series occurrences</button></p>}
      <div className="maintenance-calendar-controls"><button type="button" disabled={occurrences.loading || occurrences.pages.length > 0 && !next || occurrences.pages.length >= 41} onClick={() => void readOccurrences(next ?? undefined)}>{occurrences.pages.length ? "Load older series occurrences" : "Load series occurrences"}</button>
        {occurrences.pages.length > 0 && <button type="button" disabled={occurrences.loading} onClick={() => void readOccurrences(undefined, true)}>Refresh series occurrences</button>}
        {occurrences.loading && <button type="button" onClick={() => { occurrenceOwner.current?.abort(); occurrenceOwner.current = undefined; setOccurrences(previous => ({ ...previous, loading: false, error: "Occurrence read cancelled. Retry keeps the original window anchor." })); }}>Cancel occurrence read</button>}</div>
    </section>}
  </section>;
}
