import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const origin = 'http://127.0.0.1:4322', target = '11111111-1111-4111-8111-000000000001';
const from = '2026-09-26T08:00:00.123456Z', to = '2026-09-26T12:00:00.654321Z';
const name = 'SYNTHETIC Access Alpha';
const record = { instanceId: target, instanceKey: 'synthetic-access', displayName: name, host: 'access.invalid',
  tcpPort: 1433, authenticationMode: 'windows_integrated_service_identity', encryptionMode: 'mandatory_validated',
  lifecycle: 'active', capabilityStatus: 'supported', capabilityReasons: [], configurationRevision: 1,
  discoveryRequestedAtUtc: '2026-09-25T12:00:00Z', lastDiscoveryAtUtc: '2026-09-25T12:01:00Z' };
const href = (surface = 'reports', options = {}) => `/#/${surface}?${new URLSearchParams({ target, range: 'custom', from, to, ...options })}`;
const refresh = page => page.getByRole('button', { name: '↻ Refresh', exact: true });
const reports = page => page.getByRole('region', { name: `Reports for ${name}`, exact: true });
const catalog = '**/api/v1/observation-targets?*';
const direct = `**/api/v1/observation-targets/${target}`;
const sessions = new WeakMap();
const deferred = () => { let resolve; const promise = new Promise(done => resolve = done); return { promise, resolve }; };
const json = (route, value) => route.fulfill({ contentType: 'application/json', headers: { 'cache-control': 'no-store' }, json: value });

test.beforeEach(async ({ page }) => {
  const session = { errors: [], catalog: [], direct: [] }; sessions.set(page, session);
  page.on('pageerror', error => session.errors.push(error.message));
  page.on('request', request => {
    const url = new URL(request.url());
    if (url.pathname === '/api/v1/observation-targets') session.catalog.push(url.href);
    if (url.pathname === `/api/v1/observation-targets/${target}`) session.direct.push(url.href);
  });
  await page.route('**/*', route => {
    if (new URL(route.request().url()).origin === origin) return route.continue();
    session.errors.push(`Unexpected external request: ${route.request().url()}`); return route.abort();
  });
  await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin },
    { name: 'databaseScopeFixture', value: '1', url: origin }]);
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route(catalog, route => json(route, { items: [record], nextCursor: null }));
  await page.route(direct, route => json(route, record));
});
test.afterEach(async ({ page }) => expect(sessions.get(page).errors).toEqual([]));

async function open(page, surface = 'reports', options) {
  await page.goto(href(surface, options)); await expect(refresh(page)).toBeEnabled();
  if (surface === 'reports') await expect(reports(page)).toBeVisible();
}
async function denied(page) {
  await expect(page.getByRole('alert').filter({ hasText: 'Received server labels and evidence have been cleared' })).toBeVisible();
  await expect(page.locator('body')).not.toContainText(name);
  await expect(reports(page)).toHaveCount(0);
  await expect(page.getByRole('navigation', { name: 'Loaded server status', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /Search.*Ctrl\+K/ })).toBeDisabled();
  await expect(refresh(page)).toBeEnabled();
}

for (const status of [401, 403]) test(`same-cursor catalog HTTP ${status} clears labels and requires explicit recovery`, async ({ page }) => {
  await page.clock.install({ time: new Date('2026-09-30T13:00:00Z') });
  await open(page, 'reports', { range: '1h' }); const saved = new URL(page.url()).hash;
  await page.route(catalog, route => route.fulfill({ status, body: 'PRIVATE_AUTH_FAILURE' }));
  await refresh(page).click(); await denied(page);
  await expect(page.locator('body')).not.toContainText('PRIVATE_AUTH_FAILURE');
  const count = sessions.get(page).catalog.length, directCount = sessions.get(page).direct.length;
  await page.unroute(catalog); await page.route(catalog, route => json(route, { items: [record], nextCursor: null }));
  await page.clock.runFor(120_000);
  await denied(page); expect(sessions.get(page).catalog).toHaveLength(count); expect(sessions.get(page).direct).toHaveLength(directCount);
  await refresh(page).click(); await expect(reports(page)).toBeVisible();
  expect(new URL(page.url()).hash).toBe(saved);
});

