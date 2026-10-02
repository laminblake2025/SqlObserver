import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';

const origin = 'http://127.0.0.1:4332';
const alpha = '11111111-1111-4111-8111-000000000001';
const beta = '11111111-1111-4111-8111-000000000002';
const offloaded = 'a3333333-b333-4c33-8d33-e00000000003';
const from = '2026-09-26T08:00:00.123456Z', to = '2026-09-26T12:00:00.654321Z';
const contexts = new WeakMap();
const panel = page => page.getByRole('region', { name: 'Server settings', exact: true });
const registrations = page => page.getByRole('region', { name: 'Loaded server registrations', exact: true });
const action = (page, name) => panel(page).getByRole('button', { name, exact: true });
const field = (page, name) => panel(page).getByRole('textbox', { name, exact: true });
const params = page => new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
const href = (id = alpha, open = true) => `/#/servers?${new URLSearchParams({ target: id, range: 'custom', from, to, databaseId: '5', compare: '1', ...(open ? { serverSettings: '1' } : {}) })}`;
function nativeTarget(id = alpha, overrides = {}) {
  return { instanceId: id, instanceKey: id === alpha ? 'synthetic-alpha' : id === beta ? 'synthetic-beta' : 'synthetic-offloaded',
    displayName: id === alpha ? 'SYNTHETIC Alpha' : id === beta ? 'SYNTHETIC Beta' : 'SYNTHETIC offloaded server',
    host: 'sql.example.test', tcpPort: 1433, authenticationMode: 'windows_integrated_service_identity',
    encryptionMode: 'mandatory_validated', lifecycle: 'active', capabilityStatus: 'supported', capabilityReasons: [],
    configurationRevision: 1, discoveryRequestedAtUtc: '2026-10-02T12:00:00Z', lastDiscoveryAtUtc: '2026-10-02T12:01:00Z', ...overrides };
}
async function json(route, value, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
}
function accepted(context, request) {
  const next = { ...context.targets.get(request.id), configurationRevision: request.body.expectedRevision + 1 };
  if (request.action === 'retire') {
    delete next.lastDiscoveryAtUtc;
    Object.assign(next, { lifecycle: 'retired', capabilityStatus: 'retired', capabilityReasons: [] });
  }
  else {
    delete next.lastDiscoveryAtUtc;
    Object.assign(next, { lifecycle: 'pending_discovery', capabilityStatus: 'pending', capabilityReasons: ['discovery_pending'], discoveryRequestedAtUtc: '2026-10-02T12:02:00Z' });
    if (request.action === 'update') {
      delete next.tcpPort; delete next.namedInstance; delete next.certificateHostName;
      const { expectedRevision, ...settings } = request.body; Object.assign(next, settings);
    }
  }
  context.targets.set(request.id, next); return next;
}
test.beforeEach(async ({ page }) => {
  const context = { targets: new Map([alpha, beta, offloaded].map(id => [id, nativeTarget(id)])),
    catalog: [alpha, beta], reads: [], accessReads: [], mutations: [], unexpected: [], role: 'administrator', handler: undefined };
  contexts.set(page, context);
  page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin }, { name: 'chartReferenceRole', value: 'viewer', url: origin }]);
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async () => { context.unexpected.push(`${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/.test(url.pathname)) return route.continue();
    if (url.pathname === '/api/v1/me' && request.method() === 'GET') {
      const targetId = url.searchParams.get('targetId');
      const administrator = context.role === 'administrator';
      const grantedRoles = ['Viewer', ...(administrator ? ['TargetAdministrator'] : [])];
      const access = { active: true, grantedRoles, allTargetRoles: [], targetId,
        targetRoles: targetId ? grantedRoles : [] };
      context.accessReads.push(access); return json(route, access);
    }
    if (url.pathname === '/api/v1/observation-targets' && request.method() === 'GET') {
      return json(route, { items: context.catalog.map(id => context.targets.get(id)).filter(item => item.lifecycle !== 'retired'), nextCursor: null });
    }
    const match = /^\/api\/v1\/observation-targets\/([^/]+)(?:\/(rediscovery|retire))?$/.exec(url.pathname);
    if (match && context.targets.has(match[1]) && !url.search) {
      const verb = request.method();
      const action = !match[2] && verb === 'GET' ? 'read' : !match[2] && verb === 'PUT' ? 'update'
        : match[2] && verb === 'POST' ? match[2] : undefined;
      if (!action) return reject();
      const item = { id: match[1], action, ...(action === 'read' ? {} : { body: request.postDataJSON() }) };
      (action === 'read' ? context.reads : context.mutations).push(item);
      if (context.handler && await context.handler(route, item, context)) return;
      return json(route, action === 'read' ? context.targets.get(item.id) : accepted(context, item));
    }
    // Remaining GETs use the existing synthetic-only dashboard fixture server,
    // which has no backend proxy. Every write must match a registered target.
    if (request.method() === 'GET') return route.continue();
    return reject();
  });
});
test.afterEach(async ({ page }) => {
  expect(contexts.get(page).unexpected, 'No external traffic, unregistered mutations or runtime errors').toEqual([]);
});
async function assertScope(page, id = alpha) {
  await expect.poll(() => Object.fromEntries(params(page))).toMatchObject({ target: id, range: 'custom', from, to, databaseId: '5', compare: '1' });
}
async function open(page, id = alpha) {
  await page.goto(href(id)); await expect(field(page, 'Display name')).toHaveValue(nativeTarget(id).displayName); await assertScope(page, id);
  if (contexts.get(page).role === 'administrator') await expect(action(page, 'Save settings')).toBeEnabled();
}

test('per-row settings links preserve shared scope through reload, close and history without implicit mutations', async ({ page }) => {
  await page.goto(href(alpha, false));
  const link = registrations(page).getByRole('link', { name: 'Server settings for SYNTHETIC Alpha', exact: true });
  await expect(link).toBeVisible();
  const destination = new URLSearchParams((await link.getAttribute('href')).split('?')[1]);
  expect(Object.fromEntries(destination)).toMatchObject({ target: alpha, serverSettings: '1', range: 'custom', from, to, databaseId: '5', compare: '1' });
  await link.click(); await expect(field(page, 'Display name')).toHaveValue('SYNTHETIC Alpha'); await assertScope(page);
  await page.reload(); await expect(field(page, 'Display name')).toHaveValue('SYNTHETIC Alpha');
  expect(contexts.get(page).mutations).toHaveLength(0);
  await action(page, 'Close').click(); await expect(panel(page)).toHaveCount(0); await assertScope(page);
  expect(params(page).has('serverSettings')).toBe(false);
  await page.goBack(); await expect(field(page, 'Display name')).toHaveValue('SYNTHETIC Alpha'); await assertScope(page);
  await page.goForward(); await expect(panel(page)).toHaveCount(0);
  expect(contexts.get(page).mutations).toHaveLength(0);
});

test('same-server database edits, display toggles and verified same-role refresh preserve the unsaved settings draft', async ({ page }) => {
  await open(page);
  const context = contexts.get(page), settingsReads = context.reads.length, accessReads = context.accessReads.length;
  await field(page, 'Display name').fill('SYNTHETIC unsaved workspace draft');
  await field(page, 'Windows host').fill('unsaved.example.test');
  const database = page.getByRole('combobox', { name: 'Workspace database', exact: true });
  await database.selectOption('');
  await expect.poll(() => Object.fromEntries(params(page))).toEqual({ target: alpha, range: 'custom', from, to,
    compare: '1', serverSettings: '1' });
  await expect(field(page, 'Display name')).toHaveValue('SYNTHETIC unsaved workspace draft');
  await expect(field(page, 'Windows host')).toHaveValue('unsaved.example.test');
  await database.selectOption('5'); await assertScope(page);
  expect(params(page).get('serverSettings')).toBe('1');
  await page.getByRole('button', { name: 'Show local time', exact: true }).click();
  await page.getByRole('button', { name: 'Show UTC time', exact: true }).click();
  await expect(field(page, 'Display name')).toHaveValue('SYNTHETIC unsaved workspace draft');
  await expect(field(page, 'Windows host')).toHaveValue('unsaved.example.test');
  const refresh = page.locator('.page-actions').getByRole('button', { name: '↻ Refresh', exact: true });
  await refresh.click();
  await expect.poll(() => context.accessReads.length).toBeGreaterThan(accessReads);
  await expect(refresh).toBeEnabled(); await expect(action(page, 'Save settings')).toBeEnabled();
  expect(context.accessReads.at(-1)).toMatchObject({ active: true, targetId: alpha, targetRoles: ['Viewer', 'TargetAdministrator'] });
  await expect(field(page, 'Display name')).toHaveValue('SYNTHETIC unsaved workspace draft');
  await expect(field(page, 'Windows host')).toHaveValue('unsaved.example.test');
  await assertScope(page); expect(params(page).get('serverSettings')).toBe('1');
  expect(context.reads).toHaveLength(settingsReads); expect(context.mutations).toHaveLength(0);
});

test('verified native update and rediscovery replace the loaded row, then retirement removes only that target', async ({ page }) => {
  await open(page);
  await field(page, 'Display name').fill('SYNTHETIC renamed Alpha'); await field(page, 'Windows host').fill('renamed.example.test');
  await panel(page).getByRole('radio', { name: 'Named instance', exact: true }).check();
  await field(page, 'Instance name').fill('REPORTING');
  await action(page, 'Save settings').focus(); await page.keyboard.press('Enter');
  await expect(registrations(page).getByRole('link', { name: 'SYNTHETIC renamed Alpha', exact: true })).toHaveCount(1);
  await expect(registrations(page).getByRole('link', { name: 'SYNTHETIC Alpha', exact: true })).toHaveCount(0);
  expect(contexts.get(page).mutations[0]).toEqual({ id: alpha, action: 'update', body: {
    expectedRevision: 1, displayName: 'SYNTHETIC renamed Alpha', host: 'renamed.example.test', namedInstance: 'REPORTING' } });
  await assertScope(page);
  await action(page, 'Request rediscovery').click();
  await expect(panel(page)).toContainText('Capability rediscovery requested.');
  expect(contexts.get(page).mutations[1]).toEqual({ id: alpha, action: 'rediscovery', body: { expectedRevision: 2 } });
  await field(page, 'Type server display name to retire').fill('SYNTHETIC renamed Alpha');
  await action(page, 'Retire server').click();
  await expect(panel(page)).toContainText('Server retired. Collection is stopped.');
  expect(contexts.get(page).mutations[2]).toEqual({ id: alpha, action: 'retire', body: { expectedRevision: 3 } });
  await expect(registrations(page).getByRole('link', { name: 'SYNTHETIC renamed Alpha', exact: true })).toHaveCount(0);
  await expect(registrations(page).getByRole('link', { name: 'SYNTHETIC Beta', exact: true })).toHaveCount(1);
  await expect(page.locator('.kpi').filter({ hasText: 'Registered targets' }).locator('strong')).toHaveText('1');
  await assertScope(page); await action(page, 'Close').click(); await expect(panel(page)).toHaveCount(0); await assertScope(page);
  expect(contexts.get(page).mutations).toHaveLength(3);
});

test('a settings URL for a target outside the loaded page uses native direct lookup without injecting a registration row', async ({ page }) => {
  await open(page, offloaded);
  await expect(registrations(page).getByRole('link', { name: 'SYNTHETIC offloaded server', exact: true })).toHaveCount(0);
  await field(page, 'Display name').fill('SYNTHETIC updated offloaded server');
  await action(page, 'Save settings').click();
  await expect(panel(page)).toContainText('Server settings saved; capability discovery is pending.');
  await expect(registrations(page).getByRole('link', { name: 'SYNTHETIC updated offloaded server', exact: true })).toHaveCount(0);
  await expect(registrations(page).getByRole('link', { name: 'SYNTHETIC Alpha', exact: true })).toHaveCount(1);
  await expect(registrations(page).getByRole('link', { name: 'SYNTHETIC Beta', exact: true })).toHaveCount(1);
  await expect(page.locator('.kpi').filter({ hasText: 'Registered targets' }).locator('strong')).toHaveText('2');
  await assertScope(page, offloaded); await page.reload();
  await expect(field(page, 'Display name')).toHaveValue('SYNTHETIC updated offloaded server');
  await assertScope(page, offloaded);
  expect(contexts.get(page).reads.some(request => request.id === offloaded)).toBe(true);
  expect(contexts.get(page).mutations).toHaveLength(1);
});

test('an uncertain native settings receipt cannot update the loaded list and readback does not claim an applied callback', async ({ page }) => {
  const context = contexts.get(page); let first = true;
  context.handler = async (route, request) => {
    if (request.action === 'read' || !first) return false;
    first = false; const changed = accepted(context, request);
    await json(route, { ...changed, instanceId: beta, displayName: 'SECRET_FOREIGN <script>window.targetInjected=true</script>' }); return true;
  };
  await open(page); await field(page, 'Display name').fill('SYNTHETIC accepted but unverified');
  await action(page, 'Save settings').click();
  await expect(panel(page)).toContainText('Reload server settings before another change.');
  await expect(action(page, 'Save settings')).toBeDisabled();
  await expect(registrations(page).getByRole('link', { name: 'SYNTHETIC Alpha', exact: true })).toHaveCount(1);
  await expect(registrations(page).getByRole('link', { name: 'SYNTHETIC accepted but unverified', exact: true })).toHaveCount(0);
  await expect(page.locator('body')).not.toContainText('SECRET_FOREIGN');
  await action(page, 'Reload server settings').click();
  await expect(field(page, 'Display name')).toHaveValue('SYNTHETIC accepted but unverified');
  await expect(registrations(page).getByRole('link', { name: 'SYNTHETIC Alpha', exact: true })).toHaveCount(1);
  expect(context.mutations).toHaveLength(1); await assertScope(page);
  await action(page, 'Save settings').click();
  await expect(registrations(page).getByRole('link', { name: 'SYNTHETIC accepted but unverified', exact: true })).toHaveCount(1);
  expect(context.mutations[1].body.expectedRevision).toBe(2);
});

test('readers may open current server settings from the loaded list while mutations remain unavailable', async ({ page }) => {
  contexts.get(page).role = 'reader'; await open(page);
  await expect(field(page, 'Display name')).toBeDisabled();
  await expect(panel(page)).toContainText('Target Administrator access on this server unlocks');
  for (const name of ['Save settings', 'Request rediscovery', 'Retire server']) await expect(action(page, name)).toHaveCount(0);
  await page.reload(); await expect(field(page, 'Display name')).toBeDisabled();
  await action(page, 'Close').click(); await assertScope(page);
  expect(contexts.get(page).mutations).toHaveLength(0);
});

test('invalid and duplicate server settings URL identities refuse native detail reads until a loaded row repairs the selection', async ({ page }) => {
  for (const invalid of [href(alpha).replace('serverSettings=1', 'serverSettings=unknown'),
    `${href(alpha)}&target=${beta}`, `${href(alpha)}&serverSettings=1`, href(alpha).replace(alpha, 'not-a-guid')]) {
    await page.goto(invalid);
    await expect(panel(page).getByRole('alert')).toHaveText('The server settings selection in this URL is invalid. Open settings from the server list.');
    await expect(panel(page).getByRole('form')).toHaveCount(0);
    expect(contexts.get(page).reads).toHaveLength(0); expect(contexts.get(page).mutations).toHaveLength(0);
  }
  await registrations(page).getByRole('link', { name: 'Server settings for SYNTHETIC Alpha', exact: true }).click();
  await expect(field(page, 'Display name')).toHaveValue('SYNTHETIC Alpha');
  expect(params(page).getAll('target')).toEqual([alpha]); expect(params(page).getAll('serverSettings')).toEqual(['1']);
  expect(contexts.get(page).mutations).toHaveLength(0);
});

for (const width of [320, 390, 768, 1440]) test(`server settings workspace preserves dark accessibility and readable controls at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 900 }); await open(page);
  await field(page, 'Display name').focus(); await page.keyboard.press('Tab'); await expect(field(page, 'Windows host')).toBeFocused();
  const focused = await field(page, 'Windows host').evaluate(element => ({ width: getComputedStyle(element).outlineWidth, style: getComputedStyle(element).outlineStyle }));
  expect(focused.style).not.toBe('none'); expect(parseFloat(focused.width)).toBeGreaterThanOrEqual(2);
  for (const control of [field(page, 'Display name'), field(page, 'Windows host')]) {
    const box = await control.boundingBox(); expect(box).not.toBeNull();
    expect(box.width).toBeGreaterThanOrEqual(150); expect(box.height).toBeGreaterThanOrEqual(36);
    expect(box.x).toBeGreaterThanOrEqual(0); expect(box.x + box.width).toBeLessThanOrEqual(width + 1);
  }
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`server-settings-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
  expect(violations).toEqual([]);
  const directory = resolve('../artifacts/dashboard-server-management/screenshots'); await mkdir(directory, { recursive: true });
  await page.evaluate(() => window.scrollTo(0, 0));
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
  await page.screenshot({ path: resolve(directory, `settings-workspace-${width}.png`), fullPage: true });
  await assertScope(page); expect(contexts.get(page).mutations).toHaveLength(0);
});
