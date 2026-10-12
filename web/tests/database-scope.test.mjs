import test from 'node:test';
import assert from 'node:assert/strict';
import { readOverviewScope, overviewHref } from '../src/features/overview/overviewModel.ts';
import { readRoute, activityHistoryHref } from '../src/dashboardModel.ts';
import { databaseJumpHref, queryJumpHref } from '../src/components/commandPaletteModel.ts';
import { loadDatabaseScopeChoices, DatabaseScopeLoadError } from '../src/components/databaseScopeModel.ts';

const target = '11111111-1111-4111-8111-000000000001';
const other = '11111111-1111-4111-8111-000000000002';
const from = '2026-09-26T08:00:00.000Z', to = '2026-09-26T12:00:00.000Z';
const selected = { target, databaseId: 6, range: 'custom', from, to, compare: true };
const params = href => new URLSearchParams(href.split('?')[1]);
const page = (items, nextCursor = null, instanceId = target) => ({ instanceId, repositoryTimeUtc: to, items, nextCursor });
const signal = () => new AbortController().signal;

test('database context survives all same-server diagnostic links and time changes', () => {
  for (const surface of ['health', 'activity', 'queries', 'resources', 'waits', 'deadlocks', 'operations']) {
    const href = overviewHref(selected, surface, target);
    assert.equal(params(href).get('databaseId'), '6');
    assert.equal(readRoute(href).databaseId, 6);
    assert.equal(readOverviewScope(href).databaseId, 6);
    assert.equal(params(href).get('from'), from);
    assert.equal(params(href).get('to'), to);
    assert.equal(params(href).get('compare'), '1');
  }
  assert.equal(params(overviewHref({ ...selected, range: '1h' }, 'activity')).get('databaseId'), '6');
});

test('switching servers or opening a fleet link clears database context while preserving time', () => {
  for (const destination of [other, '']) {
    const href = overviewHref(selected, 'overview', destination);
    assert.equal(params(href).get('databaseId'), null);
    assert.equal(params(href).get('from'), from);
    assert.equal(params(href).get('to'), to);
  }
});

test('valid database context needs no query selection and invalid IDs never become a scope', () => {
  for (const databaseId of [1, 6, 32767]) {
    const href = `#/queries?target=${target}&databaseId=${databaseId}&queryFingerprint=invalid`;
    assert.equal(readOverviewScope(href).databaseId, databaseId);
    const route = readRoute(href);
    assert.equal(route.databaseId, databaseId);
    assert.equal(route.queryFingerprint, undefined);
  }
  for (const databaseId of ['', '0', '-1', '01', '1.5', 'NaN', '32768', '99999999999999999999']) {
    const href = `#/health?target=${target}&databaseId=${databaseId}`;
    assert.equal(readOverviewScope(href).databaseId, undefined);
    assert.equal(readRoute(href).databaseId, undefined);
  }
  assert.equal(readOverviewScope('#/health?databaseId=6').databaseId, undefined);
});

test('exact database/query jumps replace old database context without duplicate parameters', () => {
  for (const href of [databaseJumpHref(selected, target, 5), queryJumpHref(selected, target, 5, 'a'.repeat(64))]) {
    assert.deepEqual(params(href).getAll('databaseId'), ['5']);
    assert.equal(readRoute(href).databaseId, 5);
    assert.equal(params(href).get('from'), from);
  }
  const same = activityHistoryHref(target, '2026-09-26T10:00:00Z', undefined, selected);
  assert.equal(params(same).get('databaseId'), '6');
  const changed = activityHistoryHref(other, '2026-09-26T10:00:00Z', undefined, selected);
  assert.equal(params(changed).get('databaseId'), null);
});

test('database inventory reads complete bounded pages in order using the exact target and signal', async () => {
  const abortSignal = signal(), calls = [];
  const result = await loadDatabaseScopeChoices(target, abortSignal, async (selectedTarget, selectedSignal, cursor) => {
    calls.push(cursor);
    assert.equal(selectedTarget, target); assert.equal(selectedSignal, abortSignal);
    return cursor ? page([{ databaseId: 6, name: 'Warehouse' }]) : page([{ databaseId: 5, name: 'Orders' }], 'page-2');
  });
  assert.deepEqual(calls, [undefined, 'page-2']);
  assert.deepEqual(result.items, [{ databaseId: 5, name: 'Orders' }, { databaseId: 6, name: 'Warehouse' }]);
  assert.equal(result.complete, true); assert.equal(result.repositoryTimeUtc, to);
});

test('inventory stops after twenty pages and reports retained choices as partial', async () => {
  let calls = 0;
  const result = await loadDatabaseScopeChoices(target, signal(), async () => {
    calls += 1; return page([{ databaseId: calls, name: `Database ${calls}` }], `page-${calls + 1}`);
  });
  assert.equal(calls, 20); assert.equal(result.items.length, 20); assert.equal(result.complete, false);
});

test('cursor loops and duplicate database IDs fail with validated partial choices', async () => {
  for (const kind of ['cursor', 'duplicate']) {
    let calls = 0;
    await assert.rejects(loadDatabaseScopeChoices(target, signal(), async () => {
      calls += 1;
      return calls === 1 ? page([{ databaseId: 5, name: 'Orders' }], 'repeat')
        : kind === 'cursor' ? page([{ databaseId: 6, name: 'Warehouse' }], 'repeat')
          : page([{ databaseId: 5, name: 'Different name' }]);
    }), error => {
      assert.ok(error instanceof DatabaseScopeLoadError);
      assert.equal(error.choices.complete, false);
      assert.deepEqual(error.choices.items.filter(item => item.databaseId === 5), [{ databaseId: 5, name: 'Orders' }]);
      assert.equal(calls, 2); return true;
    });
  }
});

test('foreign-target and invalid inventory rows are rejected without adopting their names', async () => {
  for (const invalid of [page([{ databaseId: 6, name: 'Foreign' }], null, other),
    page([{ databaseId: 0, name: 'Invalid' }]), page([{ databaseId: 32768, name: 'Invalid' }]),
    page([{ databaseId: 6, name: '' }])]) {
    await assert.rejects(loadDatabaseScopeChoices(target, signal(), async () => invalid), error => {
      assert.ok(error instanceof DatabaseScopeLoadError);
      assert.deepEqual(error.choices.items, []); return true;
    });
  }
});

test('later inventory failure retains earlier validated choices without pretending completeness', async () => {
  let calls = 0;
  await assert.rejects(loadDatabaseScopeChoices(target, signal(), async () => {
    calls += 1;
    if (calls === 2) throw new Error('repository unavailable');
    return page([{ databaseId: 5, name: 'Orders' }], 'page-2');
  }), error => {
    assert.ok(error instanceof DatabaseScopeLoadError);
    assert.deepEqual(error.choices.items, [{ databaseId: 5, name: 'Orders' }]);
    assert.equal(error.choices.complete, false); return true;
  });
});

test('aborting inventory prevents reading another page and propagates cancellation', async () => {
  const controller = new AbortController(); let calls = 0;
  await assert.rejects(loadDatabaseScopeChoices(target, controller.signal, async () => {
    calls += 1; controller.abort(new Error('inventory cancelled'));
    return page([{ databaseId: 5, name: 'Orders' }], 'page-2');
  }), /inventory cancelled/);
  assert.equal(calls, 1);
});
