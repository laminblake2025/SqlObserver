import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { origin, alpha, beta, from, to, healthNavigationHash, healthNavigationResponse } from './healthNavigationFixture.mjs';
import { alertLifecycleHistoryBackend, lifecycleFixtureBinding } from '../tools/alert-lifecycle-history-fixture.mjs';

const sessions = new WeakMap();
const tabs = page => page.getByRole('tablist', { name: 'Server summary sections', exact: true });
const tab = (page, name) => tabs(page).getByRole('tab', { name, exact: true });
const database = page => page.getByRole('combobox', { name: 'Database context', exact: true });
const parameters = page => new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
const selected = async (page, name) => { await expect(tab(page, name)).toHaveAttribute('aria-selected', 'true'); };
async function mounted(page, options = {}) {
  await page.goto(`/component-tests/health.html?navigation=true${healthNavigationHash(options)}`);
  await expect(tabs(page)).toBeVisible();
}
async function firstCommit(page, name) {
  const commit = JSON.parse(await page.locator('output[aria-label="First committed health navigation"]').textContent());
  expect(commit.tab).toBe(name); expect(commit.hash).toBe(new URL(page.url()).hash);
}
test.beforeEach(async ({ page }) => {
  const session = { unexpected: [], reads: [] }; sessions.set(page, session);
  const lifecycle = alertLifecycleHistoryBackend({ count: 0, cutoffUtc: to });
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  page.on('pageerror', error => session.unexpected.push(`Page error: ${error.message}`));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async () => { session.unexpected.push(`${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/u.test(url.pathname)) return route.continue();
    if (request.method() !== 'GET') return reject();
    let value; try {
      if (/^\/api\/v1\/observation-targets\/[^/]+\/alerts\/history$/.test(url.pathname)) {
        const item = lifecycleFixtureBinding(url);
        if (![alpha, beta].includes(item.binding.targetId)) return reject();
        value = lifecycle.read(item.binding, item.cursor);
      } else value = healthNavigationResponse(url);
    } catch { return reject(); }
    session.reads.push(url.href);
    await route.fulfill({ status: 200, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
  });
});
test.afterEach(async ({ page }) => {
  expect(sessions.get(page).unexpected, 'All API reads must use registered native local fixtures; page errors and external requests fail').toEqual([]);
});

test('Back and Forward restore an absent Health section during the first committed render', async ({ page }) => {
  await mounted(page); await selected(page, 'Dashboard');
  const original = new URL(page.url()).hash;
  await tab(page, 'Collection health').click(); await selected(page, 'Collection health');
  await expect.poll(() => parameters(page).get('healthSection')).toBe('collection');
  await page.goBack(); await selected(page, 'Dashboard'); expect(new URL(page.url()).hash).toBe(original);
  await firstCommit(page, 'Dashboard');
  await page.goForward(); await selected(page, 'Collection health'); await firstCommit(page, 'Collection health');
  expect(Object.fromEntries(parameters(page))).toMatchObject({ target: alpha, range: 'custom', from, to, compare: '1', healthSection: 'collection' });
});

test('reloading a bare route gives the same default section as history restoration', async ({ page }) => {
  await mounted(page); await tab(page, 'Collection health').click(); await selected(page, 'Collection health');
  await page.goBack(); await selected(page, 'Dashboard'); const bare = page.url();
  await page.reload(); await selected(page, 'Dashboard'); expect(page.url()).toBe(bare);
  expect(parameters(page).has('healthSection')).toBe(false);
});

test('bare database context retains Dashboard while clearing and restoring the database filter', async ({ page }) => {
  await mounted(page, { databaseId: '7' }); await selected(page, 'Dashboard');
  await expect(database(page)).toHaveValue('7');
  await database(page).selectOption(''); await selected(page, 'Dashboard'); await firstCommit(page, 'Dashboard');
  expect(parameters(page).has('healthSection')).toBe(false); expect(parameters(page).has('databaseId')).toBe(false);
  await page.goBack(); await selected(page, 'Dashboard'); await firstCommit(page, 'Dashboard');
  await expect(database(page)).toHaveValue('7');
  await page.goForward(); await selected(page, 'Dashboard'); await firstCommit(page, 'Dashboard');
});

for (const [value, name] of [['summary', 'Dashboard'], ['collection', 'Collection health']]) test(`explicit ${value} wins over database default through database additions and clearing`, async ({ page }) => {
  await mounted(page, { healthSection: value }); await selected(page, name);
  await database(page).selectOption('7'); await selected(page, name); await firstCommit(page, name);
  expect(parameters(page).get('healthSection')).toBe(value); expect(parameters(page).get('databaseId')).toBe('7');
  await database(page).selectOption(''); await selected(page, name); await firstCommit(page, name);
  expect(parameters(page).get('healthSection')).toBe(value); expect(parameters(page).has('databaseId')).toBe(false);
});

test('returning from an explicit section to a bare database URL restores Dashboard and survives reload', async ({ page }) => {
  await mounted(page, { databaseId: '7' }); await selected(page, 'Dashboard');
  await tab(page, 'Collection health').click(); await selected(page, 'Collection health');
  await page.goBack(); await selected(page, 'Dashboard'); await firstCommit(page, 'Dashboard');
  expect(parameters(page).has('healthSection')).toBe(false);
  await page.reload(); await selected(page, 'Dashboard'); await expect(database(page)).toHaveValue('7');
});

test('explicit section survives target changes and refresh while native source reads follow the new target', async ({ page }) => {
  await mounted(page, { healthSection: 'databases', databaseId: '7' }); await selected(page, 'Databases');
  await page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption('beta');
  await selected(page, 'Databases');
  await expect(page.getByRole('heading', { name: 'Server summary for Beta', exact: true })).toBeVisible();
  await expect(page.getByRole('article').filter({ has: page.getByRole('heading', { name: 'Synthetic Beta inventory', exact: true }) })).toBeVisible();
  expect(parameters(page).get('target')).toBe(beta); expect(parameters(page).get('healthSection')).toBe('databases');
  const beforeRefresh = sessions.get(page).reads.filter(url => url.includes(`/${beta}/health`)).length;
  await page.getByRole('button', { name: 'Refresh health evidence', exact: true }).click(); await selected(page, 'Databases');
  await expect.poll(() => sessions.get(page).reads.filter(url => url.includes(`/${beta}/health`)).length).toBeGreaterThan(beforeRefresh);
  await page.getByRole('button', { name: 'Open default health section', exact: true }).click();
  await selected(page, 'Dashboard'); await firstCommit(page, 'Dashboard');
});

for (const width of [320, 390, 768, 1440]) test(`Health history navigation remains accessible in dark layout at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 }); await mounted(page); await selected(page, 'Dashboard');
  const verifyInsetFocus = async name => {
    const button = tab(page, name); await button.focus(); await expect(button).toBeFocused();
    const geometry = await button.evaluate(element => {
      const style = getComputedStyle(element), box = element.getBoundingClientRect(), owner = element.closest('[role="tablist"]').getBoundingClientRect();
      return { width: parseFloat(style.outlineWidth), offset: parseFloat(style.outlineOffset), style: style.outlineStyle,
        left: box.left - owner.left, right: owner.right - box.right, height: box.height };
    });
    expect(geometry.style).not.toBe('none'); expect(geometry.width).toBeGreaterThanOrEqual(2);
    expect(geometry.offset + geometry.width, 'The focus outline fits inside the tab instead of being clipped by mobile tablist overflow').toBeLessThanOrEqual(0);
    expect(geometry.left).toBeGreaterThanOrEqual(-1); expect(geometry.right).toBeGreaterThanOrEqual(-1); expect(geometry.height).toBeGreaterThanOrEqual(44);
  };
  await verifyInsetFocus('Dashboard');
  await tab(page, 'Dashboard').focus(); await page.keyboard.press('End');
  await selected(page, 'Collection health'); await expect(tab(page, 'Collection health')).toBeFocused();
  await verifyInsetFocus('Collection health');
  await page.goBack(); await selected(page, 'Dashboard'); await firstCommit(page, 'Dashboard');
  await tab(page, 'Dashboard').focus(); await page.keyboard.press('ArrowRight');
  await selected(page, 'Databases'); await expect(tab(page, 'Databases')).toBeFocused();
  const focus = await tab(page, 'Databases').evaluate(element => ({ style: getComputedStyle(element).outlineStyle,
    width: parseFloat(getComputedStyle(element).outlineWidth) }));
  expect(focus.style).not.toBe('none'); expect(focus.width).toBeGreaterThanOrEqual(2);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`health-navigation-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-access-navigation/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
  await page.evaluate(() => window.scrollTo({ top: 0, left: 0, behavior: 'instant' }));
  await page.screenshot({ path: `${directory}/health-navigation-${width}.png`, fullPage: true });
});
