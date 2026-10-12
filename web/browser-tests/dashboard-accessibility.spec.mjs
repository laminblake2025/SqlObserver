import { revealHealthSupport, revealDashboardSupport, expandDisclosure } from './redesign-controls.mjs';
import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';

const origin = 'http://127.0.0.1:4432';
const target = '11111111-1111-4111-8111-000000000002';
const screens = [
  ['fleet', 'overview'],
  ['server', 'health'],
  ['activity', 'activity'],
  ['queries', 'queries'],
  ['deadlocks', 'deadlocks'],
  ['health-checks', 'operations'],
  ['alerts', 'alerts'],
  ['resources', 'resources'],
  ['waits', 'waits'],
];

async function auditResponsiveState(page, testInfo, name) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth), `${name} must fit its viewport`).toBeLessThanOrEqual(1);
  const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze();
  await testInfo.attach(`${name}-accessibility-results`, { body: JSON.stringify(results.violations, null, 2), contentType: 'application/json' });
  expect(results.violations, JSON.stringify(results.violations.map(v => ({ id: v.id, nodes: v.nodes.map(n => n.target) })))).toEqual([]);
}

async function saveFullPage(page, filename) {
  const folder = resolve('../artifacts/dashboard-refactor/screenshots');
  await mkdir(folder, { recursive: true });
  await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
  await page.screenshot({ path: resolve(folder, filename), fullPage: true });
}

async function open(page, surface, disclose = true) {
  await page.context().addCookies([
    { name: 'dashboardRefactor', value: '1', url: origin },
    { name: 'chartReferenceRole', value: 'viewer', url: origin },
    ...(surface === 'resources' ? [{ name: 'resourceInspectionFixture', value: '1', url: origin }] : []),
  ]);
  await page.goto(`/#/${surface}?${new URLSearchParams({ target, range: '1h', ...(surface === 'queries' ? { queryView: 'cache' } : {}) })}`);
  if (surface === 'alerts') await expect(page.getByRole('heading', { name: 'Alert triage', exact: true })).toBeVisible();
  else await expect(page.getByRole('combobox', { name: 'Time range', exact: true })).toHaveValue('1h');
  await page.waitForLoadState('networkidle');
  if(surface==='health' && disclose)await revealHealthSupport(page);
  if (surface === 'deadlocks') await expect(page.getByRole('img', { name: 'Deadlock relationship diagram' })).toBeVisible();
  if (surface === 'queries') await expect(page.getByRole('region', { name: 'Plan cache interval ranking', exact: true })).toContainText('bbbbbbbbbbbb');
  if (surface === 'resources') await expect(page.getByRole('region', { name: 'Per-NUMA page life expectancy', exact: true }).locator('.overview-chart')).toHaveCount(2);
  if (surface === 'waits') await expect(page.getByRole('region', { name: 'Wait categories over time', exact: true }).getByRole('img', { name: /^Server wait time by category/ })).toHaveCount(1);
}

for (const width of [320, 390, 768, 1440]) {
  for (const [name, surface] of screens) {
    test(`${name}: dark, accessible and responsive at ${width}px`, async ({ page }, testInfo) => {
      await page.setViewportSize({ width, height: 1000 });
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      await open(page, surface);
      if (surface === 'waits') {
        await page.getByRole('combobox', { name: 'Query Store category', exact: true }).selectOption('6');
        await expect.poll(() => new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('waitCategory')).toBe('6');
      }
      await expect(page.locator('main')).toBeVisible();
      {
        const plots = page.locator('.chart-plot-scroll');
        if (surface === 'resources') expect(await plots.count()).toBeGreaterThanOrEqual(6);
        if (['fleet', 'server', 'waits'].includes(name)) expect(await plots.count()).toBeGreaterThan(0);
        const geometry = await plots.evaluateAll(elements => elements.map(element => ({
          named: Boolean(element.getAttribute('aria-label')), focusable: element.tabIndex === 0,
          axisSizes: [...element.querySelectorAll('svg text')].map(text =>
            Number.parseFloat(getComputedStyle(text).fontSize) * Math.abs(text.getScreenCTM()?.a ?? 0)),
        })));
        for (const plot of geometry) {
          expect(plot.named && plot.focusable).toBe(true);
          expect(plot.axisSizes.length).toBeGreaterThan(0);
          expect(Math.min(...plot.axisSizes)).toBeGreaterThanOrEqual(11);
        }
      }
      expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
      expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth), 'Page must fit; wide evidence scrolls locally').toBeLessThanOrEqual(1);
      const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze();
      await testInfo.attach('accessibility-results', { body: JSON.stringify(results.violations, null, 2), contentType: 'application/json' });
      expect(results.violations, JSON.stringify(results.violations.map(v => ({ id: v.id, nodes: v.nodes.map(n => n.target) })))).toEqual([]);
      expect(errors).toEqual([]);
      const folder = resolve('../artifacts/dashboard-refactor/screenshots');
      await mkdir(folder, { recursive: true });
      await page.screenshot({ path: resolve(folder, `${name}-${width}.png`), fullPage: true });
      if (surface === 'waits') {
        await page.evaluate(() => { location.hash += '&waitCategory=11'; });
        await expect(page.getByText('The requested Query Store wait category is invalid. Choose a supported category; no other category has been selected.', { exact: true })).toBeVisible();
        await auditResponsiveState(page, testInfo, 'waits-invalid-category');
        await saveFullPage(page, `waits-invalid-${width}.png`);
      }
    });
  }
}

test('keyboard skip link, chart evidence, time editor and palette remain accessible', async ({ page }) => {
  await open(page, 'health', false);
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
  await expect(page.getByLabel(/^From \(/)).toBeFocused();
  expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations).toEqual([]);
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: /^Search/ }).click();
  await expect(page.getByRole('searchbox', { name: 'Search servers' })).toBeFocused();
  expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations).toEqual([]);
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

