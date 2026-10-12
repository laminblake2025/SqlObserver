import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { ApiRequestError } from "../../apiClient.ts";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { changeAlertAssignment, getAlertAssignment, getAlertAssignmentHistory } from "./alertAssignmentApi.ts";
import { assignmentCommand, type AlertAssignmentCommand, type AlertAssignmentHistory, type AlertAssignmentReceipt,
  type AlertAssignmentStatus } from "./alertAssignmentModel.ts";
import { validNotificationReason } from "./alertSnoozeModel.ts";
import type { FleetAlert } from "./alertTypes.ts";
import type { AlertMutationLane } from "./alertMutationLane.ts";
import "./alertAssignment.css";
interface Props {
  readonly selected: FleetAlert; readonly allowed: boolean; readonly hold: () => void; readonly pinMutation: () => void; readonly deny: () => void;
  readonly mutationLane: AlertMutationLane; readonly mutationKey: string;
}
interface Owner { readonly controller: AbortController; readonly command?: AlertAssignmentCommand; readonly readback?: boolean; readonly newIntent?: boolean; }
interface View {
  readonly current?: AlertAssignmentStatus; readonly receivedAtUtc?: string; readonly receipt?: AlertAssignmentReceipt;
  readonly busy?: boolean; readonly message?: string; readonly uncertain?: AlertAssignmentCommand; readonly conflict?: boolean;
  readonly failedRead?: { readonly readback: boolean; readonly newIntent: boolean };
}
interface HistoryView { readonly pages: readonly AlertAssignmentHistory[]; readonly receivedAtUtc?: string;
  readonly loading?: boolean; readonly error?: string; readonly attempt?: { readonly before?: string; readonly replace: boolean }; }
