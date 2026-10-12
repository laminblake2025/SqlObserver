import test from 'node:test';
import assert from 'node:assert/strict';
import { readWaitCategory, waitCategoryHref, retainWaitCategoryHref } from '../src/features/activity/waitSelectionModel.ts';

const target = '11111111-1111-4111-8111-000000000001';
const scope = { target, range: 'custom', from: '2026-09-26T08:00:00.123456Z', to: '2026-09-26T12:00:00.654321Z', databaseId: 6, compare: true };

test('wait category URL preserves the server, exact window, database and comparison', () => {
  const href = waitCategoryHref(scope, 17);
  assert.equal(readWaitCategory(href, target.toUpperCase()), 17);
  const params = new URLSearchParams(href.split('?')[1]);
  assert.equal(params.get('from'), scope.from);
  assert.equal(params.get('to'), scope.to);
  assert.equal(params.get('databaseId'), '6');
  assert.equal(params.get('compare'), '1');
  assert.equal(readWaitCategory(waitCategoryHref(scope)), undefined);
});

test('wait category rejects ambiguous or unsupported input instead of substituting Lock', () => {
  const href = waitCategoryHref(scope, 0);
  for (const suffix of ['&waitCategory=3', `&target=${target}`, '&databaseId=5', '&range=1h', '&compare=0', '&from=2026-09-26T08:00:00Z']) assert.equal(readWaitCategory(href + suffix), null);
  for (const input of ['11', '21', '-1', '03', 'NaN', '', '3.0']) assert.equal(readWaitCategory(waitCategoryHref(scope) + '&waitCategory=' + input), null);
  assert.equal(readWaitCategory(href.replace('#/waits?', '#/resources?')), null);
  assert.equal(readWaitCategory(href, '11111111-1111-4111-8111-000000000002'), null);
  assert.throws(() => waitCategoryHref(scope, 11), /supported/);
});

test('wait category retention follows same-server scope edits and clears on other destinations or targets', () => {
  const current = waitCategoryHref(scope, 20);
  const changed = waitCategoryHref({ ...scope, range: '1h', databaseId: 5 });
  assert.equal(readWaitCategory(retainWaitCategoryHref(changed, current)), 20);
  const other = waitCategoryHref({ ...scope, target: '11111111-1111-4111-8111-000000000002' });
  assert.equal(retainWaitCategoryHref(other, current), other);
  assert.equal(retainWaitCategoryHref('#/overview?range=1h', current), '#/overview?range=1h');
});
