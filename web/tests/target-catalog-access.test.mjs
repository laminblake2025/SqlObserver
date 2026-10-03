import test from 'node:test';
import assert from 'node:assert/strict';
import { TargetCatalogAccess } from '../src/features/targets/targetCatalogAccess.ts';

test('catalog denial aborts sibling identity/direct reads and blocks automatic reads', () => {
  const access = new TargetCatalogAccess();
  const catalog = access.begin(), identity = access.begin(), direct = access.begin();
  access.deny();
  for (const read of [catalog, identity, direct]) {
    assert.equal(read.signal.aborted, true);
    assert.equal(read.current(), false);
  }
  assert.equal(access.begin(), undefined);
});

test('explicit retry permits fresh reads without reviving responses from older generations', () => {
  const access = new TargetCatalogAccess(), old = access.begin();
  access.deny(); access.retry();
  const fresh = access.begin();
  assert.equal(fresh.current(), true); assert.equal(old.current(), false);
  access.deny(); access.retry();
  assert.equal(fresh.current(), false); assert.equal(access.begin().current(), true);
});

test('cancelling one request leaves siblings current; disposal fences all pending reads', () => {
  const access = new TargetCatalogAccess(), first = access.begin(), second = access.begin();
  first.cancel(); assert.equal(first.current(), false); assert.equal(second.current(), true);
  access.dispose(); assert.equal(second.current(), false); assert.equal(second.signal.aborted, true);
  assert.equal(access.begin().current(), true);
});

test('same-batch mutation receipts cannot restore denied labels or cross explicit recovery', () => {
  const access = new TargetCatalogAccess(), oldReceipt = access.receiptGuard();
  assert.equal(oldReceipt(), true); access.deny(); assert.equal(oldReceipt(), false);
  access.retry(); assert.equal(oldReceipt(), false);
  const freshReceipt = access.receiptGuard(); assert.equal(freshReceipt(), true);
  access.dispose(); assert.equal(freshReceipt(), false);
});
