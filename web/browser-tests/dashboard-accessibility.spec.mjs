import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';

const origin = 'http://127.0.0.1:4322';
const target = '11111111-1111-4111-8111-000000000002';
const screens = [
  ['fleet', 'overview'],
  ['server', 'health'],
  ['activity', 'activity'],
  ['queries', 'queries'],
  ['deadlocks', 'deadlocks'],
  ['health-checks', 'operations'],
  ['alerts', 'alerts'],
];

async function open(page, surface) {
  await page.context().addCookies([
    { name: 'dashboardRefactor', value: '1', url: origin },
    { name: 'chartReferenceRole', value: 'viewer', url: origin },
  ]);
  await page.goto(`/#/${surface}?${new URLSearchParams({ target, range: '1h', ...(surface === 'queries' ? { queryView: 'cache' } : {}) })}`);
  if (surface === 'alerts') await expect(page.getByRole('heading', { name: 'Alert triage', exact: true })).toBeVisible();
  else await expect(page.getByRole('combobox', { name: 'Time range', exact: true })).toHaveValue('1h');
  await page.waitForLoadState('networkidle');
  if (surface === 'deadlocks') await expect(page.getByRole('img', { name: 'Deadlock relationship diagram' })).toBeVisible();
  if (surface === 'queries') await expect(page.getByRole('region', { name: 'Plan cache interval ranking', exact: true })).toContainText('bbbbbbbbbbbb');
}

for (const width of [320, 390, 768, 1440]) {
  for (const [name, surface] of screens) {
    test(`${name}: dark, accessible and responsive at ${width}px`, async ({ page }, testInfo) => {
      await page.setViewportSize({ width, height: 1000 });
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      await open(page, surface);
      await expect(page.locator('main')).toBeVisible();
      expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
      expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth), 'Page must fit; wide evidence scrolls locally').toBeLessThanOrEqual(1);
      const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze();
      await testInfo.attach('accessibility-results', { body: JSON.stringify(results.violations, null, 2), contentType: 'application/json' });
      expect(results.violations, JSON.stringify(results.violations.map(v => ({ id: v.id, nodes: v.nodes.map(n => n.target) })))).toEqual([]);
      expect(errors).toEqual([]);
      const folder = resolve('../artifacts/dashboard-refactor/screenshots');
      await mkdir(folder, { recursive: true });
      await page.screenshot({ path: resolve(folder, `${name}-${width}.png`), fullPage: true });
    });
  }
}

test('keyboard skip link, chart evidence, time editor and palette remain accessible', async ({ page }) => {
  await open(page, 'health');
  await page.keyboard.press('Tab');
  const skip = page.getByRole('link', { name: 'Skip to content' });
  await expect(skip).toBeFocused();
  await skip.press('Enter');
  await expect(page.locator('main')).toBeFocused();
  const valuesToggle = page.getByText('View chart values', { exact: true }).first();
  await valuesToggle.focus();
  await valuesToggle.press('Enter');
  const values = page.getByRole('region', { name: 'Chart observation values', exact: true }).first();
  await expect(values).toBeVisible();
  await values.focus();
  await expect(values).toBeFocused();
  await page.getByRole('combobox', { name: 'Time range', exact: true }).selectOption('custom');
  await expect(page.getByLabel('From (UTC)', { exact: true })).toBeFocused();
  expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations).toEqual([]);
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: /^Search/ }).click();
  await expect(page.getByRole('searchbox', { name: 'Search servers' })).toBeFocused();
  expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations).toEqual([]);
});

test('narrow alert evidence preserves readable columns and scrolls with the keyboard', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 1000 });
  await open(page, 'alerts');
  const evidence = page.getByRole('region', { name: 'Target alert observations', exact: true });
  expect(await evidence.evaluate(element => element.scrollWidth > element.clientWidth)).toBe(true);
  await evidence.focus();
  await evidence.press('ArrowRight');
  await expect.poll(() => evidence.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
});

