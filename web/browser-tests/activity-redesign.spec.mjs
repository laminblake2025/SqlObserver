import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
const target = '11111111-1111-4111-8111-000000000002', origin = 'http://127.0.0.1:4358';
const snapshotId = '55555555-5555-4555-8555-555555555555', observedUtc = '2026-10-09T16:00:00.123456Z';
const shots = resolve('../artifacts/web-browser/visual-reference/activity');
const identity = (id, request = 0) => (id * 100 + request).toString(16).padStart(64, '0');
const delta = fields => ({ seconds: 10, cpuMs: '0', reads: '0', writes: '0', logicalReads: '0', ...fields });
const row = (sessionId, fields = {}) => ({ identity: identity(sessionId), sessionId, requestId: 0, databaseId: 7, databaseName: 'SqlObserverLabSales', login: 'lab_reader', clientHost: 'SQL-LAB-CLIENT', application: 'Lab workload', status: 'running', command: 'SELECT', isUser: true, waitType: null, blocker: null, cpuMs: '12345', memoryBytes: '1024', reads: '200', writes: '0', logicalReads: '300', elapsedMs: '2000', queryId: '77777777-7777-4777-8777-777777777777', queryState: 'available', engineStartup: '2026-10-09T10:00:00', sessionLogin: '2026-10-09T15:00:00', requestStart: '2026-10-09T15:59:58', delta: delta({}), ...fields });
const rows = [
  ...Array.from({ length: 69 }, (_, i) => row(i + 1, { isUser: false, databaseId: 0, databaseName: null, login: null, application: null, clientHost: null, status: 'background', command: i === 6 ? 'CHECKPOINT' : 'SYSTEM TASK', queryId: null, queryState: 'omitted', elapsedMs: '99999999', delta: i === 6 ? delta({ writes: '15000' }) : null })),
  row(72, { elapsedMs: '12000', application: 'Sales API' }), row(81, { blocker: 72, waitType: 'LCK_M_X', status: 'suspended', application: 'Order processor' }), row(84, { blocker: 72, waitType: 'LCK_M_S', status: 'suspended', application: 'Sales API' }), row(93, { delta: delta({ cpuMs: '7200' }), application: 'Reporting service' }), row(105, { elapsedMs: '48000', application: 'Batch import' }),
].sort((a, b) => a.sessionId - b.sessionId);
const state = new WeakMap();
test.beforeEach(async ({ page }) => {
  const session = { metadata: [], sql: [], errors: [], rows, failPage: false, denyText: false, next: false }; state.set(page, session);
  await page.clock.setFixedTime(new Date('2026-10-09T16:00:30Z'));
  page.on('pageerror', error => session.errors.push(error.message));
  await page.context().addCookies(Object.entries({ dashboardRefactor: '1', databaseScopeFixture: '1', chartReferenceRole: 'admin', incidentWorkspaceFixture: '1', activityQueryReaderFixture: '1' }).map(([name, value]) => ({ name, value, url: origin })));
  await page.route('**/activity/live**', async route => {
    const url = new URL(route.request().url()); expect(route.request().method()).toBe('GET'); expect(url.origin).toBe(origin);
    if (url.pathname.endsWith('/query')) { session.sql.push(url); return route.fulfill({ status: session.denyText ? 403 : 200, contentType: 'application/json', body: JSON.stringify(session.denyText ? {} : { state: 'available', text: 'SELECT * FROM dbo.Sales WHERE Id = @Id;' }) }); }
    if (url.pathname.endsWith('/history')) return route.fulfill({ contentType: 'application/json', body: JSON.stringify([{ id: snapshotId, observedUtc, truncated: false }]) });
    expect(url.searchParams.get('includeSystem')).toBe('true'); expect(url.searchParams.get('includeIdle')).toBe('true'); expect(url.searchParams.get('sort')).toBe('session');
    const next = url.searchParams.has('cursor'); session.metadata.push(url);
    if (next) { expect(url.searchParams.get('snapshot')).toBe(snapshotId); expect(url.searchParams.get('cursor')).toBe('cGFnZTI='); }
    if (next && session.failPage) return route.fulfill({ status: 503 });
    const data = { snapshotId, observedUtc, repositoryTimeUtc: '2026-10-09T16:00:30Z', state: 'available', truncated: false, databases: [{ id: 7, name: 'SqlObserverLabSales' }], rows: session.rows.slice(next ? 50 : 0, next ? undefined : 50), nextCursor: next ? null : 'cGFnZTI=' };
    return route.fulfill({ contentType: 'application/json', body: JSON.stringify(data) });
  });
});
test.afterEach(async ({ page }) => expect(state.get(page).errors).toEqual([]));
async function open(page) { await page.goto(`/#/activity?target=${target}&range=1h`); await expect(page.getByRole('heading', { name: 'Activity', exact: true })).toBeVisible(); await expect(page.getByRole('button', { name: 'Inspect 72/0', exact: true })).toBeVisible(); }
async function accessible(page) { expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true); const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze(); expect(result.violations.map(item => ({ id: item.id, nodes: item.nodes.map(node => node.target) }))).toEqual([]); }
for (const size of [{ width: 1600, height: 1000 }, { width: 1440, height: 900 }, { width: 390, height: 844 }]) test(`prioritized all-session layout, chain and keyboard inspection at ${size.width}x${size.height}`, async ({ page }) => {
  await page.setViewportSize(size); await mkdir(shots, { recursive: true }); await open(page);
  await expect(page.locator('.activity-summary-card').nth(0)).toContainText('74'); await expect(page.locator('.activity-summary-card').nth(1)).toContainText('6'); await expect(page.locator('.activity-summary-card').nth(2)).toContainText('2'); await expect(page.locator('.activity-summary-card').nth(3)).toContainText('5');
  expect(state.get(page).metadata).toHaveLength(2);
  await expect(page.getByRole('region', { name: 'Session details', exact: true })).toHaveCount(0); await expect(page.getByRole('region', { name: 'Blocking tree · loaded snapshot', exact: true })).toHaveCount(0);
  await expect(page.locator('.activity-session-row').first()).toContainText('Blocking 2 sessions');
  await page.screenshot({ path: resolve(shots, `all-${size.width}.png`) });
  await writeFile(resolve(shots, `geometry-${size.width}.json`), JSON.stringify(await page.locator('.topbar, .page-heading, .server-navigation, .activity-capture-heading, .activity-capture-status, .activity-summary-grid, .activity-quick-filters, .activity-list-toolbar, .activity-session-table, .activity-session-row:first-child, .activity-session-row:first-child .activity-inspect-cell').evaluateAll(elements => elements.map(element => ({ class: element.className, x: element.getBoundingClientRect().x, y: element.getBoundingClientRect().y, width: element.getBoundingClientRect().width, height: element.getBoundingClientRect().height, minWidth: getComputedStyle(element).minWidth }))), null, 2));
  await expect(page.getByRole('button', { name: 'Inspect 72/0', exact: true })).toBeInViewport();
  await accessible(page); await page.screenshot({ path: resolve(shots, `all-${size.width}.png`) });
  const inspect = page.getByRole('button', { name: 'Inspect 72/0', exact: true }); await inspect.focus(); await inspect.press('Enter');
  const details = page.getByRole('region', { name: 'Session details', exact: true }); await expect(details).toBeVisible(); await expect(details.getByRole('button', { name: 'Inspect captured session 81/0', exact: true })).toBeVisible();
  expect(state.get(page).sql).toHaveLength(0); expect(new URL(page.url()).hash).toContain('sessionIdentity=' + identity(72));
  if (size.width === 390) { expect(await page.locator('.activity-inline-inspector').count()).toBe(1); await inspect.scrollIntoViewIfNeeded(); }
  await accessible(page); await page.screenshot({ path: resolve(shots, `selected-${size.width}.png`) });
  if (size.width === 390) await details.screenshot({ path: resolve(shots, 'inspection-390.png') });
  await details.getByRole('button', { name: 'Captured SQL', exact: true }).click(); expect(state.get(page).sql).toHaveLength(0);
  await details.getByRole('button', { name: 'Load captured SQL', exact: true }).click(); await expect(details.locator('pre')).toContainText('SELECT * FROM dbo.Sales'); expect(state.get(page).sql).toHaveLength(1);
  await details.getByRole('button', { name: 'Close details', exact: true }).click(); await expect(details).toHaveCount(0);
});
test('local filters, session grouping, exact secondary requests and manual sort preserve one capture', async ({ page }) => {
  const session = state.get(page); session.rows = [...rows, row(93, { identity: identity(93, 2), requestId: 2, elapsedMs: '45000', delta: null })];
  await open(page); const count = session.metadata.length;
  await page.getByRole('button', { name: 'System 69', exact: true }).click(); await expect(page.locator('.activity-session-row')).toHaveCount(50); await page.getByRole('button', { name: 'Next page', exact: true }).click(); await expect(page.locator('.activity-session-row')).toHaveCount(19);
  await page.getByRole('button', { name: 'Needs attention 6', exact: true }).click(); await expect(page.locator('.activity-session-row')).toHaveCount(6);
  await page.getByRole('searchbox', { name: 'Search sessions' }).fill('Reporting'); await expect(page.locator('.activity-session-row')).toHaveCount(1); await page.getByText('2 requests', { exact: true }).click();
  await page.locator('.activity-session-row details').getByRole('button', { name: 'Inspect 93/2', exact: true }).click(); await expect(page.getByRole('region', { name: 'Session details' })).toContainText('Request 2'); expect(session.sql).toHaveLength(0); expect(new URL(page.url()).hash).toContain(identity(93, 2));
  await page.getByRole('searchbox', { name: 'Search sessions' }).fill(''); await page.getByRole('combobox', { name: 'Sort', exact: true }).selectOption('cpuRate'); await expect(page.locator('.activity-session-row').first()).toContainText('Reporting service'); expect(session.metadata).toHaveLength(count);
});
test('threshold settings persist per server and reset; SQL denial clears text and retains metadata', async ({ page }) => {
  await open(page); await page.getByRole('button', { name: 'Triage settings', exact: true }).click(); await page.getByRole('spinbutton', { name: 'User request elapsed (seconds)' }).fill('60'); await page.getByRole('button', { name: 'Apply thresholds', exact: true }).click(); await expect(page.locator('.activity-summary-card').nth(1)).toContainText('5');
  await page.reload(); await expect(page.locator('.activity-summary-card').nth(1)).toContainText('5'); await page.getByRole('button', { name: 'Triage settings', exact: true }).click(); await page.getByRole('button', { name: 'Reset defaults', exact: true }).click(); await expect(page.locator('.activity-summary-card').nth(1)).toContainText('6');
  await page.getByRole('button', { name: 'Inspect 72/0', exact: true }).click(); await page.getByRole('button', { name: 'Captured SQL', exact: true }).click(); await page.getByRole('button', { name: 'Load captured SQL', exact: true }).click(); await expect(page.locator('pre.query-text')).toBeVisible(); state.get(page).denyText = true; await page.getByRole('button', { name: 'Load captured SQL', exact: true }).click(); await expect(page.locator('pre.query-text')).toHaveCount(0); await expect(page.getByRole('button', { name: 'Inspect 72/0', exact: true })).toBeVisible();
});
test('database presentation changes retain the same capture and label graph context outside that database', async ({ page }) => {
  await open(page); await page.getByRole('button', { name: 'Inspect 72/0', exact: true }).click();
  const session = state.get(page), count = session.metadata.length;
  await page.locator('.database-scope-compact select').selectOption('5');
  await expect(page.locator('.activity-summary-card').first().locator('strong')).toHaveText('0');
  await expect(page.getByRole('region', { name: 'Session details' })).toContainText('Context outside the selected database');
  await expect(page.getByRole('button', { name: 'Inspect captured session 81/0', exact: true })).toBeVisible();
  expect(session.metadata).toHaveLength(count); expect(session.sql).toHaveLength(0);
  await page.locator('.database-scope-compact select').selectOption(''); await expect(page.locator('.activity-summary-card').first().locator('strong')).toHaveText('74'); expect(session.metadata).toHaveLength(count);
});

