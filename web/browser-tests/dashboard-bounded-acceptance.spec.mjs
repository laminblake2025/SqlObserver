import { test, expect } from '@playwright/test';
import { revealHealthSupport } from './redesign-controls.mjs';
import AxeBuilder from '@axe-core/playwright';
import { evidence, database, collector } from '../component-tests/healthFixture.mjs';
import { cpuObservationBinding, cpuObservationAlpha, cpuObservationFixtureReceipt, installCpuObservationHistoryFixture } from '../tools/cpu-observation-history-fixture.mjs';
import { agentActivityHistoryBinding, agentActivityHistoryAlpha, installAgentActivityHistoryFixture } from '../tools/agent-activity-history-fixture.mjs';
import { agAlpha, agFrom, agTo, installAgAccessFixture } from '../component-tests/availabilityEvidenceAccessFixture.mjs';

const origin = 'http://127.0.0.1:4432';
const target = '11111111-1111-4111-8111-000000000001';
const from = '2026-09-26T08:00:00.123456Z', to = '2026-09-26T12:00:00.654321Z';
const failures = new WeakMap();
const context = page => Object.fromEntries(new URLSearchParams(new URL(page.url()).hash.split('?')[1]));

test.beforeEach(async ({ page }) => {
  const errors = []; failures.set(page, errors); page.on('pageerror', error => errors.push(error.message));
  await page.clock.setFixedTime(new Date('2026-10-04T12:00:00Z'));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.context().addCookies(Object.entries({ dashboardRefactor: '1', chartReferenceRole: 'viewer', databaseScopeFixture: '1', resourceInspectionFixture: '1' })
    .map(([name, value]) => ({ name, value, url: origin })));
  await page.route('**/*', route => {
    if (new URL(route.request().url()).origin === origin) return route.continue();
    errors.push(`Unexpected external request: ${route.request().url()}`); return route.abort();
  });
});
test.afterEach(async ({ page }) => expect(failures.get(page)).toEqual([]));

async function capture(page, info, name) {
  const screenshot = info.outputPath(`${name}.png`);
  await page.screenshot({ path: screenshot, fullPage: true });
  await info.attach(name, { path: screenshot, contentType: 'image/png' });
}
async function geometryAndAxe(page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa']).analyze()).violations).toEqual([]);
}

