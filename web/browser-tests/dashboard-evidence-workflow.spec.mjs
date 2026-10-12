import { revealDashboardSupport, expandDisclosure } from './redesign-controls.mjs';
import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';

const origin = 'http://127.0.0.1:4432', target = '11111111-1111-4111-8111-000000000001';
const from = '2026-09-26T08:00:00.123456Z', to = '2026-09-26T12:00:00.654321Z';
const attempts = new WeakMap();
test.beforeEach(async ({ page }) => {
  const attempt = { reads: [], pending: new Set(), errors: [] }; attempts.set(page, attempt);
  await page.clock.setFixedTime(new Date('2026-10-03T12:00:01Z'));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.context().addCookies(Object.entries({ dashboardRefactor: '1', chartReferenceRole: 'viewer',
    databaseScopeFixture: '1', timelineFixture: '1', alertLifecycleHistoryFixture: '1' }).map(([name, value]) => ({ name, value, url: origin })));
  page.on('pageerror', error => attempt.errors.push(error.message));
  page.on('request', request => { if (new URL(request.url()).pathname.startsWith('/api/')) { attempt.reads.push(request); attempt.pending.add(request); } });
  page.on('requestfinished', request => attempt.pending.delete(request)); page.on('requestfailed', request => attempt.pending.delete(request));
});
test.afterEach(async ({ page }) => {
  expect(attempts.get(page).errors).toEqual([]);
  expect(attempts.get(page).reads.every(request => request.method() === 'GET'), 'Disclosure is a read-only investigation action').toBe(true);
});
async function settled(page) {
  await page.evaluate(() => new Promise(done => requestAnimationFrame(() => requestAnimationFrame(done))));
  await expect.poll(() => attempts.get(page).pending.size).toBe(0);
}

for (const width of [320, 390, 768, 1440]) test(`dashboard answers precede folded raw evidence with keyboard access at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 });
  await page.goto('/#/health?' + new URLSearchParams({ target, range: 'custom', from, to, databaseId: '6', compare: '1', healthSection: 'summary' }));
  await revealDashboardSupport(page);await expandDisclosure(page,'Retained alerts, server lifecycle & SQL Agent evidence');await expandDisclosure(page,'Current blocking graph evidence');
  const timeline = page.getByRole('region', { name: 'Investigation timeline', exact: true });
  const history = page.getByRole('region', { name: 'Alert lifecycle history', exact: true });
  const agent = page.getByRole('region', { name: 'SQL Agent first-observed evidence', exact: true });
  const timelineBrowser = timeline.getByText(/^Browse loaded timeline events \(\d+\)$/).locator('xpath=parent::details');
  const historyBrowser = history.getByText('Browse alert lifecycle events', { exact: true }).locator('xpath=parent::details');
  const agentBrowser = agent.getByText('Browse SQL Agent failure evidence', { exact: true }).locator('xpath=parent::details');
  await expect(page.locator('.server-dashboard-charts .chart-plot-scroll').first()).toBeVisible();
  await expect(agentBrowser.locator('summary')).toBeVisible(); await settled(page);
  const workspace = page.locator('summary').filter({ hasText: /^Investigation workspace$/ }).locator('..');
  await expect(workspace).not.toHaveAttribute('open', '');
  await expect(page.getByRole('heading', { name: 'Server summary for SYNTHETIC SQL 01', exact: true })).toHaveCount(0);
  await expect(page.locator('.health-screen').getByRole('button', { name: 'Close', exact: true })).toHaveCount(0);
  const navBeforeKpis = await page.evaluate(() => Boolean(document.querySelector('.server-navigation')?.compareDocumentPosition(document.querySelector('.server-dashboard-kpis')) & Node.DOCUMENT_POSITION_FOLLOWING));
  expect(navBeforeKpis, 'Embedded section navigation precedes heavy dashboard content').toBe(true);
  const selectors = ['.dashboard-head-blockers', '.server-dashboard-kpis', '.dashboard-timeline', '.server-dashboard-charts',
    '.server-dashboard-blocking', '.alert-lifecycle-history', '.server-agent-evidence'];
  const ordered = await page.evaluate(selectors => {
    const elements = selectors.map(selector => document.querySelector('.server-dashboard ' + selector));
    return elements.every(Boolean) && elements.slice(1).every((element, index) => Boolean(elements[index].compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING));
  }, selectors);
  expect(ordered, 'Answers, timeline and charts/current blocking precede raw history').toBe(true);
  for (const browser of [timelineBrowser, historyBrowser, agentBrowser]) await expect(browser).not.toHaveAttribute('open', '');
  await expect(timeline.getByRole('button', { name: /^Align charts:/ })).toHaveCount(0);
  await expect(history.getByRole('table')).toHaveCount(0); await expect(agent.getByRole('table')).toHaveCount(0);
  const readCount = attempts.get(page).reads.length, hash = new URL(page.url()).hash;
  const disclosures = [[timelineBrowser, 'Loaded timeline event list'], [historyBrowser, 'Received alert lifecycle event table'], [agentBrowser, 'SQL Agent failure observations']];
  for (const [browser, regionName] of disclosures) {
    const summary = browser.locator('summary'); await summary.focus(); await page.keyboard.press('Enter');
    await expect(browser).toHaveAttribute('open', ''); await expect(summary).toBeFocused();
    await page.keyboard.press('Tab'); const region = browser.getByRole('region', { name: regionName, exact: true }); await expect(region).toBeFocused();
    const focus = await region.evaluate(element => ({ width: parseFloat(getComputedStyle(element).outlineWidth), style: getComputedStyle(element).outlineStyle }));
    expect(focus.style).not.toBe('none'); expect(focus.width).toBeGreaterThanOrEqual(2);
    await summary.focus(); await page.keyboard.press('Enter'); await expect(browser).not.toHaveAttribute('open', ''); await expect(summary).toBeFocused();
  }
  await settled(page); expect(attempts.get(page).reads).toHaveLength(readCount); expect(new URL(page.url()).hash).toBe(hash);
  expect(Object.fromEntries(new URLSearchParams(hash.split('?')[1]))).toMatchObject({ target, range: 'custom', from, to, databaseId: '6', compare: '1' });
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`dashboard-evidence-workflow-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = resolve('../artifacts/dashboard-evidence-workflow/screenshots'); await mkdir(directory, { recursive: true });
  await page.evaluate(() => { document.activeElement?.blur(); scrollTo({ top: 0, left: 0, behavior: 'instant' }); });
  await page.screenshot({ path: resolve(directory, `dashboard-evidence-workflow-${width}.png`), fullPage: true });
});
