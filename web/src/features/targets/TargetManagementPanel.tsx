import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import type { OverviewScope } from "../overview/overviewTypes";
import { canManageAlertRules, type MyAccess } from "./meApi";
import {
  getObservationTarget, requestObservationTargetRediscovery, retireObservationTarget,
  TargetManagementError, updateObservationTarget,
} from "./targetApi";
import type { ObservationTargetSummary, UpdateObservationTargetRequest } from "./targetTypes";

interface Props {
  readonly instanceId: string;
  readonly scope: OverviewScope;
  readonly access: MyAccess | undefined;
  readonly onChanged: (target: ObservationTargetSummary) => void;
  readonly onClose: () => void;
}
interface Draft {
  readonly displayName: string;
  readonly host: string;
  readonly addressMode: "tcp" | "named";
  readonly port: string;
  readonly namedInstance: string;
  readonly certificateHostName: string;
}
type Action = "reload" | "save" | "rediscover" | "retire";
interface Request {
  readonly controller: AbortController;
  readonly epoch: number;
  readonly action: Action;
}
interface Message { readonly epoch: number; readonly text: string; }
const hostPattern = "[A-Za-z0-9][A-Za-z0-9._:\\-]*";
const instancePattern = "[A-Za-z0-9][A-Za-z0-9_$\\-]*";

export function TargetManagementPanel(props: Props) {
  return <ServerSettings key={props.instanceId.toLowerCase()} {...props} instanceId={props.instanceId.toLowerCase()} />;
}

