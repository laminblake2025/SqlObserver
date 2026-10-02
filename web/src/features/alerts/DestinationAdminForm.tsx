import { useEffect, useRef, useState, type FormEvent } from "react";
import type { DestinationAdminItem } from "./alertAdminListsApi";
import { changeDestination, createDestination, DestinationAttempts, prepareDestination,
  type DestinationAction, type DestinationValues } from "./destinationAdminActions";

const attempts = new DestinationAttempts();
const eventLogReference = "SqlObserver Alerts";

export function DestinationAdminForm({ instanceId, destination, onSaved, onCancel }: {
  readonly instanceId: string; readonly destination?: DestinationAdminItem;
  readonly onSaved: (destinationId: string, action: "create" | DestinationAction) => void;
  readonly onCancel?: () => void;
}) {
  const [values, setValues] = useState<DestinationValues>({
    kind: destination?.kind ?? "https-webhook",
    configurationReference: destination?.kind === "windows-event-log" ? eventLogReference : "",
    enabled: destination?.enabled ?? false,
  });
  const [busy, setBusy] = useState<string>();
  const [error, setError] = useState<string>();
  const active = useRef<AbortController | null>(null);
  const mounted = useRef(true);
  useEffect(() => {
    mounted.current = true;
    return () => { mounted.current = false; active.current?.abort(); active.current = null; };
  }, []);
  const current = (controller: AbortController) => mounted.current && active.current === controller && !controller.signal.aborted;

  async function create() {
    if (destination || active.current) return;
    const controller = new AbortController(); active.current = controller;
    setError(undefined);
    try {
      const prepared = prepareDestination(values);
      const attempt = attempts.createFor(instanceId, prepared);
      setBusy("create");
      await createDestination(instanceId, prepared, attempt, controller.signal);
      if (!current(controller)) return;
      attempts.completeCreate(instanceId, prepared);
      setValues({ ...values, configurationReference: "", enabled: false });
      onSaved(attempt.destinationId, "create");
    } catch (failure) {
      if (current(controller)) setError(failure instanceof Error ? failure.message : "The destination could not be configured.");
    } finally { if (active.current === controller) { active.current = null; if (mounted.current) setBusy(undefined); } }
  }

  async function change(action: DestinationAction) {
    if (!destination || active.current) return;
    const controller = new AbortController(); active.current = controller;
    setError(undefined);
    try {
      const prepared = prepareDestination({ ...values, enabled: action === "disable" ? false : values.enabled });
      if (action === "approve" && !prepared.enabled)
        throw new Error("Enable the destination before approving it.");
      const operationId = attempts.changeFor(instanceId, destination.destinationId, destination.revision, action, prepared);
      setBusy(action);
      await changeDestination(instanceId, destination, action, prepared, operationId, controller.signal);
      if (!current(controller)) return;
      attempts.completeChange(instanceId, destination.destinationId, destination.revision, action, prepared);
      setValues({ ...values, configurationReference: "" });
      onSaved(destination.destinationId, action);
    } catch (failure) {
      if (current(controller)) setError(failure instanceof Error ? failure.message : "The destination change could not be saved.");
    } finally { if (active.current === controller) { active.current = null; if (mounted.current) setBusy(undefined); } }
  }

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    void (destination ? change("update") : create());
  }

  return <form className="panel maintenance-form" aria-label={destination ? "Manage destination" : "Configure a destination"} onSubmit={submit}>
    <div><h3>{destination ? "Manage destination" : "Configure a destination"}</h3><p>{destination
      ? "Enter the intended pre-provisioned reference again. Saving, approving, or disabling also writes this reference to the destination. Approval checks the server configuration before delivery can begin."
      : "Enter a pre-provisioned reference, not a URL or secret. A new destination needs approval before it can deliver alerts."}</p></div>
    {error ? <p role="alert">{error}</p> : null}
    <div className="maintenance-fields">
      <label>Kind{destination
        ? <input value={destination.kind === "https-webhook" ? "HTTPS webhook" : "Windows Event Log"} disabled />
        : <select value={values.kind} disabled={Boolean(busy)} onChange={event => {
          const kind = event.target.value as DestinationValues["kind"];
          setValues({ ...values, kind, configurationReference: kind === "windows-event-log" ? eventLogReference : "" });
        }}><option value="https-webhook">HTTPS webhook</option><option value="windows-event-log">Windows Event Log</option></select>}</label>
      <label>Configuration reference<input required maxLength={256} autoComplete="off" value={values.configurationReference} disabled={Boolean(busy)} onChange={event => setValues({ ...values, configurationReference: event.target.value })} /></label>
      <label>Enabled<input type="checkbox" checked={values.enabled} disabled={Boolean(busy)} onChange={event => setValues({ ...values, enabled: event.target.checked })} /></label>
    </div>
    <div className="form-actions">{destination ? <>
      <button className="primary" type="submit" disabled={Boolean(busy)}>{busy === "update" ? "Saving…" : "Save changes"}</button>
      <button className="secondary-button" type="button" disabled={Boolean(busy) || !values.enabled} onClick={() => void change("approve")}>{busy === "approve" ? "Approving…" : "Approve configuration"}</button>
      {destination.enabled ? <button className="secondary-button" type="button" disabled={Boolean(busy)} onClick={() => void change("disable")}>{busy === "disable" ? "Disabling…" : "Disable destination"}</button> : null}
      <button className="secondary-button" type="button" disabled={Boolean(busy)} onClick={onCancel}>Cancel</button>
    </> : <button className="primary" type="submit" disabled={Boolean(busy)}>{busy === "create" ? "Configuring…" : "Configure destination"}</button>}</div>
  </form>;
}
