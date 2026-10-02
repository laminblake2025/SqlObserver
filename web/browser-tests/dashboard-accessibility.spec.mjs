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