for (const width of [320, 390, 768, 1440]) {
  test(`selected blocking chain keeps keyboard controls and responsive evidence at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.context().addCookies([{ name: 'blockingChainFixture', value: '1', url: origin }]);
    await open(page, 'activity');
    const chain = page.getByRole('combobox', { name: 'Blocking chain (loaded page)', exact: true });
    const action = page.getByRole('button', { name: 'Filter chain: Session 51', exact: true });
    await action.focus();
    await action.press('Enter');
    await expect(chain).toHaveValue('session:51');
    await expect(action).toHaveAttribute('aria-pressed', 'true');
    await expect(action).toBeFocused();
    const clear = page.getByRole('button', { name: 'Clear chain filter', exact: true });
    await clear.focus();
    await clear.press('Enter');
    await expect(chain).toHaveValue('');
    await chain.selectOption('session:81');
    await expect(page.getByRole('region', { name: 'Loaded session observations', exact: true })).toContainText('81');
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze();
    await testInfo.attach('accessibility-results', { body: JSON.stringify(results.violations, null, 2), contentType: 'application/json' });
    expect(results.violations).toEqual([]);
    const folder = resolve('../artifacts/dashboard-refactor/screenshots');
    await mkdir(folder, { recursive: true });
    // Keyboard navigation scrolls the viewport; reset it before a full-page
    // capture so sticky navigation is rendered at the top of the document.
    await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
    await page.screenshot({ path: resolve(folder, `activity-chain-${width}.png`), fullPage: true });
  });
}

for (const width of [320, 390, 768, 1440]) {
  for (const surface of ['activity', 'resources']) {
    test(`database context on ${surface} supports keyboard use and responsive evidence at ${width}px`, async ({ page }, testInfo) => {
      await page.setViewportSize({ width, height: 1000 });
      await page.context().addCookies([
        { name: 'dashboardRefactor', value: '1', url: origin },
        { name: 'chartReferenceRole', value: 'viewer', url: origin },
        { name: 'databaseScopeFixture', value: '1', url: origin },
      ]);
      await page.goto(`/#/${surface}?${new URLSearchParams({ target, range: '1h', databaseId: '6' })}`);
      const database = page.getByRole('combobox', { name: 'Workspace database', exact: true });
      await expect(database).toHaveValue('6');
      await expect(database.locator('option:checked')).toHaveText('Warehouse 02 · ID 6');
      const notice = page.getByTestId('database-context-notice');
      await expect(notice).toContainText('Database ID 6');
      await expect(notice).toContainText('server-wide');

      // Native select navigation must update shared context without a mouse.
      await database.focus();
      await expect(database).toBeFocused();
      await database.press('Home');
      await database.press('Enter');
      await expect(database).toHaveValue('');
      await expect(notice).toHaveCount(0);
      await database.press('End');
      await database.press('Enter');
      await expect(database).toHaveValue('6');
      await expect(database).toBeFocused();
      await expect(notice).toContainText('Database ID 6');
      await page.waitForLoadState('networkidle');

      expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth), 'Selected database context must fit the viewport').toBeLessThanOrEqual(1);
      const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze();
      await testInfo.attach('accessibility-results', { body: JSON.stringify(results.violations, null, 2), contentType: 'application/json' });
      expect(results.violations, JSON.stringify(results.violations.map(v => ({ id: v.id, nodes: v.nodes.map(n => n.target) })))).toEqual([]);
      const folder = resolve('../artifacts/dashboard-refactor/screenshots');
      await mkdir(folder, { recursive: true });
      await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
      await page.screenshot({ path: resolve(folder, `database-${surface}-${width}.png`), fullPage: true });
    });
  }
}

for (const width of [320, 390, 768, 1440]) {
  test(`investigation timeline supports keyboard alignment and responsive evidence at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.context().addCookies([
      { name: 'timelineFixture', value: '1', url: origin },
      { name: 'dashboardRefactor', value: '1', url: origin },
      { name: 'chartReferenceRole', value: 'viewer', url: origin },
      { name: 'databaseScopeFixture', value: '1', url: origin },
    ]);
    const timelineTarget = '11111111-1111-4111-8111-000000000001';
    const from = '2026-09-26T08:00:00.000Z';
    const to = '2026-09-26T12:00:00.000Z';
    await page.goto(`/#/health?${new URLSearchParams({ target: timelineTarget, range: 'custom', from, to, databaseId: '6' })}`);
    const timeline = page.getByRole('region', { name: 'Investigation timeline', exact: true });
    await expect(timeline.getByRole('heading', { name: 'Investigation timeline', exact: true })).toBeVisible();
    const category = timeline.getByRole('combobox', { name: 'Timeline event category', exact: true });
    await category.selectOption('plan_regression');
    const align = timeline.getByRole('button', { name: /^Align charts: Plan regression comparison/ });
    await expect(align).toHaveCount(1);
    await align.focus();
    await align.press('Enter');
    await expect(align).toBeFocused();
    await expect(align).toHaveAttribute('aria-pressed', 'true');
    await expect(timeline.getByRole('status')).toContainText('Chart crosshair:');
    await expect(timeline.getByRole('status').locator('time')).toHaveAttribute('datetime', '2026-09-26T11:00:00.000Z');
    const evidence = timeline.getByRole('link', { name: /^Open evidence: Plan regression comparison/ });
    await expect(evidence).toBeVisible();
    await expect(evidence).toHaveAttribute('href', /databaseId=6/);
    await evidence.focus();
    await expect(evidence).toBeFocused();
    const coverage = timeline.getByText('Timeline coverage', { exact: true });
    await coverage.focus();
    await coverage.press('Enter');
    await expect(coverage).toBeFocused();
    await expect(timeline.locator('details')).toHaveAttribute('open', '');
    await expect(timeline.locator('details')).toContainText('different time meanings');
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth), 'Timeline evidence must fit; timestamps and links must wrap locally').toBeLessThanOrEqual(1);
    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze();
    await testInfo.attach('accessibility-results', { body: JSON.stringify(results.violations, null, 2), contentType: 'application/json' });
    expect(results.violations, JSON.stringify(results.violations.map(v => ({ id: v.id, nodes: v.nodes.map(n => n.target) })))).toEqual([]);
    const folder = resolve('../artifacts/dashboard-refactor/screenshots');
    await mkdir(folder, { recursive: true });
    await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
    await page.screenshot({ path: resolve(folder, `timeline-${width}.png`), fullPage: true });
    const timelineBounds = await timeline.boundingBox();
    expect(timelineBounds).not.toBeNull();
    // Keep the full-page capture at the tested viewport. A taller viewport for
    // this detail capture gives the sticky navigation room above the section.
    await page.setViewportSize({ width, height: Math.ceil(timelineBounds.height) + 400 });
    await timeline.evaluate(element => element.scrollIntoView({ block: 'center' }));
    await timeline.screenshot({ path: resolve(folder, `timeline-detail-${width}.png`) });
  });
}

