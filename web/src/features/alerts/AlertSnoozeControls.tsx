import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { ApiRequestError } from "../../apiClient.ts";
import { useTimeFormatter } from "../../TimeDisplayContext";
import { changeAlertNotifications, getAlertNotificationState, type AlertNotificationReceipt } from "./alertSnoozeApi.ts";
import { notificationCommand, validNotificationReason, type AlertNotificationCommand,
  type AlertNotificationState, type NotificationAction } from "./alertSnoozeModel.ts";
import type { FleetAlert } from "./alertTypes.ts";
import type { AlertMutationLane } from "./alertMutationLane.ts";
import "./alertSnooze.css";

interface Props {
  readonly selected: FleetAlert; readonly allowed: boolean; readonly otherMutationBusy: boolean;
  readonly hold: () => void; readonly deny: () => void; readonly onBusyChange?: (busy: boolean) => void;
  readonly mutationLane?: AlertMutationLane; readonly mutationKey?: string;
}
interface Attempt { readonly action: NotificationAction; readonly command: AlertNotificationCommand; }
type ReadIntent = "status" | "readback";
interface Owner { readonly controller: AbortController; readonly kind: "mutation" | ReadIntent; readonly attempt?: Attempt; readonly newIntent?: boolean; }
interface View {
  readonly current?: AlertNotificationState; readonly receivedAtUtc?: string; readonly receipt?: AlertNotificationReceipt;
  readonly busy?: Owner["kind"]; readonly message?: string; readonly failedRead?: ReadIntent; readonly uncertain?: Attempt;
  readonly conflict?: boolean; readonly failedNewIntent?: boolean;
}

