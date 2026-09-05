export const destinations = { overview: 'Overview', servers: 'Servers', health: 'Health', activity: 'Activity', queries: 'Query performance', deadlocks: 'Deadlocks', alerts: 'Alerts', operations: 'Operations', analytics: 'Analytics', reports: 'Reports' } as const;
export type Destination = keyof typeof destinations;
export function readRoute(hash: string): { page: Destination; target: string } {
  const [path, query] = hash.replace(/^#\/?/, '').split('?');
  return { page: Object.hasOwn(destinations, path ?? '') ? path as Destination : 'overview', target: new URLSearchParams(query).get('target') ?? '' };
}
export function routeHref(page: Destination, target: string): string { return `#/${page}${target ? `?target=${encodeURIComponent(target)}` : ''}`; }
export function formatAddress(target: { host: string; namedInstance?: string | null; tcpPort?: number | null }): string { return target.namedInstance != null ? `${target.host}\\${target.namedInstance}` : `${target.host}:${target.tcpPort ?? 'unavailable'}`; }
export const capabilityLabels: Readonly<Record<string, string>> = { pending: 'Discovery pending', supported: 'Supported', degraded: 'Degraded visibility', unsupported: 'Unsupported', unreachable: 'Unreachable', authentication_failed: 'Authentication failed', tls_validation_failed: 'TLS validation failed', timed_out: 'Discovery timed out', security_policy_rejected: 'Security policy rejected', disabled: 'Disabled', retired: 'Retired' };
