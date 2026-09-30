import { useEffect, useRef, useState } from "react";
import { policyCommand, policyDraft, type BackupPolicyCommand, type BackupPolicyDraft, type CurrentBackupPolicy } from "./backupPolicy.ts";
import { BackupPolicyError, getCurrentBackupPolicy, saveBackupPolicy } from "./backupPolicyApi.ts";

export function BackupPolicyEditor({ targetId, targetRevision, fingerprint, recoveryModel, onSaved }: {
  readonly targetId: string; readonly targetRevision: number; readonly fingerprint: string;
  readonly recoveryModel: string; readonly onSaved: () => void;
}) {
  const [open, setOpen] = useState(false);
  return <div className="backup-policy-editor">
    <button type="button" aria-expanded={open} onClick={() => setOpen(value => !value)}>{open ? "Close policy editor" : "Configure backup policy"}</button>
    {open && <PolicyForm key={`${targetId}:${targetRevision}:${fingerprint}`} targetId={targetId} targetRevision={targetRevision}
      fingerprint={fingerprint} recoveryModel={recoveryModel} onSaved={onSaved} />}
  </div>;
}
function PolicyForm({ targetId, targetRevision, fingerprint, recoveryModel, onSaved }: {
  readonly targetId: string; readonly targetRevision: number; readonly fingerprint: string;
  readonly recoveryModel: string; readonly onSaved: () => void;
}) {
  const [current, setCurrent] = useState<CurrentBackupPolicy | null>();
  const [draft, setDraft] = useState<BackupPolicyDraft>(() => policyDraft(null));
  const [attempt, setAttempt] = useState<BackupPolicyCommand>();
  const [busy, setBusy] = useState(true), [message, setMessage] = useState<string>(), [retryable, setRetryable] = useState(false);
  const [reload, setReload] = useState(0);
  const controller = useRef<AbortController | undefined>(undefined);
  useEffect(() => {
    const active = new AbortController(); controller.current = active; setBusy(true); setMessage(undefined);
    void getCurrentBackupPolicy(targetId, targetRevision, fingerprint, active.signal).then(policy => {
      if (active.signal.aborted) return;
      setCurrent(policy); setDraft(policyDraft(policy)); setAttempt(undefined); setRetryable(false); setBusy(false);
    }).catch((error: unknown) => {
      if (!active.signal.aborted) { setCurrent(undefined); setBusy(false); setMessage(error instanceof BackupPolicyError ? error.message : "Current policy could not be loaded."); }
    });
    return () => { active.abort(); controller.current?.abort(); };
  }, [targetId, targetRevision, fingerprint, reload]);
  const update = (key: keyof BackupPolicyDraft, value: string) => { setDraft(previous => ({ ...previous, [key]: value })); setMessage(undefined); };
  const save = () => {
    if (busy || current === undefined) return;
    let command: BackupPolicyCommand;
    try { command = attempt ?? policyCommand(draft, targetRevision, current?.policyRevision ?? 0, crypto.randomUUID(), recoveryModel); }
    catch (error) { setMessage(error instanceof Error ? error.message : "Check the explicit policy settings."); return; }
    setAttempt(command); setBusy(true); setMessage(undefined); setRetryable(false);
    const active = new AbortController(); controller.current?.abort(); controller.current = active;
    void saveBackupPolicy(targetId, fingerprint, command, active.signal).then(policy => {
      if (active.signal.aborted) return;
      setCurrent(policy); setDraft(policyDraft(policy)); setAttempt(undefined); setBusy(false);
      setMessage(`Backup policy revision ${policy.policyRevision} saved. Assessments refresh at the current time context.`); onSaved();
    }).catch((error: unknown) => {
      if (active.signal.aborted) return;
      setBusy(false); setRetryable(error instanceof BackupPolicyError && error.retryable);
      setMessage(error instanceof BackupPolicyError ? error.message : "The save result is uncertain. Reload the current policy.");
    });
  };
  const locked = busy || current === undefined || attempt !== undefined;
  return <form aria-label={`Backup policy for ${fingerprint}`} onSubmit={event => { event.preventDefault(); save(); }}>
    <p>Editing the current policy for this database identity. New limits are not suggested.
      Enabling requires recent complete inventory. Changes are audited and apply to later assessments.</p>
    {current !== undefined && <p>{current === null ? "No current policy is configured." : `Current policy revision ${current.policyRevision}.`}</p>}
    {busy && <p role="status">{attempt ? "Saving backup policy…" : "Loading current backup policy…"}</p>}
    <fieldset disabled={locked}><legend>Explicit backup policy</legend>
      <label>Policy status<select required value={draft.enabled} onChange={e => update("enabled", e.target.value)}>
        <option value="">Choose status</option><option value="true">Enabled</option><option value="false">Disabled</option></select></label>
      <label>Full age limit (seconds)<input required type="number" min="1" max="2592000" step="1" value={draft.fullSeconds} onChange={e => update("fullSeconds", e.target.value)} /></label>
      <label>Log age limit (seconds)<input type="number" min="1" max="2592000" step="1" value={draft.logSeconds} onChange={e => update("logSeconds", e.target.value)} /></label>
      <p>Leave the log limit blank only when no log threshold is required. Enabled Full/Bulk-logged recovery requires one.</p>
      <label>Observation age limit (seconds)<input required type="number" min="1" max="3600" step="1" value={draft.observationSeconds} onChange={e => update("observationSeconds", e.target.value)} /></label>
      <label>Full backup requirement<select required value={draft.fullRequirement} onChange={e => update("fullRequirement", e.target.value)}>
        <option value="">Choose requirement</option><option value="full_image">Full image (copy-only allowed)</option><option value="differential_base">Differential base (copy-only excluded)</option></select></label>
      <label>Checksums<select required value={draft.checksums} onChange={e => update("checksums", e.target.value)}>
        <option value="">Choose checksum policy</option><option value="true">Required</option><option value="false">Not required</option></select></label>
    </fieldset>
    {message && <p role="status">{message}</p>}
    <div className="form-actions"><button type="submit" disabled={busy || current === undefined || !!attempt && !retryable}>
      {attempt ? "Retry same policy save" : "Save backup policy"}</button>
      <button type="button" disabled={busy} onClick={() => setReload(value => value + 1)}>Reload current policy</button></div>
    {attempt && !busy && <p>The submitted settings remain fixed. Reload the current policy before making another edit.</p>}
  </form>;
}
