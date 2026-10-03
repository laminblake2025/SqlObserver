import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';

const origin = 'http://127.0.0.1:4322', target = '11111111-1111-4111-8111-000000000001';
const from = '2026-09-26T08:00:00.123456Z', to = '2026-09-26T12:00:00.654321Z';
const href = options => '/#/health?' + new URLSearchParams({ target, range: 'custom', from, to, databaseId: '6', compare: '1', ...options });
const scope = page => Object.fromEntries(new URLSearchParams(new URL(page.url()).hash.split('?')[1]));
const frame = page => page.getByRole('region', { name: 'Workspace server and database context', exact: true });
const summary = (page, name) => page.locator('summary').filter({ hasText: new RegExp(`^${name}$`) });
const attempts = new WeakMap();
test.beforeEach(async ({ page }) => {
  const attempt = { requests: [], pending: new Set(), errors: [] }; attempts.set(page, attempt);
  await page.clock.setFixedTime(new Date('2026-10-03T12:00:00Z'));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.context().addCookies(Object.entries({ dashboardRefactor: '1', chartReferenceRole: 'viewer', databaseScopeFixture: '1' })
    .map(([name, value]) => ({ name, value, url: origin })));
  page.on('pageerror', error => attempt.errors.push(error.message));
  page.on('request', request => { if (new URL(request.url()).pathname.startsWith('/api/')) { attempt.requests.push(request); attempt.pending.add(request); } });
  page.on('requestfinished', request => attempt.pending.delete(request)); page.on('requestfailed', request => attempt.pending.delete(request));
  await page.route('**/*', route => {
    if (new URL(route.request().url()).origin === origin) return route.continue();
    attempt.errors.push(`External request: ${route.request().url()}`); return route.abort();
  });
});
test.afterEach(async ({ page }) => {
  expect(attempts.get(page).errors).toEqual([]);
  expect(attempts.get(page).requests.every(request => request.method() === 'GET')).toBe(true);
});
async function settled(page) {
  await page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))));
  await expect.poll(() => attempts.get(page).pending.size).toBe(0);
}
async function toggle(page, name, opened) {
  const control = summary(page, name); await control.focus(); await page.keyboard.press('Enter');
  await expect(control).toBeFocused();
  if (opened) await expect(control.locator('..')).toHaveAttribute('open', '');
  else await expect(control.locator('..')).not.toHaveAttribute('open', '');
}

for (const width of [320, 390, 768, 1440]) test(`the compact context frame keeps dashboard answers and optional tools accessible at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 }); await page.goto(href());
  const database = frame(page).getByRole('combobox', { name: 'Workspace database', exact: true });
  await expect(database).toHaveValue('6');
  await expect(frame(page).locator('.database-scope-status')).toContainText('2 databases loaded');
  await expect(frame(page).locator('.workspace-context-scope')).toContainText('Database ID 6');
  await expect(page.getByRole('tab', { name: 'Dashboard', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('.server-dashboard-kpis')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Server summary for SYNTHETIC SQL 01', exact: true })).toHaveCount(0);
  await expect(page.locator('.health-screen').getByRole('button', { name: 'Close', exact: true })).toHaveCount(0);
  for (const name of ['Context details', 'Inventory details', 'Investigation workspace']) await expect(summary(page, name).locator('..')).not.toHaveAttribute('open', '');
  await settled(page); const count = attempts.get(page).requests.length, hash = new URL(page.url()).hash;
  await toggle(page, 'Context details', true);
  await expect(page.getByTestId('database-context-notice')).toBeVisible();
  await expect(page.getByTestId('database-context-notice')).toContainText('server-wide metrics, waits, blocking history, deadlocks, alerts and health checks remain server-wide.');
  await toggle(page, 'Context details', false);
  await toggle(page, 'Inventory details', true);
  await expect(frame(page).getByText(/^Repository snapshot:/)).toBeVisible();
  await expect(frame(page).getByText(/missing entries do not mean a database is absent/)).toBeVisible();
  await toggle(page, 'Inventory details', false);
  await toggle(page, 'Investigation workspace', true);
  await expect(page.getByText('Operator or Target Administrator access unlocks saving an incident for this server.', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Open incident', exact: true })).toHaveCount(0);
  await toggle(page, 'Investigation workspace', false);
  await settled(page); expect(attempts.get(page).requests).toHaveLength(count); expect(new URL(page.url()).hash).toBe(hash);
  expect(scope(page)).toMatchObject({ target, range: 'custom', from, to, databaseId: '6', compare: '1' });
  const focus = await summary(page, 'Investigation workspace').evaluate(element => ({ style: getComputedStyle(element).outlineStyle, width: parseFloat(getComputedStyle(element).outlineWidth) }));
  expect(focus.style).not.toBe('none'); expect(focus.width).toBeGreaterThanOrEqual(2);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`workspace-context-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = resolve('../artifacts/dashboard-context-frame/screenshots'); await mkdir(directory, { recursive: true });
  await page.evaluate(() => { document.activeElement?.blur(); scrollTo({ top: 0, left: 0, behavior: 'instant' }); });
  await page.screenshot({ path: resolve(directory, `workspace-context-${width}.png`), fullPage: true });
});

