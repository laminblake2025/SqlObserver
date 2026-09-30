import { backupDuration, backupLabel, type BackupPage, type BackupCheck, type BackupDatabase } from "./backupAssessment.ts";
import type { ReactNode } from "react";

const labels = { within_threshold: "Within limit", policy_violated: "Policy violation", indeterminate: "Unknown", not_required: "Not required", disabled: "Disabled" };
const tones = { within_threshold: "current", policy_violated: "critical", indeterminate: "unavailable", not_required: "neutral", disabled: "neutral" };

export function BackupAssessmentView({ page, formatTime, renderPolicy }: {
  readonly page: BackupPage; readonly formatTime: (value: string | null | undefined) => string;
  readonly renderPolicy?: (database: BackupDatabase) => ReactNode;
}) {
  return <>
    <p className="table-note">Assessed {formatTime(page.atUtc)} · source observed {formatTime(page.sourceObservedAtUtc)} ·
      {page.collectorEnabled ? " collection enabled" : " collection disabled"} · {backupLabel(page.coverage)} coverage</p>
    {page.action && <p className="evidence-callout">{page.action}</p>}
    {page.evidenceStatus !== "retained" && <p role="status">{backupLabel(page.evidenceStatus)} · no current database verdicts are available.</p>}
    {page.items.length > 0 && <div className="table-scroll"><table className="evidence-table">
      <caption>Backup freshness per observed database identity</caption>
      <thead><tr><th scope="col">Database identity</th><th scope="col">Full backup</th><th scope="col">Log backup</th><th scope="col">Evidence and policy</th></tr></thead>
      <tbody>{page.items.map(database => <tr key={database.databaseFingerprint ?? `unknown:${database.databaseOrdinal}`}>
        <th scope="row">{database.databaseFingerprint ? <code title={database.databaseFingerprint}>{database.databaseFingerprint.slice(0, 8)}…{database.databaseFingerprint.slice(-4)}</code> : "Identity unknown"}
          <small>Inventory row {database.databaseOrdinal} · {backupLabel(database.recoveryModel)} recovery</small>
          {database.scope === "availability_group_local_only" && <small>AG: local history only · preference {database.localReplicaPreferred === null ? "unknown" : database.localReplicaPreferred ? "local replica preferred" : "another replica preferred"}</small>}</th>
        <td><CheckView check={database.full} /></td><td><CheckView check={database.log} /></td>
        <td><DatabaseEvidence database={database} formatTime={formatTime} policyEditor={renderPolicy?.(database)} /></td>
      </tr>)}</tbody>
    </table></div>}
    <p className="table-note">Within limit compares reported backup age with an explicit policy. Verify restore chains and media separately.
      Database labels are opaque identities. Backup names and device paths are not collected here.</p>
  </>;
}

function CheckView({ check }: { readonly check: BackupCheck }) {
  return <div className={`backup-check evidence-${tones[check.status]}`}>
    <strong><span className="status-mark" aria-hidden="true" />{labels[check.status]}</strong>
    <span>{backupLabel(check.reason)}</span>
    <small>Age: {backupDuration(check.observedAgeSeconds)} · limit: {check.maximumAgeSeconds === null ? "Not configured / not required" : backupDuration(check.maximumAgeSeconds)}</small>
    {check.action && <p>{check.action}</p>}
  </div>;
}
function DatabaseEvidence({ database: d, formatTime, policyEditor }: {
  readonly database: BackupDatabase; readonly formatTime: (value: string | null | undefined) => string;
  readonly policyEditor?: ReactNode;
}) {
  return <>
    {d.observedIssues.length > 0 && <ul aria-label={`Observed backup issues for inventory row ${d.databaseOrdinal}`}>
      {d.observedIssues.map((issue, index) => <li key={index}>{backupLabel(issue.kind)}{issue.copyOnly ? " copy-only" : ""}: {backupLabel(issue.reason)}</li>)}
    </ul>}
    <details><summary>Evidence for inventory row {d.databaseOrdinal}</summary>
      <dl><dt>Database fingerprint</dt><dd><code>{d.databaseFingerprint ?? "Unknown"}</code></dd>
        <dt>Database state</dt><dd>{d.catalogDatabaseState ?? "Unknown"} · {backupLabel(d.coverage)} coverage</dd>
        <dt>Policy</dt><dd>{d.policy ? `Revision ${d.policyRevision} · ${d.policy.enabled ? "enabled" : "disabled"} · ${backupLabel(d.policy.fullRequirement)}` : "Not configured"}</dd>
        {d.policy && <><dt>Observation age limit</dt><dd>{backupDuration(d.policy.maximumObservationAgeSeconds)}</dd>
          <dt>Require checksums</dt><dd>{d.policy.requireChecksums ? "Yes" : "No"}</dd></>}
      </dl>
      {d.samples.length === 0 ? <p>No eligible backup record was observed in bounded history.</p> : d.samples.map(s => <dl key={`${s.kind}:${s.copyOnly}`}>
        <dt>{backupLabel(s.kind)}{s.copyOnly ? " · copy-only" : ""}</dt>
        <dd>Recorded finish: {s.recordedFinishUtc ? formatTime(s.recordedFinishUtc) : "Unresolved"}</dd>
        <dt>Timestamp evidence</dt><dd>{backupLabel(s.timeState)} · qualification {s.qualificationRevision ?? "none"} ·
          {s.qualificationTrusted ? " active at this cutoff" : " unavailable at this cutoff"}</dd>
        <dt>Raw start / finish (SQL Server local)</dt><dd>{s.sourceLocalStart?.replace("T", " ") ?? "Unknown"} / {s.sourceLocalFinish?.replace("T", " ") ?? "Unknown"}</dd>
        <dt>Recorded start offset</dt><dd>{s.recordedStartOffsetMinutes === null ? "Unknown" : `${s.recordedStartOffsetMinutes} minutes`}</dd>
        <dt>Size (bytes)</dt><dd>{s.sizeBytes ?? "Unknown"}</dd>
        <dt>Checksum / damage</dt><dd>{s.hasChecksum === null ? "Unknown" : s.hasChecksum ? "Present" : "Absent"} / {s.isDamaged === null ? "Unknown" : s.isDamaged ? "Damaged" : "Not marked damaged"}</dd>
      </dl>)}
      <p>Raw local clocks retain their recorded values. An inactive qualification cannot establish age from a recorded UTC finish.</p>
      {policyEditor}
    </details>
  </>;
}
