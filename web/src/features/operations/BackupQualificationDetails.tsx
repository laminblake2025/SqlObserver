import type { BackupQualification } from "./backupQualification.ts";

export function BackupQualificationDetails({ current }: { readonly current: BackupQualification }) {
  return <div className="backup-qualification-details">
    <p>{current.enabled ? "Active historical time qualification" : current.qualificationRevision === 0 ? "No historical time qualification is configured." : "Historical time qualification is revoked."}
      {current.qualificationRevision > 0 && ` · command revision ${current.qualificationRevision}`}</p>
    {current.enabled && <dl><dt>Qualified SQL source zone ID</dt><dd>{current.sourceZoneId}</dd>
      <dt>Qualified interval (UTC, end exclusive)</dt><dd><code>{current.fromUtc}</code> to <code>{current.toUtc}</code></dd>
      <dt>Historical configuration evidence SHA-256</dt><dd><code>{current.evidenceSha256}</code></dd>
      <dt>Stored historical rules SHA-256</dt><dd><code>{current.rulesSha256}</code></dd></dl>}
    <p>Qualification records an administrator's verified historical configuration. It does not prove backup restoreability,
      current freshness or all-replica coverage. Revocation preserves recorded evidence and withholds current age until new qualified evidence is collected.</p>
  </div>;
}
