import { alpha, beta, database, evidence } from './healthFixture.mjs';
import { activityFixture } from './activityFixture.mjs';
export { alpha, beta };
export const origin = 'http://127.0.0.1:4328';
export const from = '2026-01-15T12:00:00.000Z', to = '2026-01-15T13:00:00.000Z';
export function healthNavigationHash(options = {}) {
  return `#/health?${new URLSearchParams({ target: alpha, range: 'custom', from, to, compare: '1', ...options })}`;
}
export function healthNavigationResponse(url) {
  const blocking = /^\/api\/v1\/observation-targets\/([^/]+)\/activity\/blocking\/current$/.exec(url.pathname);
  if (blocking && [alpha, beta].includes(blocking[1]) && url.searchParams.toString() === 'limit=256')
    return activityFixture(blocking[1])['blocking/current'];
  if (url.pathname === '/api/v1/overview') {
    if (![alpha, beta].includes(url.searchParams.get('targetId'))) throw new Error('Unexpected overview target');
    return { targetId: url.searchParams.get('targetId'), fromUtc: url.searchParams.get('fromUtc'),
      toUtc: url.searchParams.get('toUtc'), refreshedAtUtc: to, targets: [], excludedTargets: 0, evidence: [] };
  }
  const match = /^\/api\/v1\/observation-targets\/([^/]+)\/health(?:\/(databases|files|database-option-changes|server-configuration-snapshot|server-configuration-changes))?$/u.exec(url.pathname);
  if (!match || ![alpha, beta].includes(match[1])) throw new Error('Unexpected health API');
  const value = evidence(match[1]);
  value.databases.items = [database(7, `Synthetic ${match[1] === alpha ? 'Alpha' : 'Beta'} inventory`)];
  switch (match[2]) {
    case undefined: return value.target;
    case 'databases': return value.databases;
    case 'files': return value.files;
    case 'database-option-changes': return { targetId: match[1], fromUtc: url.searchParams.get('fromUtc'),
      toUtc: url.searchParams.get('toUtc'), semantics: 'detected_between_consecutive_complete_inventory_runs', items: [] };
    case 'server-configuration-changes': return { targetId: match[1], targetRevision: 1, publicationGeneration: 0,
      fromUtc: url.searchParams.get('fromUtc'), toUtc: url.searchParams.get('toUtc'), coverage: 'never_collected', items: [], nextCursor: null };
    case 'server-configuration-snapshot': return { targetId: match[1], targetRevision: 1, runId: null,
      observedAtUtc: null, serverMajorVersion: null, collectorEnabled: false, lastOutcome: null,
      coverage: 'never_collected', items: [], nextCursor: null };
    default: throw new Error('Unregistered health response');
  }
}
