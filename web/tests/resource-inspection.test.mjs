import test from 'node:test';
import assert from 'node:assert/strict';
import { resourceInspectionUtc, resourceInspectionInputUtc } from '../src/features/resources/resourceInspectionModel.ts';

const from = '2026-09-26T08:00:00.123456Z', to = '2026-09-26T12:00:00.654321Z';

test('resource inspection retains exact UTC microseconds inside a half-open investigation window', () => {
  assert.equal(resourceInspectionUtc(from, from, to), from);
  assert.equal(resourceInspectionUtc('2026-09-26T12:00:00.654320Z', from, to), '2026-09-26T12:00:00.654320Z');
  assert.throws(() => resourceInspectionUtc('2026-09-26T08:00:00.123455Z', from, to), /inside the shared window/);
  assert.throws(() => resourceInspectionUtc(to, from, to), /before its end/);
});

test('resource keyboard alignment treats native date/time input as UTC and accepts short fractions', () => {
  assert.equal(resourceInspectionInputUtc('2026-09-26T10:30', from, to), '2026-09-26T10:30:00.000Z');
  assert.equal(resourceInspectionInputUtc('2026-09-26T10:30:00.1', from, to), '2026-09-26T10:30:00.100Z');
  assert.equal(resourceInspectionInputUtc('2026-09-26T10:30:00.01', from, to), '2026-09-26T10:30:00.010Z');
  assert.equal(resourceInspectionInputUtc('2026-09-26T10:30:00.123456', from, to), '2026-09-26T10:30:00.123456Z');
});

test('resource inspection rejects malformed calendars, local timestamps, invalid windows and out-of-window native input', () => {
  for (const invalid of ['', 'not-a-date', '2026-02-30T10:00:00Z', '2026-09-26T10:00:00', '2026-09-26T10:00:00+01:00']) assert.throws(() => resourceInspectionUtc(invalid, from, to));
  for (const invalid of ['', '2026-02-30T10:00', '2026-09-26T10:00Z', '2026-09-26T07:00', '2026-09-26T13:00']) assert.throws(() => resourceInspectionInputUtc(invalid, from, to));
  assert.throws(() => resourceInspectionUtc('2026-09-26T10:00:00Z', to, from));
  assert.throws(() => resourceInspectionUtc('2026-09-26T10:00:00Z', from, from));
});

test('resource inspection accepts repository seventh fractional zero without rounding a nonzero digit', () => {
  assert.equal(resourceInspectionUtc('2026-09-26T10:00:00.1234560Z', from, to), '2026-09-26T10:00:00.123456Z');
  assert.throws(() => resourceInspectionUtc('2026-09-26T10:00:00.1234567Z', from, to));
});
