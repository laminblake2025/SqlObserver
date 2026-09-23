import { test, expect } from '@playwright/test';
import { mockEvidence, routeFor, targetA, targetB, fromUtc, toUtc, queryHash, planB } from './fixtures.mjs';

test('deadlock continuation keeps the exact investigation window', async ({ page }) => {
  const state = await mockEvidence(page);
  await page.goto(routeFor('deadlocks'));
  const next = page.getByRole('button', { name: /next/i });
  await expect(next).toBeEnabled();
  await next.click();
  await expect.poll(() => state.requests.filter(u => u.pathname.endsWith('/deadlocks') && u.searchParams.has('cursor')).length).toBe(1);
  await expect(next).toBeDisabled();
  const pages = state.requests.filter(u => u.pathname.endsWith('/deadlocks'));
  expect(pages.every(u => u.searchParams.get('fromUtc') === fromUtc && u.searchParams.get('toUtc') === toUtc)).toBe(true);
  await expect(page.getByText('Deadlock evidence is unavailable.', { exact: true })).toHaveCount(0);
});

test('second plan selection shows its metrics and survives refresh', async ({ page }) => {
  const state = await mockEvidence(page);
  await page.goto(routeFor('queries'));
  const rows = page.locator('tbody tr').filter({ hasText: queryHash.slice(0, 12) });
  await expect(rows).toHaveCount(3);
  const second = rows.nth(1).getByRole('button');
  await second.click();
  await expect(second).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('.kpi').filter({ hasText: 'CPU ms' }).locator('strong')).toHaveText('222');
  await expect(page.locator('.query-investigation')).toContainText(planB);
  const before = state.requests.filter(u => u.pathname.endsWith('/top')).length;
  await page.getByRole('button', { name: /Refresh/, exact: false }).first().click();
  await expect.poll(() => state.requests.filter(u => u.pathname.endsWith('/top')).length).toBeGreaterThan(before);
  await expect(second).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('.kpi').filter({ hasText: 'CPU ms' }).locator('strong')).toHaveText('222');
});

test('database and source filters are sent to the server', async ({ page }) => {
  const state = await mockEvidence(page);
  await page.goto(routeFor('queries'));
  await page.getByRole('combobox', { name: /^Database/ }).selectOption('6');
  await expect.poll(() => state.requests.some(u => u.pathname.endsWith('/top') && u.searchParams.get('databaseId') === '6')).toBe(true);
  await page.getByRole('combobox', { name: /^Source/ }).selectOption('plan_cache');
  await expect.poll(() => state.requests.some(u => u.pathname.endsWith('/top') && u.searchParams.get('source') === 'plan_cache')).toBe(true);
});

test('overview keeps evidence while refreshing and after a failed refresh', async ({ page }) => {
  const state = await mockEvidence(page);
  await page.goto(routeFor('overview', ''));
  const table = page.getByRole('table', { name: 'SQL core freshness and latest target-scoped counts' });
  await expect(table).toContainText('Fixture SQL 1');
  let release;
  state.overviewGate = new Promise(resolve => { release = resolve; });
  state.failOverview = true;
  await page.getByRole('button', { name: /Refresh/ }).first().click();
  await expect(page.getByText(/Refreshing Overview/i)).toBeVisible();
  await expect(table).toContainText('Fixture SQL 1');
  release();
  await expect(page.getByText(/Overview evidence is temporarily unavailable/)).toBeVisible();
  await expect(table).toContainText('Fixture SQL 1');
});

test('independent file failure preserves server health and provides retry', async ({ page }) => {
  const state = await mockEvidence(page);
  state.failFiles = true;
  await page.goto(routeFor('health'));
  await expect(page.locator('.health-metric').filter({ hasText: 'User connections' }).locator('strong')).toHaveText('42 connections');
  const tab = page.getByRole('tab', { name: /Databases/i });
  await tab.click();
  const retry = page.getByRole('button', { name: /Retry/i });
  await expect(retry).toBeVisible();
  state.failFiles = false;
  await retry.click();
  await expect(page.getByRole('heading', { name: 'Logical-file health' })).toBeVisible();
});