for (const surface of ['servers', 'reports', 'overview', 'alerts']) test(`catalog HTTP 503 retains received ${surface} evidence with a disclosure`, async ({ page }) => {
  await open(page, surface, surface === 'alerts' ? { target: '' } : undefined);
  const registrations = page.getByRole('region', { name: 'Loaded server registrations', exact: true });
  if (surface === 'servers') await expect(registrations).toContainText(name);
  await page.route(catalog, route => route.fulfill({ status: 503, body: 'PRIVATE_TRANSIENT_FAILURE' }));
  await refresh(page).click();
  await expect(page.getByRole('alert').filter({ hasText: 'last successful read' })).toBeVisible();
  if (surface === 'servers') await expect(registrations).toContainText(name);
  else if (surface === 'reports') await expect(reports(page)).toBeVisible();
  else await expect(page.getByRole('navigation', { name: 'Loaded server status', exact: true })).toBeVisible();
  await expect(page.locator('body')).not.toContainText('PRIVATE_TRANSIENT_FAILURE');
});

test('catalog denial fences a held direct lookup and retains the exact route', async ({ page }) => {
  await page.route(catalog, route => json(route, { items: [], nextCursor: null }));
  await open(page); const saved = new URL(page.url()).hash;
  const gate = deferred(), started = deferred(), denial = deferred(), denialStarted = deferred();
  await page.route(direct, async route => { started.resolve(); await gate.promise; await json(route, record); });
  await page.route(catalog, async route => { denialStarted.resolve(); await denial.promise; await route.fulfill({ status: 403, body: '{}' }); });
  await refresh(page).click(); await Promise.all([started.promise, denialStarted.promise]);
  denial.resolve(); await denied(page); gate.resolve();
  await expect(page.getByText('Loading selected target…', { exact: true })).toHaveCount(0);
  await denied(page); expect(new URL(page.url()).hash).toBe(saved);
});

test('identity denial fences a held successful catalog refresh', async ({ page }) => {
  await open(page);
  const gate = deferred(), started = deferred(), identity = deferred(), identityStarted = deferred();
  await page.route(catalog, async route => { started.resolve(); await gate.promise; await json(route, { items: [record], nextCursor: null }); });
  await page.route('**/api/v1/me?*', async route => { identityStarted.resolve(); await identity.promise; await route.fulfill({ status: 401, body: '{}' }); });
  await refresh(page).click(); await Promise.all([started.promise, identityStarted.promise]);
  identity.resolve(); await denied(page); gate.resolve(); await denied(page);
});

test('catalog denial removes a pending registration and fences its later native receipt', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-09-30T13:00:00Z') });
  await page.route('**/api/v1/me', route => json(route, { active: true,
    grantedRoles: ['TargetAdministrator'], allTargetRoles: ['TargetAdministrator'], targetId: null, targetRoles: [] }));
  await page.goto('/#/servers'); await expect(refresh(page)).toBeEnabled();
  await page.getByRole('button', { name: '+ Add server', exact: true }).click();
  const drawer = page.getByRole('dialog', { name: 'Add server', exact: true });
  await drawer.getByRole('textbox', { name: 'Stable key', exact: true }).fill('synthetic-denied');
  await drawer.getByRole('textbox', { name: 'Display name', exact: true }).fill('SYNTHETIC Late Receipt');
  await drawer.getByRole('textbox', { name: 'Windows host', exact: true }).fill('late.invalid');
  const gate = deferred(), started = deferred();
  await page.route('**/api/v1/observation-targets', async route => {
    expect(route.request().method()).toBe('POST'); const body = route.request().postDataJSON(); started.resolve();
    await gate.promise;
    await json(route, { ...record, ...body, lifecycle: 'pending_discovery', capabilityStatus: 'pending', lastDiscoveryAtUtc: null });
  });
  await drawer.getByRole('button', { name: 'Register target', exact: true }).click(); await started.promise;
  await page.route(catalog, route => route.fulfill({ status: 403, body: '{}' }));
  await page.clock.runFor(60_000); await denied(page); await expect(drawer).toHaveCount(0);
  gate.resolve(); await expect(page.locator('body')).not.toContainText('SYNTHETIC Late Receipt');
  await expect(page.getByRole('button', { name: '+ Add server', exact: true })).toHaveCount(0);
});

test('built Health Back/Forward and reload restore the bare route default', async ({ page }) => {
  await open(page, 'health');
  const tabs = page.getByRole('tablist', { name: 'Server summary sections', exact: true });
  const summary = tabs.getByRole('tab', { name: 'Summary', exact: true }), collection = tabs.getByRole('tab', { name: 'Collection health', exact: true });
  await expect(summary).toHaveAttribute('aria-selected', 'true'); const saved = new URL(page.url()).hash;
  await collection.click(); await expect(collection).toHaveAttribute('aria-selected', 'true');
  await page.goBack(); await expect(summary).toHaveAttribute('aria-selected', 'true'); expect(new URL(page.url()).hash).toBe(saved);
  await page.reload(); await expect(summary).toHaveAttribute('aria-selected', 'true');
  await collection.click(); await page.goBack(); await expect(summary).toHaveAttribute('aria-selected', 'true');
  await page.goForward(); await expect(collection).toHaveAttribute('aria-selected', 'true');
});

