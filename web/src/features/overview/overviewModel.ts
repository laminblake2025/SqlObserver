import type { OverviewEvidence, OverviewScope, OverviewValue, OverviewSnapshot } from './overviewTypes';

export function readOverviewScope(hash: string): OverviewScope {
  const params = new URLSearchParams(hash.split('?')[1]);
  const range = params.get('range');
  return { target: params.get('target') ?? '', range: range === '1h' || range === '6h' || range === '7d' || range === 'custom' ? range : '24h', from: params.get('from') ?? undefined, to: params.get('to') ?? undefined, compare: params.get('compare') === '1' };
}
export function overviewHref(scope: OverviewScope, page = 'overview', target = scope.target): string {
  const query = new URLSearchParams();
  if (target) query.set('target', target);
  query.set('range', scope.range);
  if (scope.range === 'custom') { if (scope.from) query.set('from', scope.from); if (scope.to) query.set('to', scope.to); }
  if (scope.compare) query.set('compare', '1');
  return `#/${page}?${query}`;
}
export function overviewWindow(scope: OverviewScope, now: number): {fromUtc: string; toUtc: string} {
  const end = scope.range === 'custom' ? Date.parse(scope.to ?? '') : now;
  const start = scope.range === 'custom' ? Date.parse(scope.from ?? '') : end - ({'1h': 1, '6h': 6, '24h': 24, '7d': 168}[scope.range]) * 3600000;
  if (!Number.isFinite(start) || !Number.isFinite(end) || start >= end || end - start > 31 * 86400000 || end > now + 60000) throw new Error('Choose a valid UTC range of up to 31 days, ending no later than now.');
  return {fromUtc:new Date(start).toISOString(), toUtc:new Date(end).toISOString()};
}
export function aggregateValue(evidence: readonly OverviewEvidence[], key: 'activeAlerts' | 'blockedSessions' | 'deadlocks'): {text: string; note: string} {
  const values: OverviewValue[] = evidence.map(item => item[key]);
  const known = values.filter(v => v.value !== null && !['stale','unavailable','unsupported','disabled'].includes(v.state));
  if (!known.length) return {text:'—', note:'No current evidence'};
  const incomplete = known.length !== values.length || known.some(v => v.state === 'partial');
  return {text:`${known.reduce((sum,v) => sum + v.value!,0).toLocaleString()}${incomplete ? '+' : ''}`, note:`${known.length}/${values.length} servers · ${incomplete ? 'partial count' : key === 'deadlocks' ? 'observed events in window' : 'latest snapshots'}`};
}
export function rankedIssues(snapshot: OverviewSnapshot) {
  return snapshot.evidence.flatMap(e => e.issues).sort((a,b) => a.priority-b.priority || (a.observedAtUtc ?? '').localeCompare(b.observedAtUtc ?? '') || a.server.localeCompare(b.server)).slice(0,5);
}
export function displayMetric(value: number | null): string { return value === null ? '—' : value.toLocaleString(undefined, {maximumFractionDigits:2}); }
