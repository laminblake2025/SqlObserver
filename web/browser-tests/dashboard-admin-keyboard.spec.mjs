import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';

const origin = 'http://127.0.0.1:4335';
const target = '11111111-1111-4111-8111-000000000001';
const destinationId = '88888888-8888-4888-8888-000000000001';
const from = '2026-09-26T08:00:00.123456Z', to = '2026-09-26T12:00:00.654321Z';
const roles = ['Viewer', 'TargetAdministrator', 'SecurityAdministrator'];
const contexts = new WeakMap();
const editor = page => page.getByRole('form', { name: 'Manage destination', exact: true });
const scope = page => new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
const href = page => `/#/${page}?${new URLSearchParams({ target, range: 'custom', from, to, databaseId: '5', compare: '1',
  ...(page === 'destinations' ? { destinationId } : {}) })}`;
const template = { name: 'synthetic_cpu', kind: 'metric_threshold', metricId: 'engine.sql_scheduler_cpu_percent',
  unit: 'percent', comparison: 'greater_than', threshold: 80, hysteresis: 5, confirmationCount: 2,
  confirmationSeconds: 60, evaluationSeconds: 30, clearConfirmationCount: 2 };
const ruleSettings = Object.fromEntries(Object.entries(template).filter(([key]) => key !== 'unit'));
const ruleItems = [1, 2].map(index => ({ ...ruleSettings, name: `SYNTHETIC rule ${index}`,
  ruleId: `66666666-6666-4666-8666-${String(index).padStart(12, '0')}`, enabled: true, revision: index,
  updatedAtUtc: '2026-10-02T12:00:00Z' }));
const maintenanceItems = [1, 2].map(index => ({ windowId: `99999999-9999-4999-8999-${String(index).padStart(12, '0')}`,
  startsAtUtc: `2026-10-0${index}T12:00:00.123456Z`, endsAtUtc: `2026-10-0${index}T13:00:00.654321Z`,
  reason: `SYNTHETIC keyboard maintenance ${index}`, revision: index, cancelledAtUtc: null }));
const nativeTarget = { instanceId: target, displayName: 'SYNTHETIC SQL 01', instanceKey: 'synthetic-1', host: 'sql1.invalid',
  lifecycle: 'active', capabilityStatus: 'supported', capabilityReasons: [], configurationRevision: 1, tcpPort: 1433,
  authenticationMode: 'windows_integrated_service_identity', encryptionMode: 'mandatory_validated',
  discoveryRequestedAtUtc: '2026-09-25T12:00:00Z', lastDiscoveryAtUtc: '2026-09-25T12:01:00Z' };
async function json(route, body, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(body) });
}
const listing = items => ({ targetId: target, listingStartedUtc: '2026-10-02T12:00:00Z', items, hasMore: false, nextCursor: null });

