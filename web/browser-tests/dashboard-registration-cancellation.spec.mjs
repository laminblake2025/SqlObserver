import { test, expect } from '@playwright/test';

const origin = 'http://127.0.0.1:4336';
const alpha = '11111111-1111-4111-8111-000000000001';
const contexts = new WeakMap();
const drawer = page => page.getByRole('dialog', { name: 'Add server', exact: true });
const target = (id = alpha, overrides = {}) => ({ instanceId: id, instanceKey: 'synthetic-original',
  displayName: 'SYNTHETIC original server', host: 'original.invalid', tcpPort: 1433,
  authenticationMode: 'windows_integrated_service_identity', encryptionMode: 'mandatory_validated',
  lifecycle: 'active', capabilityStatus: 'supported', capabilityReasons: [], configurationRevision: 1,
  discoveryRequestedAtUtc: '2026-10-02T12:00:00Z', lastDiscoveryAtUtc: '2026-10-02T12:01:00Z', ...overrides });
const json = (route, body) => route.fulfill({ status: 200, contentType: 'application/json',
  headers: { 'cache-control': 'no-store' }, body: JSON.stringify(body) });

test.beforeEach(async ({ page }) => {
  const context = { admin: true, accessReads: 0, posts: [], aborted: [], unexpected: [], hold: undefined };
  contexts.set(page, context);
  page.on('pageerror', error => context.unexpected.push(error.message));
  page.on('console', message => { if (message.type() === 'error') context.unexpected.push(message.text()); });
  page.on('requestfailed', request => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/v1/observation-targets') context.aborted.push(request.url());
  });
  await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin }]);
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = () => { context.unexpected.push(`${request.method()} ${url.href}`); return route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/u.test(url.pathname)) return route.continue();
    if (request.method() === 'GET' && url.pathname === '/api/v1/me') {
      context.accessReads++;
      const targetId = url.searchParams.get('targetId');
      const roles = context.admin ? ['Viewer', 'TargetAdministrator'] : ['Viewer'];
      return json(route, { active: true, grantedRoles: roles, allTargetRoles: context.admin ? ['TargetAdministrator'] : [],
        targetId, targetRoles: targetId ? roles : [] });
    }
    if (url.pathname === '/api/v1/observation-targets') {
      if (request.method() === 'GET') return json(route, { items: [target()], nextCursor: null });
      if (request.method() === 'POST') {
        const body = request.postDataJSON(); context.posts.push(body);
        if (!context.hold) return reject();
        return context.hold(route, body);
      }
    }
    // Finite supporting source only; the loopback fixture has no backend proxy.
    if (request.method() === 'GET' && url.pathname === '/api/v1/overview') return route.continue();
    if (request.method() === 'GET' && [
      `/api/v1/observation-targets/${alpha}/health`,
      `/api/v1/observation-targets/${alpha}/alerts/active`,
    ].includes(url.pathname)) return route.continue();
    return reject();
  });
});
test.afterEach(async ({ page }) => expect(contexts.get(page).unexpected).toEqual([]));

async function openAndSubmit(page, nearPoll = false) {
  if (nearPoll) await page.clock.install({ time: new Date('2026-10-02T12:02:00Z') });
  await page.goto('/#/servers');
  if (nearPoll) await page.clock.runFor(51_000);
  await page.getByRole('button', { name: '+ Add server', exact: true }).click();
  await drawer(page).getByRole('textbox', { name: 'Stable key', exact: true }).fill('synthetic-cancelled');
  await drawer(page).getByRole('textbox', { name: 'Display name', exact: true }).fill('SYNTHETIC cancelled receipt');
  await drawer(page).getByRole('textbox', { name: 'Windows host', exact: true }).fill('cancelled.invalid');
  const context = contexts.get(page);
  let release, started, done;
  const gate = new Promise(resolve => { release = resolve; });
  const seen = new Promise(resolve => { started = resolve; });
  const delivered = new Promise(resolve => { done = resolve; });
  context.hold = async (route, body) => {
    started(); await gate;
    try {
      await json(route, target(body.instanceId, { ...body, lifecycle: 'pending_discovery', capabilityStatus: 'pending',
        capabilityReasons: ['discovery_pending'], lastDiscoveryAtUtc: null }));
    } catch (error) {
      if (!context.aborted.length) throw error;
    } finally { done(); }
  };
  await drawer(page).getByRole('button', { name: 'Register target', exact: true }).click();
  await seen;
  await expect(drawer(page).getByRole('button', { name: 'Registering…', exact: true })).toBeDisabled();
  return { release, delivered };
}

for (const close of ['button', 'Escape']) test(`closing Add server by ${close} cancels its pending consumer and refuses a late receipt`, async ({ page }) => {
  const held = await openAndSubmit(page), context = contexts.get(page);
  try {
    if (close === 'button') await drawer(page).getByRole('button', { name: 'Close Add server', exact: true }).click();
    else await page.keyboard.press('Escape');
    await expect(drawer(page)).not.toBeVisible();
    await expect.poll(() => context.aborted.length).toBe(1);
  } finally { held.release(); }
  await held.delivered;
  await expect(page.getByRole('link', { name: 'SYNTHETIC cancelled receipt', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: '+ Add server', exact: true }).click();
  await expect(drawer(page).getByRole('textbox', { name: 'Stable key', exact: true })).toHaveValue('');
  await expect(drawer(page).getByRole('button', { name: 'Register target', exact: true })).toBeEnabled();
  expect(context.posts).toHaveLength(1);
});

test('verified registration access loss cancels the form and regrant does not reopen its drawer', async ({ page }) => {
  const held = await openAndSubmit(page, true), context = contexts.get(page);
  try {
    const reads = context.accessReads; context.admin = false;
    // The real workspace clock renews /me while the native modal is open.
    // This tick occurs before the pending consumer's 30-second timeout.
    await page.clock.runFor(10_001);
    await expect.poll(() => context.accessReads).toBeGreaterThan(reads);
    await expect(drawer(page)).not.toBeVisible();
    await expect.poll(() => context.aborted.length).toBe(1);
  } finally { held.release(); }
  await held.delivered;
  await expect(page.getByRole('link', { name: 'SYNTHETIC cancelled receipt', exact: true })).toHaveCount(0);
  const reads = context.accessReads; context.admin = true;
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await expect.poll(() => context.accessReads).toBeGreaterThan(reads);
  await expect(page.getByRole('button', { name: '+ Add server', exact: true })).toBeVisible();
  await expect(drawer(page)).not.toBeVisible();
  await page.getByRole('button', { name: '+ Add server', exact: true }).click();
  await expect(drawer(page).getByRole('textbox', { name: 'Stable key', exact: true })).toHaveValue('');
  expect(context.posts).toHaveLength(1);
});
