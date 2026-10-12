import test from 'node:test';
import assert from 'node:assert/strict';
import { readMaintenanceSeriesSelection, maintenanceSeriesSelectionHref, retainMaintenanceSeriesSelectionHref } from '../src/features/alerts/maintenanceSeriesSelection.ts';
import { retainAdminSelectionHref } from '../src/features/alerts/adminSelectionModel.ts';
const target = '11111111-1111-4111-8111-111111111111', other = '22222222-2222-4222-8222-222222222222';
const series = '33333333-3333-4333-8333-333333333333', window = '44444444-4444-4444-8444-444444444444';
const base = '#/maintenance?' + new URLSearchParams({target, range:'custom', from:'2026-09-26T08:00:00.123456Z', to:'2026-09-26T12:00:00.654321Z', windowId:window});
test('series selection preserves exact target/window/time and restores its canonical identity', () => {
  const href = maintenanceSeriesSelectionHref(base, series.toUpperCase());
  assert.equal(readMaintenanceSeriesSelection(href, target), series);
  const p = new URLSearchParams(href.split('?')[1]);
  assert.equal(p.get('windowId'), window); assert.equal(p.get('from'), '2026-09-26T08:00:00.123456Z');
  assert.equal(p.get('to'), '2026-09-26T12:00:00.654321Z'); assert.equal(p.get('target'), target);
});
test('absent selection differs from empty duplicate zero or foreign-target series selections', () => {
  assert.equal(readMaintenanceSeriesSelection(base, target), undefined);
  for (const suffix of ['&seriesId=', '&seriesId=invalid', '&seriesId=00000000-0000-0000-0000-000000000000', '&seriesId='+series+'&seriesId='+series])
    assert.equal(readMaintenanceSeriesSelection(base+suffix, target), null);
  assert.equal(readMaintenanceSeriesSelection(base+'&seriesId='+series, other), null);
  assert.equal(readMaintenanceSeriesSelection(base.replace('maintenance','rules')+'&seriesId='+series, target), null);
});
test('same-target time changes retain series and exact selected one-off occurrence', () => {
  const current = base+'&seriesId='+series;
  const next = retainAdminSelectionHref('#/maintenance?'+new URLSearchParams({target,range:'1h'}), current);
  const p = new URLSearchParams(next.split('?')[1]);
  assert.equal(p.get('seriesId'), series); assert.equal(p.get('windowId'), window); assert.equal(p.get('range'),'1h');
});
test('explicit invalid series selections survive time changes without newest-series substitution', () => {
  for (const values of [[''], ['invalid'], [series,series]]) {
    const current = new URLSearchParams({target}); for (const value of values) current.append('seriesId', value);
    const next = retainMaintenanceSeriesSelectionHref('#/maintenance?'+new URLSearchParams({target,range:'7d'}), '#/maintenance?'+current);
    assert.deepEqual(new URLSearchParams(next.split('?')[1]).getAll('seriesId'), values);
    assert.equal(readMaintenanceSeriesSelection(next,target),null);
  }
});
test('series navigation never carries an identity to a different target or diagnostic page', () => {
  const current = base+'&seriesId='+series;
  for (const next of ['#/maintenance?target='+other, '#/rules?target='+target, '#/maintenance?target='+target+'&target='+target])
    assert.equal(retainMaintenanceSeriesSelectionHref(next,current),next);
});
test('explicit selection rejects route or target ambiguity rather than fabricating a target', () => {
  for (const hash of ['#/maintenance', '#/maintenance?target='+target+'&target='+other, '#/rules?target='+target])
    assert.throws(() => maintenanceSeriesSelectionHref(hash,series));
  assert.throws(() => maintenanceSeriesSelectionHref(base,'00000000-0000-0000-0000-000000000000'));
});
