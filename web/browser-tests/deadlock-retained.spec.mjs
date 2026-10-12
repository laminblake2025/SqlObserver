import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
const origin = 'http://127.0.0.1:4432', target = '11111111-1111-4111-8111-000000000001';
const from = '2026-09-26T08:00:00.123456Z', to = '2026-09-26T12:00:00.654321Z';
const href = options => `/#/deadlocks?${new URLSearchParams({ target, range: 'custom', from, to, compare: '1', ...options })}`;
const events = page => page.getByRole('complementary', { name: 'Captured deadlock events', exact: true });
const detail = page => page.getByRole('region', { name: 'Selected deadlock detail', exact: true });
const pager = page => page.getByRole('navigation', { name: 'Deadlock event pages', exact: true });
const state = new WeakMap();
test.beforeEach(async ({ page }) => {
  const context = { errors: [], lists: [] }; state.set(page, context);
  page.on('pageerror', error => context.errors.push(error.message));
  page.on('request', request => { const url = new URL(request.url()); if (url.pathname.endsWith('/deadlocks')) context.lists.push(url); });
  await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin },
    { name: 'databaseScopeFixture', value: '1', url: origin }, { name: 'deadlockRetainedFixture', value: '1', url: origin }]);
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
});
test.afterEach(async ({ page }) => expect(state.get(page).errors).toEqual([]));
async function ready(page) {
  await expect(events(page).locator('.event-card')).toHaveCount(25);
  await expect(detail(page).getByRole('table')).toContainText('72');
}
async function checked(page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  expect((await new AxeBuilder({ page }).analyze()).violations).toEqual([]);
}

test('deadlock investigated pages survive workspace refresh and visited navigation stays local', async ({ page }) => {
  await page.goto(href()); await ready(page);
  await events(page).locator('.event-card').first().click();
  const selected = page.url();
  await pager(page).getByRole('button', { name: 'Next', exact: true }).click();
  await expect(detail(page)).toContainText('not on the loaded event page');
  const received = await events(page).locator('.event-card').first().textContent();
  const count = state.get(page).lists.length;
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await expect(page.locator('.deadlock-investigation-note').filter({ hasText: 'Browsing received deadlock evidence' })).toContainText('Workspace refresh keeps this investigation');
  await expect(events(page).locator('.event-card').first()).toHaveText(received);
  await pager(page).getByRole('button', { name: 'Previous', exact: true }).click();
  await expect(events(page).locator('.event-card').first()).toHaveAttribute('aria-pressed', 'true');
  await pager(page).getByRole('button', { name: 'Next', exact: true }).click();
  await expect(events(page).locator('.event-card').first()).toHaveText(received);
  expect(state.get(page).lists).toHaveLength(count); expect(page.url()).toBe(selected);
  await page.reload(); await ready(page);
  await expect(events(page).locator('.event-card').first()).toHaveAttribute('aria-pressed', 'true');
  expect(new URL(page.url()).hash).toContain('deadlockAt=');
});