test.beforeEach(async ({ page }, info) => {
  const context = { roles, unexpected: [], mutations: [], failedRequests: [], reads: [], handler: undefined,
    destination: { destinationId, kind: 'https-webhook', enabled: true, approved: true, revision: 1 } };
  contexts.set(page, context);
  await info.attach('native-fixture-context', { body: JSON.stringify({ source: 'Synthetic local native administrative DTOs and receipts; no remote backend or SQL execution',
    authority: 'Explicit UI access fixtures do not prove server authorization', executedAtUtc: new Date().toISOString() }), contentType: 'application/json' });
  await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin }]);
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  page.on('requestfailed', request => { if (request.method() === 'PUT') context.failedRequests.push(request.failure()?.errorText); });
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = () => { context.unexpected.push(`${request.method()} ${url.href}`); return route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/u.test(url.pathname)) return route.continue();
    if (request.method() === 'GET' && url.pathname === '/api/v1/me') {
      const targetId = url.searchParams.get('targetId');
      if (targetId !== null && targetId !== target) return reject();
      return json(route, { active: true, targetId, grantedRoles: context.roles, allTargetRoles: context.roles,
        targetRoles: targetId ? context.roles : [] });
    }
    if (request.method() === 'GET' && url.pathname === '/api/v1/observation-targets')
      return json(route, { items: [nativeTarget], nextCursor: null });
    if (request.method() === 'GET' && url.pathname === `/api/v1/observation-targets/${target}`) return json(route, nativeTarget);
    // Only these two registered GETs reach the existing loopback synthetic
    // server. There is no general GET fallback and no backend proxy.
    if (request.method() === 'GET' && (url.pathname === '/api/v1/overview'
      || url.pathname === `/api/v1/observation-targets/${target}/health/databases`)) return route.continue();
    const prefix = `/api/v1/observation-targets/${target}/alerts/`;
    if (request.method() === 'GET' && url.pathname === `${prefix}maintenance/series` && url.search === '?limit=50')
      return json(route, { targetId: target, serverNowUtc: '2026-10-03T15:00:00Z', items: [], nextBeforeSeriesId: null });
    if (request.method() === 'GET' && url.pathname === `${prefix}rules/catalog` && !url.search)
      return json(route, { targetId: target, digest: 'a'.repeat(64), items: [template] });
    if (request.method() === 'GET' && ['rules', 'maintenance', 'destinations'].some(resource => url.pathname === prefix + resource)
      && url.searchParams.get('limit') === '100' && !url.searchParams.has('cursor')) {
      context.reads.push(url.pathname);
      if (url.pathname === `${prefix}destinations` && !context.roles.includes('SecurityAdministrator')) return json(route, {}, 403);
      const items = url.pathname === `${prefix}rules` ? ruleItems : url.pathname === `${prefix}maintenance` ? maintenanceItems
        : [context.destination, { destinationId: '88888888-8888-4888-8888-000000000002', kind: 'windows-event-log', enabled: false, approved: false, revision: 1 }];
      return json(route, listing(items));
    }
    if (request.method() === 'PUT' && url.pathname === `${prefix}destinations/${destinationId}` && !url.search) {
      const body = request.postDataJSON();
      if (Object.keys(body).sort().join('|') !== 'configurationReference|destinationId|enabled|expectedRevision|kind|operationId'
        || body.destinationId !== destinationId || body.kind !== 'https-webhook' || typeof body.enabled !== 'boolean'
        || !/^[0-9a-f-]{36}$/iu.test(body.operationId) || body.configurationReference !== 'SYNTHETIC-KEYBOARD-WEBHOOK'
        || !Number.isSafeInteger(body.expectedRevision)) return reject();
      context.mutations.push(body);
      if (context.handler && await context.handler(route, body, context)) return;
      if (body.expectedRevision !== context.destination.revision) return json(route, {}, 409);
      context.destination = { ...context.destination, enabled: body.enabled, approved: false, revision: body.expectedRevision + 1 };
      return route.fulfill({ status: 204 });
    }
    return reject();
  });
});
test.afterEach(async ({ page }) => {
  expect(contexts.get(page).unexpected, 'Unexpected API/external traffic and runtime errors are rejected before any proxy').toEqual([]);
});
async function assertScope(page) {
  expect(Object.fromEntries(scope(page))).toMatchObject({ target, range: 'custom', from, to, databaseId: '5', compare: '1' });
}
async function openEditor(page) {
  await page.goto(href('destinations'));
  await page.getByRole('complementary', { name: 'HTTPS webhook', exact: true }).getByRole('button', { name: 'Manage destination', exact: true }).press('Enter');
  await expect(editor(page)).toBeVisible();
  await editor(page).getByRole('textbox', { name: 'Configuration reference', exact: true }).fill('SYNTHETIC-KEYBOARD-WEBHOOK');
}

test('Enter submits an edit and uncertain retry keeps the exact native update without duplicate pending dispatch', async ({ page }) => {
  const context = contexts.get(page);
  let release, delivered;
  const held = new Promise(resolve => { release = resolve; });
  const finished = new Promise(resolve => { delivered = resolve; });
  context.handler = async (route, _body, state) => {
    if (state.mutations.length !== 1) return false;
    await held; await route.fulfill({ status: 503, contentType: 'text/html', body: '<script>synthetic-secret</script>' }); delivered(); return true;
  };
  await openEditor(page);
  const reference = editor(page).getByRole('textbox', { name: 'Configuration reference', exact: true });
  await reference.press('Enter');
  await expect.poll(() => context.mutations.length).toBe(1);
  await expect(editor(page).getByRole('button', { name: 'Saving…', exact: true })).toBeDisabled();
  await editor(page).evaluate(form => { form.requestSubmit(); form.requestSubmit(); });
  expect(context.mutations).toHaveLength(1);
  release(); await finished;
  await expect(editor(page).getByRole('alert')).toHaveText('Destination configuration or approval is temporarily unavailable. Retry the same action.');
  await expect(page.locator('body')).not.toContainText('synthetic-secret');
  await reference.press('Enter');
  await expect.poll(() => context.mutations.length).toBe(2);
  expect(context.mutations[1]).toEqual(context.mutations[0]);
  expect(context.mutations[0]).toMatchObject({ expectedRevision: 1, destinationId, enabled: true, kind: 'https-webhook' });
  await expect(page.getByText('Destination updated. Approval is required for the new revision.', { exact: true })).toBeVisible();
  await expect(editor(page)).toHaveCount(0); await assertScope(page);
});

