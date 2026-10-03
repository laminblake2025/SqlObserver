import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';

const origin = 'http://127.0.0.1:4322';
const target = '11111111-1111-4111-8111-000000000001';
const from = '2026-09-26T08:00:00.000Z', to = '2026-09-26T12:00:00.000Z';
const at = '2026-09-26T10:00:00.123456Z';
const runs = new WeakMap();
const labels = { health: 'Dashboard', overview: 'Fleet' };
const parameters = page => new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
const href = (surface, extra = {}) => '/#/' + surface + '?' + new URLSearchParams({ target, range: 'custom', from, to, databaseId: '5', compare: '1', ...extra });
const alignment = (page, surface) => page.getByRole('region', { name: `${labels[surface]} chart alignment`, exact: true });
const field = (page, surface) => alignment(page, surface).getByRole('textbox', { name: `${labels[surface]} inspection time (UTC)`, exact: true });
const clear = (page, surface) => alignment(page, surface).getByRole('button', { name: `Clear ${labels[surface].toLowerCase()} alignment`, exact: true });

test.beforeEach(async ({ page }) => {
  const run = { requests: [], pending: new Set(), errors: [] }; runs.set(page, run);
  await page.clock.setFixedTime(new Date('2026-10-03T12:00:01Z'));
  await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin },
    { name: 'chartReferenceRole', value: 'viewer', url: origin }, { name: 'databaseScopeFixture', value: '1', url: origin }]);
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  page.on('pageerror', error => run.errors.push(error.message));
  page.on('request', request => { if (new URL(request.url()).pathname.startsWith('/api/')) { run.requests.push({ method: request.method(), url: request.url() }); run.pending.add(request); } });
  page.on('requestfinished', request => run.pending.delete(request)); page.on('requestfailed', request => run.pending.delete(request));
});
test.afterEach(async ({ page }) => {
  expect(runs.get(page).errors).toEqual([]);
  expect(runs.get(page).requests.every(request => request.method === 'GET'), 'Alignment and plot browsing must remain read-only').toBe(true);
});
async function settled(page) {
  await page.evaluate(() => new Promise(resolveFrame => requestAnimationFrame(() => requestAnimationFrame(resolveFrame))));
  await expect.poll(() => runs.get(page).pending.size).toBe(0);
}
async function open(page, surface, extra = {}) {
  await page.goto(href(surface, extra)); await expect(field(page, surface)).toBeVisible();
  await expect(page.locator('.chart-plot-scroll svg').first()).toBeVisible(); await settled(page);
}
async function unchangedScope(page) {
  expect(Object.fromEntries(parameters(page))).toMatchObject({ target, range: 'custom', from, to, databaseId: '5', compare: '1' });
}
async function aligned(page, surface) {
  await expect(alignment(page, surface).locator('time')).toHaveAttribute('datetime', at);
  expect(parameters(page).get('chartAt')).toBe(at); await unchangedScope(page);
  const plots = page.locator('.chart-plot-scroll svg');
  expect(await plots.count()).toBeGreaterThan(1);
  for (const plot of await plots.all()) await expect(plot.locator('.chart-crosshair line')).toHaveCount(1);
}

for (const surface of ['health', 'overview']) test(`${labels[surface]} exact keyboard alignment preserves scope and performs no chartAt read; reload, Back and clear restore it`, async ({ page }) => {
  await open(page, surface); const count = runs.get(page).requests.length;
  await field(page, surface).fill(at); await field(page, surface).press('Enter'); await aligned(page, surface); await settled(page);
  expect(runs.get(page).requests).toHaveLength(count);
  await page.reload(); await expect(field(page, surface)).toHaveValue(at); await aligned(page, surface); await settled(page);
  const restoredReads = runs.get(page).requests.length;
  await clear(page, surface).click(); await expect(alignment(page, surface).locator('time')).toHaveCount(0);
  await expect(page.locator('.chart-crosshair')).toHaveCount(0); expect(parameters(page).has('chartAt')).toBe(false); await unchangedScope(page); await settled(page);
  expect(runs.get(page).requests).toHaveLength(restoredReads);
  await page.goBack(); await aligned(page, surface); await settled(page); expect(runs.get(page).requests).toHaveLength(restoredReads);
  await page.goForward(); await expect(page.locator('.chart-crosshair')).toHaveCount(0); await unchangedScope(page); await settled(page);
  expect(runs.get(page).requests).toHaveLength(restoredReads);
});