test('long native application names keep every column and inspection action visible beside details', async ({ page }) => {
  const session = state.get(page);
  session.rows = rows.map(value => value.sessionId === 72 ? { ...value, application: 'SQL Observer Activity qualification 5ef3afa1e954407388fdaa988d61f47b blocker' } : value);
  await page.setViewportSize({ width: 1440, height: 900 }); await open(page);
  await page.getByRole('button', { name: 'Inspect 72/0', exact: true }).click();
  const list = page.locator('.activity-session-list .live-table-scroll');
  expect(await list.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
  await expect(page.getByRole('button', { name: 'Inspect 72/0', exact: true })).toBeInViewport();
  await expect(page.locator('.activity-session-row').first().locator('.activity-context-cell')).toContainText('5ef3afa1e954407388fdaa988d61f47b blocker');
});
test('paging failure stays partial instead of reporting an all-clear, and refresh is deferred during list interaction', async ({ page }) => {
  state.get(page).failPage = true; await page.goto(`/#/activity?target=${target}&range=1h`); await expect(page.getByText('Partial metadata coverage', { exact: true })).toBeVisible(); await expect(page.getByText(/Ranking and counts cover only the validated rows/)).toBeVisible(); await expect(page.locator('.activity-summary-card').nth(0)).toContainText('50');
  state.get(page).failPage = false; await page.getByRole('button', { name: 'Refresh now', exact: true }).click(); await expect(page.locator('.activity-summary-card').nth(0)).toContainText('74');
  await page.getByRole('searchbox', { name: 'Search sessions' }).focus(); await page.clock.runFor(11_000); await expect(page.getByRole('button', { name: 'Apply capture update', exact: true })).toBeVisible(); await expect(page.getByRole('searchbox', { name: 'Search sessions' })).toBeFocused();
  const scroll = page.locator('.activity-session-list .live-table-scroll'); await scroll.evaluate(element => { element.scrollTop = 180; }); const previous = await scroll.evaluate(element => element.scrollTop);
  await page.getByRole('button', { name: 'Apply capture update', exact: true }).click(); expect(await scroll.evaluate(element => element.scrollTop)).toBe(previous);
});