for (const width of [320, 390, 768, 1440]) {
  test(`selected blocking chain keeps keyboard controls and responsive evidence at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.context().addCookies([{ name: 'blockingChainFixture', value: '1', url: origin }]);
    await open(page, 'activity');
    await expandDisclosure(page,'Advanced session filters');
    await page.getByRole('button',{name:'Inspect 51/0',exact:true}).first().click();
    await page.getByRole('region',{name:'Session details',exact:true}).getByRole('button',{name:'Blocking chain',exact:true}).click();
    const chain = page.getByRole('combobox', { name: 'Blocking chain (capture)', exact: true });
    const action = page.getByRole('button', { name: 'Filter chain: Session 51', exact: true });
    await action.focus();
    await action.press('Enter');
    await expect(chain).toHaveValue('session:51');
    await expect(chain).toHaveValue('session:51');
    await expect(action).toBeFocused();
    const clear = page.getByRole('button', { name: 'Clear chain filter', exact: true });
    await clear.focus();
    await clear.press('Enter');
    await expect(chain).toHaveValue('');
    await chain.selectOption('session:81');
    await expect(page.getByRole('region', { name: 'Loaded session observations', exact: true })).toContainText('81');
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze();
    await testInfo.attach('accessibility-results', { body: JSON.stringify(results.violations, null, 2), contentType: 'application/json' });
    expect(results.violations).toEqual([]);
    const folder = resolve('../artifacts/dashboard-refactor/screenshots');
    await mkdir(folder, { recursive: true });
    // Keyboard navigation scrolls the viewport; reset it before a full-page
    // capture so sticky navigation is rendered at the top of the document.
    await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
    await page.screenshot({ path: resolve(folder, `activity-chain-${width}.png`), fullPage: true });
  });
}

for (const width of [320, 390, 768, 1440]) {
  for (const surface of ['activity', 'resources']) {
    test(`database context on ${surface} supports keyboard use and responsive evidence at ${width}px`, async ({ page }, testInfo) => {
      await page.setViewportSize({ width, height: 1000 });
      await page.context().addCookies([
        { name: 'dashboardRefactor', value: '1', url: origin },
        { name: 'chartReferenceRole', value: 'viewer', url: origin },
        { name: 'databaseScopeFixture', value: '1', url: origin },
      ]);
      await page.goto(`/#/${surface}?${new URLSearchParams({ target, range: '1h', databaseId: '6' })}`);
      const database = page.getByRole('combobox', { name: 'Workspace database', exact: true });
      await expect(database).toHaveValue('6');
      await expect(database.locator('option:checked')).toHaveText('Warehouse 02 · ID 6');
      const notice = page.getByTestId('database-context-notice');
      await expect(notice).toContainText('Database ID 6');
      await expect(notice).toContainText('server-wide');
      const contextDetails = notice.locator('xpath=parent::details');
      await expect(contextDetails).not.toHaveAttribute('open', '');
      await contextDetails.locator('summary').focus(); await page.keyboard.press('Enter');
      await expect(contextDetails).toHaveAttribute('open', ''); await expect(notice).toBeVisible();

      // Native select navigation must update shared context without a mouse.
      await database.focus();
      await expect(database).toBeFocused();
      await database.press('Home');
      await database.press('Enter');
      await expect(database).toHaveValue('');
      await expect(notice).toHaveCount(0);
      await database.press('End');
      await database.press('Enter');
      await expect(database).toHaveValue('6');
      await expect(database).toBeFocused();
      await expect(notice).toContainText('Database ID 6');
      await page.waitForLoadState('networkidle');

      expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth), 'Selected database context must fit the viewport').toBeLessThanOrEqual(1);
      const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze();
      await testInfo.attach('accessibility-results', { body: JSON.stringify(results.violations, null, 2), contentType: 'application/json' });
      expect(results.violations, JSON.stringify(results.violations.map(v => ({ id: v.id, nodes: v.nodes.map(n => n.target) })))).toEqual([]);
      const folder = resolve('../artifacts/dashboard-refactor/screenshots');
      await mkdir(folder, { recursive: true });
      await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
      await page.screenshot({ path: resolve(folder, `database-${surface}-${width}.png`), fullPage: true });
    });
  }
}

