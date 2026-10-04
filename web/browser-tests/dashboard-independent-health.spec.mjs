import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { evidence, database, collector } from '../component-tests/healthFixture.mjs';

const origin = 'http://127.0.0.1:4322';
const target = '11111111-1111-4111-8111-000000000002';
const from = '2026-09-26T08:00:00.123456Z', to = '2026-09-26T12:00:00.654321Z';
const run = '33333333-3333-4333-8333-333333333333';
const context = page => Object.fromEntries(new URLSearchParams(new URL(page.url()).hash.split('?')[1]));
const panel = page => page.getByRole('region', { name: 'Server diagnostics for SYNTHETIC SQL 02', exact: true });
async function capture(page, name) {
  const folder = fileURLToPath(new URL('../../artifacts/dashboard-independent-evidence-2026-10-04/screenshots/', import.meta.url));
  await mkdir(folder, { recursive: true });
  await page.evaluate(() => {
    window.scrollTo({ top: 0, behavior: 'instant' });
    return new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
  });
  await page.screenshot({ path: `${folder}${name}.png`, fullPage: true });
}

async function setup(page, failed) {
  await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin },
    { name: 'chartReferenceRole', value: 'viewer', url: origin }]);
  const failures = [];
  page.on('pageerror', error => failures.push(error.message));
  const latest = evidence(target);
  latest.target.collectors.push(collector('engine.configurations'));
  latest.databases.items = [database(7, 'SYNTHETIC independent inventory')];
  const reads = [];
  if (failed === 'files') await page.route('**/api/v1/overview?*', async route => {
    const url = new URL(route.request().url());
    if (url.searchParams.get('targetId') !== target) return route.fallback();
    expect(route.request().method()).toBe('GET');
    await route.fulfill({ status: 503, contentType: 'application/json', json: { detail: 'PRIVATE_ANALYTICS_STACK' } });
  });
  await page.route(`**/api/v1/observation-targets/${target}/health{,/**}`, async route => {
    const url = new URL(route.request().url()), suffix = url.pathname.split('/health')[1];
    expect(route.request().method()).toBe('GET'); reads.push(url.href);
    let value;
    if (suffix === '' || suffix === '/databases' || suffix === '/files') {
      const kind = suffix === '' ? 'target' : suffix.slice(1);
      if (kind === failed) return route.fulfill({ status: 503, contentType: 'application/json', json: { detail: 'PRIVATE_PROVIDER_STACK' } });
      value = latest[kind];
    } else if (suffix === '/server-configuration-changes') {
      expect(Object.fromEntries(url.searchParams)).toEqual({ fromUtc: from, toUtc: to, limit: '100' });
      value = { targetId: target, targetRevision: 1, publicationGeneration: 1, fromUtc: from, toUtc: to,
        coverage: 'observed_changes_only', nextCursor: null, items: [{ configurationId: 1,
          configurationName: 'SYNTHETIC independently selected configuration', sourceType: 'int', configuredValue: '8', inUseValue: '8',
          previousConfiguredValue: '4', previousInUseValue: '4', configuredChanged: true, inUseChanged: true,
          previousObservedAtUtc: '2026-09-26T07:59:00Z', observedAtUtc: '2026-09-26T10:00:00Z',
          detectedAtUtc: '2026-09-26T10:00:00Z', runId: run }] };
    } else if (suffix === '/database-option-changes') {
      expect(Object.fromEntries(url.searchParams)).toEqual({ fromUtc: from, toUtc: to, limit: '100' });
      value = { targetId: target, fromUtc: from, toUtc: to,
        semantics: 'detected_between_consecutive_complete_inventory_runs', items: [] };
    } else return route.fallback();
    await route.fulfill({ status: 200, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, json: value });
  });
  return { failures, reads };
}

for (const width of [320, 1440]) test(`file and analytics outages preserve selected configuration history and exact Rewind context at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 });
  const state = await setup(page, 'files');
  await page.goto(`/#/health?${new URLSearchParams({ target, range: 'custom', from, to, databaseId: '7',
    healthSection: 'summary', configurationRun: run })}`);
  const selected = panel(page).locator(`[data-configuration-run="${run}"]`);
  await expect(selected).toContainText('SYNTHETIC independently selected configuration');
  await expect(selected).toBeFocused();
  await expect(panel(page).getByRole('alert').filter({ hasText: 'The health request failed safely.' })).toBeVisible();
  await expect(panel(page).locator('.health-summary')).toContainText('Current evidence');
  expect(context(page)).toMatchObject({ target, range: 'custom', from, to, databaseId: '7', configurationRun: run });
  await expect(panel(page)).not.toContainText('PRIVATE_PROVIDER_STACK');
  await expect(panel(page)).not.toContainText('PRIVATE_ANALYTICS_STACK');
  await expect(panel(page).getByRole('alert').filter({ hasText: 'Overview evidence is temporarily unavailable.' })).toBeVisible();
  // No comparable analytics window exists for timeline markers. This isolates
  // the selected history reader and proves it survives both independent outages.
  expect(state.reads.filter(url => new URL(url).pathname.endsWith('/server-configuration-changes'))).toHaveLength(1);
  expect(state.failures).toEqual([]);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach('partial-health-axe', { body: JSON.stringify(violations), contentType: 'application/json' }); expect(violations).toEqual([]);
  await capture(page, `file-outage-configuration-${width}`);
});

for (const width of [320, 1440]) test(`core outage preserves database inventory and independently scoped option history at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 }); const state = await setup(page, 'target');
  await page.goto(`/#/health?${new URLSearchParams({ target, range: 'custom', from, to, databaseId: '7', healthSection: 'databases' })}`);
  const inventory = panel(page).getByRole('region', { name: 'Database health', exact: true });
  const options = panel(page).getByRole('region', { name: 'Database option changes', exact: true });
  await expect(inventory).toContainText('SYNTHETIC independent inventory');
  await expect(options.locator('time').nth(0)).toHaveAttribute('datetime', from);
  await expect(options.locator('time').nth(1)).toHaveAttribute('datetime', to);
  await expect(panel(page).getByRole('alert').filter({ hasText: 'The health request failed safely.' })).toBeVisible();
  expect(context(page)).toMatchObject({ target, range: 'custom', from, to, databaseId: '7', healthSection: 'databases' });
  await expect(panel(page)).not.toContainText('PRIVATE_PROVIDER_STACK'); expect(state.failures).toEqual([]);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations).toEqual([]);
  await capture(page, `core-outage-databases-${width}`);
});
