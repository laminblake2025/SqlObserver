import { useEffect, useMemo, useRef, useState } from "react";
import { useTimeFormatter } from "../../TimeDisplayContext";
import type { OverviewScope } from "../overview/overviewTypes";
import { assertBackupPagePosition, assertBackupSnapshot, backupCutoff, type BackupPage } from "./backupAssessment.ts";
import { BackupAssessmentError, getBackupAssessment } from "./backupAssessmentApi.ts";
import { BackupAssessmentView } from "./BackupAssessmentView";
import { BackupPolicyEditor } from "./BackupPolicyEditor";
import { BackupQualificationPanel } from "./BackupQualificationPanel";

export function BackupAssessmentPanel({ instanceId, scope, refresh, canManagePolicies = false }: {
  readonly instanceId: string; readonly scope: OverviewScope; readonly refresh: number;
  readonly canManagePolicies?: boolean;
}) {
  const [restart, setRestart] = useState(0);
  const selection = useMemo(() => {
    try { return { atUtc: backupCutoff(scope, Date.now()) }; }
    catch { return { error: "Choose a valid time range for backup assessment." }; }
  }, [scope.range, scope.from, scope.to, refresh, restart]);
  return <section aria-label="Backup freshness assessments" className="backup-assessment">
    <h3>Backup freshness</h3><p>Full and log backup ages against explicit per-database limits, at the selected range's end.
      The range's start does not filter snapshot records. Pause Live to browse a fixed assessment.</p>
    {selection.error ? <p role="alert">{selection.error}</p> : <BackupSnapshot requestKey={`${instanceId}:${selection.atUtc}:${refresh}:${restart}`}
      instanceId={instanceId} atUtc={selection.atUtc!} live={scope.range !== "custom"} canManagePolicies={canManagePolicies} onRestart={() => setRestart(value => value + 1)} />}
    <p className="table-note">A TargetAdministrator scoped to this server configures backup policies and verifies historical time-zone rules.</p>
    {canManagePolicies && scope.range === "custom" && <p className="table-note">Return to Live to configure current backup policies and historical time rules. Rewind evidence is read-only.</p>}
  </section>;
}
function BackupSnapshot({ instanceId, atUtc, requestKey, live, canManagePolicies, onRestart }: {
  readonly instanceId: string; readonly atUtc: string; readonly requestKey: string; readonly live: boolean; readonly onRestart: () => void;
  readonly canManagePolicies: boolean;
}) {
  const formatTime = useTimeFormatter();
  const [storedPaging, setPaging] = useState<{ key: string; index: number; cursors: readonly (string | undefined)[] }>({ key: "", index: 0, cursors: [undefined] });
  const paging = storedPaging.key === requestKey ? storedPaging : { key: requestKey, index: 0, cursors: [undefined] };
  const [loaded, setLoaded] = useState<{ key: string; index: number; retry: number; page: BackupPage }>();
  const [failed, setFailed] = useState<{ key: string; index: number; retry: number; error: BackupAssessmentError }>();
  const [retry, setRetry] = useState(0); const first = useRef<{ key: string; page: BackupPage } | undefined>(undefined);
  const page = loaded?.key === requestKey && loaded.index === paging.index && loaded.retry === retry ? loaded.page : undefined;
  const error = failed?.key === requestKey && failed.index === paging.index && failed.retry === retry ? failed.error : undefined;
  const displayed = page ?? (!error && live && loaded?.index === paging.index && loaded.page.targetId === instanceId ? loaded.page : undefined);
  const cursor = paging.cursors[paging.index];
  useEffect(() => {
    const controller = new AbortController();
    void getBackupAssessment(instanceId, atUtc, controller.signal, cursor).then(next => {
      if (controller.signal.aborted) return;
      assertBackupPagePosition(next, paging.index);
      if (first.current?.key === requestKey) assertBackupSnapshot(first.current.page, next);
      else first.current = { key: requestKey, page: next };
      setLoaded({ key: requestKey, index: paging.index, retry, page: next }); setFailed(undefined);
    }).catch((failure: unknown) => {
      if (!controller.signal.aborted) setFailed({ key: requestKey, index: paging.index, retry, error: failure instanceof BackupAssessmentError ? failure
        : new BackupAssessmentError("The backup snapshot changed. Restart from the first page.", true) });
    });
    return () => controller.abort();
  }, [instanceId, atUtc, requestKey, paging.index, cursor, retry]);
  const loading = !page && !error;
  return <article className="panel" aria-label="Backup assessment snapshot" aria-busy={loading}>
    {loading && <p role="status">{displayed ? "Updating backup assessments; the previous assessment remains displayed…" : "Loading backup assessments…"}</p>}
    {error && <p role="alert">{error.message}{error.restart ? <button type="button" onClick={onRestart}>Restart backup assessment</button>
      : error.retryable ? <button type="button" onClick={() => setRetry(value => value + 1)}>Retry backup assessment</button> : null}</p>}
    {displayed && live && canManagePolicies && <BackupQualificationPanel key={`${instanceId}:${displayed.targetRevision}`} targetId={instanceId}
      targetRevision={displayed.targetRevision} onChanged={onRestart} />}
    {displayed && <BackupAssessmentView page={displayed} formatTime={formatTime} renderPolicy={live && canManagePolicies ? database =>
      database.databaseFingerprint ? <BackupPolicyEditor key={`${instanceId}:${displayed.targetRevision}:${database.databaseFingerprint}`}
        targetId={instanceId} targetRevision={displayed.targetRevision} fingerprint={database.databaseFingerprint}
        recoveryModel={database.recoveryModel} onSaved={onRestart} /> : null : undefined} />}
    <nav className="pager" aria-label="Backup assessment pages">
      <button type="button" disabled={loading || paging.index === 0} onClick={() => setPaging({ ...paging, index: 0 })}>First backup page</button>{" "}
      <button type="button" disabled={loading || paging.index === 0} onClick={() => setPaging({ ...paging, index: paging.index - 1 })}>Previous backup page</button>{" "}
      <span>Page {paging.index + 1}</span>{" "}
      <button type="button" disabled={!page?.nextCursor} onClick={() => {
        if (page?.nextCursor) setPaging({ key: requestKey, index: paging.index + 1, cursors: [...paging.cursors.slice(0, paging.index + 1), page.nextCursor!] });
      }}>Next backup page</button>
    </nav>
  </article>;
}