test('late response from old target cannot replace newly selected target', async ({ page }) => {
  const state = await mockEvidence(page);
  let release;
  state.targetGate = new Promise(resolve => { release = resolve; });
  try {
    await page.goto(routeFor('health', targetA));
    await page.getByLabel('Target server').selectOption(targetB);
    await expect(page.locator('.health-metric').filter({ hasText: 'User connections' }).locator('strong')).toHaveText('77 connections');
    release();
    await expect(page.locator('.health-metric').filter({ hasText: 'User connections' }).locator('strong')).toHaveText('77 connections');
    await expect(page.getByLabel('Target server')).toHaveValue(targetB);
  } finally { release(); }
});

test('query investigation is keyboard operable and announces selection', async ({ page }) => {
  await mockEvidence(page);
  await page.goto(routeFor('queries'));
  const second = page.locator('tbody tr').nth(1).getByRole('button');
  await expect(second).toBeVisible();
  // Reach the row through the actual tab order, including navigation and filters.
  // Programmatic focus would conceal a broken keyboard path to the evidence.
  for (let tab = 0; tab < 60 && !await second.evaluate(element => element === document.activeElement); tab++) {
    await page.keyboard.press('Tab');
  }
  await expect(second).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(second).toBeFocused();
  await expect(second).toHaveAttribute('aria-pressed', 'true');
  await expect(page.locator('.kpi').filter({ hasText: 'CPU ms' }).locator('strong')).toHaveText('222');
  await expect(page.locator('section[aria-live] table')).toHaveCount(0);
});

test('relative navigation captures a fresh window and explicitly moves it to now', async ({ page }) => {
  const state = await mockEvidence(page);
  const initial = Date.parse(toUtc) + 60_000;
  await page.clock.setFixedTime(initial);
  await page.goto(`/#/overview?target=${targetA}&range=1h`);
  await expect(page.getByRole('table')).toBeVisible();
  const navigated = initial + 20 * 60_000;
  await page.clock.setFixedTime(navigated);
  await page.getByRole('navigation').getByRole('link', { name: 'Query performance' }).click();
  const latest = () => state.requests.filter(u => u.pathname.endsWith('/top')).at(-1);
  await expect.poll(() => latest()?.searchParams.get('toUtc')).toBe(new Date(navigated).toISOString());
  const second = page.locator('tbody tr').nth(1).getByRole('button');
  await second.click();
  await page.clock.setFixedTime(navigated + 10 * 60_000);
  const count = state.requests.filter(u => u.pathname.endsWith('/top')).length;
  await page.getByRole('button', { name: /^.*Refresh$/ }).first().click();
  await expect.poll(() => state.requests.filter(u => u.pathname.endsWith('/top')).length).toBeGreaterThan(count);
  expect(latest().searchParams.get('toUtc')).toBe(new Date(navigated).toISOString());
  await expect(second).toHaveAttribute('aria-pressed', 'true');
  await page.getByRole('button', { name: 'Move window to now' }).click();
  await expect.poll(() => latest()?.searchParams.get('toUtc')).toBe(new Date(navigated + 10 * 60_000).toISOString());
});

test('acknowledgement cannot race a retained alert refresh', async ({ page }) => {
  const state = await mockEvidence(page);
  state.alertState = 'firing';
  await page.goto(routeFor('alerts'));
  const acknowledge = page.getByRole('button', { name: 'Acknowledge', exact: true });
  await expect(acknowledge).toBeEnabled();
  let release;
  state.alertGate = new Promise(resolve => { release = resolve; });
  try {
    await page.getByRole('button', { name: /Refresh/ }).first().click();
    await expect(acknowledge).toBeDisabled();
    release();
    await expect(acknowledge).toBeEnabled();
    await acknowledge.click();
    await expect(page.getByText(/Acknowledgement recorded/)).toBeVisible();
    await expect(acknowledge).toHaveCount(0);
  } finally { release(); }
});
