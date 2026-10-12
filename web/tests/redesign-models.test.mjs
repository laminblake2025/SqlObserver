import assert from 'node:assert/strict';
import test from 'node:test';
import { displayRangeInput, editedRangeUtc } from '../src/components/timeRangeEditorModel.ts';
import { chartViewportIdentity } from '../src/components/chartViewportIdentity.ts';
import { analyticsSummaryRow } from '../src/features/analytics/analyticsSummaryModel.ts';

test('local range edits honor DST, reject nonexistent and duplicate instants, and preserve fractional seconds', () => {
  const previous = process.env.TZ; process.env.TZ = 'America/New_York';
  try {
    assert.equal(displayRangeInput('2026-07-01T12:34:56.789123Z', 'local'), '2026-07-01T08:34:56.789');
    assert.equal(editedRangeUtc('2026-07-01T08:34:56.789', 'local'), '2026-07-01T12:34:56.789');
    assert.equal(editedRangeUtc('2026-01-01T08:34:56.789', 'local'), '2026-01-01T13:34:56.789');
    assert.throws(() => editedRangeUtc('2026-03-08T02:30', 'local'), /does not exist/);
    assert.throws(() => editedRangeUtc('2026-11-01T01:30', 'local'), /occurs twice/);
    assert.equal(editedRangeUtc('2026-11-01T05:30', 'utc'), '2026-11-01T05:30');
  } finally { if (previous === undefined) delete process.env.TZ; else process.env.TZ = previous; }
});

test('chart pan identity survives live refresh and changes on deliberate context changes', () => {
  const href = '#/overview?target=11111111-1111-4111-8111-000000000001&range=1h';
  assert.equal(chartViewportIdentity(['workload'], href), chartViewportIdentity(['workload'], href + '&chartAt=2026-10-01T12:00:00Z'));
  assert.notEqual(chartViewportIdentity(['workload'], href), chartViewportIdentity(['blocking'], href));
  assert.notEqual(chartViewportIdentity(['workload'], href), chartViewportIdentity(['workload'], href.replace('1h', '6h')));
  assert.notEqual(chartViewportIdentity(['workload'], href), chartViewportIdentity(['workload'], href.replace('000000000001', '000000000002')));
});

test('analytics summaries retain exact evidence and unknown values', () => {
  const input = { jobId: 'exact-id', jobKind: 'rollup', status: 'partial', requestedAtUtc: '2026-10-01T00:00:00.123456Z', attempt: 2, safeMetadata: { missing: null } };
  const row = analyticsSummaryRow('jobs', input, value => `display ${value}`);
  assert.equal(row.Status, 'partial'); assert.equal(row.Requested, 'display ' + input.requestedAtUtc);
  assert.deepEqual(row.Evidence, input);
  assert.equal(analyticsSummaryRow('evidence-packets', { confidence: null }, value => value).Confidence, null);
});
