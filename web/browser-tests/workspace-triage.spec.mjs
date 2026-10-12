import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';

const origin = 'http://127.0.0.1:4432';
const target = '11111111-1111-4111-8111-000000000001';
const from = '2026-09-26T08:00:00.123456Z', to = '2026-09-26T12:00:00.654321Z';
const scan = '22222222-2222-4222-8222-222222222222';
const scope = page => Object.fromEntries(new URLSearchParams(new URL(page.url()).hash.split('?')[1]));
const database = page => page.getByRole('combobox', { name: 'Workspace database', exact: true });
const inbox = (page, fleet = false) => page.locator(fleet ? '.fleet-alerts-screen' : '.target-alerts-screen');
const notes = new WeakMap();
test.beforeEach(async ({ page }) => {
  notes.set(page, []); page.on('pageerror', error => notes.get(page).push(error.message));
  await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin },
    { name: 'chartReferenceRole', value: 'viewer', url: origin },
    { name: 'databaseScopeFixture', value: '1', url: origin },
    { name: 'alertSelectionFixture', value: '1', url: origin },
    { name: 'alertOperatorFixture', value: '1', url: origin }]);
});
test.afterEach(async ({ page }) => expect(notes.get(page)).toEqual([]));
const href = (page = 'health', options = {}) => `/#/${page}?${new URLSearchParams({ target, range: 'custom', from, to, compare: '1', ...options })}`;
async function healthyFrame(page) { await expect(database(page).getByRole('option', { name: 'Warehouse · ID 6', exact: true })).toHaveCount(1); }
async function checked(page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
}
function held() {
  let start, release, end;
  const started = new Promise(resolve => { start = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  const ended = new Promise(resolve => { end = resolve; });
  return { started, release: async () => { release(); await ended; }, handle: async (route, response) => {
    start(route.request()); await gate;
    await route.fulfill(response).catch(() => {}); end();
  } };
}

for (const width of [320, 1440]) test(`Workspace triage database change preserves Health section and exact scan URL at ${width}`, async ({ page }) => {
  await page.setViewportSize({ width, height: 1000 });
  for (const healthSection of ['collection', 'summary']) {
    await page.goto(href('health', { healthSection, configurationRun: scan })); await healthyFrame(page);
    await database(page).focus(); await database(page).selectOption('6');
    const selection = healthSection === 'summary' ? { configurationRun: scan } : {};
    await expect.poll(() => scope(page)).toMatchObject({ target, range: 'custom', from, to, compare: '1', databaseId: '6', healthSection, ...selection });
    if (healthSection === 'collection') expect(scope(page).configurationRun).toBeUndefined();
    await expect(page.getByRole('tab', { name: healthSection === 'collection' ? 'Collection health' : 'Dashboard', exact: true })).toHaveAttribute('aria-selected', 'true');
    await page.reload(); await healthyFrame(page);
    expect(scope(page)).toMatchObject({ databaseId: '6', healthSection, ...selection, from, to });
  }
  await checked(page);
});

test('Workspace triage denies a previously loaded workspace database inventory without losing the fixed URL selection', async ({ page }) => {
  await page.goto(href('health', { healthSection: 'collection', databaseId: '6' })); await healthyFrame(page);
  await page.route(`**/observation-targets/${target}/health/databases?*`, route => route.fulfill({ status: 403, body: 'SECRET_INVENTORY_DENIAL' }));
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await expect(database(page)).toBeDisabled();
  await expect(database(page).getByRole('option', { name: 'Warehouse · ID 6', exact: true })).toHaveCount(0);
  await expect(page.locator('body')).not.toContainText('SECRET_INVENTORY_DENIAL');
  expect(scope(page)).toMatchObject({ databaseId: '6', healthSection: 'collection', from, to });
  await checked(page);
});

for (const fleet of [false, true]) test(`Workspace triage ${fleet ? 'fleet' : 'target'} held continuation survives the real workspace tick`, async ({ page }) => {
  await page.clock.install({ time: new Date('2026-10-03T13:00:00Z') });
  await page.goto(href('alerts', { target: fleet ? '' : target, range: '1h' }));
  const screen = inbox(page, fleet), read = held();
  await expect(screen.getByRole('button', { name: 'Load more', exact: true })).toBeEnabled();
  await page.clock.runFor(50_000);
  await page.route(fleet ? '**/api/v1/alerts/active?*' : `**/observation-targets/${target}/alerts/active?*`, async route => {
    if (!new URL(route.request().url()).searchParams.has('cursor')) return route.continue();
    const response = await route.fetch(); await read.handle(route, { response });
  });
  await screen.getByRole('button', { name: 'Load more', exact: true }).click(); const request = await read.started;
  await page.clock.runFor(10_000);
  expect(request.failure()).toBeNull(); await expect(screen).not.toContainText('SYNTHETIC paged alert');
  await read.release();
  await expect(screen.getByRole('button', { name: fleet ? 'Select SYNTHETIC paged alert on SYNTHETIC SQL 02' : 'SYNTHETIC paged alert', exact: true })).toBeVisible();
  await expect(screen).toContainText('101 loaded');
});

for (const width of [320, 1440]) test(`Workspace triage retained fleet alert review remains accessible through Refresh at ${width}`, async ({ page }) => {
  await page.setViewportSize({ width, height: 1000 });
  let reads = 0;
  page.on('request', request => { if (new URL(request.url()).pathname === '/api/v1/alerts/active') reads++; });
  await page.goto(href('alerts', { target: '' })); const screen = inbox(page, true);
  await expect(screen.getByRole('button', { name: 'Load more', exact: true })).toBeEnabled();
  await screen.getByRole('button', { name: 'Load more', exact: true }).focus(); await page.keyboard.press('Enter');
  const selected = screen.getByRole('button', { name: 'Select SYNTHETIC paged alert on SYNTHETIC SQL 02', exact: true });
  await selected.focus(); await page.keyboard.press('Enter');
  await expect(page.getByRole('complementary', { name: 'SYNTHETIC paged alert', exact: true })).toBeVisible();
  const receivedReads = reads;
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await expect(screen).toContainText('Retained'); await expect(selected).toHaveAttribute('aria-pressed', 'true');
  expect(reads).toBe(receivedReads);
  expect(scope(page)).toMatchObject({ alertTarget: '11111111-1111-4111-8111-000000000002', alert: '66666666-6666-4666-8666-666666666666', from, to, compare: '1' });
  await checked(page);
  // Keep capture below pinned chrome, at the same width as the behavior checks.
  const box = await screen.boundingBox(); await page.setViewportSize({ width, height: Math.ceil(box.height) + 800 });
  await screen.scrollIntoViewIfNeeded(); await page.evaluate(() => document.activeElement?.blur());
  await mkdir('../artifacts/dashboard-workspace-triage/screenshots', { recursive: true });
  await screen.screenshot({ path: `../artifacts/dashboard-workspace-triage/screenshots/built-triage-${width}.png` });
});

test('Workspace triage acknowledgement survives a workspace tick and records one accepted operation', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-10-03T13:00:00Z') });
  await page.goto(href('alerts', { range: '1h' })); const screen = inbox(page), response = held(), operations = [];
  await expect(screen.getByRole('button', { name: 'Acknowledge', exact: true })).toBeEnabled();
  await page.clock.runFor(50_000);
  await page.route(`**/observation-targets/${target}/alerts/*/acknowledge`, async route => {
    operations.push(route.request().postDataJSON()); await response.handle(route, { status: 204 });
  });
  await screen.getByRole('button', { name: 'Acknowledge', exact: true }).click(); const request = await response.started;
  await page.clock.runFor(10_000); expect(request.failure()).toBeNull();
  await expect(screen.getByRole('button', { name: 'Acknowledging…', exact: true })).toBeDisabled();
  await response.release();
  await expect(screen.getByRole('status').filter({ hasText: 'Acknowledgement recorded.' })).toBeVisible();
  expect(operations).toHaveLength(1); expect(Object.keys(operations[0])).toEqual(['operationId']);
});