for (const width of [320, 390, 768, 1440]) {
  test(`investigation timeline supports keyboard alignment and responsive evidence at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.context().addCookies([
      { name: 'timelineFixture', value: '1', url: origin },
      { name: 'dashboardRefactor', value: '1', url: origin },
      { name: 'chartReferenceRole', value: 'viewer', url: origin },
      { name: 'databaseScopeFixture', value: '1', url: origin },
    ]);
    const timelineTarget = '11111111-1111-4111-8111-000000000001';
    const from = '2026-09-26T08:00:00.000Z';
    const to = '2026-09-26T12:00:00.000Z';
    await page.goto(`/#/health?${new URLSearchParams({ target: timelineTarget, range: 'custom', from, to, databaseId: '6' })}`);
    const timeline = page.getByRole('region', { name: 'Investigation timeline', exact: true });
    await expect(timeline.getByRole('heading', { name: 'Investigation timeline', exact: true })).toBeVisible();
    const category = timeline.getByRole('combobox', { name: 'Timeline event category', exact: true });
    await category.selectOption('plan_regression');
    const eventBrowser = timeline.getByText(/^Browse loaded timeline events \(\d+\)$/).locator('xpath=parent::details');
    await expect(eventBrowser).not.toHaveAttribute('open', '');
    await eventBrowser.locator('summary').focus(); await page.keyboard.press('Enter');
    const align = timeline.getByRole('button', { name: /^Align charts: Plan regression comparison/ });
    await expect(align).toHaveCount(1);
    await align.focus();
    await align.press('Enter');
    await expect(align).toBeFocused();
    await expect(align).toHaveAttribute('aria-pressed', 'true');
    await expect(timeline.locator('.dashboard-timeline-status').filter({ hasText: 'Chart crosshair:' })).toContainText('Chart crosshair:');
    await expect(timeline.locator('.dashboard-timeline-status').filter({ hasText: 'Chart crosshair:' })).not.toHaveAttribute('role', 'status');
    await expect(timeline.locator('.dashboard-timeline-status').filter({ hasText: 'Chart crosshair:' })).not.toHaveAttribute('aria-live', /.+/);
    await expect(timeline.locator('.dashboard-timeline-status').filter({ hasText: 'Chart crosshair:' }).locator('time')).toHaveAttribute('datetime', '2026-09-26T11:00:00.000Z');
    const selectionQuery = new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
    expect(selectionQuery.get('timelineEvent')).toMatch(/^plan:/);
    expect(selectionQuery.get('timelineAt')).toBe('2026-09-26T11:00:00.000Z');
    await page.reload();
    await expect(timeline.getByRole('link', { name: 'Open selected event evidence', exact: true })).toBeVisible();
    await expect(eventBrowser).not.toHaveAttribute('open', '');
    await timeline.getByRole('button', { name: 'Show selected event in timeline list', exact: true }).press('Enter');
    await expect(align).toBeFocused();
    await expect(align).toHaveAttribute('aria-pressed', 'true');
    await expect(timeline.locator('.dashboard-timeline-status').filter({ hasText: 'Chart crosshair:' }).locator('time')).toHaveAttribute('datetime', '2026-09-26T11:00:00.000Z');
    expect(new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('databaseId')).toBe('6');
    const evidence = timeline.getByRole('link', { name: /^Open evidence: Plan regression comparison/ });
    await expect(evidence).toBeVisible();
    await expect(evidence).toHaveAttribute('href', /databaseId=6/);
    await evidence.focus();
    await expect(evidence).toBeFocused();
    const coverage = timeline.getByText('Timeline coverage', { exact: true });
    await coverage.focus();
    await coverage.press('Enter');
    await expect(coverage).toBeFocused();
    const coverageDetails = coverage.locator('xpath=parent::details');
    await expect(coverageDetails).toHaveAttribute('open', '');
    await expect(coverageDetails).toContainText('different time meanings');
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth), 'Timeline evidence must fit; timestamps and links must wrap locally').toBeLessThanOrEqual(1);
    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze();
    await testInfo.attach('accessibility-results', { body: JSON.stringify(results.violations, null, 2), contentType: 'application/json' });
    expect(results.violations, JSON.stringify(results.violations.map(v => ({ id: v.id, nodes: v.nodes.map(n => n.target) })))).toEqual([]);
    const folder = resolve('../artifacts/dashboard-refactor/screenshots');
    await mkdir(folder, { recursive: true });
    await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
    await page.screenshot({ path: resolve(folder, `timeline-${width}.png`), fullPage: true });
    const timelineBounds = await timeline.boundingBox();
    expect(timelineBounds).not.toBeNull();
    // Keep the full-page capture at the tested viewport. A taller viewport for
    // this detail capture gives the sticky navigation room above the section.
    await page.setViewportSize({ width, height: Math.ceil(timelineBounds.height) + 400 });
    await timeline.evaluate(element => element.scrollIntoView({ block: 'center' }));
    await timeline.screenshot({ path: resolve(folder, `timeline-detail-${width}.png`) });
  });
}

for (const width of [320, 390, 768, 1440]) {
  test(`shared resource keyboard inspection is accessible and responsive at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.context().addCookies([
      { name: 'dashboardRefactor', value: '1', url: origin },
      { name: 'chartReferenceRole', value: 'viewer', url: origin },
      { name: 'databaseScopeFixture', value: '1', url: origin },
      { name: 'resourceInspectionFixture', value: '1', url: origin },
    ]);
    const from = '2026-09-26T08:00:00.000Z', to = '2026-09-26T12:00:00.000Z';
    await page.goto(`/#/resources?${new URLSearchParams({ target, range: 'custom', from, to, databaseId: '6' })}`);
    const resources = page.getByRole('region', { name: 'Resources for SYNTHETIC SQL 02', exact: true });
    const file = resources.getByRole('button', { name: 'View read and write latency trend for database 6, file Warehouse_data', exact: true });
    await file.focus();
    await file.scrollIntoViewIfNeeded();
    await expect(file).toBeVisible();
    await expect(file).toBeInViewport();
    const fileActionBounds = await file.boundingBox();
    expect(fileActionBounds).not.toBeNull();
    expect(fileActionBounds.width).toBeGreaterThan(0);
    expect(fileActionBounds.height).toBeGreaterThan(0);
    expect(fileActionBounds.height, 'File history action must remain a readable compact button').toBeLessThanOrEqual(64);
    await file.press('Enter');
    await expect(resources.locator('.overview-chart')).toHaveCount(7);
    const alignment = resources.getByRole('region', { name: 'Resource chart alignment', exact: true });
    const input = alignment.getByLabel('Resource inspection time (UTC)', { exact: true });
    await input.fill('2026-09-26T10:30:00.125');
    const align = alignment.getByRole('button', { name: 'Align resource charts', exact: true });
    await align.focus(); await align.press('Enter');
    await expect(align).toBeFocused();
    await expect(alignment.getByRole('status').locator('time')).toHaveAttribute('datetime', '2026-09-26T10:30:00.125Z');
    await expect(resources.locator('.chart-crosshair')).toHaveCount(7);
    await page.reload();
    await expect(file).toHaveAttribute('aria-pressed', 'true');
    await expect(resources.locator('.chart-crosshair')).toHaveCount(7);
    await expect(alignment.getByRole('status').locator('time')).toHaveAttribute('datetime', '2026-09-26T10:30:00.125Z');
    await auditResponsiveState(page, testInfo, 'resource-selected-inspection');
    await saveFullPage(page, `resource-inspection-${width}.png`);
    await input.fill('2026-09-26T12:00');
    await align.press('Enter');
    await expect(input).toHaveAttribute('aria-invalid', 'true');
    await expect(alignment.getByRole('alert')).toContainText('before its end');
    await expect(resources.locator('.chart-crosshair')).toHaveCount(0);
    await auditResponsiveState(page, testInfo, 'resource-invalid-inspection');
    await saveFullPage(page, `resource-inspection-invalid-${width}.png`);
    const query = new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
    expect(Object.fromEntries(query)).toMatchObject({ target, range: 'custom', from, to, databaseId: '6' });
  });
}

