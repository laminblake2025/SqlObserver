import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const origin = 'http://127.0.0.1:4328';
const alpha = '11111111-1111-4111-8111-000000000001';
const beta = '11111111-1111-4111-8111-000000000002';
const contexts = new WeakMap();
const panel = page => page.getByRole('region', { name: 'Server settings', exact: true });
const action = (page, name) => panel(page).getByRole('button', { name, exact: true });
const field = (page, name) => panel(page).getByRole('textbox', { name, exact: true });
const callbacks = page => page.getByRole('status', { name: 'Settings callbacks', exact: true });
function nativeTarget(id = alpha, overrides = {}) {
  return { instanceId: id, instanceKey: id === alpha ? 'synthetic-alpha' : 'synthetic-beta',
    displayName: id === alpha ? 'SYNTHETIC Alpha' : 'SYNTHETIC Beta', host: 'sql.example.test', tcpPort: 1433,
    authenticationMode: 'windows_integrated_service_identity', encryptionMode: 'mandatory_validated',
    lifecycle: 'active', capabilityStatus: 'supported', capabilityReasons: [], configurationRevision: 1,
    discoveryRequestedAtUtc: '2026-10-02T12:00:00Z', lastDiscoveryAtUtc: '2026-10-02T12:01:00Z', ...overrides };
}
async function json(route, value, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
}
function applied(context, request) {
  const current = context.targets.get(request.id);
  const next = { ...current, configurationRevision: request.body.expectedRevision + 1 };
  if (request.action === 'retire') {
    delete next.lastDiscoveryAtUtc;
    Object.assign(next, { lifecycle: 'retired', capabilityStatus: 'retired', capabilityReasons: [] });
  }
  else {
    delete next.lastDiscoveryAtUtc;
    Object.assign(next, { lifecycle: 'pending_discovery', capabilityStatus: 'pending', capabilityReasons: ['discovery_pending'],
      discoveryRequestedAtUtc: '2026-10-02T12:02:00Z' });
    if (request.action === 'update') {
      delete next.namedInstance; delete next.tcpPort; delete next.certificateHostName;
      const { expectedRevision, ...settings } = request.body; Object.assign(next, settings);
    }
  }
  context.targets.set(request.id, next); return next;
}
function heldMutation() {
  let start, release, complete;
  const started = new Promise(resolve => { start = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  const finished = new Promise(resolve => { complete = resolve; });
  return { started, async finish() { release(); await finished; }, async handle(route, request, context) {
    if (request.action === 'read') return false;
    start(route.request()); await gate;
    try { await json(route, applied(context, request)); }
    catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
    finally { complete(); }
    return true;
  } };
}
test.beforeEach(async ({ page }) => {
  const context = { targets: new Map([[alpha, nativeTarget()], [beta, nativeTarget(beta)]]), reads: [], mutations: [], unexpected: [], handler: undefined };
  contexts.set(page, context);
  page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async () => { context.unexpected.push(`${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/.test(url.pathname)) return route.continue();
    const match = /^\/api\/v1\/observation-targets\/([^/]+)(?:\/(rediscovery|retire))?$/.exec(url.pathname);
    if (!match || !context.targets.has(match[1]) || url.search) return reject();
    const verb = request.method();
    const action = !match[2] && verb === 'GET' ? 'read' : !match[2] && verb === 'PUT' ? 'update'
      : match[2] && verb === 'POST' ? match[2] : undefined;
    if (!action) return reject();
    const item = { id: match[1], action, ...(action === 'read' ? {} : { body: request.postDataJSON() }) };
    (action === 'read' ? context.reads : context.mutations).push(item);
    if (context.handler && await context.handler(route, item, context)) return;
    return json(route, action === 'read' ? context.targets.get(item.id) : applied(context, item));
  });
});
test.afterEach(async ({ page }) => {
  expect(contexts.get(page).unexpected, 'No API proxy, external traffic or runtime errors').toEqual([]);
});
async function mount(page) {
  await page.goto('/component-tests/target-management.html');
  await expect(page.getByRole('heading', { name: 'Server settings panel harness', exact: true })).toBeVisible();
  await expect(field(page, 'Display name')).toHaveValue('SYNTHETIC Alpha');
}
async function saveName(page, name = 'SYNTHETIC updated Alpha') {
  await field(page, 'Display name').fill(name); await action(page, 'Save settings').click();
}
async function expectLocked(page) {
  await expect(action(page, 'Save settings')).toBeDisabled();
  await expect(action(page, 'Request rediscovery')).toBeDisabled();
  await expect(action(page, 'Retire server')).toBeDisabled();
  await expect(panel(page)).toContainText(/Reload server settings before (?:making )?another change\./);
}

test('loads current native settings independently of Rewind and renders provider strings as inert text', async ({ page }) => {
  await mount(page);
  const context = contexts.get(page);
  const inert = '<img src="https://invalid.example/target" onerror="window.targetInjected=true">';
  context.targets.set(alpha, nativeTarget(alpha, { displayName: inert, capabilityReasons: ['<script>window.targetInjected=true</script>'] }));
  await action(page, 'Reload server settings').click();
  await expect(field(page, 'Display name')).toHaveValue(inert);
  await expect(panel(page)).toContainText(/current|latest/i);
  await expect(panel(page)).toContainText(/Windows service identity/i);
  await expect(panel(page)).toContainText('10/02/2026');
  expect(context.reads.every(item => item.id === alpha)).toBe(true);
  await expect(panel(page).locator('img, script')).toHaveCount(0);
  expect(await page.evaluate(() => window.targetInjected)).toBeUndefined();
  expect(context.mutations).toHaveLength(0);
});

test('submits mutually exclusive native TCP and named-instance settings with each verified next revision', async ({ page }) => {
  await mount(page);
  await field(page, 'Display name').fill('SYNTHETIC updated Alpha');
  await field(page, 'Windows host').fill('updated.example.test');
  await panel(page).getByRole('spinbutton', { name: 'Port', exact: true }).fill('1444');
  await field(page, 'Certificate host name (optional)').fill('certificate.example.test');
  await action(page, 'Save settings').focus(); await page.keyboard.press('Enter');
  await expect(callbacks(page)).toHaveText('1');
  expect(contexts.get(page).mutations[0]).toEqual({ id: alpha, action: 'update', body: {
    expectedRevision: 1, displayName: 'SYNTHETIC updated Alpha', host: 'updated.example.test', tcpPort: 1444,
    certificateHostName: 'certificate.example.test' } });
  await expect(panel(page)).toContainText('Server settings saved; capability discovery is pending.');
  await panel(page).getByRole('radio', { name: 'Named instance', exact: true }).check();
  await field(page, 'Instance name').fill('APP');
  await field(page, 'Certificate host name (optional)').fill('');
  await action(page, 'Save settings').click(); await expect(callbacks(page)).toHaveText('2');
  expect(contexts.get(page).mutations[1].body).toEqual({ expectedRevision: 2, displayName: 'SYNTHETIC updated Alpha',
    host: 'updated.example.test', namedInstance: 'APP' });
  await expect(page.getByRole('status', { name: 'Changed revision', exact: true })).toHaveText('3');
  await expect(panel(page).getByRole('spinbutton', { name: 'Port', exact: true })).toHaveCount(0);
});

test('rediscovery preserves an unsaved draft and retirement requires the loaded display name before revision-checked POST', async ({ page }) => {
  await mount(page); await field(page, 'Display name').fill('Unsaved draft');
  await action(page, 'Request rediscovery').click(); await expect(callbacks(page)).toHaveText('1');
  expect(contexts.get(page).mutations[0]).toEqual({ id: alpha, action: 'rediscovery', body: { expectedRevision: 1 } });
  await expect(field(page, 'Display name')).toHaveValue('Unsaved draft');
  await expect(panel(page)).toContainText('Capability rediscovery requested.');
  const confirmation = field(page, 'Type server display name to retire');
  await confirmation.fill('Unsaved draft');
  await expect(action(page, 'Retire server')).toBeDisabled();
  expect(contexts.get(page).mutations).toHaveLength(1);
  await confirmation.fill('SYNTHETIC Alpha '); await expect(action(page, 'Retire server')).toBeDisabled();
  await confirmation.fill('SYNTHETIC Alpha'); await action(page, 'Retire server').click();
  await expect(callbacks(page)).toHaveText('2');
  expect(contexts.get(page).mutations[1]).toEqual({ id: alpha, action: 'retire', body: { expectedRevision: 2 } });
  await expect(panel(page)).toContainText('Server retired. Collection is stopped.');
  await expect(action(page, 'Save settings')).toBeDisabled();
  await expect(action(page, 'Request rediscovery')).toBeDisabled();
});

test('reader, wrong-server, pending and inactive access never unlock commands and exact server administration does', async ({ page }) => {
  await mount(page);
  const access = page.getByRole('combobox', { name: 'Access scenario', exact: true });
  for (const role of ['reader', 'mixed', 'unverified', 'inactive']) {
    await access.selectOption(role);
    await expect(field(page, 'Display name')).toBeDisabled();
    for (const name of ['Save settings', 'Request rediscovery', 'Retire server']) await expect(action(page, name)).toHaveCount(0);
    await expect(panel(page)).toContainText('Target Administrator access on this server unlocks');
  }
  expect(contexts.get(page).mutations).toHaveLength(0);
  await access.selectOption('administrator'); await expect(action(page, 'Save settings')).toBeEnabled();
  await page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption(beta);
  await expect(field(page, 'Display name')).toHaveValue('SYNTHETIC Beta');
  await action(page, 'Request rediscovery').click(); await expect(callbacks(page)).toHaveText('1');
  expect(contexts.get(page).mutations[0]).toEqual({ id: beta, action: 'rediscovery', body: { expectedRevision: 1 } });
});

test('pending form activation dispatches once and role loss aborts the receipt and requires readback after restoration', async ({ page }) => {
  const held = heldMutation(); contexts.get(page).handler = held.handle;
  await mount(page); await saveName(page);
  const request = await held.started;
  await expect(action(page, 'Saving settings…')).toBeDisabled();
  await page.keyboard.press('Enter'); await page.keyboard.press('Enter');
  expect(contexts.get(page).mutations).toHaveLength(1);
  const aborted = page.waitForEvent('requestfailed', { predicate: candidate => candidate === request });
  await page.getByRole('combobox', { name: 'Access scenario', exact: true }).selectOption('reader');
  await aborted; await held.finish();
  await expect(callbacks(page)).toHaveText('0');
  await page.getByRole('combobox', { name: 'Access scenario', exact: true }).selectOption('administrator');
  await expectLocked(page);
  contexts.get(page).handler = undefined;
  await action(page, 'Reload server settings').click();
  await expect(action(page, 'Save settings')).toBeEnabled();
  await expect(field(page, 'Display name')).toHaveValue('SYNTHETIC updated Alpha');
  expect(contexts.get(page).mutations).toHaveLength(1);
});

test('conflict refuses blind replay and explicit Reload adopts the current revision before a new change', async ({ page }) => {
  const context = contexts.get(page); let conflict = true;
  context.handler = async (route, request) => {
    if (request.action === 'read' || !conflict) return false;
    context.targets.set(alpha, nativeTarget(alpha, { configurationRevision: 4, displayName: 'SYNTHETIC concurrent edit' }));
    conflict = false; await json(route, { detail: 'SECRET_CONFLICT <script>window.targetInjected=true</script>' }, 409); return true;
  };
  await mount(page); await saveName(page); await expectLocked(page); await expect(callbacks(page)).toHaveText('0');
  await expect(panel(page)).not.toContainText('SECRET_CONFLICT');
  expect(context.mutations).toHaveLength(1);
  await action(page, 'Reload server settings').click();
  await expect(field(page, 'Display name')).toHaveValue('SYNTHETIC concurrent edit');
  await saveName(page, 'SYNTHETIC after readback'); await expect(callbacks(page)).toHaveText('1');
  expect(context.mutations[1].body.expectedRevision).toBe(4);
});

for (const receipt of ['unavailable', 'foreign']) test(`${receipt} mutation receipt cannot infer success or retry until explicit authoritative readback`, async ({ page }) => {
  const context = contexts.get(page); let first = true;
  context.handler = async (route, request) => {
    if (request.action === 'read' || !first) return false;
    first = false; const accepted = applied(context, request);
    await json(route, receipt === 'foreign' ? { ...accepted, instanceId: beta } : { detail: 'SECRET_UNKNOWN <img src=https://invalid.example>' }, receipt === 'foreign' ? 200 : 503);
    return true;
  };
  await mount(page); await saveName(page); await expectLocked(page);
  await expect(callbacks(page)).toHaveText('0');
  await expect(panel(page)).not.toContainText('SECRET_UNKNOWN');
  expect(context.mutations).toHaveLength(1);
  await action(page, 'Reload server settings').click();
  await expect(field(page, 'Display name')).toHaveValue('SYNTHETIC updated Alpha');
  await saveName(page, 'SYNTHETIC verified successor'); await expect(callbacks(page)).toHaveText('1');
  expect(context.mutations[1].body.expectedRevision).toBe(2);
});

test('a definite administrative throttle keeps the draft and permits a same-revision retry', async ({ page }) => {
  const context = contexts.get(page); let throttle = true;
  context.handler = async (route, request) => {
    if (request.action === 'read' || !throttle) return false;
    throttle = false; await json(route, { detail: 'SECRET_RATE_LIMIT' }, 429); return true;
  };
  await mount(page); await saveName(page);
  await expect(panel(page)).toContainText(/Too many administrative requests/i);
  await expect(action(page, 'Save settings')).toBeEnabled();
  await expect(field(page, 'Display name')).toHaveValue('SYNTHETIC updated Alpha');
  await action(page, 'Save settings').click(); await expect(callbacks(page)).toHaveText('1');
  expect(context.mutations[1].body).toEqual(context.mutations[0].body);
  await expect(panel(page)).not.toContainText('SECRET_RATE_LIMIT');
});

test('native permission denial rejects the old access snapshot until fresh verified access and explicit Reload', async ({ page }) => {
  const context = contexts.get(page);
  context.handler = async (route, request) => {
    if (request.action === 'read') return false;
    await json(route, { detail: 'SECRET_ROLE_PROVIDER <script>window.targetInjected=true</script>' }, 403); return true;
  };
  await mount(page); await action(page, 'Request rediscovery').click();
  await expect(callbacks(page)).toHaveText('0');
  await expect(panel(page)).toContainText('Target management requires the Target Administrator role on this server.');
  await expect(panel(page)).not.toContainText('SECRET_ROLE_PROVIDER');
  for (const name of ['Save settings', 'Request rediscovery', 'Retire server']) await expect(action(page, name)).toHaveCount(0);
  expect(context.mutations).toHaveLength(1);
  await action(page, 'Reload server settings').click();
  await expect(panel(page)).toContainText('Latest server settings loaded.');
  await expect(action(page, 'Request rediscovery')).toHaveCount(0);
  await page.getByRole('button', { name: 'Refresh access snapshot', exact: true }).click();
  await expect(action(page, 'Request rediscovery')).toBeDisabled();
  await action(page, 'Reload server settings').click();
  await expect(action(page, 'Request rediscovery')).toBeEnabled();
  expect(context.mutations).toHaveLength(1);
});

for (const change of ['target', 'unmount']) test(`late mutation receipt after ${change} cannot publish a callback, message or another server's draft`, async ({ page }) => {
  const held = heldMutation(); contexts.get(page).handler = held.handle;
  await mount(page); await saveName(page); const request = await held.started;
  const aborted = page.waitForEvent('requestfailed', { predicate: candidate => candidate === request });
  if (change === 'target') await page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption(beta);
  else await page.getByRole('button', { name: 'Unmount settings', exact: true }).click();
  await aborted; await held.finish(); await expect(callbacks(page)).toHaveText('0');
  if (change === 'target') {
    await expect(field(page, 'Display name')).toHaveValue('SYNTHETIC Beta');
    await expect(panel(page)).not.toContainText('Server settings saved;');
  } else await expect(panel(page)).toHaveCount(0);
});

for (const command of ['Save settings', 'Request rediscovery']) test(`AlreadyRetired receipt for ${command} is terminal and never claims that command was applied`, async ({ page }) => {
  const context = contexts.get(page);
  context.handler = async (route, request) => {
    if (request.action === 'read') return false;
    const retired = nativeTarget(alpha, { configurationRevision: 9, lifecycle: 'retired', capabilityStatus: 'retired', displayName: 'SYNTHETIC previously retired', host: 'previous.example.test' });
    delete retired.lastDiscoveryAtUtc;
    context.targets.set(alpha, retired); await json(route, retired); return true;
  };
  await mount(page); await action(page, command).click(); await expect(callbacks(page)).toHaveText('1');
  await expect(panel(page)).toContainText('Server is retired. No settings or discovery changes were applied.');
  await expect(panel(page)).not.toContainText('Server settings saved;');
  await expect(panel(page)).not.toContainText('Capability rediscovery requested.');
  await expect(action(page, 'Save settings')).toBeDisabled(); await expect(action(page, 'Request rediscovery')).toBeDisabled();
  await expect(page.getByRole('status', { name: 'Changed revision', exact: true })).toHaveText('9');
  expect(context.mutations).toHaveLength(1);
});

test('malformed current settings stay unavailable without reflecting the provider and recover only through Reload', async ({ page }) => {
  const context = contexts.get(page); let invalid = true;
  context.handler = async (route, request) => {
    if (request.action !== 'read' || !invalid) return false;
    await json(route, { ...nativeTarget(beta), displayName: 'SECRET_PROVIDER <script>window.targetInjected=true</script>' }); return true;
  };
  await page.goto('/component-tests/target-management.html');
  await expect(panel(page).getByRole('alert')).toBeVisible();
  await expect(panel(page)).not.toContainText('SECRET_PROVIDER');
  expect(context.mutations).toHaveLength(0);
  invalid = false; await action(page, 'Reload server settings').click();
  await expect(field(page, 'Display name')).toHaveValue('SYNTHETIC Alpha');
  await expect(action(page, 'Save settings')).toBeEnabled();
});

async function audit(page, info, name) {
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`${name}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
  expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-server-management/screenshots', import.meta.url));
  await mkdir(directory, { recursive: true });
  await page.evaluate(() => window.scrollTo(0, 0));
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
  await page.screenshot({ path: `${directory}/${name}.png`, fullPage: true });
}
for (const width of [320, 390, 768, 1440]) test(`server settings valid, typed confirmation and uncertain recovery remain accessible at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 900 }); await mount(page);
  const display = field(page, 'Display name'); await display.focus(); await page.keyboard.press('Tab');
  await expect(field(page, 'Windows host')).toBeFocused();
  const focus = await field(page, 'Windows host').evaluate(element => ({ width: getComputedStyle(element).outlineWidth, style: getComputedStyle(element).outlineStyle }));
  expect(focus.style).not.toBe('none'); expect(parseFloat(focus.width)).toBeGreaterThanOrEqual(2);
  for (const control of [display, field(page, 'Windows host')]) {
    const box = await control.boundingBox(); expect(box).not.toBeNull();
    expect(box.width).toBeGreaterThanOrEqual(150); expect(box.height).toBeGreaterThanOrEqual(36);
    expect(box.x).toBeGreaterThanOrEqual(0); expect(box.x + box.width).toBeLessThanOrEqual(width + 1);
  }
  await audit(page, info, `settings-mounted-${width}`);
  await field(page, 'Type server display name to retire').fill('SYNTHETIC Alpha');
  await expect(action(page, 'Retire server')).toBeEnabled();
  await audit(page, info, `settings-confirmation-${width}`);
  await action(page, 'Reload server settings').click();
  await expect(field(page, 'Type server display name to retire')).toHaveValue('');
  contexts.get(page).handler = async (route, request) => {
    if (request.action === 'read') return false;
    await json(route, { detail: 'SECRET_UNCERTAIN' }, 503); return true;
  };
  await saveName(page); await expectLocked(page);
  await action(page, 'Reload server settings').focus(); await expect(action(page, 'Reload server settings')).toBeFocused();
  await audit(page, info, `settings-unknown-${width}`);
});
