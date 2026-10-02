import type { OverviewEvidence, OverviewResource, OverviewScope, OverviewValue, OverviewSnapshot } from './overviewTypes';
import { canonicalUtcTimestamp, utcMicroseconds } from '../../utcTimestamp.ts';

export function readOverviewScope(hash: string): OverviewScope {
  const params = new URLSearchParams(hash.split('?')[1]);
  const range = params.get('range');
  const target = params.get('target') ?? '';
  const database = params.getAll('databaseId');
  const databaseId = target && database.length === 1 && /^[1-9]\d{0,4}$/.test(database[0]!) && Number(database[0]) <= 32767 ? Number(database[0]) : undefined;
  return { target, range: range === '1h' || range === '6h' || range === '7d' || range === 'custom' ? range : '24h', from: params.get('from') ?? undefined, to: params.get('to') ?? undefined, compare: params.get('compare') === '1', ...(databaseId !== undefined ? { databaseId } : {}) };
}
export function overviewHref(scope: OverviewScope, page = 'overview', target = scope.target): string {
  const query = new URLSearchParams();
  if (target) query.set('target', target);
  if (target && target.toLowerCase() === scope.target.toLowerCase() && Number.isInteger(scope.databaseId) && scope.databaseId! >= 1 && scope.databaseId! <= 32767) query.set('databaseId', String(scope.databaseId));
  query.set('range', scope.range);
  if (scope.range === 'custom') { if (scope.from) query.set('from', scope.from); if (scope.to) query.set('to', scope.to); }
  if (scope.compare) query.set('compare', '1');
  return `#/${page}?${query}`;
}
export function issueHref(scope: OverviewScope, target: string, destination: string, observedAtUtc: string | null, title?: string): string {
  if (destination === 'operations' && (title === 'Backup faults observed' || title === 'Backup policy checks failed'))
    return backupAssessmentHref(scope, target, observedAtUtc);
  const href = overviewHref(scope, destination, target);
  if (destination === 'operations' && title === 'SQL Agent failures') return `${href}&check=agent`;
  return destination === 'activity' && observedAtUtc ? `${href}&at=${encodeURIComponent(observedAtUtc)}` : href;
}
export function backupAssessmentHref(scope: OverviewScope, target: string, assessedAtUtc: string | null): string {
  return overviewHref(assessedAtUtc ? {
    ...scope, range: 'custom', from: new Date(Date.parse(assessedAtUtc) - 3600000).toISOString(), to: assessedAtUtc,
  } : { ...scope, range: '1h', from: undefined, to: undefined }, 'operations', target);
}
export function overviewWindow(scope: OverviewScope, now: number): {fromUtc: string; toUtc: string} {
  const end = scope.range === 'custom' ? Date.parse(scope.to ?? '') : now;
  const start = scope.range === 'custom' ? Date.parse(scope.from ?? '') : end - ({'1h': 1, '6h': 6, '24h': 24, '7d': 168}[scope.range]) * 3600000;
  if (!Number.isFinite(start) || !Number.isFinite(end) || !Number.isFinite(now)) throw new Error('Choose a valid UTC range of up to 31 days, ending no later than now.');
  const boundary=(input:string|undefined,milliseconds:number) => {
    try { return { value: utcMicroseconds(input), text: canonicalUtcTimestamp(input!) }; }
    catch { return { value: BigInt(Math.trunc(milliseconds))*1000n, text: new Date(milliseconds).toISOString() }; }
  };
  const from=boundary(scope.range==='custom'?scope.from:undefined,start),to=boundary(scope.range==='custom'?scope.to:undefined,end);
  if (from.value>=to.value || to.value-from.value>31n*86400n*1000000n || to.value>BigInt(Math.trunc(now+60000))*1000n) throw new Error('Choose a valid UTC range of up to 31 days, ending no later than now.');
  return {fromUtc:from.text,toUtc:to.text};
}
export function customUtcWindow(from:string,to:string,now:number,maximumDays=31): {fromUtc:string;toUtc:string} {
  const parse=(value:string) => /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,3})?)?$/.test(value) ? Date.parse(`${value}Z`) : NaN;
  const start=parse(from),end=parse(to);
  if (!Number.isFinite(start)||!Number.isFinite(end)) throw new Error('Enter both dates and times in UTC.');
  if (start>=end) throw new Error('The end must be after the start.');
  if (end-start>maximumDays*86400000) throw new Error(`Choose a range of ${maximumDays} days or less.`);
  if (end>now) throw new Error('The end must not be in the future (UTC).');
  return {fromUtc:new Date(start).toISOString(),toUtc:new Date(end).toISOString()};
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

function isWindowedResource(resource: OverviewResource): boolean {
  return resource.label.startsWith('host.') || resource.label.startsWith('replication.');
}

function observationAge(observedAtUtc: string, refreshedAtUtc: string): string {
  const milliseconds = Date.parse(refreshedAtUtc) - Date.parse(observedAtUtc);
  if (!Number.isFinite(milliseconds) || milliseconds < 0) return 'unavailable';
  const seconds = Math.floor(milliseconds / 1000);
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ${seconds % 60}s`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ${minutes % 60}m`;
  const days = Math.floor(hours / 24);
  return `${days}d ${hours % 24}h`;
}

export function overviewResourceObservationText(resource: OverviewResource, refreshedAtUtc: string, formatTime: (value: string) => string = value => new Date(value).toISOString().replace('T', ' ')): string {
  const windowed = isWindowedResource(resource);
  const timestamp = resource.observedAtUtc && Number.isFinite(Date.parse(resource.observedAtUtc))
    ? formatTime(resource.observedAtUtc)
    : 'No observation';
  const age = windowed && resource.observedAtUtc
    ? ` · Observation age: ${observationAge(resource.observedAtUtc, refreshedAtUtc)}`
    : '';
  return `${windowed ? 'Latest in selected window' : 'Latest source snapshot'} · ${resource.state}${age} · ${timestamp}`;
}

export function overviewEvidenceFooter(snapshot: Pick<OverviewSnapshot, 'fromUtc' | 'toUtc' | 'refreshedAtUtc'>, formatTime: (value: string) => string = value => value): string {
  return `Current cards and waits use latest source snapshots. SQL Agent history uses first observation inside the selected window; other operational panels state their own coverage. Host, storage, and replication metric rows show the latest observation inside the selected window (${formatTime(snapshot.fromUtc)} to ${formatTime(snapshot.toUtc)}); observation age is measured against refresh time ${formatTime(snapshot.refreshedAtUtc)}. Historical charts use ${formatTime(snapshot.fromUtc)} to ${formatTime(snapshot.toUtc)}. Independent collectors can have different coverage.`;
}