export function AlertAssignmentControls({ selected, allowed, hold, pinMutation, deny, mutationLane, mutationKey }: Props) {
  const time = useTimeFormatter(), [chosen, setChosen] = useState(""), [reason, setReason] = useState("");
  const [view, setView] = useState<View>({}), [history, setHistory] = useState<HistoryView>({ pages: [] });
  const mounted = useRef(false), authority = useRef(allowed), owner = useRef<Owner | undefined>(undefined), historyOwner = useRef<AbortController | undefined>(undefined);
  authority.current = allowed;
  useLayoutEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; owner.current?.controller.abort(); if (owner.current) mutationLane.release(owner.current);
      owner.current = undefined; historyOwner.current?.abort(); historyOwner.current = undefined; };
  }, []);
  useLayoutEffect(() => {
    const request = owner.current; if (!request) return;
    request.controller.abort(); mutationLane.release(request); owner.current = undefined;
    setView(previous => ({ ...previous, busy: false, uncertain: request.command ?? previous.uncertain,
      failedRead: request.command ? previous.failedRead : { readback: Boolean(request.readback), newIntent: Boolean(request.newIntent) },
      message: "Assignment authority changed; pending work stopped. Any mutation completion remains uncertain." }));
  }, [allowed]);
  useEffect(() => { void readStatus(); }, [allowed]);
  const current = (request: Owner) => mounted.current && owner.current === request && !request.controller.signal.aborted
    && (!request.command || authority.current);
  function definitiveDenial(failure: unknown): boolean {
    if (!(failure instanceof ApiRequestError) || ![401, 403].includes(failure.status)) return false;
    owner.current?.controller.abort(); if (owner.current) mutationLane.release(owner.current); owner.current = undefined;
    historyOwner.current?.abort(); historyOwner.current = undefined;
    setView({}); setHistory({ pages: [] }); deny(); return true;
  }
  async function readStatus(readback = false, newIntent = false) {
    if (!mounted.current || owner.current) return;
    const request: Owner = { controller: new AbortController(), readback, newIntent }; owner.current = request;
    setView(previous => ({ ...previous, busy: true, message: undefined, failedRead: undefined }));
    try {
      const value = await getAlertAssignment(selected.targetId, selected.alertId, request.controller.signal);
      if (!current(request)) return;
      setView(previous => ({ ...previous, current: value, receivedAtUtc: new Date().toISOString(), busy: false, failedRead: undefined,
        ...(newIntent ? { uncertain: undefined, conflict: false } : {}),
        message: readback ? "Current assignment read back from the server. Received inbox rows remain unchanged." : undefined }));
    } catch (failure) {
      if (!current(request) || definitiveDenial(failure)) return;
      setView(previous => ({ ...previous, busy: false, failedRead: { readback, newIntent },
        message: readback ? "Assignment operation recorded, but current readback is unavailable. Retry status only; the committed operation will not be sent again."
          : "Current assignment status is unavailable. Retry status before starting a new assignment." }));
    } finally { if (owner.current === request) owner.current = undefined; }
  }
  async function send(command: AlertAssignmentCommand) {
    if (!mounted.current || !authority.current || owner.current) return;
    const request: Owner = { controller: new AbortController(), command };
    if (!mutationLane.claim(mutationKey, request, "assignment")) return;
    owner.current = request; pinMutation(); setView(previous => ({ ...previous, busy: true, uncertain: command, message: undefined, conflict: false }));
    try {
      const receipt = await changeAlertAssignment(selected.targetId, selected.alertId, command, request.controller.signal);
      if (!current(request)) return;
      owner.current = undefined; mutationLane.release(request);
      setView(previous => ({ ...previous, busy: false, receipt, uncertain: undefined,
        message: "Assignment operation recorded. Reading current server state; the immutable receipt does not prove the current owner." }));
      await readStatus(true);
    } catch (failure) {
      if (!current(request) || definitiveDenial(failure)) return;
      const rejected = failure instanceof ApiRequestError && failure.status === 422;
      const conflict = failure instanceof ApiRequestError && [409, 422].includes(failure.status);
      setView(previous => ({ ...previous, busy: false, uncertain: command, conflict,
        message: rejected ? "This assignment was not applied because the responder is no longer approved. Refresh assignment status to obtain the current roster before a new operation."
          : conflict ? "Assignment episode, revision or approved responder changed. Refresh assignment status before starting a new operation."
          : "Assignment completion is uncertain. Retry keeps the same operation, assignee, episode, revision and reason." }));
    } finally { mutationLane.release(request); if (owner.current === request) owner.current = undefined; }
  }
  function cancel() {
    const request = owner.current; if (!request) return;
    request.controller.abort(); mutationLane.release(request); owner.current = undefined;
    setView(previous => ({ ...previous, busy: false, uncertain: request.command ?? previous.uncertain,
      failedRead: request.command ? previous.failedRead : { readback: Boolean(request.readback), newIntent: Boolean(request.newIntent) },
      message: request.command ? "Assignment request cancelled; server completion is uncertain. Retry preserves the exact original payload."
        : "Assignment status read cancelled. Retry repeats only this read." }));
  }
  async function readHistory(before?: string, replace = false) {
    if (!mounted.current || historyOwner.current) return;
    if (!replace && history.pages.length >= 20) { setHistory(previous => ({ ...previous, error: "Retained assignment history limit reached: 20 pages / 1,000 changes." })); return; }
    const controller = new AbortController(); historyOwner.current = controller; hold();
    setHistory(previous => ({ ...previous, loading: true, error: undefined, attempt: { before, replace } }));
    try {
      const page = await getAlertAssignmentHistory(selected.targetId, selected.alertId, controller.signal, replace ? [] : history.pages, before);
      if (!mounted.current || controller.signal.aborted || historyOwner.current !== controller) return;
      setHistory(previous => ({ pages: replace ? [page] : [...previous.pages, page], receivedAtUtc: new Date().toISOString(), loading: false }));
    } catch (failure) {
      if (!mounted.current || controller.signal.aborted || historyOwner.current !== controller || definitiveDenial(failure)) return;
      setHistory(previous => ({ ...previous, loading: false, error: "Assignment history is unavailable. Retry preserves this exact operation anchor." }));
    } finally { if (historyOwner.current === controller) historyOwner.current = undefined; }
  }
  function cancelHistory() { historyOwner.current?.abort(); historyOwner.current = undefined;
    setHistory(previous => ({ ...previous, loading: false, error: "Assignment history read cancelled. Received changes remain visible; retry keeps its anchor." })); }
  const status = view.current, active = status?.state === "firing" || status?.state === "acknowledged";
  const changedEpisode = status && selected.episodeId && status.episodeId.toLowerCase() !== selected.episodeId.toLowerCase();
  const permitted = allowed && status?.canAssign;
  const ready = permitted && active && !changedEpisode && !view.busy && !view.failedRead && !view.uncertain
    && !mutationLane.busy(mutationKey) && validNotificationReason(reason);
  const approved = status?.eligibleAssignees.some(candidate => candidate.sid === chosen);
  const changes = history.pages.flatMap(page => page.items), next = history.pages.at(-1)?.nextBeforeOperationId;
  return <section className="alert-assignment-controls" aria-label="Alert assignment">
    <h3>Assignment</h3>
    <p className="alert-triage-note">Assign an approved responder to investigate. Assignment does not grant server access, resolve alerts or alter notification delivery.</p>
    {selected.assigneeName && <p className="alert-triage-receipt">Received row: assigned to {selected.assigneeName}. This is retained inbox evidence.</p>}
    {status && <div><p>{status.assigneeName ? `Assigned to ${status.assigneeName} at server read` : "Unassigned at server read"}. Current alert state: {status.state}.</p>
      {status.assigneeSid && <p>Assigned responder identifier: {status.assigneeSid}</p>}
      {status.assignmentReason && <p>Assignment reason: {status.assignmentReason}</p>}
      {status.assignedBy && <p>Assigned by: {status.assignedBy}</p>}
      {status.assignedAtUtc && <p>Assigned at: <time dateTime={status.assignedAtUtc}>{time(status.assignedAtUtc)}</time></p>}
      <p className="alert-triage-receipt">Assignment server as of <time dateTime={status.serverNowUtc}>{time(status.serverNowUtc)}</time>; received {time(view.receivedAtUtc!)}.
        Current assignment is separate from retained inbox evidence and the shared time window.</p></div>}
    {!allowed || status && !status.canAssign ? <p className="detail-note">An Operator or Target Administrator role on this server is required to assign, reassign or unassign.</p> : <>
      {status && !status.eligibleAssignees.length && <p className="detail-note">No approved responders are configured for this server. Ask the application administrator to configure approved responders for this server.</p>}
      <p className="alert-triage-note">Responder names are administrator-approved roster labels, not a live directory lookup or a statement of current directory membership.</p>
      {changedEpisode && <p role="alert">The current assignment episode differs from this received row. Return the inbox to latest before starting a new operation.</p>}
      {status && !active && <p className="detail-note">Only firing or acknowledged alerts can change assignment. Return the inbox to latest to review the current alert state.</p>}
      <div className="alert-triage-controls"><label>Approved assignee<select value={chosen} disabled={Boolean(view.busy)} onChange={event => setChosen(event.target.value)}>
        <option value="">Choose an approved responder</option>{status?.eligibleAssignees.map(candidate => <option key={candidate.sid} value={candidate.sid}>{candidate.displayName}{status.eligibleAssignees.filter(item => item.displayName === candidate.displayName).length > 1 ? ` · ${candidate.sid}` : ""}</option>)}
      </select></label><label>Assignment reason<input value={reason} maxLength={512} disabled={Boolean(view.busy)} onChange={event => setReason(event.target.value)} /></label>
      <button type="button" disabled={!ready || !approved || chosen === status?.assigneeSid} onClick={() => { if (ready && status && approved) void send(assignmentCommand(status, chosen, reason)); }}>{status?.assigneeSid ? "Reassign alert" : "Assign alert"}</button>
      {status?.assigneeSid && <button type="button" disabled={!ready} onClick={() => { if (ready) void send(assignmentCommand(status, null, reason)); }}>Unassign alert</button>}
      </div><p className="alert-triage-note">A safe reason of at most 512 UTF-8 bytes is required for every change, including unassignment.</p>
    </>}
    {view.busy && <p role="status">{owner.current?.command ? "Recording assignment operation…" : "Reading current assignment status…"}</p>}
    {view.message && <p role="status" className="status-message">{view.message}</p>}
    <div className="alert-triage-actions">{view.busy && <button type="button" onClick={cancel}>Cancel assignment request</button>}
      {view.failedRead && <button type="button" disabled={Boolean(view.busy)} onClick={() => void readStatus(view.failedRead!.readback, view.failedRead!.newIntent)}>Retry assignment status</button>}
      {allowed && view.uncertain && !view.conflict && <button type="button" disabled={Boolean(view.busy) || mutationLane.busy(mutationKey)} onClick={() => void send(view.uncertain!)}>Retry assignment operation</button>}
      {status && <button type="button" disabled={Boolean(view.busy)} onClick={() => void readStatus(false, true)}>Refresh assignment status</button>}
    </div>
    {view.receipt && <p className="alert-triage-receipt">Immutable assignment receipt: operation {view.receipt.operationId}; audit {view.receipt.auditId}; applied {time(view.receipt.appliedAtUtc)}.
      Replayed receipts cannot establish the current owner.</p>}
    <section aria-label="Assignment history"><h4>Assignment history</h4><p className="alert-triage-note">Immutable changes across all alert episodes, newest recorded sequence first. At most 20 pages / 1,000 retained changes; explicit reads preserve received history.</p>
      {history.pages.length > 0 && <><p className="alert-triage-receipt">{changes.length} assignment changes received in {history.pages.length} page(s). Last received {time(history.receivedAtUtc!)}.</p>
        <div className="table-scroll" role="region" tabIndex={0} aria-label="Assignment change observations"><table><caption>Received assignment changes</caption>
          <thead><tr><th scope="col">Applied</th><th scope="col">Change</th><th scope="col">Reason / actor</th></tr></thead><tbody>{changes.map(change => <tr key={change.operationId}>
            <td><time dateTime={change.appliedAtUtc}>{time(change.appliedAtUtc)}</time></td><td>{change.previousAssigneeName ?? "Unassigned"} → {change.assigneeName ?? "Unassigned"}<small>{change.previousAssigneeSid ?? "No prior owner"} → {change.assigneeSid ?? "No owner"}</small><small>Episode {change.episodeId} · revision {change.revision}</small></td>
            <td>{change.reason}<small>{change.actorSid}</small></td></tr>)}</tbody></table></div></>}
      {history.error && <p role="alert">{history.error} {history.attempt && <button type="button" disabled={history.loading} onClick={() => void readHistory(history.attempt!.before, history.attempt!.replace)}>Retry assignment history</button>}</p>}
      <div className="alert-triage-actions"><button type="button" disabled={Boolean(history.loading) || history.pages.length >= 20 || history.pages.length > 0 && !next}
        onClick={() => void readHistory(next)}>{history.pages.length ? "Load older assignment changes" : "Load assignment history"}</button>
        {history.pages.length > 0 && <button type="button" disabled={Boolean(history.loading)} onClick={() => void readHistory(undefined, true)}>Refresh assignment history</button>}
        {history.loading && <button type="button" onClick={cancelHistory}>Cancel assignment history read</button>}</div>
    </section>
  </section>;
}