for (const width of [320, 390, 768, 1440]) {
  test(`persistent fleet navigation keeps keyboard focus and responsive evidence at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.context().addCookies([{ name: 'databaseScopeFixture', value: '1', url: origin }]);
    await open(page, 'health');
    const fleet = page.getByRole('navigation', { name: 'Loaded server status', exact: true });
    await expect(fleet).toBeVisible();
    await expect(fleet.getByRole('link')).toHaveCount(10);
    const current = fleet.locator('a[aria-current="location"]');
    await expect(current).toHaveCount(1);
    await expect(current).toHaveAccessibleName(/^SYNTHETIC SQL 02:/);
    await expect(current).toHaveAttribute('href', new RegExp(`target=${target}`));

    await page.getByRole('link', { name: 'Resources', exact: true }).click();
    await expect(page).toHaveURL(/#\/resources\?/);
    await expect(fleet).toBeVisible();
    await expect(current).toHaveAccessibleName(/^SYNTHETIC SQL 02:/);
    const entries = fleet.getByRole('region', { name: 'Loaded fleet server entries', exact: true });
    expect(await entries.evaluate(element => element.scrollWidth > element.clientWidth), 'The complete loaded fleet scrolls inside its region').toBe(true);
    await entries.evaluate(element => { element.scrollLeft = 0; });
    await entries.focus();
    await entries.press('ArrowRight');
    await expect(entries).toBeFocused();
    await expect.poll(() => entries.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);

    await page.evaluate(() => window.scrollTo(0, 600));
    const stripBounds = await fleet.boundingBox();
    expect(stripBounds).not.toBeNull();
    if (width <= 960) {
      const sidebarBounds = await page.locator('.sidebar').boundingBox();
      expect(sidebarBounds).not.toBeNull();
      expect(stripBounds.y, 'Pinned fleet navigation must sit below the mobile sidebar').toBeGreaterThanOrEqual(sidebarBounds.y + sidebarBounds.height - 1);
    } else {
      expect(stripBounds.y, 'Desktop fleet navigation pins to the viewport').toBeGreaterThanOrEqual(-1);
      expect(stripBounds.y).toBeLessThanOrEqual(1);
    }
    const database = page.getByRole('combobox', { name: 'Workspace database', exact: true });
    await database.focus();
    await expect(database).toBeFocused();
    await expect.poll(async () => {
      const control = await database.boundingBox();
      const pinned = await fleet.boundingBox();
      return control && pinned ? control.y - (pinned.y + pinned.height) : -1;
    }, { message: 'A focused workspace control must remain visible below pinned fleet navigation' }).toBeGreaterThanOrEqual(-1);
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze();
    await testInfo.attach('accessibility-results', { body: JSON.stringify(results.violations, null, 2), contentType: 'application/json' });
    expect(results.violations, JSON.stringify(results.violations.map(v => ({ id: v.id, nodes: v.nodes.map(n => n.target) })))).toEqual([]);
    const folder = resolve('../artifacts/dashboard-refactor/screenshots');
    await mkdir(folder, { recursive: true });
    await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
    await entries.evaluate(element => {
      const link = element.querySelector('a[aria-current="location"]');
      if (link) {
        const linkLeft = link.getBoundingClientRect().left - element.getBoundingClientRect().left + element.scrollLeft;
        element.scrollLeft = Math.max(0, linkLeft - (element.clientWidth - link.getBoundingClientRect().width) / 2);
      }
    });
    await page.screenshot({ path: resolve(folder, `workspace-fleet-${width}.png`), fullPage: true });
    await fleet.screenshot({ path: resolve(folder, `workspace-fleet-detail-${width}.png`) });
  });
}
