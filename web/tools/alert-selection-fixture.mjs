// Synthetic native active-inbox pages. The cutoff bounds ordering, not historical state.
export const alertSelectionSnapshot = '2026-09-30T12:00:00.000Z';
const first = { alertId: '44444444-4444-4444-8444-444444444444', ruleId: '55555555-5555-4555-8555-555555555555',
  ruleName: 'SYNTHETIC sustained blocking', state: 'firing', firstObservedUtc: '2026-09-30T09:00:00.000Z',
  firedUtc: '2026-09-30T09:01:00.000Z', value: 12, reason: '12 distinct sessions blocked', deliverySuppressed: false };
const second = { alertId: '66666666-6666-4666-8666-666666666666', ruleId: '77777777-7777-4777-8777-777777777777',
  ruleName: 'SYNTHETIC paged alert', state: 'firing', firstObservedUtc: '2026-09-30T08:00:00.000Z',
  firedUtc: '2026-09-30T08:01:00.000Z', value: 7, reason: 'Synthetic bounded second-page evidence', deliverySuppressed: false };
const roundTrip = value => value.replace(/\.(\d{3})Z$/, '.$10000+00:00');
export function nativeTargetAlertCursor(target, snapshot, last) {
  return Buffer.from(`${target}|${roundTrip(snapshot)}|${roundTrip(last.firedUtc ?? last.firstObservedUtc)}|${last.alertId}`).toString('base64');
}
const fleetCursor = (snapshot, last) => Buffer.from(`${roundTrip(snapshot)}|${roundTrip(last.firedUtc ?? last.firstObservedUtc)}|${last.targetId}|${last.alertId}`).toString('base64');
export function nativeAlertSelectionPage(targets, targetId, cursor) {
  const rows = [first, ...Array.from({ length: 99 }, (_, index) => {
    const at = new Date(Date.parse('2026-09-30T09:00:00.000Z') - index * 1000).toISOString();
    return { ...first, alertId: `99999999-9999-4999-8999-${String(index + 1).padStart(12, '0')}`,
      ruleName: `SYNTHETIC intermediate alert ${index + 1}`, firstObservedUtc: at, firedUtc: at };
  })];
  const fleet = targetId === undefined;
  const decorate = (row, target) => fleet ? { ...row, targetId: target.instanceId, targetName: target.displayName } : row;
  const items = rows.map(row => decorate(row, targets[0]));
  const next = fleet ? fleetCursor(alertSelectionSnapshot, items.at(-1)) : nativeTargetAlertCursor(targetId, alertSelectionSnapshot, items.at(-1));
  if (cursor !== null && cursor !== next) return undefined;
  return { ...(fleet ? {} : { targetId }), snapshotUtc: alertSelectionSnapshot,
    items: cursor ? [decorate(second, targets[1])] : items, nextCursor: cursor ? null : next };
}
