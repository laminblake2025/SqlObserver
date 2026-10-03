import type { OverviewScope } from './features/overview/overviewTypes';
import { readOverviewScope, overviewHref } from './features/overview/overviewModel.ts';
import { healthCheckTab, type HealthCheckTab } from './features/operations/agentHistoryModel.ts';
import { analyticsSurfaceCatalog } from './features/analytics/analyticsSurfaceCatalog.ts';
import type { AnalyticsSurface } from './features/analytics/analyticsTypes';

export const destinations = { overview: 'Overview', servers: 'Servers', health: 'Server summary', activity: 'Activity', waits: 'Waits', resources: 'Resources', queries: 'Query performance', deadlocks: 'Deadlocks', alerts: 'Alerts', rules: 'Alert rules', maintenance: 'Maintenance windows', destinations: 'Destinations', permissions: 'Permission script', operations: 'Health checks', analytics: 'Analytics', reports: 'Reports' } as const;
export type Destination = keyof typeof destinations;
export type HealthSection = "summary" | "databases" | "collection";
export const navigationDestinations = ['overview', 'servers', 'activity', 'waits', 'resources', 'queries', 'deadlocks', 'alerts', 'operations', 'analytics', 'reports'] as const satisfies readonly Destination[];
export interface DashboardRoute {
  readonly page: Destination;
  readonly target: string;
  readonly activityAtUtc?: string;
  readonly activityEventId?: string;
  readonly databaseId?: number;
  readonly queryFingerprint?: string;
  readonly planFingerprint?: string;
  readonly querySource?: 'query_store' | 'plan_cache';
  readonly querySelectionInvalid?: true;
  readonly healthCheck?: HealthCheckTab;
  readonly healthSection?: HealthSection;
  readonly configurationRunId?: string;
  readonly analyticsSurface?: AnalyticsSurface;
  readonly analyticsSelectionInvalid?: true;
}
const utcTimestamp = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/;
const guid = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
export const invalidQuerySelectionMessage = 'The query selection in this URL is invalid. Select a query from the ranking.';
export function readRoute(hash: string): DashboardRoute {
  const route = hash.replace(/^#\/?/, ''), separator = route.indexOf('?');
  const path = separator < 0 ? route : route.slice(0, separator);
  const query = separator < 0 ? undefined : route.slice(separator + 1);
  const page = Object.hasOwn(destinations, path ?? '') ? path as Destination : 'overview';
  const params = new URLSearchParams(query);
  const scope = readOverviewScope(hash);
  const base = { page, target: scope.target, ...(scope.databaseId !== undefined ? { databaseId: scope.databaseId } : {}) };
  const check = healthCheckTab(params.get('check'));
  const section = params.get('healthSection');
  if (page === 'analytics') {
    const surfaces = params.getAll('surface');
    if (surfaces.length === 0) return { ...base, analyticsSurface: 'incidents' };
    const surface = surfaces[0];
    return surfaces.length === 1 && analyticsSurfaceCatalog.some(option => option.value === surface)
      ? { ...base, analyticsSurface: surface as AnalyticsSurface }
      : { ...base, analyticsSelectionInvalid: true };
  }
  if (page === 'health' && (section === 'summary' || section === 'databases' || section === 'collection')) {
    const run = params.get('configurationRun');
    return { ...base, healthSection: section, ...(section === 'summary' && run && guid.test(run) ? { configurationRunId: run.toLowerCase() } : {}) };
  }
  if (page === 'operations' && check) return { ...base, healthCheck: check };
  const databaseId = scope.databaseId;
  const queryFingerprint = params.get('queryFingerprint');
  // `source` binds the inspected query; `querySource` independently filters its
  // ranking. Invalid exact selections must never silently choose a first value.
  if (page === 'queries' && ['queryFingerprint', 'planFingerprint', 'source'].some(key => params.has(key))) {
    const planFingerprint = params.get('planFingerprint');
    const source = params.get('source');
    const target = params.get('target');
    if (['target', 'databaseId', 'queryFingerprint'].some(key => params.getAll(key).length !== 1)
        || ['planFingerprint', 'source'].some(key => params.getAll(key).length > 1)
        || target === null || !guid.test(target) || target === '00000000-0000-0000-0000-000000000000'
        || databaseId === undefined || queryFingerprint === null || !/^[0-9a-f]{64}$/i.test(queryFingerprint)
        || planFingerprint !== null && !/^[0-9a-f]{64}$/i.test(planFingerprint)
        || source !== null && source !== 'query_store' && source !== 'plan_cache'
        || planFingerprint !== null && source === 'plan_cache') return { ...base, querySelectionInvalid: true };
    return { ...base,
      queryFingerprint: queryFingerprint.toLowerCase(),
      ...(planFingerprint !== null ? { planFingerprint: planFingerprint.toLowerCase() } : {}),
      ...(source !== null ? { querySource: source } : {}) };
  }
  const activityAtUtc = params.get('at');
  if (page === 'activity' && activityAtUtc !== null && utcTimestamp.test(activityAtUtc) && Number.isFinite(Date.parse(activityAtUtc))) {
    const activityEventId = params.get('event');
    return activityEventId !== null && guid.test(activityEventId)
      ? { ...base, activityAtUtc, activityEventId }
      : { ...base, activityAtUtc };
  }
  return base;
}
export function routeHref(page: Destination, target: string): string { return `#/${page}${target ? `?target=${encodeURIComponent(target)}` : ''}`; }
export function analyticsSurfaceHref(scope: OverviewScope, target: string, surface: AnalyticsSurface): string {
  if (!analyticsSurfaceCatalog.some(option => option.value === surface)) throw new Error('Choose an available analytics surface.');
  return `${overviewHref(scope, 'analytics', target)}&surface=${encodeURIComponent(surface)}`;
}
export function retainAnalyticsSurfaceHref(href: string, currentHash: string): string {
  if (href.split('?')[0] !== '#/analytics' || currentHash.split('?')[0] !== '#/analytics') return href;
  const next = new URLSearchParams(href.split('?')[1]);
  if (next.has('surface')) return href;
  const selected = new URLSearchParams(currentHash.split('?')[1]).getAll('surface');
  for (const value of selected) next.append('surface', value);
  return `#/analytics?${next}`;
}
export function healthSectionHref(scope: OverviewScope, target: string, section: HealthSection, configurationRunId?: string): string {
  const selection = section === "summary" && target.toLowerCase() === scope.target.toLowerCase() && configurationRunId && guid.test(configurationRunId)
    ? `&configurationRun=${configurationRunId.toLowerCase()}` : "";
  return `${overviewHref(scope, 'health', target)}&healthSection=${section}${selection}`;
}
export function activityHistoryHref(target: string, occurredAtUtc: string, eventId?: string, scope?: OverviewScope): string {
  const query = new URLSearchParams(scope ? overviewHref(scope, 'activity', target).split('?')[1] : undefined);
  if (target) query.set('target', target);
  if (scope) {
    query.set('range', scope.range);
    if (scope.range === 'custom') { if (scope.from) query.set('from', scope.from); if (scope.to) query.set('to', scope.to); }
    if (scope.compare) query.set('compare', '1');
  }
  // Native DateTimeOffset responses use +00:00; Activity routes restore Z timestamps.
  query.set('at', occurredAtUtc.replace(/\+00:00$/u, 'Z'));
  if (eventId !== undefined && guid.test(eventId)) query.set('event', eventId);
  return `#/activity?${query}`;
}
export function formatAddress(target: { host: string; namedInstance?: string | null; tcpPort?: number | null }): string { return target.namedInstance != null ? `${target.host}\\${target.namedInstance}` : `${target.host}:${target.tcpPort ?? 'unavailable'}`; }
export const capabilityLabels: Readonly<Record<string, string>> = { pending: 'Discovery pending', supported: 'Supported', degraded: 'Degraded visibility', unsupported: 'Unsupported', unreachable: 'Unreachable', authentication_failed: 'Authentication failed', tls_validation_failed: 'TLS validation failed', timed_out: 'Discovery timed out', security_policy_rejected: 'Security policy rejected', disabled: 'Disabled', retired: 'Retired' };
