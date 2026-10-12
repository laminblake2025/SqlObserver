import { useEffect, useRef, useState } from "react";
import { editQualificationDraft, emptyQualificationDraft, qualificationCommand, type BackupQualification, type QualificationCommand, type QualificationDraft } from "./backupQualification.ts";
import { BackupQualificationError, getBackupQualification, setBackupQualification } from "./backupQualificationApi.ts";
import { BackupQualificationDetails } from "./BackupQualificationDetails";

export function BackupQualificationPanel({ targetId, targetRevision, onChanged }: {
  readonly targetId: string; readonly targetRevision: number; readonly onChanged: () => void;
}) {
  const [open, setOpen] = useState(false);
  return <section className="backup-qualification" aria-label="Historical backup time rules">
    <button type="button" aria-expanded={open} onClick={() => setOpen(value => !value)}>{open ? "Close historical time rules" : "Manage historical time rules"}</button>
    {open && <QualificationForm key={`${targetId}:${targetRevision}`} targetId={targetId} targetRevision={targetRevision} onChanged={onChanged} />}
  </section>;
}
function QualificationForm({ targetId, targetRevision, onChanged }: {
  readonly targetId: string; readonly targetRevision: number; readonly onChanged: () => void;
}) {
  const [current, setCurrent] = useState<BackupQualification>();
  const [draft, setDraft] = useState<QualificationDraft>(emptyQualificationDraft);
  const [attempt, setAttempt] = useState<QualificationCommand>();
  const [busy, setBusy] = useState(true), [reload, setReload] = useState(0);
  const [error, setError] = useState<string>(), [notice, setNotice] = useState<string>(), [retryable, setRetryable] = useState(false);
  const controller = useRef<AbortController | undefined>(undefined);
  useEffect(() => {
    const active = new AbortController(); controller.current = active; setBusy(true); setError(undefined);
    void getBackupQualification(targetId, targetRevision, active.signal).then(value => {
      if (active.signal.aborted) return;
      setCurrent(value); setDraft(emptyQualificationDraft()); setAttempt(undefined); setRetryable(false); setBusy(false);
    }).catch((failure: unknown) => {
      if (!active.signal.aborted) { setCurrent(undefined); setBusy(false); setError(failure instanceof BackupQualificationError ? failure.message : "Current qualification could not be loaded."); }
    });
    return () => { active.abort(); controller.current?.abort(); };
  }, [targetId, targetRevision, reload]);
  const update = <K extends keyof QualificationDraft>(field: K, value: QualificationDraft[K]) => {
    setDraft(previous => editQualificationDraft(previous, field, value));
    setError(undefined); setNotice(undefined);
  };
  const submit = () => {
    if (busy || !current) return;
    let command: QualificationCommand;
    try { command = attempt ?? qualificationCommand(draft, current, crypto.randomUUID()); }
    catch (failure) { setError(failure instanceof Error ? failure.message : "Check the qualification fields."); return; }
    setAttempt(command); setBusy(true); setError(undefined); setNotice(undefined); setRetryable(false);
    const active = new AbortController(); controller.current?.abort(); controller.current = active;
    void setBackupQualification(targetId, command, active.signal).then(revision => {
      if (active.signal.aborted) return;
      setNotice(`Historical time ${command.enabled ? "qualification" : "revocation"} recorded at command revision ${revision}. ${command.enabled
        ? "Collect new backup evidence to use the active rules." : "Current ages remain unknown until rules are qualified again and new evidence is collected."}`);
      setReload(value => value + 1); onChanged();
    }).catch((failure: unknown) => {
      if (active.signal.aborted) return;
      setBusy(false); setRetryable(failure instanceof BackupQualificationError && failure.retryable);
      setError(failure instanceof BackupQualificationError ? failure.message : "The result is uncertain. Reload the current qualification.");
    });
  };
  const locked = busy || !current || attempt !== undefined;
  return <div className="backup-qualification-editor">
    {current && <BackupQualificationDetails current={current} />}
    {busy && <p role="status">{attempt ? "Recording historical time command…" : "Loading current historical time qualification…"}</p>}
    <form aria-label="Historical backup time qualification" onSubmit={event => { event.preventDefault(); submit(); }}>
      <fieldset disabled={locked}><legend>Explicit historical time command</legend>
        <label>Historical time action<select required value={draft.action} onChange={e => update("action", e.target.value)}>
          <option value="">Choose action</option><option value="qualify">Qualify verified historical rules</option>
          <option value="revoke" disabled={!current?.enabled}>Revoke active qualification</option></select></label>
        {draft.action === "qualify" && <>
          <p>Use a retained .NET TimeZoneInfo serialized rules artifact and a SHA-256 digest of your historical configuration evidence.
            A current zone ID or the browser's time zone cannot establish historical rules.</p>
          <label>Verified SQL source zone ID<input required maxLength={128} value={draft.sourceZoneId} onChange={e => update("sourceZoneId", e.target.value)} /></label>
          <label>Qualified from (UTC)<input required placeholder="YYYY-MM-DDTHH:mm:ss.ffffffZ" value={draft.fromUtc} onChange={e => update("fromUtc", e.target.value)} /></label>
          <label>Qualified to (UTC, exclusive)<input required placeholder="YYYY-MM-DDTHH:mm:ss.ffffffZ" value={draft.toUtc} onChange={e => update("toUtc", e.target.value)} /></label>
          <label>Verified .NET serialized zone rules<textarea required maxLength={65536} value={draft.serializedZoneRules} onChange={e => update("serializedZoneRules", e.target.value)} /></label>
          <label>Historical configuration evidence SHA-256<input required maxLength={64} value={draft.evidenceSha256} onChange={e => update("evidenceSha256", e.target.value)} /></label>
          <label className="qualification-confirm"><input type="checkbox" checked={draft.confirmed} onChange={e => update("confirmed", e.target.checked)} />
            I verified the historical SQL source configuration and time-zone rules for the entire UTC interval.</label>
        </>}
        {draft.action === "revoke" && <label className="qualification-confirm"><input type="checkbox" checked={draft.revokeConfirmed} onChange={e => update("revokeConfirmed", e.target.checked)} />
          I confirm revocation. Recorded evidence remains, and current ages may become unknown.</label>}
      </fieldset>
      {error && <p role="alert">{error}</p>}{notice && <p role="status">{notice}</p>}
      <div className="form-actions"><button type="submit" disabled={busy || !current || !!attempt && !retryable}>
        {attempt ? "Retry same time command" : "Submit historical time command"}</button>
        <button type="button" disabled={busy} onClick={() => { setNotice(undefined); setReload(value => value + 1); }}>Reload current qualification</button></div>
      {attempt && !busy && <p>Submitted fields remain fixed. Reload current qualification before changing the command.</p>}
    </form>
  </div>;
}
