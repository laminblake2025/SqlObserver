import { test, expect } from '@playwright/test';
import { cpuObservationBinding, cpuObservationAlpha, installCpuObservationHistoryFixture } from '../tools/cpu-observation-history-fixture.mjs';

const origin = 'http://127.0.0.1:4322';
const errors = new WeakMap();
test.beforeEach(async ({ page }) => {
  const issues = []; errors.set(page, issues);
  page.on('pageerror', error => issues.push(error.message));
  await page.route('**/*', route => new URL(route.request().url()).origin === origin ? route.continue() : route.abort());
  await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin }, { name: 'chartReferenceRole', value: 'viewer', url: origin }]);
});
test.afterEach(async ({ page }) => expect(errors.get(page)).toEqual([]));

for (const range of ['1h', '6h', '24h', '7d']) test(`overview renders connected lines without persistent dots for ${range}`, async ({ page }, info) => {
  await page.setViewportSize({ width: range === '24h' ? 390 : 1440, height: 1100 });
  await page.goto('/#/overview?' + new URLSearchParams({ target: cpuObservationAlpha, range }));
  await page.getByRole('combobox', { name: 'Workload breakdown', exact: true }).selectOption('server');
  const chart = page.locator('svg.overview-chart').first();
  await expect(chart).toBeVisible();
  const paths = chart.locator('path.chart-series');
  await expect(paths).toHaveCount(1);
  expect(await paths.first().getAttribute('d')).toContain('L');
  const targets = chart.locator('circle.chart-point-hit-target');
  expect(await targets.count()).toBeGreaterThan(1);
  await expect(targets.first()).toHaveCSS('opacity', '0');
  await targets.first().hover();
  await expect(targets.first()).toHaveCSS('opacity', '1');
  expect(await targets.first().locator('title').textContent()).toContain('samples');
  await page.mouse.move(0, 0);
  await expect(targets.first()).toHaveCSS('opacity', '0');
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  if (range === '24h') await page.screenshot({ path: info.outputPath('overview-lines-mobile.png') });
  if (range === '1h') await page.screenshot({ path: info.outputPath('overview-lines-desktop.png') });
});

test('overview keeps missing samples as gaps and an isolated observation visible', async ({ page }) => {
  await page.route('**/api/v1/overview?*', async route => {
    const response = await route.fetch(), body = await response.json();
    for (const evidence of body.evidence) for (const series of evidence.series) {
      if (series.points.length > 2) series.points[1] = { ...series.points[1], value: null, samples: 0 };
    }
    await route.fulfill({ response, json: body });
  });
  await page.goto('/#/overview?' + new URLSearchParams({ target: cpuObservationAlpha, range: '1h' }));
  await page.getByRole('combobox', { name: 'Workload breakdown', exact: true }).selectOption('server');
  const chart = page.locator('svg.overview-chart').first();
  await expect(chart.locator('path.chart-series')).toHaveCount(2);
  const paths = await chart.locator('path.chart-series').evaluateAll(elements => elements.map(element => element.getAttribute('d')));
  expect(paths[0]).not.toContain('L'); expect(paths[1]).toContain('L');
  await expect(chart.locator('circle.chart-marker:not(.chart-point-hit-target)')).toHaveCount(1);
  await expect(chart.locator('circle.chart-marker:not(.chart-point-hit-target)')).toHaveCSS('opacity', '1');
});

test('previous overview comparison stays dashed and has no persistent dots', async ({ page }) => {
  await page.goto('/#/overview?' + new URLSearchParams({ target: cpuObservationAlpha, range: '24h', compare: '1' }));
  await page.getByRole('combobox', { name: 'Workload breakdown', exact: true }).selectOption('server');
  const chart = page.locator('svg.overview-chart').first();
  await expect(chart.locator('path.chart-series')).toHaveCount(2);
  await expect(chart.locator('path.chart-series[stroke-dasharray]')).toHaveCount(1);
  expect(await chart.locator('path.chart-series[stroke-dasharray]').getAttribute('d')).toContain('L');
  await expect(chart.locator('circle.chart-marker:not(.chart-point-hit-target)')).toHaveCount(0);
});

for (const gap of [false, true]) test(`CPU lines preserve independent component gaps and quality: gap=${gap}`, async ({ page }, info) => {
  const session = await installCpuObservationHistoryFixture(page, { origin, allowOtherApi: true, adjusted: gap, componentGap: gap });
  await page.goto('/#/resources?' + new URLSearchParams({ target: cpuObservationAlpha, range: 'custom', from: cpuObservationBinding.fromUtc, to: cpuObservationBinding.toUtc }));
  const sql = page.getByRole('img', { name: 'SQL-process CPU independent observations and gaps' });
  const host = page.getByRole('img', { name: 'Target-host CPU independent observations and gaps' });
  await expect(sql).toBeVisible(); await expect(host).toBeVisible();
  await expect(sql.locator('path.chart-series')).toHaveCount(gap ? 0 : 1);
  await expect(sql.locator('[data-cpu-gap]')).toHaveCount(gap ? 1 : 0);
  await expect(host.locator('path.chart-series')).toHaveCount(1);
  if (gap) await expect(host.locator('[data-quality="provider_adjusted"]')).toHaveCount(1);
  else await expect(sql.locator('circle.chart-marker:not(.chart-point-hit-target)')).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'CPU component collection', exact: true })).toContainText('Collection disabled');
  await expect(page.getByRole('button', { name: 'Enable CPU component collection', exact: true })).toHaveCount(0);
  expect(session.unexpected).toEqual([]);
  if (gap) await sql.screenshot({ path: info.outputPath('cpu-lines-gap.png') });
});