function ServerSettings({ instanceId, scope, access, onChanged, onClose }: Props) {
  const headingId = useId();
  const formatTime = useTimeFormatter();
  const latestAccess = useRef(access);
  latestAccess.current = access;
  const rejectedAccess = useRef<{ readonly snapshot: MyAccess | undefined } | undefined>(undefined);
  const [, refreshAccessFence] = useState(0);
  const canManage = canManageAlertRules(access, instanceId)
    && (rejectedAccess.current === undefined || access !== rejectedAccess.current.snapshot);
  const authority = useRef({ canManage, epoch: 0 });
  if (authority.current.canManage !== canManage) {
    authority.current = { canManage, epoch: authority.current.epoch + (authority.current.canManage ? 1 : 0) };
  }
  const alive = useRef(true);
  const active = useRef<Request | undefined>(undefined);
  const changed = useRef(onChanged);
  changed.current = onChanged;
  const [target, setTarget] = useState<ObservationTargetSummary>();
  const [draft, setDraft] = useState<Draft>();
  const [confirmation, setConfirmation] = useState("");
  const [pending, setPending] = useState<Action>();
  const [reloadRequired, setReloadRequired] = useState(true);
  const locked = useRef(true);
  const [error, setError] = useState<Message>();
  const [notice, setNotice] = useState<Message>();
  const retired = target?.lifecycle === "retired";
  const disabled = !canManage || pending !== undefined || reloadRequired || retired || target === undefined;
  const current = (request: Request) => alive.current && active.current === request
    && !request.controller.signal.aborted && request.epoch === authority.current.epoch;

  useEffect(() => {
    alive.current = true;
    return () => { alive.current = false; active.current?.controller.abort(); active.current = undefined; };
  }, []);

  useEffect(() => {
    const request = active.current;
    if (request === undefined || request.epoch === authority.current.epoch) return;
    request.controller.abort(); active.current = undefined; setPending(undefined);
    if (request.action !== "reload") {
      locked.current = true; setReloadRequired(true);
    }
  });

  useEffect(() => { void reload(); }, [instanceId]);

  async function reload() {
    if (active.current !== undefined) return;
    const mayUnlock = rejectedAccess.current === undefined || (canManageAlertRules(latestAccess.current, instanceId)
      && latestAccess.current !== rejectedAccess.current.snapshot);
    const request: Request = { controller: new AbortController(), epoch: authority.current.epoch, action: "reload" };
    active.current = request; locked.current = true; setReloadRequired(true);
    setPending("reload"); setError(undefined); setNotice(undefined);
    try {
      const received = await getObservationTarget(instanceId, request.controller.signal);
      if (!current(request)) return;
      setTarget(received); setDraft(draftFromTarget(received)); setConfirmation("");
      if (mayUnlock) rejectedAccess.current = undefined;
      locked.current = !mayUnlock; setReloadRequired(!mayUnlock);
      setNotice({ epoch: request.epoch, text: "Latest server settings loaded." });
    } catch {
      if (current(request)) setError({ epoch: request.epoch, text: "Server settings are unavailable. Reload server settings to retry." });
    } finally {
      if (current(request)) { active.current = undefined; setPending(undefined); }
    }
  }

  function edit(field: keyof Draft, value: string) {
    setDraft(previous => previous === undefined ? previous : { ...previous, [field]: value });
    setError(undefined); setNotice(undefined);
  }

  async function save(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (disabled || locked.current || active.current !== undefined || target === undefined || draft === undefined) return;
    let body: UpdateObservationTargetRequest;
    try { body = updateBody(draft, target.configurationRevision); }
    catch { setError({ epoch: authority.current.epoch, text: "Use a display name of at most 512 UTF-8 bytes, valid host and instance names, and a whole TCP port from 1 to 65535." }); return; }
    await command("save", signal => updateObservationTarget(instanceId, body, signal));
  }

  async function command(action: Exclude<Action, "reload">, send: (signal: AbortSignal) => Promise<ObservationTargetSummary>) {
    if (!authority.current.canManage || disabled || locked.current || active.current !== undefined || target === undefined) return;
    if (action === "retire" && confirmation !== target.displayName) return;
    const request: Request = { controller: new AbortController(), epoch: authority.current.epoch, action };
    active.current = request; setPending(action); setError(undefined); setNotice(undefined);
    try {
      const received = await send(request.controller.signal);
      if (!current(request) || !authority.current.canManage) return;
      setTarget(received); setConfirmation("");
      if (action === "save" || received.lifecycle === "retired") setDraft(draftFromTarget(received));
      const text = received.lifecycle === "retired"
        ? action === "retire" ? "Server retired. Collection is stopped." : "Server is retired. No settings or discovery changes were applied."
        : action === "save" ? "Server settings saved; capability discovery is pending." : "Capability rediscovery requested.";
      setNotice({ epoch: request.epoch, text });
      changed.current(received);
    } catch (failure: unknown) {
      if (!current(request)) return;
      const definiteRetry = failure instanceof TargetManagementError && !failure.outcomeUnknown
        && (failure.status === 429 || failure.status === 400);
      if (!definiteRetry) { locked.current = true; setReloadRequired(true); }
      const accessDenied = failure instanceof TargetManagementError && (failure.status === 401 || failure.status === 403);
      if (accessDenied) {
        rejectedAccess.current = { snapshot: latestAccess.current };
        refreshAccessFence(value => value + 1);
      }
      const message = failure instanceof TargetManagementError ? failure.message : "The server change could not be confirmed.";
      setError({ epoch: request.epoch + (accessDenied ? 1 : 0), text: definiteRetry ? message : `${message} Reload server settings before another change.` });
    } finally {
      if (current(request)) { active.current = undefined; setPending(undefined); }
    }
  }

  return <section className="target-management-panel" aria-labelledby={headingId}>
    <div className="target-management-heading">
      <div><p className="eyebrow">Server administration</p><h2 id={headingId}>Server settings</h2></div>
      <button type="button" className="secondary-button" onClick={onClose}>Close</button>
    </div>
    <p>Latest server configuration is independent of the selected {scope.range === "custom" ? "custom " : ""}history window. Reload replaces unsaved settings with the authoritative configuration.</p>
    <div className="target-management-actions">
      <button type="button" className="secondary-button" disabled={pending !== undefined} onClick={() => void reload()}>
        {pending === "reload" ? "Loading server settings…" : "Reload server settings"}
      </button>
    </div>
    {error?.epoch === authority.current.epoch ? <p role="alert" className="status-message">{error.text}</p> : null}
    {notice?.epoch === authority.current.epoch ? <p role="status" className="status-message">{notice.text}</p> : null}
    {reloadRequired && target !== undefined ? <p className="detail-note">Reload server settings before making another change.</p> : null}
    {rejectedAccess.current !== undefined ? <p role="status">Administrative access was rejected. Wait for refreshed access information, then reload server settings before another change.</p> : null}
    {!canManage ? <p role="status">Target Administrator access on this server unlocks settings changes, rediscovery and retirement.</p> : null}
    {target !== undefined && draft !== undefined ? <>
      <dl className="target-management-summary">
        <div><dt>Server ID</dt><dd>{target.instanceId}</dd></div>
        <div><dt>Stable key</dt><dd>{target.instanceKey}</dd></div>
        <div><dt>Configuration revision</dt><dd>{target.configurationRevision}</dd></div>
        <div><dt>Lifecycle</dt><dd>{target.lifecycle.replaceAll("_", " ")}</dd></div>
        <div><dt>Capability status</dt><dd>{target.capabilityStatus.replaceAll("_", " ")}</dd></div>
        <div><dt>Last discovery</dt><dd>{target.lastDiscoveryAtUtc === undefined ? "Not observed" : formatTime(target.lastDiscoveryAtUtc)}</dd></div>
      </dl>
      <p>Server ID and stable key are fixed. Connections use the Collector Windows service identity with required TLS encryption. No SQL login credentials are stored.</p>
      {target.encryptionMode === "mandatory_trust_server_certificate" && <p role="status">Trust server certificate is enabled for this target by an administrator. Traffic is encrypted; certificate identity validation is skipped.</p>}
      {retired ? <p role="status">This server is retired; settings changes and rediscovery are disabled.</p> : null}
      <form aria-label="Server settings" className="target-form target-management-form" onSubmit={event => void save(event)}>
        <fieldset disabled={disabled}>
          <legend>Connection settings</legend>
          <label>Display name<input name="displayName" maxLength={512} required value={draft.displayName} onChange={event => edit("displayName", event.target.value)} /></label>
          <label>Windows host<input name="host" maxLength={255} pattern={hostPattern} required value={draft.host} onChange={event => edit("host", event.target.value)} /></label>
          <fieldset><legend>Address</legend>
            <label className="choice"><input type="radio" name="addressMode" value="tcp" checked={draft.addressMode === "tcp"} onChange={() => edit("addressMode", "tcp")} />TCP port</label>
            <label className="choice"><input type="radio" name="addressMode" value="named" checked={draft.addressMode === "named"} onChange={() => edit("addressMode", "named")} />Named instance</label>
          </fieldset>
          {draft.addressMode === "tcp"
            ? <label>Port<input name="tcpPort" type="number" min="1" max="65535" step="1" required value={draft.port} onChange={event => edit("port", event.target.value)} /></label>
            : <label>Instance name<input name="namedInstance" maxLength={128} pattern={instancePattern} required value={draft.namedInstance} onChange={event => edit("namedInstance", event.target.value)} /></label>}
          <label>Certificate host name (optional)<input name="certificateHostName" maxLength={255} pattern={hostPattern} value={draft.certificateHostName} onChange={event => edit("certificateHostName", event.target.value)} /></label>
          {canManage ? <button type="submit" disabled={disabled}>{pending === "save" ? "Saving settings…" : "Save settings"}</button> : null}
        </fieldset>
      </form>
      {canManage ? <>
        <div className="target-management-actions"><button type="button" disabled={disabled}
          onClick={() => void command("rediscover", signal => requestObservationTargetRediscovery(instanceId, target.configurationRevision, signal))}>
          {pending === "rediscover" ? "Requesting rediscovery…" : "Request rediscovery"}
        </button></div>
        <fieldset className="target-management-retirement" disabled={disabled}>
          <legend>Retire server</legend>
          <p>Retirement stops collection. No restore action is available here. Type the current server display name exactly: <strong>{target.displayName}</strong>.</p>
          <label>Type server display name to retire<input name="retirementConfirmation" maxLength={512} autoComplete="off" value={confirmation} onChange={event => setConfirmation(event.target.value)} /></label>
          <button type="button" disabled={disabled || confirmation !== target.displayName}
            onClick={() => void command("retire", signal => retireObservationTarget(instanceId, target.configurationRevision, signal))}>
            {pending === "retire" ? "Retiring server…" : "Retire server"}
          </button>
        </fieldset>
      </> : null}
    </> : null}
  </section>;
}