test('access loss cancels a held edit and a restored role requires explicit reopening with the authoritative revision', async ({ page }) => {
  const context = contexts.get(page);
  let release, delivered;
  const held = new Promise(resolve => { release = resolve; });
  const finished = new Promise(resolve => { delivered = resolve; });
  context.handler = async (route, body, state) => {
    if (state.mutations.length !== 1) return false;
    // Materialize a native change before losing its response. Cancellation does
    // not roll it back, and a later read supplies the authoritative revision.
    state.destination = { ...state.destination, revision: body.expectedRevision + 1, approved: false };
    await held;
    try { await route.fulfill({ status: 204 }); } catch (error) { if (!state.failedRequests.length) throw error; }
    finally { delivered(); }
    return true;
  };
  await openEditor(page);
  await editor(page).getByRole('textbox', { name: 'Configuration reference', exact: true }).press('Enter');
  await expect.poll(() => context.mutations.length).toBe(1);
  context.roles = ['Viewer'];
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await expect(editor(page)).toHaveCount(0);
  await expect.poll(() => context.failedRequests.length).toBe(1);
  release(); await finished;
  await expect(page.getByText('Destination updated. Approval is required for the new revision.', { exact: true })).toHaveCount(0);
  context.roles = roles;
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  const manage = page.getByRole('complementary', { name: 'HTTPS webhook', exact: true }).getByRole('button', { name: 'Manage destination', exact: true });
  await expect(manage).toBeVisible(); await expect(editor(page)).toHaveCount(0);
  await manage.press('Enter');
  await editor(page).getByRole('textbox', { name: 'Configuration reference', exact: true }).fill('SYNTHETIC-KEYBOARD-WEBHOOK');
  await editor(page).getByRole('textbox', { name: 'Configuration reference', exact: true }).press('Enter');
  await expect.poll(() => context.mutations.length).toBe(2);
  expect(context.mutations[1].expectedRevision).toBe(2);
  expect(context.mutations[1].operationId).not.toBe(context.mutations[0].operationId);
  await expect(page.getByText('Destination updated. Approval is required for the new revision.', { exact: true })).toBeVisible();
  await assertScope(page);
});

for (const width of [320, 390, 768, 1440]) {
  test(`all admin table regions support dark focus and keyboard scrolling at ${width}px`, async ({ page }, info) => {
    await page.setViewportSize({ width, height: 1000 });
    for (const [surface, label] of [['rules', 'Alert rules table'], ['maintenance', 'Configured maintenance windows table'], ['destinations', 'Notification destinations table']]) {
      await page.goto(href(surface));
      const region = page.getByRole('region', { name: label, exact: true });
      await expect(region.getByRole('row')).toHaveCount(3);
      await expect(region).toHaveAttribute('tabindex', '0');
      await region.focus(); await expect(region).toBeFocused();
      const presentation = await region.evaluate(element => {
        const style = getComputedStyle(element), box = element.getBoundingClientRect();
        return { scheme: getComputedStyle(document.documentElement).colorScheme, outline: style.outlineStyle,
          width: Number.parseFloat(style.outlineWidth), x: box.x, right: box.right,
          viewport: innerWidth, overflows: element.scrollWidth > element.clientWidth };
      });
      expect(presentation.scheme).toContain('dark'); expect(presentation.outline).not.toBe('none');
      expect(presentation.width).toBeGreaterThanOrEqual(2);
      expect(presentation.x).toBeGreaterThanOrEqual(-1); expect(presentation.right).toBeLessThanOrEqual(presentation.viewport + 1);
      expect(presentation.overflows).toBe(true);
      await region.press('ArrowRight');
      await expect.poll(() => region.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
      await expect(region).toBeFocused();
      expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
      const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
      await info.attach(`${surface}-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
      expect(violations).toEqual([]); await assertScope(page);
    }
    const directory = resolve('../artifacts/dashboard-admin-keyboard/screenshots');
    await mkdir(directory, { recursive: true });
    const path = resolve(directory, `admin-keyboard-${width}.png`);
    await page.evaluate(() => {
      if (document.activeElement instanceof HTMLElement) document.activeElement.blur();
      document.querySelectorAll('.table-scroll').forEach(element => { element.scrollLeft = 0; });
      window.scrollTo({ top: 0, left: 0, behavior: 'instant' });
    });
    await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
    await page.screenshot({ path, fullPage: true });
    await info.attach(`admin-keyboard-${width}`, { path, contentType: 'image/png' });
    expect(contexts.get(page).mutations).toEqual([]);
  });
}
