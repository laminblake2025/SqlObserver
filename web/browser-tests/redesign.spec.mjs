import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';

const origin = 'http://127.0.0.1:4358';
const target = '11111111-1111-4111-8111-000000000002';
const requests = new WeakMap(), errors = new WeakMap();
const shotDir = resolve('../artifacts/web-browser/visual-reference/redesign');
test.beforeEach(async ({ page }) => {
  requests.set(page, []); errors.set(page, []);
  page.on('pageerror', error => errors.get(page).push(error.message));
  page.on('request', request => { if (new URL(request.url()).pathname.startsWith('/api/')) requests.get(page).push(request); });
  await page.context().addCookies(Object.entries({ dashboardRefactor: '1', databaseScopeFixture: '1', chartReferenceRole: 'admin', incidentWorkspaceFixture: '1', queryRowTrendFixture: '1' }).map(([name, value]) => ({ name, value, url: origin })));
});
test.afterEach(async ({ page }) => expect(errors.get(page)).toEqual([]));
async function settled(page) {
  await expect(page.getByText('Loading diagnostic workspace…', { exact: true })).toHaveCount(0);
  await page.waitForLoadState('networkidle');
}
async function geometryAndAxe(page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), 'No page-level horizontal overflow').toBe(true);
  const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
  expect(result.violations.map(item => ({ id: item.id, impact: item.impact, nodes: item.nodes.map(node => node.target) }))).toEqual([]);
}
for (const size of [{ width: 1600, height: 1000 }, { width: 1440, height: 900 }, { width: 390, height: 844 }]) {
  test(`three redesign screens and accessible layout at ${size.width}×${size.height}`, async ({ page }) => {
    await page.setViewportSize(size); await mkdir(shotDir, { recursive: true });
    await page.goto('/#/overview?range=1h'); await expect(page.getByRole('heading', { name: 'Server fleet', exact: true })).toBeVisible(); await settled(page);
    await expect(page.locator('.overview-issue')).toHaveCount(5); // Three visible priorities, two behind disclosure.
    await expect(page.locator('.overview-issue').first()).toBeInViewport();
    await expect(page.locator('.overview-issue').first().getByText('Open evidence →', {exact:true})).toBeInViewport();
    await page.evaluate(() => window.scrollTo(0, 0)); await geometryAndAxe(page); await page.screenshot({ path: resolve(shotDir, `fleet-${size.width}.png`) });
    await page.goto(`/#/health?target=${target}&range=1h`); await expect(page.getByRole('button', { name: 'Inspect head blocker 51', exact: true })).toBeVisible(); await settled(page);
    await expect(page.getByRole('button', { name: 'Save investigation', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Inspect head blocker 51', exact: true })).toBeInViewport();
    expect(requests.get(page).filter(request => new URL(request.url()).pathname === '/api/v1/overview').slice(1).every(request => new URL(request.url()).searchParams.get('targetId') === target)).toBe(true);
    await page.evaluate(() => window.scrollTo(0, 0)); await geometryAndAxe(page); await page.screenshot({ path: resolve(shotDir, `server-${size.width}.png`) });
    await page.getByRole('link', { name: 'Activity', exact: true }).click(); await expect(page.getByRole('button', { name: 'Inspect 51/0', exact: true })).toBeVisible(); await settled(page); await page.evaluate(() => window.scrollTo(0, 0));
    await expect(page.getByRole('button', { name: 'Inspect 51/0', exact: true })).toBeInViewport();
    await page.getByRole('button', { name: 'Inspect 51/0', exact: true }).click();
    await expect(page.getByRole('region', { name: 'Session details' })).toContainText('Session 51');
    await expect(page.getByRole('region', { name: 'Blocking tree · loaded snapshot' })).toContainText('same captured snapshot');
    await expect(page.getByRole('button', { name: 'Load captured SQL', exact: true })).toHaveCount(0);
    expect(requests.get(page).some(request => new URL(request.url()).pathname.endsWith('/activity/live/query'))).toBe(false);
    await page.evaluate(() => window.scrollTo(0, 0)); await geometryAndAxe(page); await page.screenshot({ path: resolve(shotDir, `blocking-${size.width}.png`) });
  });
}

test('detail navigation avoids fleet reads, saves an exact frozen investigation, and restores deep links', async ({ page }) => {
  await page.goto(`/#/health?target=${target}&range=1h`); await expect(page.getByRole('button', { name: 'Save investigation', exact: true })).toBeEnabled(); await settled(page);
  expect(requests.get(page).filter(request => new URL(request.url()).pathname === '/api/v1/overview').every(request => new URL(request.url()).searchParams.get('targetId') === target)).toBe(true);
  await page.getByRole('button', { name: 'Save investigation', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Incident title', exact: true })).toBeFocused();
  await page.getByRole('button', {name:'Close draft',exact:true}).click();
  await expect(page.getByRole('button', {name:'Save investigation',exact:true})).toBeFocused();
  await page.getByRole('button', {name:'Save investigation',exact:true}).click();
  await expect(page.getByRole('textbox', {name:'Incident title',exact:true})).toBeFocused();
  await page.getByRole('textbox', { name: 'Incident title', exact: true }).fill('SYNTHETIC redesign capture');
  await page.getByRole('button', { name: 'Save incident', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'SYNTHETIC redesign capture', exact: true })).toBeVisible();
  const hash = new URL(page.url()).hash; expect(hash).toContain('incident='); expect(hash).toContain('range=custom');
  await page.reload(); await expect(page.getByRole('heading', { name: 'SYNTHETIC redesign capture', exact: true })).toBeVisible();
});

test('custom editor follows display timezone and untouched UTC bounds retain microseconds', async ({ page }) => {
  const from = '2026-10-01T12:00:00.123456Z', to = '2026-10-01T13:00:00.654321Z';
  await page.goto('/#/overview?' + new URLSearchParams({ range: 'custom', from, to }));
  await page.getByRole('button', { name: 'Edit local range', exact: true }).click();
  await expect(page.locator('input[name=from]')).toHaveValue('2026-10-01T08:00:00.123');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  expect(new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('from')).toBe(from);
  await page.getByRole('button', { name: 'Show UTC time', exact: true }).click();
  await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
  await expect(page.locator('input[name=from]')).toHaveValue('2026-10-01T12:00:00.123');
});

test('viewer cannot save investigations and narrow navigation is keyboard accessible', async ({ page }) => {
  await page.context().addCookies([{ name: 'chartReferenceRole', value: 'viewer', url: origin }, { name: 'incidentWorkspaceFixture', value: '0', url: origin }]);
  await page.setViewportSize({ width: 390, height: 844 }); await page.goto(`/#/health?target=${target}&range=1h`); await settled(page);
  await expect(page.getByRole('button', { name: 'Save investigation', exact: true })).toHaveCount(0);
  const menu = page.getByRole('button', { name: 'Menu', exact: true }); await menu.press('Enter');
  await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeVisible(); await menu.press('Escape'); await expect(menu).toBeFocused(); await expect(menu).toHaveAttribute('aria-expanded', 'false'); await expect(page.getByRole('navigation', { name: 'Main navigation' })).toBeHidden();
});

test('chart inspection survives refresh and resets on explicit metric change', async ({ page }) => {
  await page.goto('/#/overview?range=1h'); await expect(page.locator('.chart-plot-scroll svg').first()).toBeVisible(); await settled(page);
  const plot = page.locator('.chart-plot-scroll').first();
  // Exercise the panning path at a deliberately enlarged plot, while normal plots adapt to width.
  await plot.locator('svg').evaluate(element => { element.style.width = '1800px'; });
  await plot.evaluate(element => { element.scrollLeft = 200; });
  expect(await plot.evaluate(element => element.scrollLeft)).toBeGreaterThan(100);
  await page.getByRole('button', { name: '↻ Refresh', exact: true }).click(); await settled(page);
  expect(await plot.evaluate(element => element.scrollLeft)).toBeGreaterThan(100);
  await page.getByRole('combobox', { name: 'Workload metric', exact: true }).selectOption('engine.user_connections');
  await expect.poll(() => plot.evaluate(element => element.scrollLeft)).toBe(0);
});

test('existing diagnostic and administration bookmarks load inside the shared shell', async ({ page }) => {
 test.setTimeout(90_000);
 for (const route of ['servers','health','activity','waits','resources','queries','deadlocks','operations','analytics','reports','alerts','rules','rule-templates','maintenance','destinations','permissions']) {
  await page.goto(`/#/${route}?target=${target}&range=1h`); await settled(page);
  await expect(page.locator('.page-heading h1')).toBeVisible();
  await expect(page.getByRole('navigation', { name:'Main navigation', exact:true }).getByRole('link')).toHaveCount(5);
  await expect(page.getByRole('heading', {name:'This workspace could not load',exact:true})).toHaveCount(0);
  expect(new URL(page.url()).hash).toContain(`/${route}`);
 }
});