for (const width of [320, 390, 768, 1440]) {
  test(`restored missing resource selections stay explicit and accessible at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.clock.install({ time: new Date('2026-09-26T12:00:00Z') });
    await page.context().addCookies([
      { name: 'dashboardRefactor', value: '1', url: origin },
      { name: 'chartReferenceRole', value: 'viewer', url: origin },
      { name: 'resourceInspectionFixture', value: '1', url: origin },
    ]);
    await page.goto(`/#/resources?${new URLSearchParams({ target, range: '1h', resourceDatabaseId: '5', resourceFileId: '99', numaNodeId: '99', resourceAt: '2026-09-26T10:00:00.000Z' })}`);
    const resources = page.getByRole('region', { name: 'Resources for SYNTHETIC SQL 02', exact: true });
    await expect(resources).toContainText('Requested NUMA node 99 is not in the loaded node inventory. No other node has been selected.');
    await expect(resources).toContainText('This exact file is not in received pages. Its numeric identity is retained; no other file is selected.');
    await expect(resources).toContainText('Requested resource inspection time is outside the loaded shared window.');
    await expect(resources.locator('.chart-crosshair')).toHaveCount(0);
    await auditResponsiveState(page, testInfo, 'resource-missing-url-selections');
    await saveFullPage(page, `resource-unavailable-selection-${width}.png`);
    const clear = resources.getByRole('button', { name: 'Clear resource selections', exact: true });
    await clear.focus(); await clear.press('Enter');
    await expect.poll(() => new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('resourceFileId')).toBe(null);
  });

  test(`restored activity metadata and missing request remain accessible without protected SQL at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.clock.install({ time: new Date('2026-09-26T12:00:00Z') });
    await page.context().addCookies([
      { name: 'dashboardRefactor', value: '1', url: origin },
      { name: 'chartReferenceRole', value: 'viewer', url: origin },
      { name: 'sessionSelectionFixture', value: '1', url: origin },
    ]);
    let protectedReads = 0;
    page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/activity/live/query')) protectedReads++; });
    const parameters = new URLSearchParams({ target, range: '1h', sessionSnapshot: '88888888-8888-4888-8888-888888888888', sessionObserved: '2026-09-26T11:30:00.123456Z', sessionIdentity: 'c'.repeat(64) });
    await page.goto('/#/activity?' + parameters);
    const detail = page.getByRole('region', { name: 'Session details', exact: true });
    await expect(detail.getByRole('heading', { name: 'Session 51', exact: true })).toBeVisible();
    await expect(detail.getByRole('button', { name: 'Load captured SQL', exact: true })).toHaveCount(0);
    await detail.getByRole('button',{name:'Captured SQL',exact:true}).click();
    await expect(detail).toContainText('Captured SQL is hidden. Query Text Reader access is required for this server.');
    const clearSelection = page.getByRole('button', { name: 'Clear session selection', exact: true });
    await clearSelection.focus(); await expect(clearSelection).toBeFocused();
    expect(protectedReads).toBe(0);
    await auditResponsiveState(page, testInfo, 'activity-restored-metadata');
    await saveFullPage(page, `activity-restored-selection-${width}.png`);
    parameters.set('sessionIdentity', 'f'.repeat(64));
    await page.evaluate(query => { location.hash = '#/activity?' + query; }, parameters.toString());
    await expect(detail).toHaveCount(0);
    await expect(page.locator('.activity-capture-screen')).toContainText('The requested session was not found in this captured snapshot.');
    await auditResponsiveState(page, testInfo, 'activity-missing-request');
    await saveFullPage(page, `activity-unavailable-selection-${width}.png`);
    expect(protectedReads).toBe(0);
    const clear = page.getByRole('button', { name: 'Clear session selection', exact: true });
    await clear.focus(); await clear.press('Enter');
    await expect.poll(() => new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('sessionIdentity')).toBe(null);
  });
}

for (const width of [320, 390, 768, 1440]) {
  test(`common incident workspace draft saved and unavailable states remain accessible at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.context().addCookies([
      { name: 'dashboardRefactor', value: '1', url: origin },
      { name: 'chartReferenceRole', value: 'viewer', url: origin },
      { name: 'databaseScopeFixture', value: '1', url: origin },
      { name: 'incidentWorkspaceFixture', value: '1', url: origin },
      { name: 'resourceInspectionFixture', value: '1', url: origin },
    ]);
    const from = '2026-09-26T08:00:00.000Z', to = '2026-09-26T12:00:00.000Z';
    await page.goto(`/#/resources?${new URLSearchParams({ target, range: 'custom', from, to, databaseId: '6' })}`);
    await openInvestigationWorkspace(page);
    const open = page.getByRole('button', { name: 'Save investigation', exact: true });
    await open.focus(); await open.press('Enter');
    const title = page.getByLabel('Incident title', { exact: true });
    await expect(title).toBeFocused();
    await title.fill(`SYNTHETIC workspace investigation ${width}`);
    await page.getByLabel('Notes', { exact: true }).fill('Synthetic notes for the selected window.\nNo connected VM evidence.');
    await auditResponsiveState(page, testInfo, 'incident-workspace-draft');
    await saveFullPage(page, `incident-workspace-draft-${width}.png`);
    await page.getByRole('button', { name: 'Save incident', exact: true }).press('Enter');
    await expect(page.getByRole('heading', { name: `SYNTHETIC workspace investigation ${width}`, exact: true })).toBeVisible();
    await expect(page.getByText('No analytics evidence packets existed for this window when it was saved. This does not mean the server was healthy.', { exact: true })).toBeVisible();
    const selection = new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
    expect(selection.get('incident')).toMatch(/^[0-9a-f-]{36}$/);
    expect(Object.fromEntries(selection)).toMatchObject({ target, range: 'custom', from, to, databaseId: '6' });
    await auditResponsiveState(page, testInfo, 'incident-workspace-saved');
    await saveFullPage(page, `incident-workspace-saved-${width}.png`);
    await page.goto(`/#/health?${new URLSearchParams({ target, range: 'custom', from, to, incident: 'aaaaaaaa-aaaa-4aaa-8aaa-000000000001' })}`);
    await expect(page.getByText('This saved investigation was not found. Automatically correlated incidents remain available in Analytics.', { exact: true })).toBeVisible();
    await expect(page.locator('.saved-incident')).toHaveCount(0);
    await auditResponsiveState(page, testInfo, 'incident-workspace-unavailable');
    await saveFullPage(page, `incident-workspace-unavailable-${width}.png`);
  });
}

