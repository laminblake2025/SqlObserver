import type { OverviewScope } from './features/overview/overviewTypes';

export const destinations = { overview: 'Overview', servers: 'Servers', health: 'Server summary', activity: 'Activity', waits: 'Waits', resources: 'Resources', queries: 'Query performance', deadlocks: 'Deadlocks', alerts: 'Alerts', rules: 'Alert rules', maintenance: 'Maintenance windows', destinations: 'Destinations', operations: 'Operations', analytics: 'Analytics', reports: 'Reports' } as const;
export type Destination = keyof typeof destinations;
export const navigationDestinations = ['overview', 'servers', 'activity', 'waits', 'resources', 'queries', 'deadlocks', 'alerts', 'operations', 'analytics', 'reports'] as const satisfies readonly Destination[];
export interface DashboardRoute {
  readonly page: Destination;
  readonly target: string;
  readonly activityAtUtc?: string;
  readonly activityEventId?: string;
  readonly databaseId?: number;
  readonly queryFingerprint?: string;
}
const utcTimestamp = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/;
const guid = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
export function readRoute(hash: string): DashboardRoute {
  const [path, query] = hash.replace(/^#\/?/, '').split('?');
  const page = Object.hasOwn(destinations, path ?? '') ? path as Destination : 'overview';
  const params = new URLSearchParams(query);
  const databaseId = params.get('databaseId');
  if (page === 'health' && databaseId !== null && /^[1-9]\d{0,4}$/.test(databaseId) && Number(databaseId) <= 32767) {
    return { page, target: params.get('target') ?? '', databaseId: Number(databaseId) };
  }
  const queryFingerprint = params.get('queryFingerprint');
  if (page === 'queries' && databaseId !== null && /^[1-9]\d{0,4}$/.test(databaseId) && Number(databaseId) <= 32767
      && queryFingerprint !== null && /^[0-9a-f]{64}$/i.test(queryFingerprint)) {
    return { page, target: params.get('target') ?? '', databaseId: Number(databaseId), queryFingerprint: queryFingerprint.toLowerCase() };
  }
  const activityAtUtc = params.get('at');
  if (page === 'activity' && activityAtUtc !== null && utcTimestamp.test(activityAtUtc) && Number.isFinite(Date.parse(activityAtUtc))) {
    const activityEventId = params.get('event');
    return activityEventId !== null && guid.test(activityEventId)
      ? { page, target: params.get('target') ?? '', activityAtUtc, activityEventId }
      : { page, target: params.get('target') ?? '', activityAtUtc };
  }
  return { page, target: params.get('target') ?? '' };
}
export function routeHref(page: Destination, target: string): string { return `#/${page}${target ? `?target=${encodeURIComponent(target)}` : ''}`; }
export function activityHistoryHref(target: string, occurredAtUtc: string, eventId?: string, scope?: OverviewScope): string {
  const query = new URLSearchParams();
  if (target) query.set('target', target);
  if (scope) {
    query.set('range', scope.range);
    if (scope.range === 'custom') { if (scope.from) query.set('from', scope.from); if (scope.to) query.set('to', scope.to); }
    if (scope.compare) query.set('compare', '1');
  }
  query.set('at', occurredAtUtc);
  if (eventId !== undefined && guid.test(eventId)) query.set('event', eventId);
  return `#/activity?${query}`;
}
export function formatAddress(target: { host: string; namedInstance?: string | null; tcpPort?: number | null }): string { return target.namedInstance != null ? `${target.host}\\${target.namedInstance}` : `${target.host}:${target.tcpPort ?? 'unavailable'}`; }
export const capabilityLabels: Readonly<Record<string, string>> = { pending: 'Discovery pending', supported: 'Supported', degraded: 'Degraded visibility', unsupported: 'Unsupported', unreachable: 'Unreachable', authentication_failed: 'Authentication failed', tls_validation_failed: 'TLS validation failed', timed_out: 'Discovery timed out', security_policy_rejected: 'Security policy rejected', disabled: 'Disabled', retired: 'Retired' };
