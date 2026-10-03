import { useMemo } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import type { OverviewScope } from "../overview/overviewTypes";
import { assertBackupPagePosition, assertBackupSnapshot, backupCutoff, type BackupPage } from "./backupAssessment.ts";
import { BackupAssessmentError, getBackupAssessment } from "./backupAssessmentApi.ts";
import { BackupAssessmentView } from "./BackupAssessmentView";
import { BackupPolicyEditor } from "./BackupPolicyEditor";
import { BackupQualificationPanel } from "./BackupQualificationPanel";
import { useRetainedOperationsPage } from "./useRetainedOperationsPage";

export function BackupAssessmentPanel({ instanceId, scope, refresh, canManagePolicies = false }: {
  readonly instanceId: string; readonly scope: OverviewScope; readonly refresh: number; readonly canManagePolicies?: boolean;
}) {
  const formatTime = useTimeFormatter();
  const selection = useMemo(() => {
    try { return { atUtc: backupCutoff(scope, Date.now()) }; }
    catch { return { error: "Choose a valid time range for backup assessment." }; }
  }, [scope.range, scope.from, scope.to, refresh]);
  const context = JSON.stringify([instanceId.toLowerCase(), scope.range, scope.from, scope.to]);
  const result = useRetainedOperationsPage<string, BackupPage>({ context, input: selection.atUtc,
    inputKey: selection.atUtc ?? "invalid", refresh,
    read: (cutoff, signal, cursor) => getBackupAssessment(instanceId, cutoff, signal, cursor),
    validate: (first, next, index) => { assertBackupPagePosition(next, index); assertBackupSnapshot(first, next); },
    discardOnError: error => error instanceof BackupAssessmentError && !error.retryable && !error.restart });
  const error = result.error instanceof Error ? result.error.message : result.error ? "Backup assessment is unavailable." : undefined;
  const page = result.page;
  const editable = scope.range !== "custom" && canManagePolicies && !result.held && !result.loading && !error && result.index === 0;
  return <section aria-label="Backup freshness assessments" className="backup-assessment">
    <h3>Backup freshness</h3><p>Full and log backup ages against explicit per-database limits, at the declared cutoff.
      The range's start does not filter snapshot records. Investigated pages keep their cutoff until you return to latest.</p>
    {selection.error ? <p role="alert">{selection.error}</p> : <article className="panel" aria-label="Backup assessment snapshot" aria-busy={result.loading}>
      {result.held && <p className="table-note">Retained investigation page. Workspace polling does not replace this assessment; current policy controls are hidden.</p>}
      {page && <p className="activity-evidence">Run: {page.runId ?? "None"} · Target revision: {page.targetRevision}. Collection and policy settings describe this received assessment.</p>}
      {result.loading && <p role="status">{page ? "Reading backup assessments; the received assessment remains visible…" : "Loading backup assessments…"}</p>}
      {error && <p role="alert">{error} <button type="button" disabled={result.loading} onClick={result.retry}>Retry backup assessment</button></p>}
      {page && editable && <BackupQualificationPanel key={`${instanceId}:${page.targetRevision}`} targetId={instanceId}
        targetRevision={page.targetRevision} onChanged={result.returnLatest} />}
      {page && <BackupAssessmentView page={page} formatTime={formatTime} renderPolicy={editable ? database =>
        database.databaseFingerprint ? <BackupPolicyEditor key={`${instanceId}:${page.targetRevision}:${database.databaseFingerprint}`}
          targetId={instanceId} targetRevision={page.targetRevision} fingerprint={database.databaseFingerprint}
          recoveryModel={database.recoveryModel} onSaved={result.returnLatest} /> : null : undefined} />}
      <nav className="pager" aria-label="Backup assessment pages">
        <button type="button" disabled={result.loading || result.index === 0} onClick={result.first}>First backup page</button>{" "}
        <button type="button" disabled={result.loading || result.index === 0} onClick={result.previous}>Previous backup page</button>{" "}
        <span>Page {result.index + 1}</span>{" "}
        <button type="button" disabled={result.loading || !page?.nextCursor} onClick={() => page?.nextCursor && result.next(page.nextCursor)}>Next backup page</button>{" "}
        <button type="button" disabled={result.loading} onClick={result.returnLatest}>Return backup assessment to latest</button>
      </nav>
    </article>}
    <p className="table-note">A TargetAdministrator scoped to this server configures backup policies and verifies historical time-zone rules.</p>
    {canManagePolicies && scope.range === "custom" && <p className="table-note">Return to Live to configure current backup policies and historical time rules. Rewind evidence is read-only.</p>}
  </section>;
}
