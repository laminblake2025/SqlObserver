export const destinations = { overview: 'Overview', servers: 'Servers', health: 'Server summary', activity: 'Activity', queries: 'Query performance', deadlocks: 'Deadlocks', alerts: 'Alerts', operations: 'Operations', analytics: 'Analytics', reports: 'Reports' } as const;
export type Destination = keyof typeof destinations;
export const navigationDestinations = ['overview', 'servers', 'activity', 'queries', 'deadlocks', 'alerts', 'operations', 'analytics', 'reports'] as const satisfies readonly Destination[];
export interface DashboardRoute {
  readonly page: Destination;
  readonly target: string;
  readonly activityAtUtc?: string;
  readonly activityEventId?: string;
}
const utcTimestamp = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/;
const guid = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
export function readRoute(hash: string): DashboardRoute {
  const [path, query] = hash.replace(/^#\/?/, '').split('?');
  const page = Object.hasOwn(destinations, path ?? '') ? path as Destination : 'overview';
  const params = new URLSearchParams(query);
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
export function activityHistoryHref(target: string, occurredAtUtc: string, eventId?: string): string {
  const query = new URLSearchParams();
  if (target) query.set('target', target);
  query.set('at', occurredAtUtc);
  if (eventId !== undefined && guid.test(eventId)) query.set('event', eventId);
  return `#/activity?${query}`;
}
export function formatAddress(target: { host: string; namedInstance?: string | null; tcpPort?: number | null }): string { return target.namedInstance != null ? `${target.host}\\${target.namedInstance}` : `${target.host}:${target.tcpPort ?? 'unavailable'}`; }
export const capabilityLabels: Readonly<Record<string, string>> = { pending: 'Discovery pending', supported: 'Supported', degraded: 'Degraded visibility', unsupported: 'Unsupported', unreachable: 'Unreachable', authentication_failed: 'Authentication failed', tls_validation_failed: 'TLS validation failed', timed_out: 'Discovery timed out', security_policy_rejected: 'Security policy rejected', disabled: 'Disabled', retired: 'Retired' };
