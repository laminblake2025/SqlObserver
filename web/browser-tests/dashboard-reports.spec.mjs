import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';

const origin = 'http://127.0.0.1:4331';
const target = '11111111-1111-4111-8111-000000000001';
const otherTarget = '11111111-1111-4111-8111-000000000002';
const panel = page => page.getByRole('region', { name: /^Reports for / });
const generate = page => panel(page).getByRole('button', { name: 'Generate report', exact: true });
const html = page => panel(page).getByRole('link', { name: 'Open printable HTML', exact: true });
const kind = page => panel(page).getByRole('combobox', { name: 'Report type', exact: true });
async function fixture(page, mode = 'ready') {
  await page.context().addCookies(Object.entries({ dashboardRefactor: '1', chartReferenceRole: 'viewer',
    reportWorkflowFixture: '1', reportWorkflowSession: crypto.randomUUID(), reportWorkflowMode: mode })
    .map(([name, value]) => ({ name, value, url: origin })));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
}
async function observations(page) {
  const response = await page.request.get('/__reports-workflow');
  expect(response.ok()).toBe(true); return response.json();
}
async function windowFields(page, to = '2026-08-02T13:45') {
  await panel(page).getByLabel('From (UTC)', { exact: true }).fill('2026-08-01T13:45');
  await panel(page).getByLabel('To (UTC, exclusive)', { exact: true }).fill(to);
}

test('Reports workspace recovers an accepted request with an uncertain receipt and creates a new run only after confirmed success', async ({ page }) => {
  await fixture(page, 'uncertain'); const errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto(`/#/reports?target=${target}`);
  await kind(page).selectOption('performance-window'); await windowFields(page);
  await generate(page).focus(); await page.keyboard.press('Enter');
  await expect(panel(page).getByRole('alert')).toHaveText('The report response could not be verified.');
  await expect(html(page)).toHaveCount(0); await expect(page.locator('body')).not.toContainText('synthetic-secret');
  const accepted = await observations(page); expect(accepted.runs).toHaveLength(1);
  await generate(page).click();
  await expect(html(page)).toHaveAttribute('href', `/api/v1/observation-targets/${target}/reports/${accepted.runs[0].runId}/html`);
  const recovered = await observations(page);
  expect(recovered.runs).toHaveLength(1); expect(recovered.observations).toHaveLength(2);
  expect(recovered.observations[1].raw).toBe(recovered.observations[0].raw);
  await generate(page).click();
  await expect.poll(async () => (await observations(page)).runs.length).toBe(2);
  const renewed = await observations(page);
  expect(renewed.observations[2].body.operationId).not.toBe(recovered.observations[0].body.operationId);
  await expect(html(page)).toHaveAttribute('href', `/api/v1/observation-targets/${target}/reports/${renewed.runs[1].runId}/html`);
  expect(errors).toEqual([]);
});

test('Reports workspace changes discard the prior request and never reuse another target or window operation', async ({ page }) => {
  await fixture(page, 'uncertain'); await page.goto(`/#/reports?target=${target}`);
  await kind(page).selectOption('performance-window'); await windowFields(page);
  await generate(page).click(); await expect(panel(page).getByRole('alert')).toBeVisible();
  const first = await observations(page);
  await windowFields(page, '2026-08-03T13:45'); await generate(page).click(); await expect(html(page)).toBeVisible();
  const second = await observations(page);
  expect(second.observations[1].body.operationId).not.toBe(first.observations[0].body.operationId);
  expect(second.observations[1].body.toUtc).toBe('2026-08-03T13:45:00.000Z');
  await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(otherTarget);
  await expect(panel(page).getByRole('heading')).toHaveText('Reports for SYNTHETIC SQL 02');
  await expect(html(page)).toHaveCount(0); await generate(page).click(); await expect(html(page)).toBeVisible();
  const third = await observations(page);
  expect(third.observations[2].targetId).toBe(otherTarget);
  expect(new Set(third.observations.map(item => item.body.operationId)).size).toBe(3);
});

for (const width of [320, 390, 768, 1440]) test(`Reports workspace has readable dark controls and accessible exports at ${width}px`, async ({ page }, info) => {
  await fixture(page); await page.setViewportSize({ width, height: 1000 });
  await page.goto(`/#/reports?target=${target}`);
  await kind(page).selectOption('capacity-readiness'); await windowFields(page); await generate(page).click();
  await expect(html(page)).toBeVisible(); await html(page).focus(); await expect(html(page)).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(panel(page).getByRole('link', { name: 'Download section CSV', exact: true })).toBeFocused();
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach('reports-workspace-axe', { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
  expect(violations).toEqual([]);
  const folder = resolve('../artifacts/dashboard-reports/screenshots'); await mkdir(folder, { recursive: true });
  await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
  await page.screenshot({ path: resolve(folder, `reports-workspace-${width}.png`), fullPage: true });
});