for (const width of [320, 390, 768, 1440]) test(`bounded Health screenshot and keyboard context at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 });
  await page.goto('/#/health?' + new URLSearchParams({ target, range: 'custom', from, to, databaseId: '6', compare: '1' }));
  const frame = page.getByRole('banner', { name: 'Workspace context', exact: true });
  await expect(frame.getByRole('combobox', { name: 'Workspace database', exact: true })).toHaveValue('6');
  await revealHealthSupport(page);
  await expect(page.getByRole('tab', { name: 'Dashboard', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('.server-dashboard-kpis')).toBeVisible();
  const summary = page.locator('summary').filter({ hasText: /^Database scope details$/ });
  await summary.focus(); await summary.press('Enter'); await expect(summary).toBeFocused();
  await expect(summary.locator('..')).toHaveAttribute('open', '');
  await expect(page.getByTestId('database-context-notice')).toContainText('server-wide');
  await summary.press('Enter'); await expect(summary).toBeFocused();
  await expect(summary.locator('..')).not.toHaveAttribute('open', '');
  expect(context(page)).toMatchObject({ target, range: 'custom', from, to, databaseId: '6', compare: '1' });
  await geometryAndAxe(page); await capture(page, info, `health-${width}-keyboard`);
});

test('bounded CPU unavailable component stays a source gap with fresh screenshot', async ({ page }, info) => {
  await page.setViewportSize({ width: 390, height: 1000 });
  const session = await installCpuObservationHistoryFixture(page, { origin, allowOtherApi: true, adjusted: true, componentGap: true });
  const pin = cpuObservationFixtureReceipt(1);
  await page.goto('/#/resources?' + new URLSearchParams({ target: cpuObservationAlpha, range: 'custom', from: cpuObservationBinding.fromUtc, to: cpuObservationBinding.toUtc, cpuObservationRun: pin.runId, cpuObservationRevision: '1' }));
  const exact = page.getByRole('region', { name: 'Selected CPU observation', exact: true });
  await expect(exact).toContainText(pin.runId);
  await expect(exact).toContainText('Source Unavailable');
  await expect(exact).toContainText('Unavailable');
  await expect(page.getByRole('region', { name: 'CPU component observations', exact: true })).toContainText('no other-process percentage is inferred');
  await expect(page.getByRole('region', { name: 'CPU component collection', exact: true })).toContainText('Collection disabled');
  await expect(page.getByRole('button', { name: 'Enable CPU component collection', exact: true })).toHaveCount(0);
  await geometryAndAxe(page); await capture(page, info, 'cpu-unavailable-390');
  expect(session.unexpected).toEqual([]);
});

test('bounded Agent Unknown preserves local source clocks and absent qualification with fresh screenshot', async ({ page }, info) => {
  await page.setViewportSize({ width: 390, height: 1000 });
  const session = await installAgentActivityHistoryFixture(page, { origin, allowOtherApi: true });
  await page.goto('/#/operations?' + new URLSearchParams({ target: agentActivityHistoryAlpha, check: 'agent', range: 'custom', from: agentActivityHistoryBinding.fromUtc, to: agentActivityHistoryBinding.toUtc }));
  const panel = page.getByRole('region', { name: 'SQL Agent activity observations', exact: true });
  const exact = panel.getByRole('region', { name: 'Selected Agent activity receipt', exact: true });
  await expect(exact).toBeVisible();
  await panel.getByText('Browse jobs from selected Agent activity receipt', { exact: true }).press('Enter');
  const jobs = exact.getByRole('region', { name: 'Jobs from selected Agent activity receipt', exact: true });
  await expect(jobs).toContainText('Unknown'); await expect(jobs).toContainText('2026-11-01T01:30:00.1234567');
  await panel.getByText('Exact Agent source headers', { exact: true }).press('Enter');
  await expect(panel).toContainText('Absent');
  await jobs.focus(); await jobs.press('ArrowRight'); await expect(jobs).toBeFocused();
  await geometryAndAxe(page); await capture(page, info, 'agent-unknown-390');
  expect(session.errors).toEqual([]);
});

test('bounded AG Unknown preserves observer visibility and nullable metrics with fresh screenshot', async ({ page }, info) => {
  await page.setViewportSize({ width: 390, height: 1000 });
  const session = await installAgAccessFixture(page, { origin, app: true }); session.settings.secondary = true;
  await page.goto('/#/operations?' + new URLSearchParams({ target: agAlpha, check: 'availability', range: 'custom', from: agFrom, to: agTo }));
  const panel = page.getByRole('region', { name: 'Per-replica availability evidence', exact: true });
  await expect(panel.getByRole('article', { name: 'AG replicas', exact: true }).locator('tbody tr')).toHaveCount(50);
  await expect(panel).toContainText('Unknown'); await expect(panel).toContainText('Secondary: local state only');
  const scroll = panel.getByRole('article', { name: 'AG database replicas', exact: true }).locator('.table-scroll');
  await scroll.focus(); await scroll.press('ArrowRight'); await expect(scroll).toBeFocused();
  await geometryAndAxe(page); await capture(page, info, 'ag-unknown-390');
  expect(session.errors).toEqual([]);
});

// Representative integrated case from dashboard-independent-health.spec.mjs,
// preserving its source-independent assertions while using only fresh output.
test('bounded independent Health outage preserves configuration Rewind and fresh screenshot', async ({ page }, info) => {
  await page.setViewportSize({ width: 390, height: 1000 });
  const selectedTarget = '11111111-1111-4111-8111-000000000002';
  const run = '33333333-3333-4333-8333-333333333333';
  const latest = evidence(selectedTarget); latest.target.collectors.push(collector('engine.configurations'));
  latest.databases.items = [database(7, 'SYNTHETIC independent inventory')];
  const reads = [];
  await page.route('**/api/v1/overview?*', route => {
    if (new URL(route.request().url()).searchParams.get('targetId') !== selectedTarget) return route.fallback();
    return route.fulfill({ status: 503, json: { detail: 'PRIVATE_ANALYTICS_STACK' } });
  });
  await page.route(`**/api/v1/observation-targets/${selectedTarget}/health{,/**}`, async route => {
    const url = new URL(route.request().url()), suffix = url.pathname.split('/health')[1];
    expect(route.request().method()).toBe('GET'); reads.push(url.href);
    if (suffix === '/files') return route.fulfill({ status: 503, json: { detail: 'PRIVATE_PROVIDER_STACK' } });
    let value;
    if (suffix === '' || suffix === '/databases') value = latest[suffix === '' ? 'target' : 'databases'];
    else if (suffix === '/server-configuration-changes') {
      expect(Object.fromEntries(url.searchParams)).toEqual({ fromUtc: from, toUtc: to, limit: '100' });
      value = { targetId: selectedTarget, targetRevision: 1, publicationGeneration: 1, fromUtc: from, toUtc: to, coverage: 'observed_changes_only', nextCursor: null, items: [{ configurationId: 1, configurationName: 'SYNTHETIC independently selected configuration', sourceType: 'int', configuredValue: '8', inUseValue: '8', previousConfiguredValue: '4', previousInUseValue: '4', configuredChanged: true, inUseChanged: true, previousObservedAtUtc: '2026-09-26T07:59:00Z', observedAtUtc: '2026-09-26T10:00:00Z', detectedAtUtc: '2026-09-26T10:00:00Z', runId: run }] };
    } else if (suffix === '/database-option-changes') value = { targetId: selectedTarget, fromUtc: from, toUtc: to, semantics: 'detected_between_consecutive_complete_inventory_runs', items: [] };
    else return route.fallback();
    await route.fulfill({ status: 200, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, json: value });
  });
  await page.goto('/#/health?' + new URLSearchParams({ target: selectedTarget, range: 'custom', from, to, databaseId: '7', healthSection: 'summary', configurationRun: run }));
  const panel = page.getByRole('region', { name: 'Server diagnostics for SYNTHETIC SQL 02', exact: true });
  const selected = panel.locator(`[data-configuration-run="${run}"]`);
  await expect(selected).toContainText('SYNTHETIC independently selected configuration'); await expect(selected).toBeFocused();
  await expect(panel.getByRole('alert').filter({ hasText: 'The health request failed safely.' })).toBeVisible();
  await expect(panel.getByRole('alert').filter({ hasText: 'Overview evidence is temporarily unavailable.' })).toBeVisible();
  await expect(panel.locator('.health-summary')).toContainText('Current evidence');
  await expect(panel).not.toContainText('PRIVATE_PROVIDER_STACK'); await expect(panel).not.toContainText('PRIVATE_ANALYTICS_STACK');
  expect(context(page)).toMatchObject({ target: selectedTarget, range: 'custom', from, to, databaseId: '7', configurationRun: run });
  expect(reads.filter(url => new URL(url).pathname.endsWith('/server-configuration-changes'))).toHaveLength(1);
  await geometryAndAxe(page); await capture(page, info, 'independent-health-outage-390');
});
