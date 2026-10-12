import assert from 'node:assert/strict';
import test from 'node:test';
import { getOverview } from '../src/features/overview/overviewApi.ts';

const target = '11111111-1111-4111-8111-000000000001';
const window = { fromUtc: '2026-10-02T11:00:00Z', toUtc: '2026-10-02T12:00:00Z' };
const sample = () => ({ targetId: target, ...window, refreshedAtUtc: window.toUtc, excludedTargets: 0,
  targets: [{ targetId: target, displayName: 'SQL A', lifecycle: 'active', environment: null, tags: [], groupingRevision: 0, canManageGrouping: false }],
  evidence: [{ targetId: target, displayName: 'SQL A', collectionState: 'observed', lastObservedUtc: window.toUtc,
    activeAlerts: { value: 1, state: 'observed', observedAtUtc: window.toUtc }, blockedSessions: { value: 1, state: 'observed', observedAtUtc: window.toUtc },
    deadlocks: { value: null, state: 'unavailable', observedAtUtc: null }, series: [], gaps: [],
    resources: [{ targetId: target, server: 'SQL A', label: 'engine.cpu', unit: '%', state: 'observed', value: 20, observedAtUtc: window.fromUtc }],
    issues: [{ targetId: target, server: 'SQL A', title: 'Blocking', detail: 'One session', destination: 'activity', priority: 1, observedAtUtc: window.fromUtc }] }] });

test('overview rejects invalid resource display fields and native observed instants with safe errors', async () => {
  const original = globalThis.fetch;
  try {
    for (const patch of [{ server: {} }, { unit: [] }, { state: {} }, { observedAtUtc: {} },
      { observedAtUtc: undefined }, { observedAtUtc: '2026-02-30T12:00:00Z' }, { observedAtUtc: '2026-10-02T12:00:00.12345618Z' }]) {
      const body = sample(); Object.assign(body.evidence[0].resources[0], patch);
      globalThis.fetch = async () => Response.json(body);
      await assert.rejects(getOverview(target, window, new AbortController().signal), { message: 'Invalid Overview resource.' });
    }
  } finally { globalThis.fetch = original; }
});

test('overview rejects malformed issue fields and null nested rows before rendering or sorting', async () => {
  const original = globalThis.fetch;
  try {
    for (const patch of [{ server: {} }, { observedAtUtc: undefined }, { observedAtUtc: [] }, { observedAtUtc: '2026-02-30T12:00:00Z' }]) {
      const body = sample(); Object.assign(body.evidence[0].issues[0], patch);
      globalThis.fetch = async () => Response.json(body);
      await assert.rejects(getOverview(target, window, new AbortController().signal), { message: 'Invalid Overview issue.' });
    }
    for (const key of ['targets', 'evidence', 'resources', 'issues', 'series']) {
      const body = sample();
      if (key === 'targets' || key === 'evidence') body[key] = [null]; else body.evidence[0][key] = [null];
      globalThis.fetch = async () => Response.json(body);
      await assert.rejects(getOverview(target, window, new AbortController().signal), error => !(error instanceof TypeError) && /Overview/u.test(error.message));
    }
  } finally { globalThis.fetch = original; }
});

test('overview keeps valid nullable/seventh-zero native instants and inert provider names', async () => {
  const original = globalThis.fetch, body = sample();
  body.evidence[0].resources[0].observedAtUtc = null;
  body.evidence[0].issues[0].observedAtUtc = '2026-10-02T11:00:00.1234560+00:00';
  body.evidence[0].issues[0].server = '<img src=invalid onerror=alert(1)>';
  globalThis.fetch = async () => Response.json(body);
  try { assert.deepEqual((await getOverview(target, window, new AbortController().signal)).evidence[0].issues[0], body.evidence[0].issues[0]); }
  finally { globalThis.fetch = original; }
});
