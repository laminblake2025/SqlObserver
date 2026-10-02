import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const origin = 'http://127.0.0.1:4322';
const alpha = '11111111-1111-4111-8111-000000000001';
const beta = '11111111-1111-4111-8111-000000000002';
const from = '2026-09-26T08:00:00.000Z', to = '2026-09-26T12:00:00.000Z';
const href = (surface, target = alpha) => `/#/${surface}?${new URLSearchParams({ target, range: 'custom', from, to })}`;
const errors = new WeakMap();
test.beforeEach(async ({ page }) => {
  errors.set(page, []); page.on('pageerror', error => errors.get(page).push(error.message));
  await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin },
    { name: 'chartReferenceRole', value: 'admin', url: origin }, { name: 'databaseScopeFixture', value: '1', url: origin }]);
});
test.afterEach(async ({ page }) => { expect(errors.get(page)).toEqual([]); });

for (const [kind, patch] of [['resource', { server: {} }], ['resource', { unit: [] }],
  ['issue', { server: {} }], ['issue', { observedAtUtc: '2026-02-30T12:00:00Z' }]])
  test(`built Overview rejects malformed ${kind} ${Object.keys(patch)[0]} before rendering`, async ({ page }) => {
    await page.route('**/api/v1/overview?*', async route => {
      const response = await route.fetch(), body = await response.json();
      const row = body.evidence[0];
      if (kind === 'resource') row.resources = [{ targetId: row.targetId, server: 'SYNTHETIC SQL 01',
        label: 'engine.cpu', unit: '%', state: 'observed', value: 20, observedAtUtc: from, ...patch }];
      else row.issues = [{ targetId: row.targetId, server: 'SYNTHETIC SQL 01', title: 'Synthetic finding',
        detail: 'Synthetic evidence', destination: 'activity', priority: 1, observedAtUtc: from, ...patch }];
      await route.fulfill({ response, json: body });
    });
    await page.goto(href('overview', ''));
    await expect(page.getByRole('alert').filter({ hasText: `Invalid Overview ${kind}.` })).toBeVisible();
    await expect(page.getByRole('region', { name: 'Server health grid', exact: true })).toHaveCount(0);
  });

test('built database search follows a server change and navigates using the new native context', async ({ page }) => {
  await page.goto(href('health')); await page.keyboard.press('Control+k');
  const dialog = page.getByRole('dialog', { name: 'Jump to evidence' });
  await dialog.getByRole('button', { name: 'Databases', exact: true }).click();
  await expect(dialog.getByRole('button', { name: 'Warehouse Database ID 6', exact: true })).toBeVisible();
  await page.evaluate(hash => { location.hash = hash; }, href('health', beta).slice(1));
  await expect(dialog.getByRole('button', { name: 'Warehouse Database ID 6', exact: true })).toHaveCount(0);
  const result = dialog.getByRole('button', { name: 'Warehouse 02 Database ID 6', exact: true });
  await expect(result).toBeVisible(); await result.focus(); await result.press('Enter');
  await expect(dialog).not.toBeVisible();
  await expect.poll(() => Object.fromEntries(new URLSearchParams(new URL(page.url()).hash.split('?')[1])))
    .toMatchObject({ target: beta, databaseId: '6', range: 'custom', from, to });
});

test('built configuration recovery requires explicit current-state readback in Rewind', async ({ page }) => {
  let enabled = false, mutations = 0, reads = 0;
  await page.route(`**/${alpha}/health/server-configuration-snapshot?*`, route => {
    reads += 1;
    return route.fulfill({ status: 200, contentType: 'application/json', json: { targetId: alpha, targetRevision: 1,
      runId: null, observedAtUtc: null, serverMajorVersion: 16, collectorEnabled: enabled, lastOutcome: null,
      coverage: 'never_collected', items: [], nextCursor: null } });
  });
  await page.route(`**/${alpha}/health/server-configuration-collection`, route => {
    const body = route.request().postDataJSON(); mutations += 1;
    expect(body).toEqual({ expectedTargetRevision: 1, expectedEnabled: enabled, enabled: !enabled });
    enabled = body.enabled;
    return route.fulfill({ status: 200, contentType: 'application/json', json: { targetId: beta,
      targetRevision: 1, collectorEnabled: enabled, scheduleRevision: 2 } });
  });
  await page.goto(href('health'));
  const panel = page.getByRole('region', { name: 'Current server settings', exact: true });
  await expect(panel).toContainText('even when the timeline is rewound');
  await panel.getByRole('button', { name: 'Enable configuration collection', exact: true }).click();
  await expect(panel.getByRole('alert')).toContainText('outcome is unknown');
  await expect(panel.getByRole('button', { name: 'Enable configuration collection', exact: true })).toBeDisabled();
  expect(mutations).toBe(1); const previousReads = reads;
  await panel.getByRole('button', { name: 'Reload current state', exact: true }).click();
  await expect(panel.getByRole('button', { name: 'Disable configuration collection', exact: true })).toBeEnabled();
  expect(reads).toBeGreaterThan(previousReads); expect(mutations).toBe(1);
  expect(Object.fromEntries(new URLSearchParams(new URL(page.url()).hash.split('?')[1])))
    .toMatchObject({ target: alpha, range: 'custom', from, to });
});

for (const width of [320, 1440]) test(`built database search remains dark and keyboard usable at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 900 }); await page.goto(href('health'));
  await page.keyboard.press('Control+k'); const dialog = page.getByRole('dialog', { name: 'Jump to evidence' });
  await dialog.getByRole('button', { name: 'Databases', exact: true }).click();
  const search = dialog.getByRole('searchbox', { name: 'Search databases', exact: true });
  await search.fill('Warehouse'); await search.focus(); await search.press('ArrowDown');
  await expect(dialog.getByRole('button', { name: 'Warehouse Database ID 6', exact: true })).toBeFocused();
  await page.keyboard.press('ArrowUp'); await expect(search).toBeFocused();
  expect(await dialog.evaluate(element => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`built-search-${width}-axe`, { body: JSON.stringify(violations), contentType: 'application/json' }); expect(violations).toEqual([]);
  await search.evaluate(element => element.blur()); await dialog.evaluate(element => { element.scrollTop = 0; });
  await page.evaluate(() => window.scrollTo({ top: 0, left: 0, behavior: 'instant' }));
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-context-search/screenshots/', import.meta.url));
  await mkdir(directory, { recursive: true }); await page.screenshot({ path: `${directory}/built-search-${width}.png`, fullPage: false });
  await page.keyboard.press('Escape'); await expect(dialog).not.toBeVisible();
});