for (const width of [320, 390, 768, 1440]) {
  test(`authorized server selector keeps keyboard focus and responsive evidence at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.context().addCookies([{ name: 'databaseScopeFixture', value: '1', url: origin }]);
    await open(page, 'health');
    const fleet = page.getByRole('combobox', { name: 'Target server', exact: true });
    await expect(fleet).toBeVisible();await expect(fleet.locator('option[value!=""]')).toHaveCount(10);
    await expect(fleet).toHaveValue(target);
    await page.getByRole('link', { name: 'Resources', exact: true }).click();
    await expect(page).toHaveURL(/#\/resources\?/);await expect(fleet).toHaveValue(target);
    await fleet.focus();await fleet.press('ArrowUp');await fleet.press('Enter');
    await expect(fleet).toBeFocused();await expect(fleet).toHaveValue('11111111-1111-4111-8111-000000000001');
    await expect(page).toHaveURL(/#\/resources\?.*target=11111111-1111-4111-8111-000000000001/);
    const database = page.getByRole('combobox', { name: 'Workspace database', exact: true });
    await database.focus();
    await expect(database).toBeFocused();
    await expect(database).toBeInViewport();
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
    const results = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze();
    await testInfo.attach('accessibility-results', { body: JSON.stringify(results.violations, null, 2), contentType: 'application/json' });
    expect(results.violations, JSON.stringify(results.violations.map(v => ({ id: v.id, nodes: v.nodes.map(n => n.target) })))).toEqual([]);
    const folder = resolve('../artifacts/dashboard-refactor/screenshots');
    await mkdir(folder, { recursive: true });
    await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
    await page.screenshot({ path: resolve(folder, `workspace-fleet-${width}.png`), fullPage: true });
    await fleet.screenshot({ path: resolve(folder, `workspace-fleet-detail-${width}.png`) });
  });
}

for (const width of [320, 390, 768, 1440]) {
  test(`current alert URL selection preserves accessible target and fleet detail at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.context().addCookies([
      { name: 'dashboardRefactor', value: '1', url: origin },
      { name: 'chartReferenceRole', value: 'viewer', url: origin },
      { name: 'alertSelectionFixture', value: '1', url: origin },
    ]);
    const firstTarget = '11111111-1111-4111-8111-000000000001';
    const pagedAlert = '66666666-6666-4666-8666-666666666666';
    for (const mode of ['target', 'fleet']) {
      const params = new URLSearchParams({ range: 'custom', from: '2026-09-30T08:00:00.000Z', to: '2026-09-30T12:00:00.000Z', alert: pagedAlert });
      params.set(mode === 'target' ? 'target' : 'alertTarget', mode === 'target' ? firstTarget : target);
      await page.goto(`/#/alerts?${params}`);
      const detail = page.getByRole('complementary', { name: 'SYNTHETIC paged alert', exact: true });
      await expect(detail.getByRole('heading', { name: 'SYNTHETIC paged alert', exact: true })).toBeVisible();
      await expect(detail).toContainText(pagedAlert);
      const select = page.getByRole('button', { name: mode === 'target' ? 'SYNTHETIC paged alert' : 'Select SYNTHETIC paged alert on SYNTHETIC SQL 02', exact: true });
      await expect(select).toHaveAttribute('aria-pressed', 'true');
      await select.focus();
      await select.press('Enter');
      await expect(select).toBeFocused();
      await expect(select).toHaveAttribute('aria-pressed', 'true');
      await expect(detail).toBeVisible();
      await auditResponsiveState(page, testInfo, `${mode}-selected-alert`);
      await saveFullPage(page, `selected-alert-${mode}-${width}.png`);
    }
  });

  test(`query palette context and unavailable database remain accessible at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.context().addCookies([
      { name: 'dashboardRefactor', value: '1', url: origin },
      { name: 'chartReferenceRole', value: 'viewer', url: origin },
      { name: 'databaseScopeFixture', value: '1', url: origin },
      { name: 'paletteCatalogFixture', value: '1', url: origin },
    ]);
    for (const databaseId of ['6', '99']) {
      await page.goto(`/#/health?${new URLSearchParams({ target, range: '1h', databaseId })}`);
      await expect(page.getByRole('combobox', { name: 'Workspace database', exact: true })).toHaveValue(databaseId);
      const search = page.getByRole('button', { name: /^Search/ });
      await search.focus();
      await search.press('Enter');
      const dialog = page.getByRole('dialog', { name: 'Jump to evidence', exact: true });
      await expect(dialog.getByRole('searchbox', { name: 'Search servers', exact: true })).toBeFocused();
      const queryMode = dialog.getByRole('button', { name: 'Query hash', exact: true });
      await queryMode.focus();
      await queryMode.press('Enter');
      await expect(queryMode).toHaveAttribute('aria-pressed', 'true');
      const input = dialog.getByRole('spinbutton', { name: 'Database ID', exact: true });
      await expect(input).toBeEnabled();
      await expect(input).toHaveValue(databaseId === '6' ? '6' : '');
      if (databaseId === '99') {
        await expect(dialog.getByText("Workspace database ID 99 is absent from this server's query database catalog. Choose an available database or enter its ID to check history.", { exact: true })).toBeVisible();
      }
      await input.focus();
      await expect(input).toBeFocused();
      await auditResponsiveState(page, testInfo, `palette-database-${databaseId}`);
      const folder = resolve('../artifacts/dashboard-refactor/screenshots');
      await mkdir(folder, { recursive: true });
      await dialog.screenshot({ path: resolve(folder, `palette-${databaseId === '6' ? 'context' : 'unavailable'}-detail-${width}.png`) });
      await saveFullPage(page, `palette-${databaseId === '6' ? 'context' : 'unavailable'}-${width}.png`);
      await page.keyboard.press('Escape');
      await expect(dialog).not.toBeVisible();
      await expect(search).toBeFocused();
    }
  });

  test(`fleet signal evidence links support keyboard investigation at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.context().addCookies([
      { name: 'dashboardRefactor', value: '1', url: origin },
      { name: 'chartReferenceRole', value: 'viewer', url: origin },
    ]);
    await page.goto('/#/overview?range=1h');
    const grid = page.getByRole('region', { name: 'Server health grid', exact: true });
    const server = grid.getByRole('link', { name: 'SYNTHETIC SQL 02', exact: true });
    await expect(server).toBeVisible();
    await page.getByRole('checkbox', { name: 'Waits & HA', exact: true }).check();
    const row = grid.getByRole('row').filter({ has: page.getByRole('link', { name: 'SYNTHETIC SQL 02', exact: true }) });
    for (const [label, destination] of [['SQL scheduler CPU', 'resources'], ['OS free via SQL', 'resources'], ['Waits', 'waits'], ['Blocking', 'activity'], ['HA', 'operations']]) {
      const link = row.getByRole('link', { name: new RegExp(`^${label}:`) });
      await expect(link).toHaveCount(1);
      await expect(link).toHaveAttribute('href', new RegExp(`#/${destination}\\?target=${target}`));
      await link.focus();
      await expect(link).toBeFocused();
    }
    await auditResponsiveState(page, testInfo, 'fleet-evidence-links');
    await saveFullPage(page, `fleet-evidence-links-${width}.png`);
    const waits = row.getByRole('link', { name: /^Waits:/ });
    await waits.focus();
    await waits.press('Enter');
    await expect(page).toHaveURL(new RegExp(`#/waits\\?target=${target}`));
  });
}

