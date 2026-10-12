import test from 'node:test';
import assert from 'node:assert/strict';
import { canAppendHealthPage, healthInvestigationMaximumPages, healthInvestigationMaximumRows } from '../src/features/health/useHealthPageRequest.ts';

test('browser page cache bounds stop sparse page trails and dense row trails before another request', () => {
  assert.equal(healthInvestigationMaximumPages, 400);
  assert.equal(healthInvestigationMaximumRows, 10_000);
  const sparse = Array.from({ length: 399 }, () => ({ items: [1] }));
  assert.equal(canAppendHealthPage(sparse), true);
  assert.equal(canAppendHealthPage([...sparse, { items: [1] }]), false);
  const full = Array.from({ length: 100 }, () => ({ items: Array(100).fill(1) }));
  assert.equal(canAppendHealthPage(full), false);
  assert.equal(canAppendHealthPage(full.slice(0, -1), 100), true);
  assert.equal(canAppendHealthPage(full.slice(0, -1), 101), false);
});
test('received-page append checks count exact rows across heterogeneous pages without changing old receipts', () => {
  const pages = [{ items: Array(25).fill(1) }, { items: Array(75).fill(1) }, { items: Array(9850).fill(1) }];
  const before = pages.map(page => page.items.length);
  assert.equal(canAppendHealthPage(pages, 50), true);
  assert.equal(canAppendHealthPage(pages, 51), false);
  assert.deepEqual(pages.map(page => page.items.length), before);
  assert.equal(canAppendHealthPage([], 100), true);
});
