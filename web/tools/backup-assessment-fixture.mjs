// Synthetic loopback evidence only. Never imported by production code.
import { readFile, appendFile, mkdir } from 'node:fs/promises';
const artifacts = new URL('../.artifacts/', import.meta.url);
const runId = '22222222-2222-4222-8222-222222222222';
const hash = n => n.toString(16).padStart(64, '0');
export function backupFixture(targetId, atUtc, scenario = 'mixed', after = 0) {
  const stamp = seconds => new Date(Date.parse(atUtc) - seconds * 1000).toISOString();
  const observed = stamp(scenario === 'stale_source' ? 7200 : 5);
  const selected = s => ({ kind: s.kind, copyOnly: s.copyOnly, finishUtc: s.recordedFinishUtc, sourceLocalFinish: s.sourceLocalFinish,
    hasChecksum: s.hasChecksum, isDamaged: s.isDamaged, metadataComplete: s.metadataComplete });
  const fullLimit = 86400, logLimit = 900;
  const items = Array.from({ length: 51 }, (_, index) => {
    const unknown = index === 50, ag = index === 2, revoked = index === 3, unresolved = index === 5;
    const sample = (kind, age) => ({ kind, copyOnly: false, recordedFinishUtc: kind === 'full' && unresolved ? null : stamp(age),
      sourceLocalFinish: stamp(age).replace('Z', ''), sourceLocalStart: stamp(age + 60).replace('Z', ''),
      recordedStartOffsetMinutes: 0, sizeBytes: '9223372036854775807', hasChecksum: true, isDamaged: kind === 'full' && (ag || revoked),
      metadataComplete: true, firstRecoveryForkFingerprint: hash(900), lastRecoveryForkFingerprint: hash(900),
      timeState: kind === 'full' && unresolved ? 'qualification_missing' : 'resolved',
      qualificationRevision: kind === 'full' && unresolved ? null : 1, qualificationTrusted: !revoked && !(kind === 'full' && unresolved) });
    const full = sample('full', 3600), log = sample('log', index === 0 ? 960 : 60);
    const policy = unknown || index === 1 ? null : { enabled: index !== 4, maximumFullAgeSeconds: fullLimit, maximumLogAgeSeconds: logLimit,
      maximumObservationAgeSeconds: 300, fullRequirement: 'full_image', requireChecksums: false };
    const check = (s, max) => {
      const reason = unknown ? 'database_identity_unknown' : !policy ? 'policy_not_configured' : !policy.enabled ? 'policy_disabled'
        : scenario === 'stale_source' ? 'stale_observation' : ag ? 'other_replica_evidence_required' : revoked ? 'timestamp_qualification_unavailable'
          : s.recordedFinishUtc === null ? 'finish_time_unknown' : null;
      if (reason) return { status: reason === 'policy_disabled' ? 'disabled' : 'indeterminate', reason, maximumAgeSeconds: policy ? max : null,
        observedAgeSeconds: null, selectedBackup: null, action: reason === 'policy_not_configured' ? 'Configure explicit full and log backup thresholds for this database identity.'
          : reason === 'other_replica_evidence_required' ? 'Inspect backup evidence on every registered replica; local history is incomplete.'
            : reason.includes('qualification') || reason === 'finish_time_unknown' ? 'Verify historical source-zone rules and collect new evidence with an active qualification.'
              : reason === 'stale_observation' ? 'Restore complete current collection coverage.' : null };
      const age = (Date.parse(atUtc) - Date.parse(s.recordedFinishUtc)) / 1000;
      return { status: age > max ? 'policy_violated' : 'within_threshold', reason: age > max ? 'age_exceeded' : 'recent_backup',
        maximumAgeSeconds: max, observedAgeSeconds: age, selectedBackup: selected(s), action: null };
    };
    return { databaseOrdinal: index + 1, databaseFingerprint: unknown ? null : hash(index + 1), familyFingerprint: hash(800), recoveryForkFingerprint: hash(900),
      groupFingerprint: ag ? hash(700) : null, agDatabaseFingerprint: ag ? hash(701) : null, localReplicaFingerprint: ag ? hash(702) : null,
      recoveryModel: 'full', catalogDatabaseState: 'ONLINE', scope: ag ? 'availability_group_local_only' : 'standalone',
      localReplicaRole: ag ? 'PRIMARY' : null, localReplicaPreferred: ag ? true : null, unclassifiedBackupSeen: false,
      coverage: unknown ? 'partial' : 'complete', policyRevision: policy ? 1 : null, policy, full: check(full, fullLimit), log: check(log, logLimit),
      observedIssues: ag || revoked ? [{ kind: 'full', copyOnly: false, reason: 'backup_damaged' }] : [], samples: unknown ? [] : [full, log] };
  });
  const page = { contractVersion: 2, targetId, atUtc, targetRevision: 1, runId, sourceObservedAtUtc: observed, completedAtUtc: stamp(1), receivedAtUtc: stamp(2),
    sourceZoneId: 'UTC', historyLowerBoundLocal: stamp(35 * 86400).replace('Z', ''), coverage: 'partial', evidenceStatus: 'retained',
    sourceRows: 151, outputItems: 151, retainedItems: 151, databaseCount: 51, collectorEnabled: true, action: null,
    items: items.slice(after, after + 50), hasMore: after === 0, nextCursor: after === 0 ? 'backup-page-2' : null };
  const empty = (status, coverage, action) => ({ ...page, evidenceStatus: status, coverage, action, items: [], hasMore: false, nextCursor: null,
    sourceRows: 0, outputItems: 0, retainedItems: 0, databaseCount: 0, sourceObservedAtUtc: null, sourceZoneId: null, historyLowerBoundLocal: null });
  if (scenario === 'not_collected') return { ...empty('not_collected', 'not_collected', 'Enable and qualify backup v2 collection for this target revision.'),
    runId: null, completedAtUtc: null, receivedAtUtc: null, collectorEnabled: false };
  if (scenario === 'failed') return empty('collection_failed', 'permission_denied', 'Resolve the collector failure or permission gap and collect again.');
  if (scenario === 'expired') return { ...empty('evidence_not_retained', 'failed', 'Choose a retained time window or collect new backup evidence.'),
    receivedAtUtc: null, sourceRows: 151, outputItems: 151 };
  return page;
}
export async function handleBackupFixture(url, res) {
  const route = /^\/api\/v2\/observation-targets\/([^/]+)\/health\/backups$/.exec(url.pathname);
  if (!route) return false;
  await mkdir(artifacts, { recursive: true });
  let scenario = 'mixed';
  try { scenario = (await readFile(new URL('backup-scenario.txt', artifacts), 'utf8')).trim(); } catch {}
  const atUtc = url.searchParams.get('atUtc') ?? new Date().toISOString();
  const cursor = url.searchParams.get('cursor');
  await appendFile(new URL('backup-requests.ndjson', artifacts), JSON.stringify({ atUtc, cursor, scenario }) + '\n');
  if (scenario === 'stale_cursor' && cursor) { res.writeHead(409, { 'content-type': 'application/problem+json' }); res.end('{"code":"cursor_stale"}'); return true; }
  res.writeHead(200, { 'content-type': 'application/json' });
  res.end(JSON.stringify(backupFixture(route[1], atUtc, scenario, cursor === 'backup-page-2' ? 50 : 0))); return true;
}
