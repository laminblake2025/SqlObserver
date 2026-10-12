import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const first = '11111111-1111-4111-8111-000000000001';
const second = '11111111-1111-4111-8111-000000000002';
const from = '2026-09-26T08:00:00.000Z', to = '2026-09-26T12:00:00.000Z';
const href = (surface = 'resources', target = first, start = from, end = to) =>
  `/#/${surface}?${new URLSearchParams({ target, range: 'custom', from: start, to: end })}`;

async function open(page, mode = 'normal', surface = 'resources') {
  await page.context().addCookies([
    { name: 'chartReferenceRole', value: 'viewer', url: 'http://127.0.0.1:4321' },
    { name: 'chartReferenceMode', value: mode, url: 'http://127.0.0.1:4321' },
  ]);
  await page.goto(href(surface));
  const summary = page.getByText('Baseline reference details', { exact: true });
  await summary.click();
  await expect(summary.locator('xpath=parent::details').getByText('SQL p10–p90 bands use matching hourly history', { exact: false })).toBeVisible();
}
const cpuChart = page => page.locator('.server-dashboard-chart').filter({ has: page.getByRole('heading', { name: 'SQL scheduler CPU', exact: true }) });
const memoryChart = page => page.locator('.server-dashboard-chart').filter({ has: page.getByRole('heading', { name: 'Memory pressure', exact: true }) });
async function openChartReferences(chart) {
  await chart.getByText('Chart semantics & references', { exact: true }).click();
  await chart.getByText('View chart references', { exact: true }).click();
}

test('Resources binds all SQL reads to the target and historical window and scales memory bands to GiB', async ({ page }) => {
  const requests = [], errors = [];
  page.on('request', request => { if (request.url().includes('/analytics/sql-baselines?')) requests.push(new URL(request.url())); });
  page.on('pageerror', error => errors.push(error.message));
  await open(page);
  await expect(cpuChart(page).locator('.chart-baseline-band')).toHaveCount(4);
  await openChartReferences(memoryChart(page));
  await expect(memoryChart(page).getByRole('cell', { name: '8–14 / 10 GiB', exact: true })).toHaveCount(12);
  await expect.poll(() => requests.length).toBe(7);
  for (const url of requests) {
    expect(url.pathname).toContain(first);
    expect(url.searchParams.get('targetRevision')).toBe('1');
    expect(url.searchParams.get('catalogVersion')).toBe('2');
    expect(Date.parse(url.searchParams.get('toUtc'))).toBe(Date.parse('2026-09-26T00:00:00Z'));
    expect(Date.parse(url.searchParams.get('toUtc')) - Date.parse(url.searchParams.get('fromUtc'))).toBe(28 * 86400000);
  }
  expect(errors).toEqual([]);
});

test('Partial SQL history keeps coverage visible and missing reference hours remain gaps', async ({ page }) => {
  await open(page, 'partial');
  await openChartReferences(cpuChart(page));
  await expect(cpuChart(page).getByRole('cell', { name: /SQL scheduler CPU baseline · partial.*coverage 25%/ })).toHaveCount(4);
  await page.context().addCookies([{ name: 'chartReferenceMode', value: 'gap', url: 'http://127.0.0.1:4321' }]);
  await page.reload();
  await expect(cpuChart(page).locator('.chart-baseline-band')).toHaveCount(2);
});

for (const mode of ['unavailable', 'empty', 'foreign', 'error']) {
  test(`${mode} SQL evidence does not draw bands or hide current observations`, async ({ page }) => {
    await page.context().addCookies([
      { name: 'chartReferenceRole', value: 'viewer', url: 'http://127.0.0.1:4321' },
      { name: 'chartReferenceMode', value: mode, url: 'http://127.0.0.1:4321' },
    ]);
    await page.goto(href());
    await expect(page.locator('.compact-evidence-action').filter({ hasText: mode === 'foreign' || mode === 'error' ? 'SQL baseline references are unavailable.' : 'No SQL baseline covers these UTC hours.' })).toBeVisible();
    await expect(cpuChart(page).locator('.chart-baseline-band')).toHaveCount(0);
    await expect(cpuChart(page).getByRole('img')).toBeVisible();
  });
}

test('Changing target during delayed references discards the previous target bands', async ({ page }) => {
  let release, notify;
  const gate = new Promise(resolve => { release = resolve; });
  const started = new Promise(resolve => { notify = resolve; });
  await page.route(`**/${first}/analytics/sql-baselines?*`, async route => { notify(); await gate; await route.continue().catch(() => {}); });
  await page.context().addCookies([{ name: 'chartReferenceRole', value: 'viewer', url: 'http://127.0.0.1:4321' }]);
  await page.goto(href());
  await expect(cpuChart(page).getByRole('img')).toBeVisible();
  await started;
  await page.getByRole('combobox', { name: 'Target server' }).selectOption(second);
  await expect(page).toHaveURL(/range=custom/);
  await expect(page.getByRole('heading', { name: 'Resources for SYNTHETIC SQL 02' })).toBeVisible();
  const selected = new URLSearchParams(page.url().split('?')[1]);
  expect(selected.get('from')).toBe(from); expect(selected.get('to')).toBe(to);
  release();
  await expect(cpuChart(page).locator('.chart-baseline-band')).toHaveCount(4);
  await expect(cpuChart(page).locator('.chart-baseline-band title').first()).toHaveText(/70–100 %$/);
  await page.locator('.sidebar-footer').scrollIntoViewIfNeeded();
  const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze();
  expect(result.violations).toEqual([]);
});

test('The server dashboard renders SQL workload and memory reference bands for the shared Rewind window', async ({ page }) => {
  await open(page, 'normal', 'health');
  const workload = page.locator('.server-dashboard-chart').filter({ has: page.getByRole('heading', { name: 'SQL workload', exact: true }) });
  await expect(workload.locator('.chart-baseline-band')).toHaveCount(4);
  await page.getByText('Resource charts, wait history & baselines', { exact: true }).click();
  await openChartReferences(workload);
  await expect(workload.getByRole('cell', { name: '8–14 / 10 batches/sec', exact: true })).toHaveCount(4);
  await expect(memoryChart(page).locator('.chart-baseline-band')).toHaveCount(12);
});