test('deadlock explicit latest failure retains the receipt and Retry repeats exact bounds', async ({ page }) => {
  await page.goto(href()); await ready(page);
  await pager(page).getByRole('button', { name: 'Next', exact: true }).click();
  await expect(pager(page).getByRole('button', { name: 'Previous', exact: true })).toBeEnabled();
  const received = await events(page).locator('.event-card').first().textContent();
  let failed;
  await page.route('**/deadlocks?*', async route => {
    failed = route.request().url(); await route.fulfill({ status: 504, body: 'PRIVATE_BACKEND_FAILURE' });
  });
  await page.getByRole('button', { name: 'Return to latest deadlock events', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Deadlock evidence is unavailable.');
  await expect(events(page).locator('.event-card').first()).toHaveText(received);
  await expect(page.locator('body')).not.toContainText('PRIVATE_BACKEND_FAILURE');
  await page.unroute('**/deadlocks?*');
  await page.getByRole('button', { name: 'Retry deadlock read', exact: true }).click();
  await ready(page);
  expect(state.get(page).lists.at(-1).href).toBe(new URL(failed).href);
});

test('deadlock held continuation survives a moving Live tick with the received window', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-09-30T13:00:00Z') });
  await page.goto(href({ range: '1h' })); await ready(page);
  await page.clock.runFor(50_000);
  let release, started; const gate = new Promise(done => release = done), start = new Promise(done => started = done);
  await page.route('**/deadlocks?*', async route => {
    if (!new URL(route.request().url()).searchParams.has('cursor')) return route.continue();
    const response = await route.fetch(); started(route.request()); await gate; await route.fulfill({ response });
  });
  await pager(page).getByRole('button', { name: 'Next', exact: true }).click(); const request = await start;
  const received = (await page.locator('.deadlock-investigation-receipt').textContent()).split(' Page ')[0];
  await page.clock.runFor(10_000); expect(request.failure()).toBeNull(); release();
  await expect(pager(page).getByRole('button', { name: 'Previous', exact: true })).toBeEnabled();
  await expect(page.locator('.deadlock-investigation-receipt')).toContainText(received);
  expect(new URL(request.url()).searchParams.has('cursor')).toBe(true);
});

test('native zero-offset deadlock detail opens Activity at the exact retained occurrence', async ({ page }) => {
  await page.clock.setFixedTime(new Date('2026-09-26T12:00:00Z'));
  await page.route('**/deadlocks/*', async route => {
    const response = await route.fetch(), value = await response.json();
    const nativeUtc = value => value.replace(/(?:\.(\d{1,6}))?Z$/u, (_, fraction = '') => `.${fraction.padEnd(7, '0')}+00:00`);
    await route.fulfill({ response, json: { ...value, summary: { ...value.summary,
      occurredAtUtc: nativeUtc(value.summary.occurredAtUtc), collectedAtUtc: nativeUtc(value.summary.collectedAtUtc) } } });
  });
  await page.route('**/activity/history/snapshots?*', route => route.fulfill({ contentType: 'application/json', body: '[]' }));
  await page.goto(href()); await ready(page);
  const link = detail(page).getByRole('link', { name: 'View activity at event time' });
  const destination = await link.getAttribute('href'), query = new URLSearchParams(destination.split('?')[1]);
  expect(query.get('at')).toBe('2026-09-26T10:00:00.0000000Z');
  expect(query.get('event')).toBeTruthy(); await link.click();
  await expect(page.getByText(/Opened for the deadlock event at/)).toBeVisible();
  expect(new URL(page.url()).hash).toBe(destination);
});

for (const status of [401, 403]) test(`deadlock latest HTTP ${status} removes retained event and relationship evidence`, async ({ page }) => {
  await page.goto(href()); await ready(page);
  await pager(page).getByRole('button', { name: 'Next', exact: true }).click();
  await expect(pager(page).getByRole('button', { name: 'Previous', exact: true })).toBeEnabled();
  await page.route('**/deadlocks?*', route => route.fulfill({ status, body: 'PRIVATE_AUTH_FAILURE' }));
  await page.getByRole('button', { name: 'Return to latest deadlock events', exact: true }).click();
  await expect(events(page).locator('.event-card')).toHaveCount(0);
  await expect(detail(page).getByRole('table')).toHaveCount(0);
  await expect(detail(page).getByRole('img')).toHaveCount(0);
  await expect(page.getByRole('alert')).toContainText(/authorized|access/i);
  await expect(page.locator('body')).not.toContainText('PRIVATE_AUTH_FAILURE'); await checked(page);
});

for (const width of [320, 1440]) test(`deadlock retained workflow dark theme and exports to Activity at ${width}`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 }); await page.goto(href()); await ready(page);
  const card = events(page).locator('.event-card').first(); await card.focus(); await page.keyboard.press('Enter');
  await expect(card).toHaveAttribute('aria-pressed', 'true');
  await pager(page).getByRole('button', { name: 'Next', exact: true }).click();
  await expect(detail(page)).toContainText('not on the loaded event page'); await checked(page);
  const link = detail(page).getByRole('link', { name: 'View activity at event time' });
  const params = new URLSearchParams((await link.getAttribute('href')).split('?')[1]);
  expect(params.get('from')).toBe(from); expect(params.get('to')).toBe(to); expect(params.get('event')).toBeTruthy();
  const folder = new URL('../../artifacts/dashboard-deadlock-retained/screenshots/', import.meta.url);
  await mkdir(folder, { recursive: true }); const path = fileURLToPath(new URL(`built-deadlock-${width}.png`, folder));
  await page.locator('.deadlock-screen').evaluate(element => element.scrollIntoView({ block: 'start', behavior: 'instant' }));
  await page.screenshot({ path, fullPage: false }); await info.attach(`built-deadlock-${width}`, { path, contentType: 'image/png' });
});