for (const width of [320, 390, 768, 1440]) {
  test(`dashboard summaries disclose partial baseline and temporal context accessibly at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.context().addCookies([
      { name: 'dashboardRefactor', value: '1', url: origin },
      { name: 'chartReferenceRole', value: 'viewer', url: origin },
      { name: 'databaseScopeFixture', value: '1', url: origin },
      { name: 'timelineFixture', value: '1', url: origin },
      { name: 'investigationContextFixture', value: '1', url: origin },
      { name: 'baselineKpiFixture', value: '1', url: origin },
      { name: 'dashboardBaselineState', value: 'partial', url: origin },
      { name: 'dashboardObservationState', value: 'partial', url: origin },
    ]);
    await page.goto(`/#/health?${new URLSearchParams({ target: '11111111-1111-4111-8111-000000000001', range: 'custom', from: '2026-09-26T08:00:00.000Z', to: '2026-09-26T12:00:00.000Z', databaseId: '6', healthSection: 'summary' })}`);
    await page.getByRole('button', { name: 'Show UTC time', exact: true }).click();
    await revealHealthSupport(page);await revealDashboardSupport(page);
    const cards = page.getByRole('region', { name: 'Latest SQL buckets and hourly baselines', exact: true });
    const cpu = cards.getByRole('region', { name: 'SQL scheduler CPU baseline comparison', exact: true });
    await expect(cpu.locator('.dashboard-baseline-value strong')).toHaveText('70');
    await expect(cpu).toContainText('Reference quality: partial.');
    await expect(cpu).toContainText('Partial SQL observations; this mean describes only available samples.');
    const baselineDetails = cpu.getByText('SQL scheduler CPU source details', { exact: true });
    await baselineDetails.focus(); await baselineDetails.press('Enter');
    await expect(baselineDetails).toBeFocused();
    await expect(cpu.locator('details')).toHaveAttribute('open', '');
    await expect(cpu).toContainText('UTC hour 155');
    const attention = page.getByRole('region', { name: 'What needs attention', exact: true });
    const finding = attention.getByRole('link', { name: /^Plan regression comparison.*database 6/ }).locator('xpath=parent::li');
    const temporalDetails = finding.getByText('Observed temporal context (2)', { exact: true });
    await temporalDetails.focus(); await temporalDetails.press('Enter');
    await expect(temporalDetails).toBeFocused();
    await expect(finding.getByRole('link', { name: 'Warehouse options detected', exact: true })).toBeVisible();
    await expect(finding.getByRole('link', { name: 'Server configuration detected', exact: true })).toBeVisible();
    await expect(finding).toContainText('Detected 8 min before this last observed query group');
    await expect(finding).toContainText('Temporal proximity does not establish causation or the actual change time.');
    await auditResponsiveState(page, testInfo, 'dashboard-summaries-expanded');
    await saveFullPage(page, `dashboard-summary-${width}.png`);
    // Capture the expanded evidence itself in addition to the entire workspace.
    const folder = resolve('../artifacts/dashboard-refactor/screenshots');
    await mkdir(folder, { recursive: true });
    // Element screenshots scroll the panel under the sticky mobile workspace.
    // A document-space page clip preserves its unobscured scroll-top layout.
    await page.evaluate(() => window.scrollTo(0, 0));
    const clip = await attention.evaluate(element => {
      const rect = element.getBoundingClientRect();
      return { x: rect.x + scrollX, y: rect.y + scrollY, width: rect.width, height: rect.height };
    });
    await page.screenshot({ path: resolve(folder, `dashboard-investigation-detail-${width}.png`), fullPage: true, clip });
  });
}

