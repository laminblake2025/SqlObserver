// Empty recurring-series inventory for existing synthetic dashboard journeys.
// New series interactions are explicitly routed by their owning browser tests.
// This helper is never imported by production code.
export function handleMaintenanceSeriesFixture(url, response, request, targets, send) {
  if (!request.headers.cookie?.split(';').some(value => value.trim() === 'dashboardRefactor=1')) return false;
  const route = /^\/api\/v1\/observation-targets\/([^/]+)\/alerts\/maintenance\/series$/.exec(url.pathname);
  if (!route || request.method !== 'GET') return false;
  if (!targets.some(target => target.instanceId === route[1])) { send(response, {}, 404); return true; }
  send(response, { targetId: route[1], serverNowUtc: new Date().toISOString(), items: [], nextBeforeSeriesId: null });
  return true;
}
