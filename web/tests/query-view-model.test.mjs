import assert from 'node:assert/strict';
import test from 'node:test';
import { readQueryViewSelection, queryViewHref } from '../src/features/queries/queryViewModel.ts';

test('every query view and ranking metric restores from a shared URL', () => {
  for (const view of ['top', 'regressed', 'cache']) for (const metric of ['cpu', 'duration', 'executions', 'logical_reads', 'writes', 'rows']) {
    const href = queryViewHref('#/queries?target=server&range=custom&from=2026-09-28T00%3A00%3A00Z&to=2026-09-29T00%3A00%3A00Z', { view, metric });
    assert.deepEqual(readQueryViewSelection(href), { view, metric });
    const parameters = new URLSearchParams(href.split('?')[1]);
    assert.equal(parameters.get('target'), 'server'); assert.equal(parameters.get('range'), 'custom');
    assert.equal(parameters.get('from'), '2026-09-28T00:00:00Z'); assert.equal(parameters.get('to'), '2026-09-29T00:00:00Z');
  }
});
test('view edits preserve exact query and plan selection and remove default parameters', () => {
  const href = '#/queries?target=a&databaseId=6&queryFingerprint=q&planFingerprint=p&source=query_store&range=7d';
  const updated = queryViewHref(href, { view: 'regressed', metric: 'writes' });
  const parameters = new URLSearchParams(updated.split('?')[1]);
  for (const [key, value] of new URLSearchParams(href.split('?')[1])) assert.equal(parameters.get(key), value);
  assert.equal(queryViewHref(updated, { view: 'top', metric: 'cpu' }), href);
});
test('unknown URL values fall back independently and do not leak to other screens', () => {
  assert.deepEqual(readQueryViewSelection('#/queries?queryView=unknown&queryMetric=duration'), { view: 'top', metric: 'duration' });
  assert.deepEqual(readQueryViewSelection('#/queries?queryView=cache&queryMetric=unknown'), { view: 'cache', metric: 'cpu' });
  assert.deepEqual(readQueryViewSelection('#/resources?queryView=cache&queryMetric=duration'), { view: 'top', metric: 'cpu' });
  assert.equal(queryViewHref('#/resources?target=a', { view: 'cache', metric: 'duration' }), '#/resources?target=a');
  assert.throws(() => queryViewHref('#/queries', { view: 'unknown', metric: 'cpu' }));
  assert.equal(queryViewHref('#/queries', { view: 'top', metric: 'cpu' }), '#/queries');
});

test('cache drilldown restores the database incarnation and clears it on server or view changes', () => {
  const cacheSelection = { targetId: 'a', databaseId: 6, databaseIncarnation: 'a'.repeat(64), queryFingerprint: 'b'.repeat(64), planFingerprint: 'c'.repeat(64) };
  const selection = { view: 'cache', metric: 'cpu', cacheSelection };
  const href = queryViewHref('#/queries?target=a&range=7d', selection);
  assert.deepEqual(readQueryViewSelection(href), selection);
  assert.equal(readQueryViewSelection(queryViewHref('#/queries?target=b', selection)).cacheSelection, undefined);
  assert.equal(readQueryViewSelection(queryViewHref(href, { ...selection, view: 'top' })).cacheSelection, undefined);
  assert.equal(readQueryViewSelection(href.replace('cacheDatabaseId=6', 'cacheDatabaseId=0')).cacheSelection, undefined);
});

test('ranking source is independent of inspected source and survives scope, view and cache changes', () => {
  const original = '#/queries?target=a&range=custom&from=2026-09-28T00%3A00%3A00.123456Z&to=2026-09-29T00%3A00%3A00.123456Z&databaseId=6&compare=previous&queryFingerprint=q&planFingerprint=p&source=query_store';
  for (const sourceFilter of ['query_store', 'plan_cache']) {
    const href = queryViewHref(original, { view: 'top', metric: 'duration', sourceFilter });
    assert.deepEqual(readQueryViewSelection(href), { view: 'top', metric: 'duration', sourceFilter });
    const parameters = new URLSearchParams(href.split('?')[1]);
    for (const [key, value] of new URLSearchParams(original.split('?')[1])) assert.equal(parameters.get(key), value);
    assert.equal(parameters.get('querySource'), sourceFilter);
    const cacheSelection = { targetId: 'a', databaseId: 6, databaseIncarnation: 'a'.repeat(64), queryFingerprint: 'b'.repeat(64), planFingerprint: 'c'.repeat(64) };
    const changed = queryViewHref(href, { ...readQueryViewSelection(href), view: 'cache', metric: 'rows', cacheSelection });
    assert.deepEqual(readQueryViewSelection(changed), { view: 'cache', metric: 'rows', sourceFilter, cacheSelection });
    assert.equal(new URLSearchParams(changed.split('?')[1]).get('source'), 'query_store');
  }
  assert.deepEqual(readQueryViewSelection(original), { view: 'top', metric: 'cpu' });
  const mixed = queryViewHref(`${original}&querySource=plan_cache`, { view: 'top', metric: 'cpu', sourceFilter: 'mixed' });
  assert.equal(new URLSearchParams(mixed.split('?')[1]).has('querySource'), false);
  assert.deepEqual(readQueryViewSelection(`${original}&querySource=mixed`), { view: 'top', metric: 'cpu' });
});

test('malformed or duplicate ranking source stays explicit until a source choice repairs it', () => {
  for (const suffix of ['querySource=', 'querySource=all', 'querySource=Query_Store', 'querySource=unknown', 'querySource=query_store&querySource=query_store', 'querySource=query_store&querySource=plan_cache']) {
    const href = `#/queries?target=a&source=query_store&${suffix}`;
    const selection = readQueryViewSelection(href);
    assert.deepEqual(selection, { view: 'top', metric: 'cpu', sourceFilterInvalid: true });
    const edited = queryViewHref(href, { ...selection, view: 'regressed', metric: 'writes' });
    assert.equal(readQueryViewSelection(edited).sourceFilterInvalid, true);
    assert.equal(new URLSearchParams(edited.split('?')[1]).get('source'), 'query_store');
    const rebuilt = queryViewHref('#/queries?target=a&databaseId=5', selection);
    assert.equal(readQueryViewSelection(rebuilt).sourceFilterInvalid, true);
    const repaired = queryViewHref(edited, { ...selection, sourceFilterInvalid: undefined, sourceFilter: 'plan_cache' });
    assert.deepEqual(readQueryViewSelection(repaired), { view: 'top', metric: 'cpu', sourceFilter: 'plan_cache' });
    assert.deepEqual(new URLSearchParams(repaired.split('?')[1]).getAll('querySource'), ['plan_cache']);
  }
});

test('writer rejects contradictory source metadata and ignores source state on another route', () => {
  assert.throws(() => queryViewHref('#/queries', { view: 'top', metric: 'cpu', sourceFilter: 'all' }));
  assert.throws(() => queryViewHref('#/queries', { view: 'top', metric: 'cpu', sourceFilter: 'mixed', sourceFilterInvalid: true }));
  assert.throws(() => queryViewHref('#/queries', { view: 'top', metric: 'cpu', sourceFilterInvalid: false }));
  assert.deepEqual(readQueryViewSelection('#/resources?querySource=plan_cache'), { view: 'top', metric: 'cpu' });
  assert.equal(queryViewHref('#/resources?target=a', { view: 'top', metric: 'cpu', sourceFilter: 'plan_cache' }), '#/resources?target=a');
});