for (const width of [320, 390, 768, 1440]) {
  test(`invalid Health response keeps a readable accessible error and independent dashboard at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin }, { name: 'chartReferenceRole', value: 'viewer', url: origin }]);
    const crashes = []; page.on('pageerror', error => crashes.push(error.message));
    await page.route(`**/observation-targets/${target}/health`, async route => {
      const response = await route.fetch(), body = await response.json();
      body.coreMetrics = null;
      await route.fulfill({ response, json: body });
    });
    await page.goto(`/#/health?${new URLSearchParams({ target, range: 'custom', from: '2026-09-26T08:00:00.000Z', to: '2026-09-26T12:00:00.000Z', healthSection: 'summary' })}`);
    await revealHealthSupport(page);
    await expect(page.getByRole('alert').filter({ hasText: 'Health evidence returned an invalid or mismatched response. Refresh to retry.' })).toBeVisible();
    await revealHealthSupport(page);
    await expect(page.getByRole('region', { name: 'Latest SQL buckets and hourly baselines', exact: true })).toBeVisible();
    await page.getByRole('button', { name: '↻ Refresh', exact: true }).focus();
    await expect(page.getByRole('button', { name: '↻ Refresh', exact: true })).toBeFocused();
    await auditResponsiveState(page, testInfo, 'health-invalid-response');
    expect(crashes).toEqual([]);
    await saveFullPage(page, `health-invalid-${width}.png`);
  });
}

for (const width of [320, 390, 768, 1440]) {
  test(`query row trends expose gaps and coverage accessibly at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.context().addCookies([
      { name: 'dashboardRefactor', value: '1', url: origin },
      { name: 'chartReferenceRole', value: 'viewer', url: origin },
      { name: 'databaseScopeFixture', value: '1', url: origin },
      { name: 'queryRowTrendFixture', value: '1', url: origin },
      { name: 'queryRowTrendState', value: 'partial', url: origin },
    ]);
    const crashes = []; page.on('pageerror', error => crashes.push(error.message));
    const url = `/#/queries?${new URLSearchParams({ target: '11111111-1111-4111-8111-000000000001', range: 'custom', from: '2026-09-26T08:00:00.000Z', to: '2026-09-26T12:00:00.000Z' })}`;
    await page.goto(url);
    const runtime = page.getByRole('region', { name: 'Ranked query observations', exact: true });
    const row = runtime.getByRole('group', { name: /^Ranking trend:/ }).first();
    await row.scrollIntoViewIfNeeded(); await expect(row.locator('svg circle')).toHaveCount(2);
    const disclosure = row.getByText('Trend values and coverage', { exact: true });
    await disclosure.focus(); await disclosure.press('Enter');
    await expect(disclosure).toBeFocused();
    await expect(row).toContainText('1 missing hourly buckets');
    await expect(row).toContainText('No observed groups; gap');
    const waits = page.getByRole('region', { name: 'Query Store wait ranking', exact: true }).getByRole('group', { name: /^Ranking trend:/ }).first();
    await waits.scrollIntoViewIfNeeded(); await expect(waits.locator('svg circle')).toHaveCount(2);
    await waits.getByText('Trend values and coverage', { exact: true }).click();
    await expect(waits).toContainText('All-category waits ms/hour bucket');
    await auditResponsiveState(page, testInfo, 'query-runtime-wait-row-trends');
    await saveFullPage(page, `query-row-trends-${width}.png`);
    await page.goto(url + '&queryView=cache');
    const cache = page.getByRole('region', { name: 'Plan cache interval ranking', exact: true });
    const cacheRow = cache.getByRole('group', { name: /^Ranking trend:/ }).first();
    await cacheRow.scrollIntoViewIfNeeded(); await expect(cacheRow.locator('svg path')).toHaveCount(2);
    const cacheDisclosure = cacheRow.getByText('Trend values and coverage', { exact: true });
    await cacheDisclosure.focus(); await cacheDisclosure.press('Enter');
    await expect(cacheDisclosure).toBeFocused();
    await expect(cacheRow).toContainText('2 unavailable comparisons');
    await expect(cacheRow).toContainText('reset'); await expect(cacheRow).toContainText('partial capture');
    await auditResponsiveState(page, testInfo, 'query-cache-row-trends');
    expect(crashes).toEqual([]);
    await saveFullPage(page, `query-cache-row-trends-${width}.png`);
  });
}