test('built bare Health defaults to Databases and clearing shared database context restores Summary', async ({ page }) => {
  await open(page, 'health', { databaseId: '5' });
  const tabs = page.getByRole('tablist', { name: 'Server summary sections', exact: true });
  await expect(tabs.getByRole('tab', { name: 'Databases', exact: true })).toHaveAttribute('aria-selected', 'true');
  const database = page.getByRole('combobox', { name: 'Workspace database', exact: true });
  await database.selectOption(''); await expect(tabs.getByRole('tab', { name: 'Summary', exact: true })).toHaveAttribute('aria-selected', 'true');
  await page.goBack(); await expect(tabs.getByRole('tab', { name: 'Databases', exact: true })).toHaveAttribute('aria-selected', 'true');
  await tabs.getByRole('tab', { name: 'Collection health', exact: true }).click(); await database.selectOption('');
  await expect(tabs.getByRole('tab', { name: 'Collection health', exact: true })).toHaveAttribute('aria-selected', 'true');
  await page.reload(); await expect(tabs.getByRole('tab', { name: 'Collection health', exact: true })).toHaveAttribute('aria-selected', 'true');
});

test('built Waits chart ignores secondary mouse input but commits a primary drag to the shared URL', async ({ page }) => {
  await open(page, 'waits');
  const chart = page.getByRole('img', { name: /^Server wait time by category/ });
  await expect(chart).toBeVisible(); await chart.scrollIntoViewIfNeeded();
  const bounds = await chart.boundingBox(), start = bounds.x + bounds.width * .25, end = bounds.x + bounds.width * .75, y = bounds.y + bounds.height * .5;
  const saved = new URL(page.url()).hash;
  for (const button of ['right', 'middle']) {
    await page.mouse.move(start, y); await page.mouse.down({ button }); await page.mouse.move(end, y); await page.mouse.up({ button });
    expect(new URL(page.url()).hash).toBe(saved);
  }
  await page.mouse.move(start, y); await page.mouse.down(); await page.mouse.move(end, y); await page.mouse.up();
  await expect.poll(() => new URL(page.url()).hash).not.toBe(saved);
  const params = new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
  expect(params.get('target')).toBe(target); expect(params.get('range')).toBe('custom');
  expect(Date.parse(params.get('from'))).toBeGreaterThan(Date.parse(from)); expect(Date.parse(params.get('to'))).toBeLessThan(Date.parse(to));
});

for (const status of [401, 403]) test(`built Waits HTTP ${status} clears charts and recovers only the explicitly retried source`, async ({ page }) => {
  await open(page, 'waits');
  const chart = page.getByRole('img', { name: /^Server wait time by category/ });
  await expect(chart).toBeVisible(); const saved = new URL(page.url()).hash;
  const trend = '**/activity/waits/trend?*';
  await page.route(trend, route => route.fulfill({ status, body: 'PRIVATE_WAITS_AUTH' }));
  await refresh(page).click();
  await expect(page.getByRole('alert').filter({ hasText: 'Previously received protected evidence was removed' })).toBeVisible();
  await expect(chart).toHaveCount(0); await expect(page.locator('body')).not.toContainText('PRIVATE_WAITS_AUTH');
  await page.unroute(trend); await refresh(page).click(); await expect(refresh(page)).toBeEnabled(); await expect(chart).toHaveCount(0);
  await page.getByRole('button', { name: 'Retry wait trend', exact: true }).click(); await expect(chart).toBeVisible();
  await expect(page.getByRole('button', { name: 'Retry wait history', exact: true })).toBeVisible();
  expect(new URL(page.url()).hash).toBe(saved);
});

for (const width of [320, 390, 768, 1440]) test(`denied workspace is dark, responsive and keyboard accessible at ${width}px`, async ({ page }) => {
  await page.setViewportSize({ width, height: 960 }); await open(page);
  await page.route(catalog, route => route.fulfill({ status: 403, body: '{}' }));
  await refresh(page).click(); await denied(page);
  await refresh(page).focus(); await page.keyboard.press('Tab'); await page.keyboard.press('Shift+Tab');
  await expect(refresh(page)).toBeFocused();
  const style = await refresh(page).evaluate(element => ({ outline: getComputedStyle(element).outlineStyle, width: getComputedStyle(element).outlineWidth }));
  expect(style.outline).not.toBe('none'); expect(parseFloat(style.width)).toBeGreaterThanOrEqual(2);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-access-navigation/screenshots/', import.meta.url));
  await mkdir(directory, { recursive: true }); await page.screenshot({ path: `${directory}/workspace-access-${width}.png`, fullPage: true });
});
