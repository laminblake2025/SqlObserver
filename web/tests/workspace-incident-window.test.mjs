import test from 'node:test';
import assert from 'node:assert/strict';
import { resolveWorkspaceIncidentWindow } from '../src/features/incidents/workspaceIncidentWindow.ts';

const target = '11111111-1111-4111-8111-000000000001';
const now = Date.parse('2026-09-26T12:00:00Z');
const custom = `#/resources?target=${target}&range=custom&from=2026-09-26T08%3A00%3A00.123456Z&to=2026-09-26T11%3A00%3A00.654321Z`;

test('workspace incident window retains exact microseconds and freezes Live bounds', () => {
  assert.deepEqual(resolveWorkspaceIncidentWindow(custom, target, now).window, { fromUtc: '2026-09-26T08:00:00.123456Z', toUtc: '2026-09-26T11:00:00.654321Z' });
  assert.deepEqual(resolveWorkspaceIncidentWindow(`#/activity?target=${target}&range=1h`, target, now).window, { fromUtc: '2026-09-26T11:00:00.000Z', toUtc: '2026-09-26T12:00:00.000Z' });
});

test('workspace incident window rejects ambiguous and contradictory scope without a substitute', () => {
  for (const suffix of ['&range=1h', '&target=' + target, '&from=2026-09-26T09:00:00Z', '&databaseId=0', '&compare=2']) assert.equal(resolveWorkspaceIncidentWindow(custom + suffix, target, now).window, undefined);
  assert.equal(resolveWorkspaceIncidentWindow(custom, '11111111-1111-4111-8111-000000000002', now).window, undefined);
  assert.equal(resolveWorkspaceIncidentWindow(`#/waits?target=${target}&range=1h&from=2026-09-26T08:00:00Z`, target, now).window, undefined);
});

test('workspace incident window refuses future, invalid calendars and windows exceeding the save contract', () => {
  for (const href of [custom.replace('2026-09-26T11', '2026-09-27T11'), custom.replace('2026-09-26T08', '2026-09-18T08'), custom.replace('2026-09-26T08', '2026-02-30T08')]) assert.equal(resolveWorkspaceIncidentWindow(href, target, now).window, undefined);
});
