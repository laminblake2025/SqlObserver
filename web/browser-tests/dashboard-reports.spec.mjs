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
  const range = page.getByRole('combobox', { name: 'Time range', exact: true });
  const editor = page.getByRole('form', { name: 'Custom UTC time range', exact: true });
  if (await range.inputValue() !== 'custom') await range.selectOption('custom');
  else if (!await editor.isVisible()) await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
  await editor.getByLabel('From (UTC)', { exact: true }).fill('2026-08-01T13:45');
  await editor.getByLabel('To (UTC)', { exact: true }).fill(to);
  await editor.getByRole('button', { name: 'Apply range', exact: true }).click();
  await expect(editor).toHaveCount(0);
}

const reportHref = (reportKind, from = '2026-08-01T13:45:00.123456Z', to = '2026-08-02T13:45:00.654321Z') =>
  `/#/reports?${new URLSearchParams({ target, range: 'custom', from, to, reportKind })}`;
async function auditAndCapture(page, info, name) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`${name}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
  expect(violations).toEqual([]);
  const folder = resolve('../artifacts/dashboard-report-time/screenshots'); await mkdir(folder, { recursive: true });
  await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
  await page.screenshot({ path: resolve(folder, `${name}.png`), fullPage: true });
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

test('Reports shared UTC window and kind survive reload and browser history without generating implicitly', async ({ page }) => {
  await fixture(page); await page.goto(reportHref('performance-window'));
  await expect(kind(page)).toHaveValue('performance-window');
  await expect(page.getByRole('combobox', { name: 'Time range', exact: true })).toHaveValue('custom');
  await generate(page).click(); await expect(html(page)).toBeVisible();
  const first = await observations(page);
  expect(first.observations[0].body.fromUtc).toBe('2026-08-01T13:45:00.123456Z');
  expect(first.observations[0].body.toUtc).toBe('2026-08-02T13:45:00.654321Z');
  await kind(page).selectOption('capacity-readiness');
  await expect(page).toHaveURL(/reportKind=capacity-readiness/);
  await expect(html(page)).toHaveCount(0);
  await page.getByRole('combobox', { name: 'Time range', exact: true }).selectOption('1h');
  await expect(page).toHaveURL(/range=1h/);
  await expect(kind(page)).toHaveValue('capacity-readiness');
  await page.goBack(); await expect(page.getByRole('combobox', { name: 'Time range', exact: true })).toHaveValue('custom');
  await page.goBack(); await expect(kind(page)).toHaveValue('performance-window');
  await page.reload(); await expect(kind(page)).toHaveValue('performance-window');
  await expect(panel(page)).toContainText('13:45:00');
  expect((await observations(page)).observations).toHaveLength(1);
  await generate(page).click(); await expect(html(page)).toBeVisible();
  const restored = await observations(page);
  expect(restored.observations[1].body).toEqual({ ...first.observations[0].body,
    operationId: restored.observations[1].body.operationId });
  expect(restored.observations[1].body.operationId).not.toBe(first.observations[0].body.operationId);
});

test('Reports moving Live context freezes an uncertain request but starts a fresh window after verified success', async ({ page }) => {
  await fixture(page, 'uncertain');
  await page.clock.setFixedTime(new Date('2026-10-02T12:00:00Z'));
  await page.goto(`/#/reports?target=${target}&range=1h&reportKind=performance-window`);
  await generate(page).click(); await expect(panel(page).getByRole('alert')).toBeVisible();
  const first = await observations(page);
  expect(first.observations[0].body).toEqual({ reportKind: 'performance-window', operationId: expect.any(String),
    fromUtc: '2026-10-02T11:00:00.000Z', toUtc: '2026-10-02T12:00:00.000Z' });
  await page.clock.setFixedTime(new Date('2026-10-02T12:02:00Z'));
  await page.locator('.page-actions').getByRole('button', { name: '↻ Refresh', exact: true }).click();
  await page.getByRole('button', { name: 'Show local time', exact: true }).click();
  await generate(page).click(); await expect(html(page)).toBeVisible();
  const recovered = await observations(page);
  expect(recovered.observations[1].raw).toBe(first.observations[0].raw);
  expect(recovered.runs).toHaveLength(1);
  await generate(page).click(); await expect.poll(async () => (await observations(page)).runs.length).toBe(2);
  const next = await observations(page);
  expect(next.observations[2].body.toUtc).toBe('2026-10-02T12:02:00.000Z');
  expect(next.observations[2].body.operationId).not.toBe(first.observations[0].body.operationId);
});