/** The parent keys this component to context, denial, target, alert and received cycle. */
export function AlertSnoozeControls({ selected, allowed, otherMutationBusy, hold, deny, onBusyChange, mutationLane, mutationKey = "" }: Props) {
  const time = useTimeFormatter(), [minutes, setMinutes] = useState(60), [reason, setReason] = useState("");
  const [view, setView] = useState<View>({});
  const mounted = useRef(false), authority = useRef(allowed), owner = useRef<Owner | undefined>(undefined);
  const busyCallback = useRef(onBusyChange); busyCallback.current = onBusyChange;
  authority.current = allowed;
  useLayoutEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; owner.current?.controller.abort(); if (owner.current) mutationLane?.release(owner.current); owner.current = undefined; };
  }, []);
  useLayoutEffect(() => {
    if (allowed || !owner.current) return;
    const request = owner.current; request.controller.abort(); mutationLane?.release(request); owner.current = undefined;
    setView(previous => ({ ...previous, busy: undefined, uncertain: request.attempt ?? previous.uncertain,
      failedRead: request.kind === "mutation" ? previous.failedRead : request.kind,
      failedNewIntent: request.newIntent,
      message: request.kind === "mutation" ? "Notification authority changed; server completion is uncertain. An authorized retry keeps the original operation and payload."
        : "Notification authority changed; the pending status read was stopped." }));
  }, [allowed]);
  useEffect(() => { if (allowed) void readStatus("status"); }, [allowed]);
  useLayoutEffect(() => {
    busyCallback.current?.(view.busy === "mutation");
    return () => { busyCallback.current?.(false); };
  }, [view.busy]);

  function isCurrent(request: Owner) { return mounted.current && authority.current && owner.current === request && !request.controller.signal.aborted; }
  function denied(failure: unknown): boolean {
    if (failure instanceof ApiRequestError && [401, 403].includes(failure.status)) { deny(); return true; }
    return false;
  }
  async function readStatus(intent: ReadIntent, newIntent = false) {
    if (!mounted.current || !authority.current || owner.current) return;
    const request: Owner = { controller: new AbortController(), kind: intent, newIntent }; owner.current = request;
    setView(previous => ({ ...previous, busy: intent, failedRead: undefined, message: undefined }));
    try {
      const current = await getAlertNotificationState(selected.targetId, selected.alertId, request.controller.signal);
      if (!isCurrent(request)) return;
      setView(previous => ({ ...previous, current, receivedAtUtc: new Date().toISOString(), busy: undefined, failedRead: undefined, failedNewIntent: undefined,
        ...(newIntent ? { uncertain: undefined, conflict: false } : {}),
        message: intent === "readback" ? "Current notification status read back from the server. Retained inbox rows remain as received." : undefined }));
    } catch (failure) {
      if (!isCurrent(request) || denied(failure)) return;
      setView(previous => ({ ...previous, busy: undefined, failedRead: intent, failedNewIntent: newIntent,
        message: intent === "readback" ? "The operation was recorded, but current notification status could not be read back. Retry status only; the committed operation will not be sent again."
          : "Current notification status is unavailable. Retry status before changing notifications." }));
    } finally { if (owner.current === request) owner.current = undefined; }
  }
  async function send(attempt: Attempt) {
    if (!mounted.current || !authority.current || owner.current || otherMutationBusy) return;
    const request: Owner = { controller: new AbortController(), kind: "mutation", attempt };
    if (mutationLane && !mutationLane.claim(mutationKey, request, "snooze")) return;
    owner.current = request;
    hold(); setView(previous => ({ ...previous, busy: "mutation", message: undefined, uncertain: attempt, conflict: false }));
    try {
      const receipt = await changeAlertNotifications(selected.targetId, selected.alertId, attempt.action, attempt.command, request.controller.signal);
      if (!isCurrent(request)) return;
      mutationLane?.release(request);
      owner.current = undefined;
      setView(previous => ({ ...previous, receipt, busy: undefined, uncertain: undefined,
        message: "Notification operation recorded. Reading current server status; the receipt does not prove the snooze is still active." }));
      await readStatus("readback");
    } catch (failure) {
      if (!isCurrent(request) || denied(failure)) return;
      const conflict = failure instanceof ApiRequestError && failure.status === 409;
      setView(previous => ({ ...previous, busy: undefined, uncertain: attempt, conflict,
        message: conflict ? "The alert episode or revision changed. Refresh notification status before starting a new operation."
          : "Notification completion is uncertain. Retry uses the same operation, episode, revision, expiry and reason; it never extends the original snooze." }));
    } finally { mutationLane?.release(request); if (owner.current === request) owner.current = undefined; }
  }
  function cancel() {
    const request = owner.current; if (!request) return;
    request.controller.abort(); mutationLane?.release(request); owner.current = undefined;
    setView(previous => ({ ...previous, busy: undefined, uncertain: request.attempt ?? previous.uncertain,
      failedRead: request.kind === "mutation" ? previous.failedRead : request.kind,
      failedNewIntent: request.newIntent,
      message: request.kind === "mutation" ? "Notification request cancelled; server completion is uncertain. Retry keeps the exact original operation and payload."
        : "Notification status read cancelled. Retry status repeats only this read." }));
  }
  const current = view.current;
  const changedEpisode = current !== undefined && selected.episodeId !== undefined && current.episodeId.toLowerCase() !== selected.episodeId.toLowerCase();
  const active = current?.state === "firing" || current?.state === "acknowledged";
  const ready = allowed && current !== undefined && !changedEpisode && active && !view.busy && !otherMutationBusy
    && !view.failedRead && !view.uncertain && validNotificationReason(reason);
  function begin(action: NotificationAction) {
    if (!ready || !current) return;
    void send({ action, command: notificationCommand(current, action, minutes, reason) });
  }
  return <section className="alert-notification-controls" aria-label="Alert notifications">
    <h3>Notifications</h3>
    <p className="alert-triage-note">Snooze notifications temporarily. Monitoring continues, and firing or acknowledged alerts stay in the inbox.</p>
    {selected.snoozedUntilUtc && <p className="alert-triage-receipt">Received inbox snooze expiry: <time dateTime={selected.snoozedUntilUtc}>{time(selected.snoozedUntilUtc)}</time>.
      {selected.snoozeReason ? ` Reason: ${selected.snoozeReason}.` : ""} This is retained row evidence.</p>}
    {!allowed ? <p className="detail-note">An Operator or Target Administrator role on this server is required to snooze or unsnooze notifications.</p> : <>
      {current && <div className="alert-notification-status">
        <p>{current.snoozed ? "Notifications snoozed at server read" : "No active notification snooze at server read"}. Current alert state: {current.state}.
          {current.deliverySuppressed && !current.snoozed ? " Delivery is suppressed for another server-owned reason." : ""}</p>
        {current.snoozedUntilUtc && <p>Snooze expiry: <time dateTime={current.snoozedUntilUtc}>{time(current.snoozedUntilUtc)}</time> ({current.snoozed ? "active at server read" : "not active at server read"}).</p>}
        {current.snoozeReason && <p>Snooze reason: {current.snoozeReason}</p>}
        {current.snoozedBy && <p>Snoozed by: {current.snoozedBy}</p>}
        <p className="alert-triage-receipt">Server as of <time dateTime={current.serverNowUtc}>{time(current.serverNowUtc)}</time>; received {time(view.receivedAtUtc!)}.
          This current read is separate from the retained inbox cutoff. Refresh status after the recorded expiry to verify current suppression. The browser does not renew or clear server snoozes.</p>
      </div>}
      {changedEpisode && <p role="alert" className="status-message">The current alert episode differs from this received row. Return the inbox to latest before changing notifications.</p>}
      {current && !active && <p className="detail-note">Only firing or acknowledged alerts can be snoozed. Return to latest to review the current alert state.</p>}
      <div className="alert-triage-controls">
        <label>Snooze duration<select value={minutes} disabled={Boolean(view.busy)} onChange={event => setMinutes(Number(event.target.value))}>
          <option value={15}>15 minutes</option><option value={60}>1 hour</option><option value={480}>8 hours</option><option value={1440}>24 hours</option>
        </select></label>
        <label>Notification reason<input value={reason} maxLength={512} disabled={Boolean(view.busy)} onChange={event => setReason(event.target.value)} /></label>
        <button type="button" disabled={!ready} onClick={() => begin("snooze")}>Snooze notifications</button>
        {current?.snoozed && <button type="button" disabled={!ready} onClick={() => begin("unsnooze")}>Unsnooze notifications</button>}
      </div>
      <p className="alert-triage-note">A reason is required (maximum 512 UTF-8 bytes, without control characters). Expiry is calculated from the server read; retry preserves it exactly.</p>
      {view.busy && <p role="status">{view.busy === "mutation" ? "Recording notification operation…" : "Reading current notification status…"}</p>}
      {view.message && <p role="status" className="status-message">{view.message}</p>}
      <div className="alert-triage-actions">
        {view.busy && <button type="button" onClick={cancel}>Cancel notification request</button>}
        {view.failedRead && <button type="button" disabled={Boolean(view.busy)} onClick={() => void readStatus(view.failedRead!, view.failedNewIntent)}>Retry notification status</button>}
        {view.uncertain && !view.conflict && <button type="button" disabled={Boolean(view.busy) || otherMutationBusy} onClick={() => void send(view.uncertain!)}>Retry notification operation</button>}
        {current && <button type="button" disabled={Boolean(view.busy)} onClick={() => void readStatus("status", true)}>Refresh notification status</button>}
      </div>
      {view.receipt && <p className="alert-triage-receipt">Immutable operation receipt: {view.receipt.operationId}; audit {view.receipt.auditId}; applied {time(view.receipt.appliedAtUtc)}.
        A replayed receipt does not establish current notification suppression.</p>}
    </>}
  </section>;
}
