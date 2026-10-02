// Isolated native SQL-target volume evidence, never connected server data.
const cookie = (request, name) => request.headers.cookie?.split(';').map(value => value.trim()).find(value => value.startsWith(name + '='))?.slice(name.length + 1);
export const volumeObservedUtc = '2026-10-02T10:00:00.123456Z';
export const volumeCompletedUtc = '2026-10-02T10:00:01.123456Z';
export function sqlVolumeFixturePage(target, cursor, mode = 'current') {
  const key = letter => target.instanceId.slice(-8) + letter.repeat(56);
  const item = (letter, totalBytes, availableBytes, trendState, trendSampleDays, daysUntilFull) => ({
    volumeKey: key(letter), identityKind: letter === 'b' ? 'file_scoped_unknown' : 'volume_id', mappedFileCount: letter === 'a' ? 3 : 1,
    totalBytes, availableBytes, observedAtUtc: volumeObservedUtc, trendState, trendSampleDays, daysUntilFull,
  });
  const rows = [
    item('a', '9223372036854775807', '4611686018427387903', 'estimated', 14, 3.2),
    item('b', null, null, 'capacity_unknown', 0, null),
    item('c', '107374182400', '0', 'full', 0, 0),
    item('d', '107374182400', '53687091200', 'insufficient_history', 2, null),
    item('e', '107374182400', '75161927680', 'not_depleting', 14, null),
    item('f', '107374182400', '21474836480', 'unstable_trend', 14, null),
  ];
  const partial = mode === 'partial', noSnapshot = mode === 'not-collected';
  const selected = noSnapshot ? [] : cursor ? rows.slice(4) : rows.slice(0, 4);
  return { instanceId: target.instanceId, targetRevision: target.configurationRevision,
    snapshotRunId: noSnapshot ? null : 'abababab-abab-4bab-8bab-' + target.instanceId.slice(-12),
    state: noSnapshot ? 'unavailable' : mode === 'superseded' ? 'superseded' : partial ? 'partial' : 'current',
    reason: noSnapshot ? 'not_collected' : mode === 'superseded' ? 'snapshot_superseded' : 'completed',
    completedAtUtc: noSnapshot ? null : volumeCompletedUtc, lossKind: noSnapshot ? null : partial ? 'source_row_limit' : 'none',
    minimumLostItems: noSnapshot ? null : partial ? 2 : 0, hasVisibilityGap: noSnapshot || partial || mode === 'superseded',
    repositoryTimeUtc: '2026-10-02T10:00:02.123456Z',
    items: partial || mode === 'superseded' ? selected.map(row => ({ ...row, trendState: 'evidence_unavailable', trendSampleDays: 0, daysUntilFull: null })) : selected,
    nextCursor: !cursor && !noSnapshot && mode !== 'superseded' ? 'dm9sdW1lOjI=' : null };
}
export function handleSqlVolumeFixture(url, response, request, targets, send) {
  if (cookie(request, 'sqlVolumeFixture') !== '1') return false;
  const route = /^\/api\/v1\/observation-targets\/([^/]+)\/resources\/volumes$/.exec(url.pathname);
  if (!route) return false;
  const target = targets.find(value => value.instanceId === route[1]);
  if (!target) { send(response, {}, 404); return true; }
  if (request.method !== 'GET') { send(response, {}, 405); return true; }
  const cursor = url.searchParams.get('cursor');
  if (cursor !== null && cursor !== 'dm9sdW1lOjI=') { send(response, {}, 400); return true; }
  const mode = cookie(request, 'sqlVolumeMode') ?? 'current';
  if (mode === 'error') { send(response, {}, 503); return true; }
  const page = sqlVolumeFixturePage(target, cursor, mode);
  if (mode === 'foreign') page.instanceId = targets[(targets.indexOf(target) + 1) % targets.length].instanceId;
  if (mode === 'revision') page.targetRevision++;
  if (mode === 'malformed') page.items = null;
  if (mode === 'invalid-capacity') page.items[0].availableBytes = '9223372036854775808';
  if (mode === 'invalid-trend') page.items[0].trendState = 'unqualified_forecast';
  if (mode === 'oversized') page.reason = 'a'.repeat(65537);
  send(response, page); return true;
}