for (const width of [320, 390, 768, 1440]) {
  test(`retained deadlock selection and unavailable identity remain accessible at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin }, { name: 'chartReferenceRole', value: 'viewer', url: origin }, { name: 'deadlockSelectionFixture', value: '1', url: origin }]);
    const targetId = '11111111-1111-4111-8111-000000000001';
    const url = `/#/deadlocks?${new URLSearchParams({ target: targetId, range: 'custom', from: '2026-09-26T08:00:00.000Z', to: '2026-09-26T12:00:00.000Z', deadlockEvent: '33333333-3333-4333-8333-333333333335', deadlockAt: '2026-09-26T09:00:00.000001Z' })}`;
    await page.goto(url);
    const detail = page.getByRole('region', { name: 'Selected deadlock detail', exact: true });
    await expect(detail.getByRole('table')).toContainText('91');
    await expect(detail).toContainText('This exact occurrence is read independently of the event-list collected cutoff.');
    const relationships = detail.getByRole('region', { name: 'Deadlock relationship table', exact: true });
    await relationships.focus(); await expect(relationships).toBeFocused();
    await auditResponsiveState(page, testInfo, 'retained-deadlock-selection');
    await saveFullPage(page, `deadlock-url-selected-${width}.png`);
    await page.evaluate(hash => { location.hash = hash; }, url.substring(1).replace('33333333-3333-4333-8333-333333333335', 'ffffffff-ffff-4fff-8fff-ffffffffffff'));
    await expect(detail.getByRole('alert')).toContainText('The requested deadlock detail is unavailable.');
    await expect(detail.getByRole('table')).toHaveCount(0);
    const clear = page.getByRole('button', { name: 'Clear deadlock selection', exact: true });
    await clear.focus(); await expect(clear).toBeFocused();
    await auditResponsiveState(page, testInfo, 'unavailable-deadlock-selection');
    await saveFullPage(page, `deadlock-url-unavailable-${width}.png`);
  });

  test(`administration retained IDs and unavailable detail remain accessible at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin }, { name: 'chartReferenceRole', value: 'viewer', url: origin }, { name: 'adminSelectionFixture', value: '1', url: origin }]);
    for (const config of [
      { surface: 'rules', key: 'ruleId', id: '66666666-6666-4666-8666-000000000002', detail: 'SYNTHETIC rule 2', capture: 'rules-url-selected' },
      { surface: 'maintenance', key: 'windowId', id: '77777777-7777-4777-8777-000000000002', detail: 'Maintenance window', capture: 'maintenance-url-selected' },
      { surface: 'destinations', key: 'destinationId', id: '88888888-8888-4888-8888-000000000999', capture: 'destinations-url-unavailable' },
    ]) {
      await page.goto(`/#/${config.surface}?${new URLSearchParams({ target: '11111111-1111-4111-8111-000000000001', range: 'custom', from: '2026-09-26T08:00:00.000Z', to: '2026-09-26T12:00:00.000Z', [config.key]: config.id })}`);
      if (config.detail) {
        await expect(page.getByRole('complementary', { name: config.detail, exact: true })).toContainText(config.id);
        const selected = page.locator('.alert-list button.table-link[aria-pressed=true]');
        await selected.focus(); await expect(selected).toBeFocused();
      } else {
        await expect(page.getByText('The selected administration item is unavailable on this server. It may have been removed or fall outside your access. Select another item.', { exact: true })).toBeVisible();
        await expect(page.locator('.detail-pane').getByRole('heading')).toHaveCount(0);
      }
      await auditResponsiveState(page, testInfo, config.capture);
      await saveFullPage(page, `${config.capture}-${width}.png`);
    }
  });

  test(`quiet evidence disclosures stay available on dashboard and Resources at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin }, { name: 'chartReferenceRole', value: 'admin', url: origin }, { name: 'baselineKpiFixture', value: '1', url: origin }, { name: 'resourceInspectionFixture', value: '1', url: origin }]);
    for (const [surface, region, capture] of [['health', 'Dashboard evidence status', 'dashboard-evidence-expanded'], ['resources', 'Resources evidence status', 'resources-evidence-expanded']]) {
      await page.goto(`/#/${surface}?${new URLSearchParams({ target: '11111111-1111-4111-8111-000000000001', range: 'custom', from: '2026-09-26T08:00:00.000Z', to: '2026-09-26T12:00:00.000Z', ...(surface === 'health' ? { healthSection: 'summary' } : {}) })}`);
      const footer = page.getByRole('region', { name: region, exact: true });
      await expect(footer).toContainText('SQL p10');
      await expect(footer.locator('.compact-evidence-action')).toHaveCount(0);
      for (const summary of ['Collection evidence details', 'Baseline reference details', 'Rule threshold details']) {
        const control = footer.getByText(summary, { exact: true });
        await control.focus(); await control.press('Enter'); await expect(control).toBeFocused();
        await expect(control.locator('xpath=parent::details')).toHaveAttribute('open', '');
      }
      const chart = page.getByText('SQL scheduler CPU evidence: observed', { exact: true });
      await chart.focus(); await chart.press('Enter');
      await expect(chart.locator('xpath=parent::details')).toContainText('2 observed of 2 loaded sample buckets');
      await auditResponsiveState(page, testInfo, capture);
      await saveFullPage(page, `${capture}-${width}.png`);
    }
  });
}

async function openInvestigationWorkspace(page) {
  await expect(page.getByRole('button', { name: 'Save investigation', exact: true })).toBeVisible();
}
