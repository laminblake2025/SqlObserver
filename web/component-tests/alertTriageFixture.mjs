export const alpha = '11111111-1111-4111-8111-111111111111', beta = '44444444-4444-4444-8444-444444444444';
export const providerText = 'sensitive provider detail must not be shown';
export const alertId = index => `22222222-2222-4222-8222-${String(index + 1).padStart(12, '0')}`;
export const snapshotFor = generation => `2026-03-12T${String(12 + generation).padStart(2, '0')}:00:00.123456Z`;
export const nativeUtc = value => value.endsWith('.123456Z') ? value.replace('.123456Z', '.1234560+00:00') : value.replace(/\.(\d{3})Z$/u, (_, fraction) => `.${fraction}0000+00:00`);
export function alert(index, generation = 1, target = alpha) {
  const fired = new Date(Date.parse(snapshotFor(generation)) - (index + 1) * 1000).toISOString();
  return { alertId: alertId(index), ruleId: `33333333-3333-4333-8333-${String(index + 1).padStart(12, '0')}`, ruleName: `Alert ${String(index + 1).padStart(3, '0')} g${generation}`,
    state: 'firing', firstObservedUtc: fired, firedUtc: fired, acknowledgedUtc: null, value: generation * 1000 + index,
    reason: 'threshold_exceeded', deliverySuppressed: false, targetId: target, targetName: target === alpha ? 'Alpha' : 'Beta' };
}
export function encodeCursor(kind, item, snapshotUtc) {
  return Buffer.from(kind === 'fleet' ? `${nativeUtc(snapshotUtc)}|${nativeUtc(item.firedUtc ?? item.firstObservedUtc)}|${item.targetId}|${item.alertId}`
    : `${item.targetId}|${nativeUtc(snapshotUtc)}|${nativeUtc(item.firedUtc ?? item.firstObservedUtc)}|${item.alertId}`).toString('base64');
}
export function decodeCursor(value, kind) {
  const parts = Buffer.from(value, 'base64').toString('utf8').split('|');
  if (parts.length !== 4) throw new Error('Unexpected native cursor');
  const snapshot = kind === 'fleet' ? parts[0] : parts[1];
  return { generation: Number(snapshot.slice(11, 13)) - 12, index: Number(parts[3].slice(-12)), snapshotUtc: snapshot };
}
export function inboxPage({ kind = 'target', target = alpha, generation = 1, index = 0, total = 210 } = {}) {
  const snapshotUtc = snapshotFor(generation);
  const items = Array.from({ length: Math.min(100, total - index) }, (_, offset) => alert(index + offset, generation, target));
  const nextCursor = index + items.length < total ? encodeCursor(kind, items.at(-1), snapshotUtc) : null;
  return { ...(kind === 'target' ? { targetId: target } : {}), snapshotUtc, items: kind === 'target' ? items.map(({ targetId: _, targetName: __, ...row }) => row) : items, nextCursor };
}
export const access = (targetId, allowed = true) => ({ active: true, targetId, grantedRoles: allowed ? ['Viewer', 'Operator'] : ['Viewer'],
  targetRoles: allowed ? ['Viewer', 'Operator'] : ['Viewer'], allTargetRoles: [] });