for (const status of [401, 403]) test(`database inventory HTTP ${status} remains visible while the filter requires explicit clearing`, async ({ page }) => {
  await page.route(`**/observation-targets/${target}/health/databases?*`, route => route.fulfill({ status, contentType: 'application/json', body: '{"detail":"PRIVATE_INVENTORY_DENIAL"}' }));
  await page.goto(href({ healthSection: 'collection' }));
  const database = frame(page).getByRole('combobox', { name: 'Workspace database', exact: true });
  await expect(database).toBeDisabled(); await expect(database).toHaveValue('6');
  await expect(frame(page).getByRole('alert')).toHaveText("Your access to this server's database inventory could not be verified. Refresh after access is restored.");
  await expect(summary(page, 'Inventory details').locator('..')).not.toHaveAttribute('open', '');
  await expect(frame(page).getByRole('status').filter({ hasText: 'Selected database ID 6 has not been received' })).toBeVisible();
  const clear = frame(page).getByRole('button', { name: 'Clear database filter', exact: true });
  await expect(clear).toBeVisible();
  await settled(page); const reads = attempts.get(page).requests.filter(request => new URL(request.url()).pathname.endsWith('/health/databases')).length;
  await clear.focus(); await clear.press('Enter');
  await expect.poll(() => scope(page).databaseId).toBeUndefined();
  await expect(database).toHaveValue(''); await expect(clear).toHaveCount(0);
  await expect(page.getByRole('tab', { name: 'Collection health', exact: true })).toHaveAttribute('aria-selected', 'true');
  expect(scope(page)).toMatchObject({ target, range: 'custom', from, to, compare: '1', healthSection: 'collection' });
  await settled(page); expect(attempts.get(page).requests.filter(request => new URL(request.url()).pathname.endsWith('/health/databases'))).toHaveLength(reads);
  await expect(page.locator('body')).not.toContainText('PRIVATE_INVENTORY_DENIAL');
});

test('a missing database warning stays outside routine details and an explicit section survives reload and history', async ({ page }) => {
  await page.goto(href({ databaseId: '99' }));
  await expect(frame(page).locator('.database-scope-selection-note')).toBeVisible();
  await expect(frame(page).locator('.database-scope-selection-note')).toHaveText('Selected database ID 99 has not been received in inventory; its filter remains selected.');
  await expect(summary(page, 'Inventory details').locator('..')).not.toHaveAttribute('open', '');
  await expect(page.getByRole('tab', { name: 'Dashboard', exact: true })).toHaveAttribute('aria-selected', 'true');
  await page.getByRole('tab', { name: 'Databases', exact: true }).click();
  await expect.poll(() => scope(page).healthSection).toBe('databases');
  await page.reload(); await expect(page.getByRole('tab', { name: 'Databases', exact: true })).toHaveAttribute('aria-selected', 'true');
  await page.getByRole('tab', { name: 'Collection health', exact: true }).click();
  await page.goBack(); await expect(page.getByRole('tab', { name: 'Databases', exact: true })).toHaveAttribute('aria-selected', 'true');
  expect(scope(page).databaseId).toBe('99');
});

for (const kind of ['timeline rail', 'SQL workload chart']) test(`changing Health section cancels a held ${kind} gesture and preserves the mounted evidence`, async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 }); await page.goto(href());
  const dashboard = page.locator('.health-dashboard-pane'), plot = kind === 'timeline rail'
    ? dashboard.locator('.dashboard-timeline-rail') : dashboard.getByRole('region', { name: 'SQL workload plot', exact: true }).locator('svg');
  await expect(plot).toBeVisible();
  await plot.evaluate(element => { element.dataset.retainedGesture = 'same-native-node'; });
  const points = async () => {
    await plot.scrollIntoViewIfNeeded();
    return plot.evaluate((element, rail) => {
      const matrix = element.getScreenCTM();
      const at = ratio => { const point = new DOMPoint(48 + 572 * ratio, rail ? 32 : 90).matrixTransform(matrix); return { x: point.x, y: point.y }; };
      return { start: at(.2), end: at(.7) };
    }, kind === 'timeline rail');
  };
  let coordinates = await points();
  await page.mouse.move(coordinates.start.x, coordinates.start.y); await page.mouse.down(); await page.mouse.move(coordinates.end.x, coordinates.end.y);
  const preview = kind === 'timeline rail' ? dashboard.locator('.dashboard-timeline-range-preview') : plot.locator('.chart-selection');
  await expect(preview).toHaveCount(1);
  // The navigation is a separate keyboard/programmatic action while the pointer owner remains held.
  await page.getByRole('tab', { name: 'Databases', exact: true }).evaluate(element => element.click());
  await expect(page.getByRole('tab', { name: 'Databases', exact: true })).toHaveAttribute('aria-selected', 'true');
  const saved = new URL(page.url()).hash; await page.mouse.up();
  expect(new URL(page.url()).hash).toBe(saved); expect(scope(page)).toMatchObject({ from, to, databaseId: '6', compare: '1', healthSection: 'databases' });
  await page.getByRole('tab', { name: 'Dashboard', exact: true }).click();
  await expect(plot).toHaveAttribute('data-retained-gesture', 'same-native-node');
  await expect(preview).toHaveCount(0);
  coordinates = await points();
  await page.mouse.move(coordinates.start.x, coordinates.start.y); await page.mouse.down(); await page.mouse.move(coordinates.end.x, coordinates.end.y); await page.mouse.up();
  await expect.poll(() => scope(page).from).not.toBe(from);
  const selected = scope(page); expect(Date.parse(selected.from)).toBeGreaterThan(Date.parse(from)); expect(Date.parse(selected.to)).toBeLessThan(Date.parse(to));
  expect(Date.parse(selected.to)).toBeGreaterThan(Date.parse(selected.from));
  expect(selected).toMatchObject({ target, range: 'custom', databaseId: '6', compare: '1' });
  expect(selected.healthSection).toBeUndefined();
  await expect(page.getByRole('tab', { name: 'Dashboard', exact: true })).toHaveAttribute('aria-selected', 'true');
});