for (const surface of ['health', 'overview']) test(`${labels[surface]} invalid, repeated and end-boundary chart inspection never substitutes a marker`, async ({ page }) => {
  await open(page, surface, { chartAt: at }); await aligned(page, surface); const count = runs.get(page).requests.length;
  for (const invalid of [to, '2026-09-26T10:00:00.1234567Z', 'not-a-UTC-time']) {
    await field(page, surface).fill(invalid); await field(page, surface).press('Enter');
    await expect(alignment(page, surface).getByRole('alert')).toBeVisible(); await aligned(page, surface); await settled(page);
    expect(runs.get(page).requests).toHaveLength(count);
  }
  await page.evaluate(next => { location.hash = next; }, href(surface, { chartAt: at }).slice(1) + '&chartAt=bad');
  await expect(alignment(page, surface)).toContainText('invalid or repeated'); await expect(page.locator('.chart-crosshair')).toHaveCount(0);
  expect(parameters(page).getAll('chartAt')).toEqual([at, 'bad']); await unchangedScope(page); await settled(page); expect(runs.get(page).requests).toHaveLength(count);
  await clear(page, surface).click(); await expect(alignment(page, surface).getByRole('alert')).toHaveCount(0);
  await page.evaluate(next => { location.hash = next; }, href(surface, { chartAt: to }).slice(1));
  await expect(field(page, surface)).toHaveValue(to); await expect(alignment(page, surface)).toContainText('outside the loaded window');
  await expect(page.locator('.chart-crosshair')).toHaveCount(0); expect(parameters(page).get('chartAt')).toBe(to); await unchangedScope(page);
  await field(page, surface).fill(at); await field(page, surface).press('Enter'); await aligned(page, surface); await settled(page); expect(runs.get(page).requests).toHaveLength(count);
});

async function touchTap(page, button) {
  await button.scrollIntoViewIfNeeded(); const box = await button.boundingBox();
  expect(box.width).toBeGreaterThanOrEqual(44); expect(box.height).toBeGreaterThanOrEqual(44);
  const session = await page.context().newCDPSession(page);
  try { await session.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: box.x + box.width / 2, y: box.y + box.height / 2, id: 1 }] });
    await session.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] }); } finally { await session.detach(); }
}

async function touchScrollToEdge(page, viewport, button, direction) {
  for (let step = 0; step < 8; step++) {
    const position = await viewport.evaluate(node => ({ left: node.scrollLeft,
      remaining: node.scrollWidth - node.clientWidth - node.scrollLeft }));
    if ((direction === 'later' ? position.remaining : position.left) <= 1) break;
    await expect(button).toBeEnabled(); await touchTap(page, button);
    if (direction === 'later') await expect.poll(() => viewport.evaluate(node => node.scrollLeft)).toBeGreaterThan(position.left);
    else await expect.poll(() => viewport.evaluate(node => node.scrollLeft)).toBeLessThan(position.left);
  }
  await expect(button).toBeDisabled();
}

for (const surface of ['health', 'overview']) for (const width of [320, 390, 768, 1440]) test(`${labels[surface]} alignment and full-window touch browsing fit dark layout at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 }); await open(page, surface); const count = runs.get(page).requests.length;
  await field(page, surface).focus(); await field(page, surface).fill(at); await page.keyboard.press('Tab');
  const apply = alignment(page, surface).getByRole('button', { name: `Align ${labels[surface].toLowerCase()} charts`, exact: true });
  await expect(apply).toBeFocused(); await page.keyboard.press('Enter'); await expect(apply).toBeFocused(); await aligned(page, surface); await settled(page);
  const focus = await apply.evaluate(node => ({ width: parseFloat(getComputedStyle(node).outlineWidth), style: getComputedStyle(node).outlineStyle }));
  expect(focus.style).not.toBe('none'); expect(focus.width).toBeGreaterThanOrEqual(2);
  expect(runs.get(page).requests).toHaveLength(count); const hash = new URL(page.url()).hash;
  const viewports = page.locator('.chart-plot-scroll');
  for (const viewport of await viewports.all()) {
    const font = await viewport.evaluate(node => { const text = node.querySelector('svg text'), m = text.getScreenCTM(); return parseFloat(getComputedStyle(text).fontSize) * Math.hypot(m.a, m.b); });
    expect(font).toBeGreaterThanOrEqual(10);
  }
  const viewport = viewports.first(), label = await viewport.getAttribute('aria-label');
  const earlier = page.getByRole('button', { name: `Scroll ${label} earlier`, exact: true }), later = page.getByRole('button', { name: `Scroll ${label} later`, exact: true });
  if (await viewport.evaluate(node => node.scrollWidth > node.clientWidth + 1)) {
    await expect(earlier).toBeDisabled();
    await touchScrollToEdge(page, viewport, later, 'later');
    await expect(later).toBeDisabled(); const end = await viewport.locator('svg .chart-gridline-vertical').last().boundingBox(), box = await viewport.boundingBox();
    expect(end.x).toBeGreaterThanOrEqual(box.x); expect(end.x).toBeLessThanOrEqual(box.x + box.width);
    await touchScrollToEdge(page, viewport, earlier, 'earlier');
    await expect(earlier).toBeDisabled();
  } else { await expect(earlier).toHaveCount(0); await expect(later).toHaveCount(0); }
  expect(new URL(page.url()).hash).toBe(hash); await settled(page); expect(runs.get(page).requests).toHaveLength(count); await unchangedScope(page);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`${surface}-chart-access-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = resolve('..', 'artifacts', 'dashboard-chart-access', 'screenshots'); await mkdir(directory, { recursive: true });
  await page.evaluate(() => { document.querySelectorAll('.chart-plot-scroll').forEach(node => { node.scrollLeft = 0; }); scrollTo({ top: 0, left: 0, behavior: 'instant' }); });
  const path = resolve(directory, `${surface}-chart-access-${width}.png`); await page.screenshot({ path, fullPage: true, animations: 'disabled' }); await info.attach(`${surface}-chart-access-${width}`, { path, contentType: 'image/png' });
});
