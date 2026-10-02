import test from 'node:test';
import assert from 'node:assert/strict';
import { customUtcInputValue, customUtcWindow } from '../src/features/overview/overviewModel.ts';

const now = Date.parse('2026-10-02T12:00:00.000Z');
const original = { fromUtc: '2026-09-26T08:00:00.123456Z', toUtc: '2026-09-26T12:00:00.654321Z' };

test('UTC editor displays milliseconds while retaining separate exact source bounds', () => {
  assert.equal(customUtcInputValue(original.fromUtc), '2026-09-26T08:00:00.123');
  assert.equal(customUtcInputValue('2026-09-26T08:00:00.1Z'), '2026-09-26T08:00:00.100');
  assert.equal(customUtcInputValue('2026-09-26T08:00:00Z'), '2026-09-26T08:00:00.000');
  for (const invalid of [undefined, '', 'not-a-date', '2026-02-30T08:00:00Z']) assert.equal(customUtcInputValue(invalid), '');
});

test('unchanged editor submission preserves microsecond bounds including native value normalization', () => {
  assert.deepEqual(customUtcWindow(customUtcInputValue(original.fromUtc), customUtcInputValue(original.toUtc), now, 31, original), original);
  const minuteBounds = { fromUtc: '2026-09-26T08:00:00.000123Z', toUtc: '2026-09-26T12:00:00.000456Z' };
  assert.deepEqual(customUtcWindow('2026-09-26T08:00', '2026-09-26T12:00', now, 31, minuteBounds), minuteBounds);
});

test('editing either bound adopts explicit milliseconds and preserves the other bound exactly', () => {
  assert.deepEqual(customUtcWindow('2026-09-26T08:00:00.777', customUtcInputValue(original.toUtc), now, 31, original), { fromUtc: '2026-09-26T08:00:00.777Z', toUtc: original.toUtc });
  assert.deepEqual(customUtcWindow(customUtcInputValue(original.fromUtc), '2026-09-26T12:00:00.888', now, 31, original), { fromUtc: original.fromUtc, toUtc: '2026-09-26T12:00:00.888Z' });
  assert.deepEqual(customUtcWindow(customUtcInputValue(original.fromUtc), customUtcInputValue(original.toUtc), now, 31, { ...original, fromEdited: true }), { fromUtc: '2026-09-26T08:00:00.123Z', toUtc: original.toUtc });
});

test('short decimal inputs mean tenths, hundredths and milliseconds without rejecting valid native values', () => {
  for (const [fraction, normalized] of [['1', '100'], ['01', '010'], ['001', '001']]) {
    assert.deepEqual(customUtcWindow(`2026-09-26T08:00:00.${fraction}`, `2026-09-26T12:00:00.${fraction}`, now), { fromUtc: `2026-09-26T08:00:00.${normalized}Z`, toUtc: `2026-09-26T12:00:00.${normalized}Z` });
  }
  for (const input of ['2026-02-30T08:00', '2026-09-26T25:00', '2026-09-26T08:00:00.1234', '2026-09-26T08:00Z']) assert.throws(() => customUtcWindow(input, '2026-09-26T12:00', now));
});

test('positive duration, maximum days and future bounds are checked at preserved microsecond precision', () => {
  const tiny = { fromUtc: '2026-09-26T08:00:00.123455Z', toUtc: '2026-09-26T08:00:00.123456Z' };
  assert.deepEqual(customUtcWindow(customUtcInputValue(tiny.fromUtc), customUtcInputValue(tiny.toUtc), now, 31, tiny), tiny);
  assert.throws(() => customUtcWindow(customUtcInputValue(tiny.fromUtc), customUtcInputValue(tiny.toUtc), now, 31, { fromUtc: tiny.toUtc, toUtc: tiny.fromUtc }), /after the start/);
  const maximum = { fromUtc: '2026-08-26T08:00:00.123456Z', toUtc: '2026-09-26T08:00:00.123456Z' };
  assert.deepEqual(customUtcWindow(customUtcInputValue(maximum.fromUtc), customUtcInputValue(maximum.toUtc), now, 31, maximum), maximum);
  const excessive = { ...maximum, toUtc: '2026-09-26T08:00:00.123457Z' };
  assert.throws(() => customUtcWindow(customUtcInputValue(excessive.fromUtc), customUtcInputValue(excessive.toUtc), now, 31, excessive), /31 days/);
  const future = { fromUtc: original.fromUtc, toUtc: '2026-10-02T12:00:00.000001Z' };
  assert.throws(() => customUtcWindow(customUtcInputValue(future.fromUtc), customUtcInputValue(future.toUtc), now, 31, future), /future/);
});
