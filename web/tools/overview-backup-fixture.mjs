// Synthetic aggregate states for loopback visual QA; no database source or recovery certification.
export function overviewBackupFixture(targetId, assessedAtUtc, index, expiring = false) {
  if (!expiring && index === 5) return null;
  const counts = overrides => ({ withinThreshold: 1, policyViolated: 0, indeterminate: 0, notRequired: 0, disabled: 0, unknownReasons: [], ...overrides });
  const summary = { targetId, assessedAtUtc, validUntilUtc: new Date(Date.parse(assessedAtUtc) + (expiring ? 8000 : 120000)).toISOString(),
    status: 'within_policy', evidenceStatus: 'retained', coverage: 'complete', databaseCount: 1, collectorEnabled: true,
    full: counts(), log: counts(), observedFaultDatabases: 0 };
  if (!expiring && index === 1) Object.assign(summary, { status: 'policy_violated', full: counts({ withinThreshold: 0, policyViolated: 1 }),
    log: counts({ withinThreshold: 0, indeterminate: 1, unknownReasons: [{ reason: 'other_replica_evidence_required', count: 1 }] }), observedFaultDatabases: 1 });
  if (!expiring && index === 2) Object.assign(summary, { status: 'indeterminate', full: counts({ withinThreshold: 0, indeterminate: 1, unknownReasons: [{ reason: 'policy_not_configured', count: 1 }] }),
    log: counts({ withinThreshold: 0, indeterminate: 1, unknownReasons: [{ reason: 'policy_not_configured', count: 1 }] }) });
  if (!expiring && index === 3) Object.assign(summary, { status: 'disabled', full: counts({ withinThreshold: 0, disabled: 1 }), log: counts({ withinThreshold: 0, disabled: 1 }) });
  if (!expiring && index === 4) Object.assign(summary, { status: 'indeterminate', full: counts({ withinThreshold: 0, indeterminate: 1, unknownReasons: [{ reason: 'timestamp_qualification_unavailable', count: 1 }] }), observedFaultDatabases: 1 });
  return summary;
}