test('Reports URL kind refusal preserves invalid selection until the user chooses a supported type', async ({ page }, info) => {
  await fixture(page); await page.setViewportSize({ width: 320, height: 1000 });
  await page.goto(`${reportHref('performance-window')}&reportKind=capacity-readiness`);
  await expect(panel(page).getByRole('alert')).toContainText('report type');
  await expect(generate(page)).toBeDisabled();
  expect((await observations(page)).observations).toHaveLength(0);
  await page.getByRole('combobox', { name: 'Time range', exact: true }).selectOption('6h');
  expect(await page.evaluate(() => new URLSearchParams(location.hash.split('?')[1]).getAll('reportKind')))
    .toEqual(['performance-window', 'capacity-readiness']);
  await expect(generate(page)).toBeDisabled();
  await auditAndCapture(page, info, 'reports-invalid-kind-320');
  await kind(page).selectOption('capacity-readiness');
  await expect(panel(page).getByRole('alert')).toHaveCount(0);
  expect(await page.evaluate(() => new URLSearchParams(location.hash.split('?')[1]).getAll('reportKind')))
    .toEqual(['capacity-readiness']);
  await generate(page).click(); await expect(html(page)).toBeVisible();
});

test('Reports selected kind refuses an unsupported shared range and can recover without substituting the window', async ({ page }, info) => {
  await fixture(page); await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(reportHref('performance-window', '2026-08-01T00:00:00Z', '2026-08-20T00:00:00Z'));
  await expect(panel(page).getByRole('alert')).toContainText('7 days');
  await expect(generate(page)).toBeDisabled();
  expect((await observations(page)).observations).toHaveLength(0);
  await auditAndCapture(page, info, 'reports-unsupported-window-1440');
  await kind(page).selectOption('capacity-readiness'); await generate(page).click(); await expect(html(page)).toBeVisible();
  const accepted = await observations(page);
  expect(accepted.observations[0].body.fromUtc).toBe('2026-08-01T00:00:00.000Z');
  expect(accepted.observations[0].body.toUtc).toBe('2026-08-20T00:00:00.000Z');
});

test('Instance Health remains explicitly current under Rewind and leaving Reports preserves the global range', async ({ page }) => {
  await fixture(page); await page.goto(reportHref('instance-health'));
  await expect(panel(page)).toContainText('current snapshot');
  await generate(page).click(); await expect(html(page)).toBeVisible();
  const accepted = await observations(page);
  expect(accepted.observations[0].body).toEqual({ reportKind: 'instance-health', operationId: expect.any(String) });
  await page.getByRole('navigation', { name: 'Main navigation' }).getByRole('link', { name: 'Waits', exact: true }).click();
  expect(await page.evaluate(() => new URLSearchParams(location.hash.split('?')[1]).get('from')))
    .toBe('2026-08-01T13:45:00.123456Z');
  await page.goBack(); await expect(kind(page)).toHaveValue('instance-health');
  await expect(page.getByRole('combobox', { name: 'Time range', exact: true })).toHaveValue('custom');
});

for (const width of [320, 390, 768, 1440]) test(`Reports workspace has readable shared time controls and accessible exports at ${width}px`, async ({ page }, info) => {
  await fixture(page); await page.setViewportSize({ width, height: 1000 });
  await page.goto(`/#/reports?target=${target}`);
  await kind(page).selectOption('capacity-readiness'); await windowFields(page); await generate(page).click();
  await expect(html(page)).toBeVisible(); await html(page).focus(); await expect(html(page)).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(panel(page).getByRole('link', { name: 'Download section CSV', exact: true })).toBeFocused();
  await auditAndCapture(page, info, `reports-workspace-${width}`);
});