function draftFromTarget(target: ObservationTargetSummary): Draft {
  return {
    displayName: target.displayName, host: target.host,
    addressMode: target.namedInstance === undefined ? "tcp" : "named",
    port: String(target.tcpPort ?? 1433), namedInstance: target.namedInstance ?? "",
    certificateHostName: target.certificateHostName ?? "",
  };
}

function updateBody(draft: Draft, expectedRevision: number): UpdateObservationTargetRequest {
  const host = new RegExp(`^(?:${hostPattern})$`, "u");
  const named = new RegExp(`^(?:${instancePattern})$`, "u");
  if (!draft.displayName.trim() || new TextEncoder().encode(draft.displayName).length > 512
    || /[\u0000-\u001f\u007f]/u.test(draft.displayName)
    || draft.host.length > 255 || !host.test(draft.host)
    || draft.certificateHostName !== "" && (draft.certificateHostName.length > 255 || !host.test(draft.certificateHostName))) throw new Error("Invalid server settings.");
  const base = { expectedRevision, displayName: draft.displayName, host: draft.host,
    ...(draft.certificateHostName === "" ? {} : { certificateHostName: draft.certificateHostName }) };
  if (draft.addressMode === "named") {
    if (draft.namedInstance.length > 128 || !named.test(draft.namedInstance)) throw new Error("Invalid instance name.");
    return { ...base, namedInstance: draft.namedInstance };
  }
  const port = Number(draft.port);
  if (!/^\d+$/u.test(draft.port) || !Number.isInteger(port) || port < 1 || port > 65535) throw new Error("Invalid TCP port.");
  return { ...base, tcpPort: port };
}
