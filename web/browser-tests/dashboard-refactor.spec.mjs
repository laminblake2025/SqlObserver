import { test, expect } from '@playwright/test';

const first = '11111111-1111-4111-8111-000000000001';
const second = '11111111-1111-4111-8111-000000000002';
const from = '2026-09-26T08:00:00.000Z';
const to = '2026-09-26T12:00:00.000Z';
const origin = 'http://127.0.0.1:4322';
const firstAlert = '44444444-4444-4444-8444-444444444444';
const pagedAlert = '66666666-6666-4666-8666-666666666666';
const href = (surface = 'overview', target = first, range = 'custom') =>
  `/#/${surface}?${new URLSearchParams({ target, range, ...(range === 'custom' ? { from, to } : {}) })}`;
const scope = page => new URLSearchParams(new URL(page.url()).hash.split('?')[1]);

async function fixture(page, role = 'viewer') {
  await page.context().addCookies([
    { name: 'dashboardRefactor', value: '1', url: origin },
    { name: 'chartReferenceRole', value: role, url: origin },
  ]);
}
async function open(page, surface = 'overview', target = first, range = 'custom') {
  await fixture(page);
  await page.goto(href(surface, target, range));
  await expect(page.getByRole('combobox', { name: 'Time range', exact: true })).toHaveValue(range);
}
async function assertHistoricalScope(page, target = first, start = from, end = to) {
  await expect.poll(() => Object.fromEntries(scope(page))).toMatchObject({ target, range: 'custom', from: start, to: end });
}
const mainNavigation = page => page.getByRole('navigation', { name: 'Main navigation' });
const fleetStrip = page => page.getByRole('navigation', { name: 'Loaded server status', exact: true });
const sessionSnapshot = '88888888-8888-4888-8888-888888888888';
const sessionObserved = '2026-09-26T11:30:00.123456Z';

async function resourceUrlFixture(page) {
  await databaseFixture(page);
  await page.clock.install({ time: new Date('2026-09-26T12:00:00Z') });
  await page.context().addCookies([{ name: 'resourceInspectionFixture', value: '1', url: origin }]);
}
async function sessionUrlFixture(page) {
  await fixture(page);
  await page.clock.install({ time: new Date('2026-09-26T12:00:00Z') });
  await page.context().addCookies([{ name: 'sessionSelectionFixture', value: '1', url: origin }]);
}
const sessionHref = (identity, observed = sessionObserved, snapshot = sessionSnapshot) => `${href('activity', first, '1h')}&${new URLSearchParams({ sessionSnapshot: snapshot, sessionObserved: observed, ...(identity ? { sessionIdentity: identity } : {}) })}`;
const seededIncident = 'aaaaaaaa-aaaa-4aaa-8aaa-000000000001';
async function incidentWorkspaceFixture(page, retry = false) {
  await databaseFixture(page);
  await page.context().addCookies([{ name: 'incidentWorkspaceFixture', value: '1', url: origin }, ...(retry ? [{ name: 'incidentWorkspaceRetry', value: '1', url: origin }] : [])]);
}

test('the common incident action is available across target diagnostics and closing a draft returns keyboard focus', async ({ page }) => {
  await incidentWorkspaceFixture(page);
  for (const surface of ['overview', 'health', 'activity', 'waits', 'resources', 'queries', 'deadlocks', 'operations', 'alerts']) {
    await page.goto(href(surface));
    const open = page.getByRole('button', { name: 'Open incident', exact: true });
    await expect(open).toHaveCount(1);
    await expect(open).toBeEnabled();
    await open.focus(); await open.press('Enter');
    await expect(page.getByLabel('Incident title', { exact: true })).toBeFocused();
    await page.getByLabel('Incident title', { exact: true }).fill(`SYNTHETIC ${surface} investigation`);
    await page.getByRole('button', { name: 'Close draft', exact: true }).press('Enter');
    await expect(open).toBeFocused();
    await expect(page.getByLabel('Incident title', { exact: true })).toHaveCount(0);
    await assertHistoricalScope(page);
  }
});

test('incident save pins the original window and revision while duplicate activation and retry keep one immutable operation', async ({ page }) => {
  await incidentWorkspaceFixture(page, true);
  await page.goto(`${href('resources')}&databaseId=6`);
  await page.getByRole('button', { name: 'Open incident', exact: true }).click();
  const title = page.getByRole('textbox', { name: 'Incident title', exact: true }), notes = page.getByRole('textbox', { name: 'Notes', exact: true });
  await title.fill('SYNTHETIC resource investigation'); await notes.fill('Synthetic note\nNo connected VM evidence.');
  await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
  await page.getByLabel('From (UTC)', { exact: true }).fill('2026-09-26T09:00');
  await page.getByLabel('To (UTC)', { exact: true }).fill('2026-09-26T11:00');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  const commands = [];
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/observation-targets/${first}/incidents/saved`, async route => {
    if (route.request().method() !== 'POST') return route.continue();
    commands.push(route.request().postDataJSON());
    if (commands.length === 1) await gate;
    await route.fulfill({ response: await route.fetch() });
  });
  await page.getByRole('button', { name: 'Save incident', exact: true }).evaluate(element => { element.click(); element.click(); });
  await expect.poll(() => commands.length).toBe(1);
  await expect(page.getByRole('button', { name: 'Saving\u2026', exact: true })).toBeDisabled();
  await expect(title).toBeDisabled(); await expect(notes).toBeDisabled();
  expect(commands[0]).toMatchObject({ expectedTargetRevision: 1, fromUtc: from, toUtc: to, title: 'SYNTHETIC resource investigation', notes: 'Synthetic note\nNo connected VM evidence.' });
  release();
  const retry = page.getByRole('button', { name: 'Retry same save', exact: true });
  await expect(retry).toBeEnabled();
  await expect(title).toBeDisabled(); await expect(notes).toBeDisabled();
  await retry.press('Enter');
  await expect.poll(() => commands.length).toBe(2);
  expect(commands[1]).toEqual(commands[0]);
  await expect(page).toHaveURL(/#\/health\?/);
  await expect.poll(() => scope(page).get('incident')).toMatch(/^[0-9a-f-]{36}$/);
  await assertHistoricalScope(page); await assertDatabaseScope(page, 6);
  await expect(page.getByRole('heading', { name: 'SYNTHETIC resource investigation', exact: true })).toBeVisible();
  await expect(page.getByText('No analytics evidence packets existed for this window when it was saved. This does not mean the server was healthy.', { exact: true })).toBeVisible();
  const incidentId = scope(page).get('incident');
  await page.reload();
  await expect(page.getByRole('heading', { name: 'SYNTHETIC resource investigation', exact: true })).toBeVisible();
  expect(scope(page).get('incident')).toBe(incidentId);
  await expect(page.locator('.incident-notes')).toHaveText('Synthetic note\nNo connected VM evidence.');
});

test('saved incident links are canonical and delayed old-target or foreign reads never display another investigation', async ({ page }) => {
  await incidentWorkspaceFixture(page);
  await page.goto(href('waits'));
  await page.getByRole('button', { name: 'Saved investigations', exact: true }).click();
  const retained = page.getByRole('link', { name: 'SYNTHETIC retained investigation 01', exact: true });
  await expect(retained).toHaveAttribute('href', /#\/health\?/);
  await expect(retained).toHaveAttribute('href', new RegExp(`incident=${seededIncident}`));
  await retained.press('Enter');
  await expect(page.getByRole('heading', { name: 'SYNTHETIC retained investigation 01', exact: true })).toBeVisible();
  let release, pending = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/observation-targets/${first}/incidents/saved/${seededIncident}`, async route => {
    const response = await route.fetch(); pending = true; await gate; await route.fulfill({ response });
  });
  await page.reload(); await expect.poll(() => pending).toBe(true);
  await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(second);
  await expect.poll(() => scope(page).get('target')).toBe(second);
  await expect(page.getByRole('button', { name: 'Open incident', exact: true })).toBeEnabled();
  release();
  await expect(page.getByRole('heading', { name: 'SYNTHETIC retained investigation 01', exact: true })).toHaveCount(0);
  await page.unroute(`**/observation-targets/${first}/incidents/saved/${seededIncident}`);
  for (const [target, id] of [[second, seededIncident], [first, 'ffffffff-ffff-4fff-8fff-ffffffffffff']]) {
    await page.goto(`${href('health', target)}&incident=${id}`);
    await expect(page.getByText('This saved investigation was not found. Automatically correlated incidents remain available in Analytics.', { exact: true })).toBeVisible();
    await expect(page.locator('.saved-incident')).toHaveCount(0);
    expect(scope(page).get('incident')).toBe(id);
  }
});

test('Viewer activity inspection exposes metadata but no protected SQL action or incident mutation', async ({ page }) => {
  await sessionUrlFixture(page);
  let protectedReads = 0, incidentWrites = 0;
  page.on('request', request => {
    if (new URL(request.url()).pathname.endsWith('/activity/live/query')) protectedReads++;
    if (new URL(request.url()).pathname.endsWith('/incidents/saved') && request.method() === 'POST') incidentWrites++;
  });
  await page.goto(sessionHref('c'.repeat(64)));
  const detail = page.getByRole('complementary', { name: 'Session details', exact: true });
  await expect(detail.getByRole('heading', { name: 'Session 51 / 1', exact: true })).toBeVisible();
  await expect(detail).toContainText('Captured SQL is hidden. Query Text Reader access is required for this server.');
  await expect(detail.getByRole('button', { name: 'Load captured SQL', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Open incident', exact: true })).toHaveCount(0);
  await expect(detail.locator('pre.query-text')).toHaveCount(0);
  expect(protectedReads).toBe(0); expect(incidentWrites).toBe(0);
});

test('revoking Query Text Reader while captured SQL is pending hides the action and rejects its late plaintext', async ({ page }) => {
  await sessionUrlFixture(page);
  await page.context().addCookies([{ name: 'activityQueryReaderFixture', value: '1', url: origin }]);
  await page.goto(sessionHref('c'.repeat(64)));
  const detail = page.getByRole('complementary', { name: 'Session details', exact: true });
  const load = detail.getByRole('button', { name: 'Load captured SQL', exact: true });
  await expect(load).toBeEnabled();
  let release, pending = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/observation-targets/${first}/activity/live/query?*`, async route => {
    const response = await route.fetch(); pending = true; await gate; await route.fulfill({ response });
  });
  await load.press('Enter'); await expect.poll(() => pending).toBe(true);
  await page.context().addCookies([{ name: 'activityQueryReaderFixture', value: '0', url: origin }]);
  await page.locator('.page-actions').getByRole('button', { name: /Refresh/ }).click();
  await expect(detail).toContainText('Captured SQL is hidden. Query Text Reader access is required for this server.');
  await expect(load).toHaveCount(0);
  release();
  await expect(detail.locator('pre.query-text')).toHaveCount(0);
  await expect(detail).not.toContainText('SELECT 1 AS synthetic_fixture_only;');
  await expect(detail.getByRole('heading', { name: 'Session 51 / 1', exact: true })).toBeVisible();
});

test('Resources URL restores file node and inspection while pointer stays transient and fixed investigation freezes only the loaded window', async ({ page }) => {
  await resourceUrlFixture(page);
  let loadedWindow;
  page.on('response', async response => {
    const url = new URL(response.url());
    if (url.pathname === '/api/v1/overview' && url.searchParams.get('targetId') === first && response.ok()) {
      const body = await response.json(); loadedWindow = { from: body.fromUtc, to: body.toUtc };
    }
  });
  await page.goto(href('resources', first, '1h'));
  const resources = page.getByRole('region', { name: 'Resources for SYNTHETIC SQL 01', exact: true });
  const file = resources.getByRole('button', { name: 'View read and write latency trend for database 5, file Orders_data', exact: true });
  await file.click();
  await resources.getByRole('combobox', { name: 'NUMA node', exact: true }).selectOption('1');
  await resources.getByLabel('Resource inspection time (UTC)', { exact: true }).fill('2026-09-26T11:30:00.125');
  await resources.getByRole('button', { name: 'Align resource charts', exact: true }).press('Enter');
  await expect.poll(() => Object.fromEntries(scope(page))).toMatchObject({ range: '1h', resourceDatabaseId: '5', resourceFileId: '1', numaNodeId: '1', resourceAt: '2026-09-26T11:30:00.125Z' });
  const selectedUrl = page.url();
  const cpu = resources.locator('.server-dashboard-chart').filter({ has: page.getByRole('heading', { name: 'SQL scheduler CPU', exact: true }) }).locator('.overview-chart');
  await cpu.scrollIntoViewIfNeeded(); const box = await cpu.boundingBox(); expect(box).not.toBeNull();
  await page.mouse.move(box.x + box.width * 0.4, box.y + box.height * 0.5);
  await expect(resources.locator('.chart-crosshair')).toHaveCount(7);
  expect(page.url()).toBe(selectedUrl);
  await page.reload();
  await expect(file).toHaveAttribute('aria-pressed', 'true');
  await expect(resources.getByRole('combobox', { name: 'NUMA node', exact: true })).toHaveValue('1');
  await expect(resources.getByRole('region', { name: 'Resource chart alignment', exact: true }).getByRole('status').locator('time')).toHaveAttribute('datetime', '2026-09-26T11:30:00.125Z');
  await expect(resources.locator('.chart-crosshair')).toHaveCount(7);
  await resources.getByRole('button', { name: 'Clear resource alignment', exact: true }).click();
  await expect.poll(() => scope(page).get('resourceAt')).toBe(null);
  await page.goBack();
  await expect(resources.locator('.chart-crosshair')).toHaveCount(7);
  await page.goForward();
  await expect(resources.locator('.chart-crosshair')).toHaveCount(0);
  const fixed = resources.getByRole('link', { name: 'Open fixed resource investigation', exact: true });
  await expect.poll(() => loadedWindow).toBeTruthy();
  const fixedParams = new URLSearchParams((await fixed.getAttribute('href')).split('?')[1]);
  expect(Object.fromEntries(fixedParams)).toMatchObject({ target: first, range: 'custom', ...loadedWindow, resourceDatabaseId: '5', resourceFileId: '1', numaNodeId: '1' });
  expect(scope(page).get('range')).toBe('1h');
  await fixed.press('Enter');
  await assertHistoricalScope(page, first, fixedParams.get('from'), fixedParams.get('to'));
  await expect(file).toHaveAttribute('aria-pressed', 'true');
});

test('Resources missing file node and out-of-window inspection remain explicit through reload without substituting evidence', async ({ page }) => {
  await resourceUrlFixture(page);
  await page.goto(`${href('resources', first, '1h')}&resourceDatabaseId=5&resourceFileId=99&numaNodeId=99&resourceAt=2026-09-26T10%3A00%3A00.000Z`);
  const resources = page.getByRole('region', { name: 'Resources for SYNTHETIC SQL 01', exact: true });
  for (let attempt = 0; attempt < 2; attempt++) {
    await expect(resources).toContainText('The selected file is not in the loaded snapshot page. Its identity is retained; no other file has been selected.');
    await expect(resources).toContainText('Requested NUMA node 99 is not in the loaded node inventory. No other node has been selected.');
    await expect(resources).toContainText('Requested resource inspection time is outside the loaded shared window.');
    await expect(resources.getByRole('combobox', { name: 'NUMA node', exact: true })).toHaveValue('99');
    await expect(resources.locator('button[aria-label^="View read and write latency trend"][aria-pressed="true"]')).toHaveCount(0);
    await expect(resources.locator('.chart-crosshair')).toHaveCount(0);
    expect(scope(page).get('resourceFileId')).toBe('99');
    if (attempt === 0) await page.reload();
  }
  await resources.getByRole('button', { name: 'Clear resource selections', exact: true }).click();
  await expect.poll(() => scope(page).get('resourceFileId')).toBe(null);
  expect(scope(page).get('numaNodeId')).toBe(null);
  expect(scope(page).get('resourceAt')).toBe(null);
  expect(scope(page).get('range')).toBe('1h');
});

test('delayed old Resources file and NUMA evidence cannot repopulate selections on another server', async ({ page }) => {
  await resourceUrlFixture(page);
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  let pending = 0;
  await page.route(`**/observation-targets/${first}/resources/**`, async route => {
    const response = await route.fetch(); pending++; await gate; await route.fulfill({ response });
  });
  await page.goto(`${href('resources', first, '1h')}&resourceDatabaseId=5&resourceFileId=1&numaNodeId=1&resourceAt=2026-09-26T11%3A30%3A00.000Z`);
  await expect.poll(() => pending).toBeGreaterThanOrEqual(2);
  await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(second);
  await expect.poll(() => scope(page).get('target')).toBe(second);
  await expect.poll(() => scope(page).get('resourceFileId')).toBe(null);
  const resources = page.getByRole('region', { name: 'Resources for SYNTHETIC SQL 02', exact: true });
  await expect(resources.getByRole('combobox', { name: 'NUMA node', exact: true })).toHaveValue('0');
  release();
  const fileHistory = resources.getByRole('region', { name: 'Selected file latency history', exact: true });
  await expect(fileHistory.getByText('Select a file in the table to inspect its history.', { exact: true })).toBeVisible();
  await expect(fileHistory.locator('.overview-chart')).toHaveCount(0);
  await expect(fileHistory).not.toContainText('Orders_data');
  await expect(resources.getByRole('combobox', { name: 'NUMA node', exact: true })).toHaveValue('0');
  await expect(resources.locator('.chart-crosshair')).toHaveCount(0);
  expect(scope(page).get('numaNodeId')).toBe(null);
  expect(scope(page).get('resourceAt')).toBe(null);
});

test('Activity URL finds the exact paged request identity rather than another row sharing its SPID and protects SQL behind an explicit action', async ({ page }) => {
  await sessionUrlFixture(page);
  await page.context().addCookies([{ name: 'activityQueryReaderFixture', value: '1', url: origin }]);
  const reads = [], protectedReads = [];
  page.on('request', request => {
    const url = new URL(request.url());
    if (url.pathname.endsWith('/activity/live')) reads.push(url);
    if (url.pathname.endsWith('/activity/live/query')) protectedReads.push(url);
  });
  await page.goto(sessionHref('c'.repeat(64)));
  const detail = page.getByRole('complementary', { name: 'Session details', exact: true });
  await expect(detail.getByRole('heading', { name: 'Session 51 / 1', exact: true })).toBeVisible();
  const captureReads = reads.filter(url => url.searchParams.get('snapshot') === sessionSnapshot);
  expect(captureReads).toHaveLength(2);
  expect(new Set(captureReads.map(url => url.searchParams.get('cursor')))).toEqual(new Set([null, 'cGFnZTI=']));
  expect(protectedReads).toHaveLength(0);
  expect(scope(page).get('range')).toBe('1h');
  await page.reload();
  await expect(detail.getByRole('heading', { name: 'Session 51 / 1', exact: true })).toBeVisible();
  expect(protectedReads).toHaveLength(0);
  await detail.getByRole('button', { name: 'Load captured SQL', exact: true }).press('Enter');
  await expect(detail.locator('pre.query-text')).toHaveText('SELECT 1 AS synthetic_fixture_only;');
  expect(protectedReads).toHaveLength(1);
  expect(protectedReads[0].searchParams.get('identity')).toBe('c'.repeat(64));
  expect(protectedReads[0].searchParams.get('snapshot')).toBe(sessionSnapshot);
});

test('Activity loaded selection and chain survive reload and history navigation without exposing protected SQL', async ({ page }) => {
  await sessionUrlFixture(page);
  let protectedReads = 0;
  page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/activity/live/query')) protectedReads++; });
  await page.goto(href('activity', first, '1h'));
  const table = page.getByRole('region', { name: 'Loaded session observations', exact: true });
  await table.getByRole('button', { name: 'Inspect 51/0', exact: true }).press('Enter');
  await expect.poll(() => Object.fromEntries(scope(page))).toMatchObject({ sessionSnapshot, sessionObserved, sessionIdentity: 'a'.repeat(64), range: '1h' });
  const chain = page.getByRole('combobox', { name: 'Blocking chain (loaded page)', exact: true });
  await chain.selectOption('session:51');
  await expect.poll(() => scope(page).get('sessionChain')).toBe('session:51');
  await page.reload();
  await expect(chain).toHaveValue('session:51');
  await expect(page.getByRole('complementary', { name: 'Session details', exact: true }).getByRole('heading', { name: 'Session 51 / 0', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Clear chain filter', exact: true }).click();
  await expect.poll(() => scope(page).get('sessionChain')).toBe(null);
  await page.goBack(); await expect(chain).toHaveValue('session:51');
  await page.goForward(); await expect(chain).toHaveValue('');
  expect(protectedReads).toBe(0);
});

test('Activity missing snapshot identity or exact observed time never substitutes another selected session', async ({ page }) => {
  await sessionUrlFixture(page);
  let protectedReads = 0;
  page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/activity/live/query')) protectedReads++; });
  for (const url of [sessionHref('f'.repeat(64)), sessionHref('a'.repeat(64), sessionObserved, '77777777-7777-4777-8777-777777777777'), sessionHref('a'.repeat(64), '2026-09-26T11:30:00.123457Z')]) {
    await page.goto(url);
    await expect(page.getByRole('complementary', { name: 'Session details', exact: true })).toHaveCount(0);
    await expect(page.locator('.live-sessions')).toContainText(/requested.*(?:not present|unavailable|not found|did not match|does not match|missing|outside the 24-hour retention)|Session snapshot did not match the requested capture and observation time/i);
    await expect(page.locator('.live-sessions button[aria-pressed="true"]').filter({ hasText: /^Inspect / })).toHaveCount(0);
    expect(scope(page).get('sessionIdentity')).toBeTruthy();
  }
  expect(protectedReads).toBe(0);
});

test('delayed old Activity selection cannot reappear after switching the workspace server', async ({ page }) => {
  await sessionUrlFixture(page);
  let release, pending = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/observation-targets/${first}/activity/live?*`, async route => {
    const response = await route.fetch(); pending = true; await gate; await route.fulfill({ response });
  });
  await page.goto(sessionHref('c'.repeat(64)));
  await expect.poll(() => pending).toBe(true);
  await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(second);
  await expect.poll(() => scope(page).get('target')).toBe(second);
  await expect.poll(() => scope(page).get('sessionIdentity')).toBe(null);
  release();
  await expect(page.getByRole('region', { name: 'Loaded session observations', exact: true }).getByRole('button', { name: 'Inspect 51/0', exact: true })).toBeVisible();
  await expect(page.getByRole('complementary', { name: 'Session details', exact: true })).toHaveCount(0);
});

test('Waits URL restores a deliberate category and rejects invalid or duplicate selection without a substitute request', async ({ page }) => {
  await fixture(page);
  const requests = [];
  page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/query-performance/waits/top')) requests.push(new URL(request.url())); });
  await page.goto(`${href('waits')}&waitCategory=6`);
  const category = page.getByRole('combobox', { name: 'Query Store category', exact: true });
  await expect(category).toHaveValue('6');
  await category.selectOption('3');
  await expect.poll(() => scope(page).get('waitCategory')).toBe('3');
  await page.reload(); await expect(category).toHaveValue('3');
  await page.goBack(); await expect(category).toHaveValue('6');
  await page.goForward(); await expect(category).toHaveValue('3');
  for (const invalid of ['11', '3&waitCategory=6', 'not-a-category']) {
    const before = requests.length;
    await page.goto(`${href('waits')}&waitCategory=${invalid}`);
    await expect(category).toHaveValue('');
    await expect(page.getByText('The requested Query Store wait category is invalid. Choose a supported category; no other category has been selected.', { exact: true })).toBeVisible();
    expect(requests).toHaveLength(before);
  }
});

async function alertFixture(page, operator = false) {
  await databaseFixture(page);
  await page.context().addCookies([{ name: 'alertSelectionFixture', value: '1', url: origin }, ...(operator ? [{ name: 'alertOperatorFixture', value: '1', url: origin }] : [])]);
}

test('target alert URL restores a paged selection and preserves keyboard focus without refetching loaded rows', async ({ page }) => {
  await alertFixture(page);
  let reads = 0;
  page.on('request', request => { if (new URL(request.url()).pathname === `/api/v1/observation-targets/${first}/alerts/active`) reads++; });
  await page.goto(`${href('alerts')}&databaseId=6&alert=${pagedAlert}`);
  const observations = page.getByRole('region', { name: 'Target alert observations', exact: true });
  const paged = observations.getByRole('button', { name: 'SYNTHETIC paged alert', exact: true });
  await expect(paged).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('complementary', { name: 'SYNTHETIC paged alert', exact: true })).toBeVisible();
  expect(reads).toBe(2);
  await assertDatabaseScope(page, 6);
  await assertHistoricalScope(page);
  await expect(page.getByText(/Alerts show the latest target-scoped state, independently of the history window/)).toContainText('Repository snapshot:');
  await expect(page.getByRole('button', { name: 'Acknowledge', exact: true })).toHaveCount(0);
  const loadedReads = reads;
  const firstRow = observations.getByRole('button', { name: 'SYNTHETIC sustained blocking', exact: true });
  const originalButton = await firstRow.elementHandle();
  await firstRow.focus();
  await firstRow.press('Enter');
  await expect.poll(() => scope(page).get('alert')).toBe(firstAlert);
  await expect(firstRow).toHaveAttribute('aria-pressed', 'true');
  await expect(firstRow).toBeFocused();
  expect(await originalButton.evaluate(element => element.isConnected)).toBe(true);
  expect(reads).toBe(loadedReads);
  await paged.focus();
  await paged.press('Enter');
  await expect.poll(() => scope(page).get('alert')).toBe(pagedAlert);
  await expect(paged).toBeFocused();
  expect(reads).toBe(loadedReads);
  await page.getByRole('textbox', { name: 'Filter by rule identifier', exact: true }).fill('55555555');
  await expect(page.locator('.detail-pane.empty-state')).toContainText('The selected alert is hidden by the current filters.');
  await expect(page.locator('aside.detail-pane')).toHaveCount(0);
  expect(scope(page).get('alert')).toBe(pagedAlert);
  await page.getByRole('button', { name: 'Show selected alert', exact: true }).click();
  await expect(page.getByRole('complementary', { name: 'SYNTHETIC paged alert', exact: true })).toBeVisible();
  await page.reload();
  await expect(paged).toHaveAttribute('aria-pressed', 'true');
  await assertDatabaseScope(page, 6);
  await page.goto(`${href('alerts')}&databaseId=6&alert=88888888-8888-4888-8888-888888888888`);
  await expect(page.locator('.detail-pane.empty-state')).toContainText('The requested alert is not present in the bounded current server alerts.');
  await expect(page.locator('aside.detail-pane')).toHaveCount(0);
  await expect(observations.getByRole('button', { name: 'SYNTHETIC sustained blocking', exact: true })).toHaveAttribute('aria-pressed', 'false');
});

test('fleet alert URL restores an exact paged alert and filtered or missing selection never substitutes another detail', async ({ page }) => {
  await alertFixture(page);
  let reads = 0;
  page.on('request', request => { if (new URL(request.url()).pathname === '/api/v1/alerts/active') reads++; });
  await page.goto(`${href('alerts', '')}&alertTarget=${second}&alert=${pagedAlert}`);
  const observations = page.getByRole('region', { name: 'Fleet alert observations', exact: true });
  const paged = observations.getByRole('button', { name: 'Select SYNTHETIC paged alert on SYNTHETIC SQL 02', exact: true });
  await expect(paged).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('complementary', { name: 'SYNTHETIC paged alert', exact: true })).toBeVisible();
  expect(reads).toBe(2);
  await expect(page.getByText(/Alerts show the latest fleet state, independently of the history window/)).toContainText('Repository snapshot:');
  await page.getByRole('textbox', { name: 'Filter loaded alerts', exact: true }).fill('blocking');
  await expect(page.locator('.detail-pane.empty-state')).toContainText('The selected alert is hidden by the current filters.');
  await expect(page.locator('aside.detail-pane')).toHaveCount(0);
  expect(scope(page).get('alert')).toBe(pagedAlert);
  await page.getByRole('button', { name: 'Show selected alert', exact: true }).click();
  await expect(paged).toHaveAttribute('aria-pressed', 'true');
  const loadedReads = reads;
  const firstRow = observations.getByRole('button', { name: 'Select SYNTHETIC sustained blocking on SYNTHETIC SQL 01', exact: true });
  const originalButton = await firstRow.elementHandle();
  await firstRow.focus();
  await firstRow.press('Enter');
  await expect.poll(() => scope(page).get('alert')).toBe(firstAlert);
  await expect.poll(() => scope(page).get('alertTarget')).toBe(first);
  await expect(firstRow).toBeFocused();
  expect(await originalButton.evaluate(element => element.isConnected)).toBe(true);
  expect(reads).toBe(loadedReads);
  await page.reload();
  await expect(firstRow).toHaveAttribute('aria-pressed', 'true');
  await page.goto(`${href('alerts', '')}&alertTarget=${first}&alert=88888888-8888-4888-8888-888888888888`);
  await expect(page.locator('.detail-pane.empty-state')).toContainText('The requested alert is not present in the bounded current inbox.');
  await expect(page.locator('aside.detail-pane')).toHaveCount(0);
});

test('late acknowledgement from an old target alert selection cannot update another selected alert', async ({ page }) => {
  await alertFixture(page, true);
  let release, requested = false, returned = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/${first}/alerts/${firstAlert}/acknowledge`, async route => { requested = true; await gate; await route.fulfill({ status: 204 }).catch(() => {}); returned = true; });
  try {
    await page.goto(`${href('alerts')}&databaseId=6&alert=${pagedAlert}`);
    const observations = page.getByRole('region', { name: 'Target alert observations', exact: true });
    await observations.getByRole('button', { name: 'SYNTHETIC sustained blocking', exact: true }).click();
    await page.getByRole('button', { name: 'Acknowledge', exact: true }).click();
    await expect.poll(() => requested).toBe(true);
    await observations.getByRole('button', { name: 'SYNTHETIC paged alert', exact: true }).click();
    await expect(page.getByRole('complementary', { name: 'SYNTHETIC paged alert', exact: true })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Acknowledge', exact: true })).toBeEnabled();
  } finally { release(); }
  await expect.poll(() => returned).toBe(true);
  expect(scope(page).get('alert')).toBe(pagedAlert);
  await expect(page.getByRole('row').filter({ has: page.getByRole('button', { name: 'SYNTHETIC sustained blocking', exact: true }) }).getByRole('cell').first()).toHaveText('firing');
  await expect(page.getByRole('status').filter({ hasText: 'Acknowledgement recorded.' })).toHaveCount(0);
});

test('acknowledgement from a prior firing cycle cannot mark refreshed alert evidence and a new cycle uses a new operation', async ({ page }) => {
  await alertFixture(page, true);
  let release, recycled = false, returned = false;
  const operations = [];
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/${first}/alerts/active?*`, async route => {
    const response = await route.fetch();
    const body = await response.json();
    await route.fulfill({ response, json: recycled ? { ...body, snapshotUtc: '2026-10-01T12:00:00.000Z', items: body.items.map(item => item.alertId === firstAlert ? { ...item, firstObservedUtc: '2026-10-01T09:00:00.000Z', firedUtc: '2026-10-01T09:01:00.000Z', value: 99 } : item) } : body });
  });
  await page.route(`**/${first}/alerts/${firstAlert}/acknowledge`, async route => {
    operations.push(route.request().postDataJSON().operationId);
    if (operations.length === 1) { await gate; await route.fulfill({ status: 204 }).catch(() => {}); returned = true; }
    else await route.fulfill({ status: 204 });
  });
  try {
    await page.goto(`${href('alerts')}&databaseId=6&alert=${firstAlert}`);
    await page.getByRole('button', { name: 'Acknowledge', exact: true }).click();
    await expect.poll(() => operations.length).toBe(1);
    recycled = true;
    await page.locator('.page-actions').getByRole('button', { name: /Refresh/ }).click();
    await expect(page.getByRole('complementary', { name: 'SYNTHETIC sustained blocking', exact: true }).locator('.alert-observed')).toContainText('99');
    await expect(page.getByRole('button', { name: 'Acknowledge', exact: true })).toBeEnabled();
  } finally { release(); }
  await expect.poll(() => returned).toBe(true);
  await expect(page.getByRole('row').filter({ has: page.getByRole('button', { name: 'SYNTHETIC sustained blocking', exact: true }) }).getByRole('cell').first()).toHaveText('firing');
  await expect(page.getByRole('status').filter({ hasText: 'Acknowledgement recorded.' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Acknowledge', exact: true }).click();
  await expect.poll(() => operations.length).toBe(2);
  expect(operations[1]).not.toBe(operations[0]);
  await expect(page.getByRole('row').filter({ has: page.getByRole('button', { name: 'SYNTHETIC sustained blocking', exact: true }) }).getByRole('cell').first()).toHaveText('acknowledged');
});

test('query palette prefills only a selected database in the current server catalog and explains missing context', async ({ page }) => {
  await databaseFixture(page);
  await page.context().addCookies([{ name: 'paletteCatalogFixture', value: '1', url: origin }]);
  await page.goto(`${href('health')}&databaseId=6`);
  await page.keyboard.press('Control+k');
  const dialog = page.getByRole('dialog', { name: 'Jump to evidence' });
  await dialog.getByRole('button', { name: 'Query hash', exact: true }).click();
  await expect(dialog.getByRole('spinbutton', { name: 'Database ID', exact: true })).toHaveValue('6');
  await expect(dialog.locator('#query-search-databases option[value="6"]')).toHaveAttribute('label', 'Warehouse');
  await page.keyboard.press('Escape');
  await page.goto(`${href('health')}&databaseId=99`);
  await page.keyboard.press('Control+k');
  await dialog.getByRole('button', { name: 'Query hash', exact: true }).click();
  const database = dialog.getByRole('spinbutton', { name: 'Database ID', exact: true });
  await expect(database).toBeEnabled();
  await expect(database).toHaveValue('');
  await expect(dialog.getByRole('status').filter({ hasText: 'Workspace database ID 99 is absent' })).toBeVisible();
  await database.fill('6');
  await expect(database).toHaveValue('6');
  expect(scope(page).get('databaseId')).toBe('99');
});

test('delayed query palette catalog from a prior server cannot prefill the new server context', async ({ page }) => {
  await databaseFixture(page);
  await page.context().addCookies([{ name: 'paletteCatalogFixture', value: '1', url: origin }]);
  let release, requested = false, returned = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/${first}/query-performance/status`, async route => { const response = await route.fetch(); requested = true; await gate; await route.fulfill({ response }).catch(() => {}); returned = true; });
  const dialog = page.getByRole('dialog', { name: 'Jump to evidence' });
  try {
    await page.goto(`${href('health')}&databaseId=6`);
    await page.keyboard.press('Control+k');
    await dialog.getByRole('button', { name: 'Query hash', exact: true }).click();
    await expect.poll(() => requested).toBe(true);
    await expect(dialog.getByRole('spinbutton', { name: 'Database ID', exact: true })).toBeDisabled();
    await page.evaluate(hash => { location.hash = hash; }, `${href('health', second).slice(1)}&databaseId=5`);
    await expect(dialog.getByRole('spinbutton', { name: 'Database ID', exact: true })).toHaveValue('5');
    await expect(dialog.locator('#query-search-databases option[value="5"]')).toHaveAttribute('label', 'Orders 02');
  } finally { release(); }
  await expect.poll(() => returned).toBe(true);
  await expect(dialog.getByRole('spinbutton', { name: 'Database ID', exact: true })).toHaveValue('5');
  await expect(dialog.locator('#query-search-databases option[value="6"]')).toHaveAttribute('label', 'Warehouse 02');
  await expect(dialog.getByRole('searchbox', { name: 'Enter query hash', exact: true })).toHaveValue('');
});

test('fleet CPU memory waits blocking and HA cells open their diagnostic evidence in the shared window', async ({ page }) => {
  await fixture(page);
  const cases = [['SQL scheduler CPU', 'resources', null], ['OS free via SQL', 'resources', null], ['Waits', 'waits', null], ['Blocking', 'activity', null], ['HA', 'operations', 'availability']];
  for (const [signal, destination, check] of cases) {
    await page.goto(href('overview', ''));
    const row = page.getByRole('region', { name: 'Server health grid', exact: true }).getByRole('row').filter({ has: page.getByRole('link', { name: 'SYNTHETIC SQL 01', exact: true }) });
    const link = row.getByRole('link', { name: new RegExp(`^${signal}:`) });
    const params = new URLSearchParams((await link.getAttribute('href')).split('?')[1]);
    expect(params.get('target')).toBe(first);
    expect(params.get('from')).toBe(from);
    expect(params.get('to')).toBe(to);
    expect(params.get('check')).toBe(check);
    await link.focus();
    await link.press('Enter');
    await expect.poll(() => new URL(page.url()).hash.split('?')[0]).toBe(`#/${destination}`);
    await assertHistoricalScope(page);
    await expect(page.getByRole('combobox', { name: 'Target server', exact: true })).toHaveValue(first);
  }
});

test('diagnostic navigation and server changes retain the selected historical window', async ({ page }) => {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await open(page, 'health');
  await expect(page.getByRole('heading', { name: 'Investigate SYNTHETIC SQL 01', exact: true })).toBeVisible();
  for (const label of ['Waits', 'Resources', 'Query performance', 'Deadlocks', 'Health checks', 'Activity']) {
    await mainNavigation(page).getByRole('link', { name: label, exact: true }).click();
    await assertHistoricalScope(page);
    await expect(page.getByRole('combobox', { name: 'Target server' })).toHaveValue(first);
  }
  await page.getByRole('combobox', { name: 'Target server' }).selectOption(second);
  await assertHistoricalScope(page, second);
  await expect(page.getByRole('heading', { name: 'Activity evidence for SYNTHETIC SQL 02' })).toBeVisible();
  expect(errors).toEqual([]);
});

test('persistent fleet strip keeps all authorized servers across diagnostics and scopes server selection correctly', async ({ page }) => {
  await databaseFixture(page);
  await page.goto(`${href('health')}&databaseId=6`);
  for (const label of [null, 'Resources', 'Query performance']) {
    if (label) await mainNavigation(page).getByRole('link', { name: label, exact: true }).click();
    await expect(fleetStrip(page)).toHaveCount(1);
    await expect(fleetStrip(page).getByRole('link')).toHaveCount(10);
    await expect(fleetStrip(page).getByRole('link', { name: /^SYNTHETIC SQL 01:/ })).toHaveAttribute('aria-current', 'location');
    await expect(fleetStrip(page).getByRole('link', { name: /^SYNTHETIC SQL 10:/ })).toHaveCount(1);
    await assertDatabaseScope(page, 6);
    await assertHistoricalScope(page);
  }
  await fleetStrip(page).getByRole('link', { name: /^SYNTHETIC SQL 01:/ }).click();
  await assertDatabaseScope(page, 6);
  await assertHistoricalScope(page);
  await expect(page.getByRole('heading', { name: 'Server summary for SYNTHETIC SQL 01', exact: true })).toBeVisible();
  await fleetStrip(page).getByRole('link', { name: /^SYNTHETIC SQL 02:/ }).click();
  await assertHistoricalScope(page, second);
  await expect.poll(() => scope(page).get('databaseId')).toBe(null);
  await expect(workspaceDatabase(page)).toHaveValue('');
  await expect(page.getByRole('heading', { name: 'Server summary for SYNTHETIC SQL 02', exact: true })).toBeVisible();
  await expect(fleetStrip(page).getByRole('link')).toHaveCount(10);
  await expect(fleetStrip(page).getByRole('link', { name: /^SYNTHETIC SQL 02:/ })).toHaveAttribute('aria-current', 'location');
});

test('persistent fleet strip rejects delayed old-window evidence and explicitly reports an unavailable refresh', async ({ page }) => {
  await fixture(page);
  let release, requested = false, released = false, failFleet = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route('**/api/v1/overview?*', async route => {
    const query = new URL(route.request().url()).searchParams;
    if (query.has('targetId')) return route.continue();
    if (failFleet) return route.fulfill({ status: 503, json: {} });
    if (query.get('fromUtc') !== from) return route.continue();
    const response = await route.fetch();
    const body = await response.json();
    const stale = { ...body, targets: body.targets.map(item => ({ ...item, displayName: `STALE WINDOW ${item.displayName}` })), evidence: body.evidence.map(item => ({ ...item, displayName: `STALE WINDOW ${item.displayName}` })) };
    requested = true;
    await gate;
    await route.fulfill({ response, json: stale }).catch(() => {});
    released = true;
  });
  try {
    await page.goto(href('health'));
    await expect.poll(() => requested).toBe(true);
    await expect(page.getByRole('status').filter({ hasText: 'Loading authorized fleet status for the selected window' })).toBeVisible();
    await expect(fleetStrip(page).getByRole('link')).toHaveCount(0);
    await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
    await page.getByLabel('From (UTC)', { exact: true }).fill('2026-09-26T09:00');
    await page.getByLabel('To (UTC)', { exact: true }).fill('2026-09-26T11:00');
    await page.getByRole('button', { name: 'Apply range', exact: true }).click();
    await expect(fleetStrip(page).getByRole('link')).toHaveCount(10);
  } finally { release(); }
  await expect.poll(() => released).toBe(true);
  await expect(fleetStrip(page).getByRole('link', { name: /STALE WINDOW/ })).toHaveCount(0);
  for (const link of await fleetStrip(page).getByRole('link').all()) {
    const query = new URLSearchParams((await link.getAttribute('href')).split('?')[1]);
    expect(query.get('from')).toBe('2026-09-26T09:00:00.000Z');
    expect(query.get('to')).toBe('2026-09-26T11:00:00.000Z');
  }
  failFleet = true;
  await page.locator('.page-actions').getByRole('button', { name: /Refresh/ }).click();
  await expect(page.getByRole('status').filter({ hasText: 'Fleet status is unavailable:' })).toContainText('Missing fleet evidence is not an all-clear signal.');
  await expect(fleetStrip(page).getByRole('link')).toHaveCount(0);
});

test('persistent fleet strip preserves keyboard focus and horizontal scroll during a live snapshot refresh', async ({ page }) => {
  await databaseFixture(page);
  await page.clock.install({ time: new Date('2026-09-26T12:00:00Z') });
  let release, pending = false, fleetRequests = 0, receivedWindow, initialWindow;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route('**/api/v1/overview?*', async route => {
    const query = new URL(route.request().url()).searchParams;
    if (query.has('targetId')) return route.continue();
    const sequence = ++fleetRequests;
    if (sequence > 2) return route.continue();
    const response = await route.fetch();
    const body = await response.json();
    if (sequence === 1) {
      initialWindow = { from: body.fromUtc, to: body.toUtc };
      return route.fulfill({ response, json: body });
    }
    receivedWindow = { from: body.fromUtc, to: body.toUtc };
    pending = true;
    await gate;
    await route.fulfill({ response, json: body }).catch(() => {});
  });
  try {
    await page.goto(`${href('health', first, '1h')}&databaseId=6`);
    await expect(fleetStrip(page).getByRole('link')).toHaveCount(10);
    await assertDatabaseScope(page, 6);
    const entries = fleetStrip(page).getByRole('region', { name: 'Loaded fleet server entries', exact: true });
    const originalEntries = await entries.elementHandle();
    const originalNavigation = await fleetStrip(page).elementHandle();
    const currentLink = fleetStrip(page).getByRole('link', { name: /^SYNTHETIC SQL 01:/ });
    const assertLiveLink = async () => {
      const bounds = new URLSearchParams((await currentLink.getAttribute('href')).split('?')[1]);
      expect(Object.fromEntries(bounds)).toMatchObject({ target: first, range: '1h', databaseId: '6' });
      expect(bounds.get('from')).toBe(null);
      expect(bounds.get('to')).toBe(null);
    };
    await assertLiveLink();
    const coverage = page.locator('.workspace-fleet-status').filter({ hasText: 'Fleet CPU and memory window:' });
    const initialCoverage = await coverage.textContent();
    await expect.poll(() => entries.evaluate(element => element.scrollWidth - element.clientWidth)).toBeGreaterThan(200);
    await entries.focus();
    await entries.evaluate(element => { element.scrollLeft = 200; });
    await expect(entries).toBeFocused();
    const initialScroll = await entries.evaluate(element => element.scrollLeft);
    await page.clock.runFor(61_000);
    await expect.poll(() => pending).toBe(true);
    await expect(fleetStrip(page)).toHaveAttribute('aria-busy', 'true');
    await expect(page.getByRole('status').filter({ hasText: /previous.*snapshot/i })).toBeVisible();
    expect(await originalNavigation.evaluate(element => element.isConnected)).toBe(true);
    expect(await originalEntries.evaluate(element => element.isConnected)).toBe(true);
    await expect(entries).toBeFocused();
    expect(await entries.evaluate(element => element.scrollLeft)).toBe(initialScroll);
    await assertLiveLink();
    await expect(coverage).toHaveText(initialCoverage);
    release();
    await expect(fleetStrip(page)).toHaveAttribute('aria-busy', 'false');
    await expect(coverage).not.toHaveText(initialCoverage);
    expect(Date.parse(receivedWindow.to)).toBeGreaterThan(Date.parse(initialWindow.to));
    expect(Date.parse(receivedWindow.from)).toBeGreaterThan(Date.parse(initialWindow.from));
    await assertLiveLink();
    expect(await originalNavigation.evaluate(element => element.isConnected)).toBe(true);
    expect(await originalEntries.evaluate(element => element.isConnected)).toBe(true);
    await expect(entries).toBeFocused();
    expect(await entries.evaluate(element => element.scrollLeft)).toBe(initialScroll);
    await expect(fleetStrip(page).getByRole('link')).toHaveCount(10);
    await fleetStrip(page).getByRole('link', { name: /^SYNTHETIC SQL 02:/ }).click();
    await expect.poll(() => scope(page).get('target')).toBe(second);
    await expect.poll(() => scope(page).get('databaseId')).toBe(null);
    await expect(page.getByRole('combobox', { name: 'Time range', exact: true })).toHaveValue('1h');
    await expect(page.getByText('Live \u00b7 moving', { exact: true })).toBeVisible();
    expect(scope(page).get('from')).toBe(null);
    expect(scope(page).get('to')).toBe(null);
    await page.reload();
    await expect(page.getByRole('combobox', { name: 'Target server', exact: true })).toHaveValue(second);
    await expect(page.getByRole('combobox', { name: 'Time range', exact: true })).toHaveValue('1h');
    await expect(page.getByText('Live \u00b7 moving', { exact: true })).toBeVisible();
    expect(scope(page).get('databaseId')).toBe(null);
  } finally { release(); }
});

test('pause stops scheduled refresh, resume refreshes, and Rewind fixes the window', async ({ page }) => {
  const requests = [];
  page.on('request', request => { if (new URL(request.url()).pathname === '/api/v1/overview') requests.push(request.url()); });
  await page.clock.install({ time: new Date('2026-10-01T16:00:00Z') });
  await open(page, 'overview', first, '1h');
  await expect(page.getByRole('heading', { name: 'Server grid', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Pause live updates', exact: true }).click();
  await expect(page.getByText('Live \u00b7 paused', { exact: true })).toBeVisible();
  const pausedRequests = requests.length;
  await page.clock.fastForward(70_000);
  expect(requests).toHaveLength(pausedRequests);
  await page.getByRole('button', { name: 'Resume live updates', exact: true }).click();
  await expect.poll(() => requests.length).toBeGreaterThan(pausedRequests);
  await expect(page.getByText('Live \u00b7 moving', { exact: true })).toBeVisible();
  await page.getByRole('combobox', { name: 'Time range', exact: true }).selectOption('custom');
  await expect(page.getByText('Rewind \u00b7 fixed', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Pause live updates', exact: true })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Server grid', exact: true })).toBeVisible();
  const rewindRequests = requests.length, selected = Object.fromEntries(scope(page));
  await page.clock.fastForward(70_000);
  expect(requests).toHaveLength(rewindRequests);
  expect(Object.fromEntries(scope(page))).toEqual(selected);
});

test('custom UTC editor supports keyboard dismissal, validation, apply and focus restoration', async ({ page }) => {
  await open(page);
  const edit = page.getByRole('button', { name: 'Edit UTC range', exact: true });
  await edit.click();
  const fromInput = page.getByLabel('From (UTC)', { exact: true });
  await expect(fromInput).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(fromInput).toHaveCount(0);
  await expect(edit).toBeFocused();
  await edit.press('Enter');
  await fromInput.fill('2026-09-26T13:00');
  await page.getByLabel('To (UTC)', { exact: true }).fill('2026-09-26T12:00');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'The end must be after the start.' })).toBeVisible();
  await assertHistoricalScope(page);
  await fromInput.fill('2026-09-26T09:00');
  await page.getByLabel('To (UTC)', { exact: true }).fill('2026-09-26T11:00');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  await assertHistoricalScope(page, first, '2026-09-26T09:00:00.000Z', '2026-09-26T11:00:00.000Z');
  await expect(fromInput).toHaveCount(0);
  await expect(edit).toBeFocused();
  const selectedUrl = page.url();
  await page.getByRole('button', { name: 'Show UTC time', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Show local time', exact: true })).toBeVisible();
  expect(page.url()).toBe(selectedUrl);
});

test('custom UTC editor preserves exact microsecond URL bounds on unchanged Apply and on one-bound edits', async ({ page }) => {
  await fixture(page);
  const exactFrom = '2026-09-26T08:00:00.123456Z', exactTo = '2026-09-26T12:00:00.654321Z';
  await page.goto(`/#/health?${new URLSearchParams({ target: first, range: 'custom', from: exactFrom, to: exactTo })}`);
  await assertHistoricalScope(page, first, exactFrom, exactTo);
  const originalUrl = page.url();
  await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
  await expect(page.getByLabel('From (UTC)', { exact: true })).toHaveValue('2026-09-26T08:00:00.123');
  await expect(page.getByLabel('To (UTC)', { exact: true })).toHaveValue('2026-09-26T12:00:00.654');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  await expect(page).toHaveURL(originalUrl);
  await assertHistoricalScope(page, first, exactFrom, exactTo);
  await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
  await page.getByLabel('From (UTC)', { exact: true }).fill('2026-09-26T08:00:00.777');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  await assertHistoricalScope(page, first, '2026-09-26T08:00:00.777Z', exactTo);
  await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
  await page.getByLabel('To (UTC)', { exact: true }).fill('2026-09-26T12:00:00.888');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  await assertHistoricalScope(page, first, '2026-09-26T08:00:00.777Z', '2026-09-26T12:00:00.888Z');
});

test('custom UTC editor accepts tenths and hundredths and keeps invalid ranges from changing scope', async ({ page }) => {
  await open(page, 'health');
  for (const [fraction, normalized] of [['1', '100'], ['01', '010']]) {
    await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
    await page.getByLabel('From (UTC)', { exact: true }).fill(`2026-09-26T08:00:00.${fraction}`);
    await page.getByLabel('To (UTC)', { exact: true }).fill(`2026-09-26T12:00:00.${fraction}`);
    await page.getByRole('button', { name: 'Apply range', exact: true }).click();
    await assertHistoricalScope(page, first, `2026-09-26T08:00:00.${normalized}Z`, `2026-09-26T12:00:00.${normalized}Z`);
  }
  const valid = page.url();
  await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
  await page.getByLabel('From (UTC)', { exact: true }).fill('2026-08-01T08:00');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'Choose a range of 31 days or less.' })).toBeVisible();
  expect(page.url()).toBe(valid);
  await page.getByLabel('From (UTC)', { exact: true }).fill('2026-09-26T13:00');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  await expect(page.getByRole('alert').filter({ hasText: 'The end must be after the start.' })).toBeVisible();
  expect(page.url()).toBe(valid);
});

test('command palette keyboard results preserve Rewind scope and restore the invoking focus', async ({ page }) => {
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await open(page);
  const launch = page.getByRole('button', { name: /^Search/ });
  await launch.focus();
  await page.keyboard.press('Control+k');
  const dialog = page.getByRole('dialog', { name: 'Jump to evidence' });
  const search = dialog.getByRole('searchbox', { name: 'Search servers' });
  await expect(search).toBeFocused();
  await expect(dialog.getByRole('status')).toHaveText('10 authorized servers searched');
  await page.keyboard.press('Escape');
  await expect(dialog).not.toBeVisible();
  await expect(launch).toBeFocused();
  await launch.press('Enter');
  await search.fill('SQL 02');
  const result = dialog.getByRole('button', { name: /SYNTHETIC SQL 02/ });
  await expect(result).toHaveCount(1);
  await search.press('ArrowDown');
  await expect(result).toBeFocused();
  await result.press('ArrowUp');
  await expect(search).toBeFocused();
  await search.press('ArrowDown');
  await result.press('Enter');
  await expect(dialog).not.toBeVisible();
  await assertHistoricalScope(page, second);
  await expect(page.getByRole('heading', { name: 'Investigate SYNTHETIC SQL 02', exact: true })).toBeVisible();
  await expect(launch).toBeFocused();
  expect(errors).toEqual([]);
});

test('investigate-first and blocking drill-downs keep the server, window and event instant', async ({ page }) => {
  await open(page, 'health', second);
  await expect(page.getByRole('heading', { name: /Head blocker \u00b7 session 51/ })).toBeVisible();
  const blocking = page.locator('.server-dashboard-blocking');
  await expect(blocking.getByText('Session 72', { exact: true })).toBeVisible();
  await expect(blocking).toContainText('LCK_M_X \u00b7 12000 ms \u00b7 depth 1');
  await blocking.getByRole('link', { name: 'Open sessions and blocking \u2192', exact: true }).click();
  await assertHistoricalScope(page, second);
  await expect(page.getByRole('heading', { name: 'Activity evidence for SYNTHETIC SQL 02' })).toBeVisible();
  await page.goto(href('overview', second));
  const issue = page.locator('.overview-issue').filter({ hasText: 'Sessions blocked' });
  await issue.click();
  await assertHistoricalScope(page, second);
  expect(scope(page).get('at')).toBe('2026-09-26T11:59:30.000Z');
  await expect(page.getByRole('heading', { name: 'Live sessions', exact: true })).toBeVisible();
});

test('chart drag selects one shared window and the comparison toggle survives navigation', async ({ page }) => {
  const requests = [];
  page.on('request', request => { if (new URL(request.url()).pathname === '/api/v1/overview') requests.push(new URL(request.url())); });
  await open(page, 'health', second);
  const workload = page.locator('.server-dashboard-chart').filter({ has: page.getByRole('heading', { name: 'SQL workload', exact: true }) });
  const chart = workload.getByRole('img');
  await expect(chart).toBeVisible();
  await chart.scrollIntoViewIfNeeded();
  const box = await chart.boundingBox();
  await page.mouse.move(box.x + box.width * .25, box.y + box.height * .5);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * .7, box.y + box.height * .5, { steps: 8 });
  await page.mouse.up();
  await expect.poll(() => {
    const current = scope(page);
    return Date.parse(current.get('from')) > Date.parse(from)
      && Date.parse(current.get('to')) < Date.parse(to);
  }).toBe(true);
  const selected = Object.fromEntries(scope(page));
  expect(Date.parse(selected.from)).toBeGreaterThan(Date.parse(from));
  expect(Date.parse(selected.to)).toBeLessThan(Date.parse(to));
  await expect(page.getByRole('heading', { name: 'SQL workload', exact: true })).toBeVisible();
  // The URL changes before React's hashchange render. Wait for a rendered link
  // to use the narrowed scope before toggling its controlled comparison input.
  const resourcesLink = mainNavigation(page).getByRole('link', { name: 'Resources', exact: true });
  await expect.poll(async () => {
    const rendered = new URLSearchParams((await resourcesLink.getAttribute('href')).split('?')[1]);
    return rendered.get('from') === selected.from && rendered.get('to') === selected.to;
  }).toBe(true);
  const compare = page.getByRole('checkbox', { name: 'Compare previous period', exact: true });
  await compare.focus();
  await compare.press('Space');
  await expect(compare).toBeChecked();
  await expect.poll(() => scope(page).get('compare')).toBe('1');
  await mainNavigation(page).getByRole('link', { name: 'Resources', exact: true }).click();
  await assertHistoricalScope(page, second, selected.from, selected.to);
  expect(scope(page).get('compare')).toBe('1');
  await expect.poll(() => requests.some(url => url.searchParams.get('targetId') === second && url.searchParams.get('fromUtc') === selected.from && url.searchParams.get('toUtc') === selected.to)).toBe(true);
});

test('viewer actions name the required roles and missing coverage is never an all-clear', async ({ page }) => {
  await fixture(page);
  await page.goto('/__fixture?mode=partial');
  await page.goto(href('overview'));
  await expect(page.getByRole('button', { name: '+ Add server', exact: true })).toHaveCount(0);
  await expect(page.getByText('0/1 servers reporting current SQL core evidence', { exact: true })).toBeVisible();
  await page.getByText('Collection coverage and evidence gaps', { exact: true }).click();
  await expect(page.getByText('Synthetic collection timeout', { exact: true })).toBeVisible();
  await page.goto(href('health'));
  await expect(page.getByText('Operator or Target Administrator access unlocks saving an incident for this server.', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Open incident', exact: true })).toHaveCount(0);
  await page.goto('/__fixture?mode=empty');
  await page.goto(href('overview'));
  await expect(page.getByRole('heading', { name: 'Start monitoring your SQL environment' })).toBeVisible();
  await expect(page.getByText('A Target Administrator with all-target access can add a server.', { exact: true })).toBeVisible();
  await page.goto('/__fixture?mode=error');
  await page.goto(href('overview'));
  await expect(page.getByRole('alert')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Server grid', exact: true })).toHaveCount(0);
});

test('fleet server search and grouping are local controls that preserve global time', async ({ page }) => {
  await open(page, 'overview', '');
  await expect(page.getByRole('heading', { name: 'Server grid', exact: true })).toBeVisible();
  await page.getByRole('combobox', { name: 'Group servers by', exact: true }).selectOption('tag');
  await expect(page.getByRole('rowheader', { name: /Tag: east/ })).toBeVisible();
  await page.getByRole('textbox', { name: 'Search loaded servers', exact: true }).fill('SQL 02');
  const grid = page.locator('.overview-servers');
  await expect(grid).toContainText('Showing 1 of 10 authorized targets');
  await grid.getByRole('link', { name: 'SYNTHETIC SQL 02', exact: true }).click();
  await assertHistoricalScope(page, second);
  await expect(page.getByRole('heading', { name: 'Investigate SYNTHETIC SQL 02', exact: true })).toBeVisible();
});


test('deadlock relationships drill into the event activity and clear selection when the server changes', async ({ page }) => {
  await open(page, 'deadlocks');
  const detail = page.getByRole('region', { name: 'Selected deadlock detail', exact: true });
  await expect(detail.getByRole('img', { name: 'Deadlock relationship diagram' })).toBeVisible();
  const events = page.getByRole('complementary', { name: 'Captured deadlock events' });
  await expect(events.locator('button.event-card')).toHaveAttribute('aria-pressed', 'true');
  await expect(detail.getByRole('table')).toContainText('51');
  await expect(detail.getByRole('table')).toContainText('72');
  const activity = detail.getByRole('link', { name: /View activity at event time/ });
  const query = new URLSearchParams((await activity.getAttribute('href')).split('?')[1]);
  expect(query.get('event')).toBe('33333333-3333-4333-8333-333333333333');
  expect(query.get('target')).toBe(first);
  await activity.click();
  await assertHistoricalScope(page);
  expect(scope(page).get('event')).toBe('33333333-3333-4333-8333-333333333333');
  await page.goto(href('deadlocks'));
  await expect(detail.getByRole('table')).toBeVisible();
  let release, requested;
  const gate = new Promise(resolve => { release = resolve; });
  const started = new Promise(resolve => { requested = resolve; });
  await page.route(`**/${second}/deadlocks/*`, async route => {
    requested(); await gate; await route.continue().catch(() => {});
  });
  await page.getByRole('combobox', { name: 'Target server' }).selectOption(second);
  await started;
  await expect(detail.getByRole('table')).toHaveCount(0);
  await expect(detail.getByRole('status')).toContainText('Loading selected event');
  release();
  await expect(detail.getByRole('table')).toBeVisible();
  await assertHistoricalScope(page, second);
});


test('session inspection preserves its observation when filters remove the request and clears on target change', async ({ page }) => {
  await open(page, 'activity', first, '1h');
  const live = page.getByRole('region', { name: 'Live sessions', exact: true });
  const inspect = live.getByRole('button', { name: 'Inspect 51/0', exact: true });
  await expect(inspect).toBeVisible();
  await inspect.click();
  await expect(inspect).toHaveAttribute('aria-pressed', 'true');
  const detail = page.getByRole('complementary', { name: 'Session details', exact: true });
  await expect(detail).toContainText('This observation is preserved when filters change or the request disappears.');
  await expect(detail).toContainText('synthetic_dba');
  const captured = await detail.getByText(/^Observed /).textContent();
  await live.getByRole('checkbox', { name: 'Blocked only', exact: true }).check();
  await expect(inspect).toHaveCount(0);
  await expect(live.getByRole('button', { name: 'Inspect 72/0', exact: true })).toBeVisible();
  await expect(detail.getByText(/^Observed /)).toHaveText(captured);
  await expect(detail).toContainText('Cumulative CPU 1200 ms');
  await page.getByRole('combobox', { name: 'Target server' }).selectOption(second);
  await expect(detail).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Activity evidence for SYNTHETIC SQL 02' })).toBeVisible();
});


test('cache query ranking shows synthetic observations and opens scoped interval history', async ({ page }) => {
  await fixture(page);
  await page.goto(`${href('queries')}&queryView=cache`);
  const cache = page.getByRole('region', { name: 'Cache interval deltas', exact: true });
  const ranking = cache.getByRole('region', { name: 'Plan cache interval ranking', exact: true });
  await expect(ranking.getByRole('table')).toBeVisible();
  await expect(ranking).toContainText('Database ID 5');
  await expect(ranking).toContainText('bbbbbbbbbbbb');
  await expect(ranking.getByRole('cell', { name: '100', exact: true })).toBeVisible();
  await expect(cache.getByRole('alert')).toHaveCount(0);
  await ranking.getByRole('button', { name: 'Inspect cccccccccccc\u2026', exact: true }).click();
  const history = page.getByRole('region', { name: 'Cache query interval history', exact: true });
  await expect(history.getByRole('table')).toBeVisible();
  await expect(history).toContainText('Measured');
  await expect(history).toContainText('Opening captured query text and plan XML requires the QueryTextReader role and a read role on this server.');
  await expect(history.getByRole('button', { name: 'Open text', exact: true })).toHaveCount(0);
  await assertHistoricalScope(page);
});


test('changing the deadlock window clears the old detail while the same event reloads', async ({ page }) => {
  await open(page, 'deadlocks');
  const detail = page.getByRole('region', { name: 'Selected deadlock detail', exact: true });
  await expect(detail.getByRole('table')).toBeVisible();
  await expect(detail.getByRole('img', { name: 'Deadlock relationship diagram' })).toBeVisible();
  const event = page.getByRole('complementary', { name: 'Captured deadlock events' }).locator('button.event-card');
  await expect(event).toHaveAttribute('aria-pressed', 'true');
  let release, requested = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/${first}/deadlocks/33333333-3333-4333-8333-333333333333`, async route => {
    requested = true;
    await gate;
    await route.continue().catch(() => {});
  });
  try {
    await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
    await page.getByLabel('From (UTC)', { exact: true }).fill('2026-09-26T09:00');
    await page.getByLabel('To (UTC)', { exact: true }).fill('2026-09-26T11:00');
    await page.getByRole('button', { name: 'Apply range', exact: true }).click();
    await assertHistoricalScope(page, first, '2026-09-26T09:00:00.000Z', '2026-09-26T11:00:00.000Z');
    await expect.poll(() => requested).toBe(true);
    await expect(detail).toHaveAttribute('aria-busy', 'true');
    await expect(detail.getByRole('table')).toHaveCount(0);
    await expect(detail.getByRole('img', { name: 'Deadlock relationship diagram' })).toHaveCount(0);
    await expect(detail.getByRole('status')).toContainText('Loading selected event participants and relationships');
  } finally {
    release();
  }
  await expect(detail.getByRole('table')).toBeVisible();
  await expect(detail.getByRole('img', { name: 'Deadlock relationship diagram' })).toBeVisible();
  await expect(detail).toHaveAttribute('aria-busy', 'false');
  await expect(event).toHaveAttribute('aria-pressed', 'true');
});


test('blocking chains filter loaded rows locally and preserve an unrelated inspected observation', async ({ page }) => {
  const requests = [];
  page.on('request', request => {
    if (new URL(request.url()).pathname.endsWith('/activity/live')) requests.push(request.url());
  });
  await page.context().addCookies([{ name: 'blockingChainFixture', value: '1', url: origin }]);
  await open(page, 'activity', first, '1h');
  const live = page.getByRole('region', { name: 'Live sessions', exact: true });
  const table = live.getByRole('region', { name: 'Loaded session observations', exact: true });
  const chain = live.getByRole('combobox', { name: 'Blocking chain (loaded page)', exact: true });
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(7);
  await page.getByRole('button', { name: 'Pause live updates', exact: true }).click();
  await table.getByRole('button', { name: 'Inspect 90/0', exact: true }).click();
  const detail = page.getByRole('complementary', { name: 'Session details', exact: true });
  await expect(detail.getByRole('heading', { name: 'Session 90 / 0', exact: true })).toBeVisible();
  const requestCount = requests.length;
  const firstChain = live.getByRole('button', { name: 'Filter chain: Session 51', exact: true });
  await firstChain.click();
  await expect(firstChain).toHaveAttribute('aria-pressed', 'true');
  await expect(chain).toHaveValue('session:51');
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(3);
  for (const id of [51, 72, 73]) await expect(table.getByRole('button', { name: `Inspect ${id}/0`, exact: true })).toBeVisible();
  await expect(table.getByRole('button', { name: 'Inspect 90/0', exact: true })).toHaveCount(0);
  await expect(detail.getByRole('heading', { name: 'Session 90 / 0', exact: true })).toBeVisible();
  await chain.selectOption('session:81');
  await expect(live.getByRole('button', { name: 'Filter chain: Session 81', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(3);
  for (const id of [81, 82, 83]) await expect(table.getByRole('button', { name: `Inspect ${id}/0`, exact: true })).toBeVisible();
  await live.getByRole('button', { name: 'Clear chain filter', exact: true }).click();
  await expect(chain).toHaveValue('');
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(7);
  expect(requests).toHaveLength(requestCount);
  await firstChain.click();
  await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(second);
  await expect(chain).toHaveValue('');
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(7);
  await expect(detail).toHaveCount(0);
});

test('a selected chain disappearing from the next snapshot produces explicit zero matches', async ({ page }) => {
  await page.context().addCookies([{ name: 'blockingChainFixture', value: '1', url: origin }]);
  await open(page, 'activity', first, '1h');
  const live = page.getByRole('region', { name: 'Live sessions', exact: true });
  const table = live.getByRole('region', { name: 'Loaded session observations', exact: true });
  const chain = live.getByRole('combobox', { name: 'Blocking chain (loaded page)', exact: true });
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(7);
  await page.getByRole('button', { name: 'Pause live updates', exact: true }).click();
  await live.getByRole('button', { name: 'Filter chain: Session 51', exact: true }).click();
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(3);
  await page.route(`**/${first}/activity/live?*`, async route => {
    const response = await route.fetch();
    const body = await response.json();
    body.rows = body.rows.filter(row => ![51, 72, 73].includes(row.sessionId));
    await route.fulfill({ response, json: body });
  });
  await live.getByRole('button', { name: 'Refresh now', exact: true }).click();
  await expect(chain).toHaveValue('session:51');
  await expect(chain.getByRole('option', { name: 'Selected chain unavailable: session:51', exact: true })).toHaveCount(1);
  await expect(live.getByText(/^Showing 0 of 4 loaded session rows/)).toBeVisible();
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(0);
  await expect(live.getByText('Selected blocking chain is not present in this loaded page. No rows match; clear the chain filter or browse another snapshot/page.', { exact: true })).toBeVisible();
  await live.getByRole('button', { name: 'Clear chain filter', exact: true }).click();
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(4);
  for (const id of [81, 82, 83, 90]) await expect(table.getByRole('button', { name: `Inspect ${id}/0`, exact: true })).toBeVisible();
});


async function databaseFixture(page) {
  await fixture(page);
  await page.context().addCookies([{ name: 'databaseScopeFixture', value: '1', url: origin }]);
}
const workspaceDatabase = page => page.getByRole('combobox', { name: 'Workspace database', exact: true });
async function assertDatabaseScope(page, databaseId) {
  await expect.poll(() => scope(page).get('databaseId')).toBe(String(databaseId));
  await expect(workspaceDatabase(page)).toHaveValue(String(databaseId));
}

test('workspace database follows diagnostic navigation, time edits, reload and browser history', async ({ page }) => {
  await databaseFixture(page);
  await page.goto(href('health'));
  await expect(workspaceDatabase(page).getByRole('option', { name: /^Warehouse / })).toHaveCount(1);
  await workspaceDatabase(page).selectOption('6');
  await assertDatabaseScope(page, 6);
  for (const label of ['Activity', 'Query performance', 'Resources', 'Waits', 'Deadlocks', 'Health checks']) {
    await mainNavigation(page).getByRole('link', { name: label, exact: true }).click();
    await assertDatabaseScope(page, 6);
    await assertHistoricalScope(page);
    await expect(page.getByTestId('database-context-notice')).toContainText('server-wide metrics, waits, blocking history, deadlocks, alerts and health checks remain server-wide.');
  }
  await mainNavigation(page).getByRole('link', { name: 'Resources', exact: true }).click();
  await assertDatabaseScope(page, 6);
  await page.reload();
  await assertDatabaseScope(page, 6);
  await assertHistoricalScope(page);
  await expect(page.getByRole('heading', { name: 'Resources for SYNTHETIC SQL 01' })).toBeVisible();
  await mainNavigation(page).getByRole('link', { name: 'Waits', exact: true }).click();
  await assertDatabaseScope(page, 6);
  await page.goBack();
  await expect(page).toHaveURL(/#\/resources\?/);
  await assertDatabaseScope(page, 6);
  await page.goForward();
  await expect(page).toHaveURL(/#\/waits\?/);
  await assertDatabaseScope(page, 6);
  await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
  await page.getByLabel('From (UTC)', { exact: true }).fill('2026-09-26T09:00');
  await page.getByLabel('To (UTC)', { exact: true }).fill('2026-09-26T11:00');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  await assertDatabaseScope(page, 6);
  await assertHistoricalScope(page, first, '2026-09-26T09:00:00.000Z', '2026-09-26T11:00:00.000Z');
  await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(second);
  await expect.poll(() => scope(page).get('databaseId')).toBe(null);
  await expect(workspaceDatabase(page)).toHaveValue('');
  await expect(workspaceDatabase(page).getByRole('option', { name: /^Warehouse 02 / })).toHaveCount(1);
  await workspaceDatabase(page).selectOption('6');
  await assertDatabaseScope(page, 6);
  await mainNavigation(page).getByRole('link', { name: 'Alerts', exact: true }).click();
  await assertDatabaseScope(page, 6);
  await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption('');
  await expect.poll(() => scope(page).get('target')).toBe(null);
  await expect.poll(() => scope(page).get('databaseId')).toBe(null);
  await expect(workspaceDatabase(page)).toHaveCount(0);
});

test('live database selection updates the URL and sends the selected database to collection reads', async ({ page }) => {
  const requests = [];
  page.on('request', request => { if (new URL(request.url()).pathname.endsWith('/activity/live')) requests.push(new URL(request.url())); });
  await databaseFixture(page);
  await page.goto(href('activity', first, '1h'));
  const live = page.getByRole('region', { name: 'Live sessions', exact: true });
  const table = live.getByRole('region', { name: 'Loaded session observations', exact: true });
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(3);
  await live.getByRole('combobox', { name: 'Database', exact: true }).selectOption('6');
  await assertDatabaseScope(page, 6);
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(1);
  await expect(table.getByRole('button', { name: 'Inspect 81/0', exact: true })).toBeVisible();
  await expect.poll(() => requests.some(url => url.searchParams.get('databaseId') === '6')).toBe(true);
  await workspaceDatabase(page).selectOption('5');
  await assertDatabaseScope(page, 5);
  await expect(live.getByRole('combobox', { name: 'Database', exact: true })).toHaveValue('5');
  await expect(table.getByRole('button', { name: /^Inspect / })).toHaveCount(2);
  await expect(table).not.toContainText('Warehouse');
});

test('database context scopes loaded cache and file rows without relabelling server resources', async ({ page }) => {
  await databaseFixture(page);
  await page.goto(`${href('queries')}&queryView=cache&databaseId=6`);
  await assertDatabaseScope(page, 6);
  const ranking = page.getByRole('region', { name: 'Plan cache interval ranking', exact: true });
  await expect(ranking.getByRole('button', { name: /^Inspect / })).toHaveCount(1);
  await expect(ranking).toContainText('dddddddddddd');
  await expect(ranking).not.toContainText('bbbbbbbbbbbb');
  await ranking.getByRole('button', { name: 'Inspect eeeeeeeeeeee\u2026', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Cache query interval history', exact: true }).getByRole('table')).toBeVisible();
  await workspaceDatabase(page).selectOption('5');
  await assertDatabaseScope(page, 5);
  for (const key of ['cacheDatabaseId', 'cacheIncarnation', 'cacheQuery', 'cachePlan']) expect(scope(page).get(key)).toBe(null);
  await expect(page.getByRole('region', { name: 'Cache query interval history', exact: true })).toHaveCount(0);
  await mainNavigation(page).getByRole('link', { name: 'Resources', exact: true }).click();
  await workspaceDatabase(page).selectOption('6');
  await assertDatabaseScope(page, 6);
  const files = page.getByRole('region', { name: 'Database file I/O', exact: true });
  await expect(files.getByRole('table')).toContainText('Warehouse_data');
  await expect(files.getByRole('table')).not.toContainText('Orders_data');
  await expect(page.getByText('CPU, memory, page life expectancy, and volume capacity remain server-wide. Database file rows are filtered only within the loaded page.', { exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'SQL scheduler CPU', exact: true })).toBeVisible();
  await expect(files).toContainText('Showing 1 of 2 loaded files for database ID 6');
  await files.getByRole('button', { name: 'View read and write latency trend for database 6, file Warehouse_data', exact: true }).click();
  const selectedFile = page.getByRole('region', { name: 'Selected file latency history', exact: true });
  await expect(selectedFile).toContainText('Database 6 / Warehouse_data');
  await workspaceDatabase(page).selectOption('5');
  await assertDatabaseScope(page, 5);
  await expect(selectedFile).toContainText('Select a file in the table to inspect its history.');
  await expect(selectedFile).not.toContainText('Warehouse_data');
});

test('unknown database scope stays explicit through missing inventory and clears safely', async ({ page }) => {
  await databaseFixture(page);
  await page.goto(`${href('resources')}&databaseId=99`);
  await assertDatabaseScope(page, 99);
  await expect(workspaceDatabase(page).getByRole('option', { name: 'Database ID 99 (not in loaded inventory)', exact: true })).toHaveCount(1);
  await expect(page.locator('.database-scope-status')).toContainText('2 databases loaded');
  await expect(page.getByText('No file rows for the selected database are present in this loaded page. Browse other file pages to continue.', { exact: true })).toBeVisible();
  await page.route(`**/${first}/health/databases?*`, route => route.fulfill({ status: 503, json: {} }));
  await page.locator('.page-actions').getByRole('button', { name: /Refresh/ }).click();
  await expect(page.locator('.database-scope-status')).toContainText('Database inventory unavailable.');
  await assertDatabaseScope(page, 99);
  await expect(workspaceDatabase(page).getByRole('option', { name: /^Warehouse / })).toHaveCount(1);
  await workspaceDatabase(page).selectOption('');
  await expect.poll(() => scope(page).get('databaseId')).toBe(null);
  await expect(workspaceDatabase(page)).toHaveValue('');
  await expect(page.getByRole('region', { name: 'Database file I/O', exact: true }).getByRole('table')).toContainText('Orders_data');
  await page.goto(`${href('resources')}&databaseId=32768`);
  await expect(workspaceDatabase(page)).toHaveValue('');
});

test('delayed database inventory from an old target never supplies names for the new server', async ({ page }) => {
  await databaseFixture(page);
  let release, requested = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/${first}/health/databases?*`, async route => { requested = true; await gate; await route.continue().catch(() => {}); });
  try {
    await page.goto(href('health'));
    await expect.poll(() => requested).toBe(true);
    await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(second);
    await expect(workspaceDatabase(page).getByRole('option', { name: /^Warehouse 02 / })).toHaveCount(1);
    await expect(workspaceDatabase(page).getByRole('option', { name: /^Orders 02 / })).toHaveCount(1);
  } finally { release(); }
  await expect(workspaceDatabase(page).getByRole('option', { name: /^Warehouse \u00b7/ })).toHaveCount(0);
  await expect(workspaceDatabase(page).getByRole('option', { name: /^Orders \u00b7/ })).toHaveCount(0);
  await expect(page.locator('.database-scope-status')).toContainText('2 databases loaded');
  await expect(page.getByRole('combobox', { name: 'Target server', exact: true })).toHaveValue(second);
});


test('changing database context clears contradictory query and event selections', async ({ page }) => {
  await databaseFixture(page);
  await page.goto(`${href('queries')}&databaseId=5&queryFingerprint=${'b'.repeat(64)}&planFingerprint=${'c'.repeat(64)}&source=query_store`);
  await expect(workspaceDatabase(page).getByRole('option', { name: /^Warehouse / })).toHaveCount(1);
  await workspaceDatabase(page).selectOption('6');
  await assertDatabaseScope(page, 6);
  for (const key of ['queryFingerprint', 'planFingerprint', 'source']) expect(scope(page).get(key)).toBe(null);
  const at = new Date(Date.now() - 60_000).toISOString();
  await page.goto(`${href('activity', first, '1h')}&databaseId=5&at=${encodeURIComponent(at)}&event=33333333-3333-4333-8333-333333333333`);
  await expect(workspaceDatabase(page).getByRole('option', { name: /^Warehouse / })).toHaveCount(1);
  await workspaceDatabase(page).selectOption('6');
  await assertDatabaseScope(page, 6);
  expect(scope(page).get('at')).toBe(null);
  expect(scope(page).get('event')).toBe(null);
  const live = page.getByRole('region', { name: 'Live sessions', exact: true });
  await expect(live.getByRole('button', { name: 'Inspect 81/0', exact: true })).toBeVisible();
});


test('obsolete database change events cannot restore scope during a server or fleet navigation', async ({ page }) => {
  await databaseFixture(page);
  await page.goto(`${href('resources')}&databaseId=5`);
  await assertDatabaseScope(page, 5);
  await expect(workspaceDatabase(page).getByRole('option', { name: /^Warehouse / })).toHaveCount(1);
  // Hash navigation and the old select event run in one browser task. React has
  // not rendered the new target when the obsolete control dispatches change.
  await workspaceDatabase(page).evaluate((select, nextHash) => {
    location.hash = nextHash;
    select.value = '6';
    select.dispatchEvent(new Event('change', { bubbles: true }));
  }, href('resources', second).slice(1));
  await expect.poll(() => scope(page).get('target')).toBe(second);
  await expect.poll(() => scope(page).get('databaseId')).toBe(null);
  await expect(workspaceDatabase(page)).toHaveValue('');
  await expect(workspaceDatabase(page).getByRole('option', { name: /^Warehouse 02 / })).toHaveCount(1);
  await expect(page.getByRole('heading', { name: 'Resources for SYNTHETIC SQL 02' })).toBeVisible();
  await workspaceDatabase(page).evaluate((select, nextHash) => {
    location.hash = nextHash;
    select.value = '6';
    select.dispatchEvent(new Event('change', { bubbles: true }));
  }, href('overview', '').slice(1));
  await expect.poll(() => scope(page).get('target')).toBe('');
  await expect.poll(() => scope(page).get('databaseId')).toBe(null);
  await expect(workspaceDatabase(page)).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Fleet overview', exact: true })).toBeVisible();
});

const investigationTimeline = page => page.getByRole('region', { name: 'Investigation timeline', exact: true });
const planTimelineButtons = page => investigationTimeline(page).getByRole('button', { name: /^Align charts: Plan regression comparison/ });
async function timelineFixture(page) {
  await databaseFixture(page);
  await page.context().addCookies([{ name: 'timelineFixture', value: '1', url: origin }]);
}

test('timeline URL restores the selected event with exact window and database context after reload', async ({ page }) => {
  await timelineFixture(page);
  const exactFrom = '2026-09-26T08:00:00.123456Z', exactTo = '2026-09-26T12:00:00.654321Z';
  await page.goto(`/#/health?${new URLSearchParams({ target: first, range: 'custom', from: exactFrom, to: exactTo, databaseId: '6', compare: '1', healthSection: 'summary' })}`);
  const selected = planTimelineButtons(page);
  await expect(selected).toHaveCount(1);
  await selected.focus();
  await selected.press('Enter');
  await expect(selected).toHaveAttribute('aria-pressed', 'true');
  await expect.poll(() => scope(page).get('timelineEvent')).toMatch(/^plan:/);
  const selectedId = scope(page).get('timelineEvent'), selectedTime = scope(page).get('timelineAt');
  await assertHistoricalScope(page, first, exactFrom, exactTo);
  await assertDatabaseScope(page, 6);
  await page.reload();
  await expect(selected).toHaveAttribute('aria-pressed', 'true');
  await expect(investigationTimeline(page).getByRole('status').locator('time')).toHaveAttribute('datetime', selectedTime);
  await expect.poll(() => page.locator('.server-dashboard .chart-crosshair').count()).toBeGreaterThanOrEqual(4);
  await assertHistoricalScope(page, first, exactFrom, exactTo);
  await assertDatabaseScope(page, 6);
  expect(scope(page).get('timelineEvent')).toBe(selectedId);
  expect(scope(page).get('compare')).toBe('1');
  await investigationTimeline(page).getByRole('button', { name: 'Clear timeline alignment', exact: true }).press('Enter');
  await expect.poll(() => scope(page).get('timelineEvent')).toBe(null);
  expect(scope(page).get('timelineAt')).toBe(null);
  await assertHistoricalScope(page, first, exactFrom, exactTo);
});

test('aligning a Live timeline event freezes its loaded window and retains keyboard focus before reload', async ({ page }) => {
  await timelineFixture(page);
  await page.clock.install({ time: new Date('2026-09-26T12:00:00Z') });
  let loadedWindow;
  page.on('response', async response => {
    const url = new URL(response.url());
    if (url.pathname === '/api/v1/overview' && url.searchParams.get('targetId') === first && response.ok()) {
      const body = await response.json();
      loadedWindow = { from: body.fromUtc, to: body.toUtc };
    }
  });
  await page.goto(`${href('health', first, '1h')}&databaseId=6`);
  await expect(planTimelineButtons(page)).toHaveCount(1);
  await expect.poll(() => loadedWindow).toBeTruthy();
  const window = { ...loadedWindow };
  const align = planTimelineButtons(page);
  const button = await align.elementHandle();
  const eventTime = await align.locator('xpath=ancestor::li').locator('time').getAttribute('datetime');
  await align.focus();
  await align.press('Enter');
  await expect.poll(() => Object.fromEntries(scope(page))).toMatchObject({ target: first, databaseId: '6', range: 'custom', from: window.from, to: window.to, timelineAt: eventTime });
  await expect.poll(() => scope(page).get('timelineEvent')).toMatch(/^plan:/);
  await expect(align).toHaveAttribute('aria-pressed', 'true');
  await expect(align).toBeFocused();
  expect(await button.evaluate(element => element.isConnected)).toBe(true);
  await expect(page.getByRole('combobox', { name: 'Time range', exact: true })).toHaveValue('custom');
  await page.reload();
  await expect(align).toHaveAttribute('aria-pressed', 'true');
  await expect(investigationTimeline(page).getByRole('status').locator('time')).toHaveAttribute('datetime', eventTime);
  await assertHistoricalScope(page, first, window.from, window.to);
  await assertDatabaseScope(page, 6);
});

test('selected Agent timeline marker retains its focused button during a pending refresh and becomes explicitly missing after failure', async ({ page }) => {
  await timelineFixture(page);
  await page.goto(`${href('health')}&healthSection=summary`);
  const timeline = investigationTimeline(page);
  const selected = timeline.getByRole('button', { name: /^Align charts: SQL Agent job failure/ }).first();
  await expect(selected).toBeVisible();
  await selected.focus(); await selected.press('Enter');
  await expect.poll(() => scope(page).get('timelineEvent')).toMatch(/^agent:/);
  const selectionId = scope(page).get('timelineEvent');
  const original = await selected.elementHandle();
  let release, pending = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/observation-targets/${first}/sql-agent/failures?*`, async route => {
    pending = true; await gate;
    await route.fulfill({ status: 503, contentType: 'application/json', body: '{}' });
  });
  // Native click() activates refresh without moving keyboard focus from the selected marker.
  await page.locator('.page-actions').getByRole('button', { name: /Refresh/ }).evaluate(element => element.click());
  await expect.poll(() => pending).toBe(true);
  await expect(selected).toHaveAttribute('aria-pressed', 'true');
  await expect(selected).toBeFocused();
  expect(await original.evaluate(element => element.isConnected)).toBe(true);
  expect(scope(page).get('timelineEvent')).toBe(selectionId);
  release();
  await expect(timeline).toContainText('The requested timeline event is not present in the loaded evidence for this window. No other event has been selected.');
  await expect(timeline.getByRole('button', { name: /^Align charts: SQL Agent job failure/ })).toHaveCount(0);
  await expect(timeline.locator('button[aria-pressed="true"]')).toHaveCount(0);
  expect(scope(page).get('timelineEvent')).toBe(selectionId);
  await timeline.getByText('Timeline coverage', { exact: true }).click();
  await expect(timeline).toContainText('SQL Agent first-observed markers are unavailable');
});

test('Overview rejects response bounds shifted by one microsecond from the fixed workspace window', async ({ page }) => {
  await timelineFixture(page);
  const exactFrom = '2026-09-26T08:00:00.123456Z', exactTo = '2026-09-26T12:00:00.654321Z';
  await page.route('**/api/v1/overview?*', async route => {
    if (new URL(route.request().url()).searchParams.get('targetId') !== first) return route.continue();
    const response = await route.fetch();
    const body = await response.json();
    await route.fulfill({ response, json: { ...body, fromUtc: '2026-09-26T08:00:00.123457Z' } });
  });
  await page.goto(`/#/health?${new URLSearchParams({ target: first, range: 'custom', from: exactFrom, to: exactTo, healthSection: 'summary' })}`);
  await expect(page.getByText('Overview returned a mismatched scope or time range.', { exact: true })).toBeVisible();
  await expect(investigationTimeline(page)).toHaveCount(0);
  await expect(page.locator('.server-dashboard .overview-chart')).toHaveCount(0);
  await assertHistoricalScope(page, first, exactFrom, exactTo);
});

test('Overview previous-period comparison requests retain the exact microsecond duration', async ({ page }) => {
  await timelineFixture(page);
  const exactFrom = '2026-09-26T08:00:00.123456Z', exactTo = '2026-09-26T12:00:00.654321Z';
  const expectedPrevious = '2026-09-26T03:59:59.592591Z';
  let comparison;
  page.on('request', request => {
    const url = new URL(request.url());
    if (url.pathname === '/api/v1/overview' && url.searchParams.get('targetId') === first && url.searchParams.get('toUtc') === exactFrom)
      comparison = Object.fromEntries(url.searchParams);
  });
  await page.goto(`/#/health?${new URLSearchParams({ target: first, range: 'custom', from: exactFrom, to: exactTo, compare: '1', healthSection: 'summary' })}`);
  await expect.poll(() => comparison).toMatchObject({ targetId: first, fromUtc: expectedPrevious, toUtc: exactFrom });
  await expect(planTimelineButtons(page)).toHaveCount(2);
  await expect(page.getByText('Previous-period evidence is unavailable.', { exact: true })).toHaveCount(0);
  await assertHistoricalScope(page, first, exactFrom, exactTo);
});

test('requested timeline selection remains explicit when hidden or absent and never selects another event', async ({ page }) => {
  await timelineFixture(page);
  await page.goto(`${href('health')}&databaseId=6`);
  const timeline = investigationTimeline(page), category = timeline.getByRole('combobox', { name: 'Timeline event category', exact: true });
  await expect(planTimelineButtons(page)).toHaveCount(1);
  await planTimelineButtons(page).click();
  await expect.poll(() => scope(page).get('timelineEvent')).toMatch(/^plan:/);
  const selectedHash = new URL(page.url()).hash;
  await category.selectOption('deadlock');
  await expect.poll(() => scope(page).get('timelineEvent')).toBe(null);
  // An externally restored selection is kept even when the user's category hides it.
  await page.evaluate(hash => { location.hash = hash; }, selectedHash);
  await expect(timeline).toContainText('The selected timeline event is hidden by this category.');
  await expect(timeline.locator('button[aria-pressed="true"]')).toHaveCount(0);
  await timeline.getByRole('button', { name: 'Show selected timeline event', exact: true }).press('Enter');
  await expect(category).toHaveValue('all');
  await expect(planTimelineButtons(page)).toHaveAttribute('aria-pressed', 'true');
  const parameters = scope(page); parameters.set('timelineEvent', 'plan:missing');
  await page.evaluate(query => { location.hash = '#/health?' + query; }, parameters.toString());
  await expect(timeline).toContainText('The requested timeline event is not present in the loaded evidence for this window. No other event has been selected.');
  await expect(timeline.locator('button[aria-pressed="true"]')).toHaveCount(0);
  await expect(page.locator('.server-dashboard .chart-crosshair')).toHaveCount(0);
  expect(scope(page).get('timelineEvent')).toBe('plan:missing');
  await page.reload();
  await expect(timeline).toContainText('No other event has been selected.');
  await expect(timeline.locator('button[aria-pressed="true"]')).toHaveCount(0);
  await timeline.getByRole('button', { name: 'Clear timeline alignment', exact: true }).click();
  await expect.poll(() => scope(page).get('timelineEvent')).toBe(null);
});

test('delayed requested timeline evidence cannot restore a selection after switching servers', async ({ page }) => {
  await timelineFixture(page);
  await page.goto(`${href('health')}&databaseId=6`);
  await expect(planTimelineButtons(page)).toHaveCount(1);
  await planTimelineButtons(page).click();
  await expect.poll(() => scope(page).get('timelineEvent')).toMatch(/^plan:/);
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  let delayed = false;
  await page.route(`**/observation-targets/${first}/query-performance/regressions?*`, async route => {
    const response = await route.fetch(); delayed = true; await gate;
    await route.fulfill({ response });
  });
  await page.reload();
  await expect.poll(() => delayed).toBe(true);
  await expect(investigationTimeline(page)).toContainText('Loading the requested timeline event');
  await page.evaluate(hash => { location.hash = hash; }, href('health', second).substring(1));
  await expect.poll(() => scope(page).get('target')).toBe(second);
  await expect.poll(() => scope(page).get('timelineEvent')).toBe(null);
  await expect(planTimelineButtons(page)).toHaveCount(2);
  release();
  await expect(investigationTimeline(page).locator('button[aria-pressed="true"]')).toHaveCount(0);
  await expect(investigationTimeline(page)).not.toContainText('Loading the requested timeline event');
  expect(scope(page).get('target')).toBe(second);
  expect(scope(page).get('timelineAt')).toBe(null);
});

test('Resources pointer and keyboard inspection align CPU memory PLE and file latency without changing the workspace window', async ({ page }) => {
  await databaseFixture(page);
  await page.context().addCookies([{ name: 'resourceInspectionFixture', value: '1', url: origin }]);
  await page.goto(`${href('resources')}&databaseId=5`);
  const resources = page.getByRole('region', { name: 'Resources for SYNTHETIC SQL 01', exact: true });
  await resources.getByRole('button', { name: 'View read and write latency trend for database 5, file Orders_data', exact: true }).click();
  const charts = resources.locator('.overview-chart');
  await expect(charts).toHaveCount(7);
  const cpu = resources.locator('.server-dashboard-chart').filter({ has: page.getByRole('heading', { name: 'SQL scheduler CPU', exact: true }) }).locator('.overview-chart');
  await cpu.scrollIntoViewIfNeeded();
  const box = await cpu.boundingBox(); expect(box).not.toBeNull();
  await page.mouse.move(box.x + box.width * 0.5, box.y + box.height * 0.5);
  await expect(resources.locator('.chart-crosshair')).toHaveCount(7);
  const pointerLabels = await resources.locator('.chart-crosshair text').allTextContents();
  expect(new Set(pointerLabels).size).toBe(1);
  await assertHistoricalScope(page); await assertDatabaseScope(page, 5);
  const input = resources.getByLabel('Resource inspection time (UTC)', { exact: true });
  await input.fill('2026-09-26T10:30:00.125');
  await resources.getByRole('button', { name: 'Align resource charts', exact: true }).press('Enter');
  await expect(resources.getByRole('region', { name: 'Resource chart alignment', exact: true }).getByRole('status').locator('time')).toHaveAttribute('datetime', '2026-09-26T10:30:00.125Z');
  await expect(resources.locator('.chart-crosshair')).toHaveCount(7);
  expect(new Set(await resources.locator('.chart-crosshair text').allTextContents()).size).toBe(1);
  await assertHistoricalScope(page); await assertDatabaseScope(page, 5);
  await input.fill('2026-09-26T12:00');
  await resources.getByRole('button', { name: 'Align resource charts', exact: true }).press('Enter');
  await expect(input).toHaveAttribute('aria-invalid', 'true');
  await expect(resources.getByRole('alert')).toHaveText('Choose an inspection time inside the shared window, before its end.');
  await expect(resources.locator('.chart-crosshair')).toHaveCount(0);
  await input.fill('2026-09-26T09:00');
  await resources.getByRole('button', { name: 'Align resource charts', exact: true }).press('Enter');
  await expect(resources.locator('.chart-crosshair')).toHaveCount(7);
  await resources.getByRole('button', { name: 'Clear resource alignment', exact: true }).press('Enter');
  await expect(resources.locator('.chart-crosshair')).toHaveCount(0);
  await expect(resources.getByRole('region', { name: 'Resource chart alignment', exact: true }).getByRole('status')).toContainText('No resource alignment selected.');
  await assertHistoricalScope(page); await assertDatabaseScope(page, 5);
});

test('periodic live configuration history refresh preserves the user current keyboard focus', async ({ page }) => {
  await timelineFixture(page);
  await page.clock.install({ time: new Date('2026-09-26T12:00:00Z') });
  let configurationRequests = 0;
  page.on('request', request => {
    if (new URL(request.url()).pathname === `/api/v1/observation-targets/${first}/health/server-configuration-changes`) configurationRequests++;
  });
  await page.goto(`${href('health', first, '1h')}&healthSection=summary&configurationRun=22222222-2222-4222-8222-222222222222`);
  const selectedRun = page.locator('li[data-configuration-run="22222222-2222-4222-8222-222222222222"]');
  await expect(selectedRun).toBeFocused();
  const initiallyFocusedText = await selectedRun.textContent();
  const initiallyFocusedRequests = configurationRequests;
  // The workspace clock emits six 10-second ticks per history refresh.
  // runFor executes every tick; fastForward intentionally coalesces intervals.
  await page.clock.runFor(61_000);
  await expect.poll(() => configurationRequests).toBeGreaterThan(initiallyFocusedRequests);
  await expect(selectedRun).not.toHaveText(initiallyFocusedText);
  await expect(selectedRun).toContainText('cost threshold for parallelism');
  await expect(selectedRun).toBeFocused();
  const before = await selectedRun.textContent();
  const timeRange = page.getByRole('combobox', { name: 'Time range', exact: true });
  await timeRange.focus();
  await expect(timeRange).toBeFocused();
  const initialRequests = configurationRequests;
  await page.clock.runFor(61_000);
  await expect.poll(() => configurationRequests).toBeGreaterThan(initialRequests);
  // A changed detection label proves that the refreshed history has rendered,
  // rather than only waiting for the network request to begin.
  await expect(selectedRun).not.toHaveText(before);
  await expect(selectedRun).toContainText('cost threshold for parallelism');
  await expect(timeRange).toBeFocused();
  await expect(timeRange).toHaveValue('1h');
  expect(scope(page).get('configurationRun')).toBe('22222222-2222-4222-8222-222222222222');
});

test('investigation timeline remains usable when dashboard metric series contain no observations', async ({ page }) => {
  await timelineFixture(page);
  await page.route('**/api/v1/overview?*', async route => {
    const response = await route.fetch();
    const body = await response.json();
    await route.fulfill({ response, json: { ...body, evidence: body.evidence.map(item => ({ ...item, series: [] })) } });
  });
  await page.goto(href('health'));
  await expect(planTimelineButtons(page)).toHaveCount(2);
  const timeline = investigationTimeline(page);
  await expect(timeline.getByRole('button', { name: /^Align charts: Deadlock/ })).toHaveCount(1);
  const emptyCharts = page.locator('.server-dashboard .overview-empty');
  await expect.poll(() => emptyCharts.count()).toBeGreaterThanOrEqual(4);
  for (const placeholder of await emptyCharts.all()) await expect(placeholder).toHaveText('No comparable observations in this window. Missing samples are not plotted as zero.');
  const comparison = planTimelineButtons(page).first();
  const eventTime = await comparison.locator('xpath=ancestor::li').locator('time').getAttribute('datetime');
  await comparison.focus();
  await comparison.press('Enter');
  await expect(comparison).toHaveAttribute('aria-pressed', 'true');
  await expect(timeline.getByRole('status')).toContainText('Chart crosshair:');
  await expect(timeline.getByRole('status').locator('time')).toHaveAttribute('datetime', eventTime);
  await expect(planTimelineButtons(page)).toHaveCount(2);
  await expect(timeline.getByRole('button', { name: /^Align charts: Deadlock/ })).toHaveCount(1);
  await expect(page.locator('.server-dashboard-chart:has(.overview-empty) .chart-crosshair')).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'Wait categories over time', exact: true }).locator('.chart-crosshair')).toHaveCount(1);
});

test('timeline category keyboard control aligns shared charts and clears hidden selections', async ({ page }) => {
  await timelineFixture(page);
  await page.goto(href('health'));
  await expect(planTimelineButtons(page)).toHaveCount(2);
  const timeline = investigationTimeline(page);
  const category = timeline.getByRole('combobox', { name: 'Timeline event category', exact: true });
  await category.focus();
  await category.press('Home');
  for (let index = 0; index < 5; index++) await category.press('ArrowDown');
  await category.press('Enter');
  await expect(category).toHaveValue('plan_regression');
  await expect(timeline.getByRole('listitem')).toHaveCount(2);
  const comparison = planTimelineButtons(page).first();
  const eventTime = await timeline.getByRole('listitem').first().locator('time').getAttribute('datetime');
  await comparison.focus();
  await comparison.press('Enter');
  await expect(comparison).toHaveAttribute('aria-pressed', 'true');
  await expect(timeline.getByRole('status')).toContainText('Chart crosshair:');
  await expect(timeline.getByRole('status').locator('time')).toHaveAttribute('datetime', eventTime);
  await expect.poll(() => page.locator('.server-dashboard .chart-crosshair').count()).toBeGreaterThanOrEqual(4);
  await timeline.getByRole('button', { name: 'Clear timeline alignment', exact: true }).press('Enter');
  await expect(comparison).toHaveAttribute('aria-pressed', 'false');
  await expect(page.locator('.server-dashboard .chart-crosshair')).toHaveCount(0);
  await comparison.press('Enter');
  await category.focus();
  await category.press('Home');
  await category.press('ArrowDown');
  await category.press('Enter');
  await expect(category).toHaveValue('deadlock');
  await expect(timeline.getByRole('listitem')).toHaveCount(1);
  await expect(timeline.getByRole('button', { name: 'Clear timeline alignment', exact: true })).toHaveCount(0);
  await expect(page.locator('.server-dashboard .chart-crosshair')).toHaveCount(0);
  await timeline.getByText('Timeline coverage', { exact: true }).click();
  await expect(timeline).toContainText('Missing observations are not evidence of normal health.');
});

test('timeline database context filters plan comparisons while retaining server deadlocks and opens the current plan', async ({ page }) => {
  await timelineFixture(page);
  await page.goto(`${href('health')}&compare=1`);
  await expect(planTimelineButtons(page)).toHaveCount(2);
  await workspaceDatabase(page).selectOption('6');
  await assertDatabaseScope(page, 6);
  await expect(planTimelineButtons(page)).toHaveCount(1);
  await expect(planTimelineButtons(page)).toHaveAccessibleName(/database 6/);
  const timeline = investigationTimeline(page);
  await expect(timeline.getByRole('button', { name: /^Align charts: Deadlock/ })).toHaveCount(1);
  await expect(timeline.getByRole('button', { name: 'Align charts: Warehouse options detected', exact: true })).toHaveCount(1);
  await expect(timeline.getByRole('button', { name: 'Align charts: Orders options detected', exact: true })).toHaveCount(0);
  await expect(timeline.getByRole('button', { name: 'Align charts: Server configuration detected', exact: true })).toHaveCount(1);
  const evidence = timeline.getByRole('link', { name: /^Open evidence: Plan regression comparison/ });
  const params = new URLSearchParams((await evidence.getAttribute('href')).split('?')[1]);
  expect(Object.fromEntries(params)).toMatchObject({ target: first, databaseId: '6', range: 'custom', from, to, compare: '1', queryView: 'regressed', queryMetric: 'duration', source: 'query_store', queryFingerprint: 'd'.repeat(64), planFingerprint: 'e'.repeat(64) });
  await evidence.focus();
  await evidence.press('Enter');
  await assertHistoricalScope(page);
  await assertDatabaseScope(page, 6);
  const regressions = page.getByRole('region', { name: 'Possible plan regression comparisons', exact: true });
  await expect(regressions).toContainText('dddddddddddd');
  await expect(regressions).not.toContainText('bbbbbbbbbbbb');
  await expect(page.getByRole('region', { name: 'Possible plan regressions', exact: true })).toContainText('This is a correlation, not proof');
  await page.goBack();
  await assertDatabaseScope(page, 6);
  const configurationEvidence = investigationTimeline(page).getByRole('link', { name: 'Open evidence: Server configuration detected', exact: true });
  await expect(configurationEvidence).toBeVisible();
  const configurationParams = new URLSearchParams((await configurationEvidence.getAttribute('href')).split('?')[1]);
  expect(configurationParams.get('healthSection')).toBe('summary');
  expect(configurationParams.get('configurationRun')).toBe('22222222-2222-4222-8222-222222222222');
  await configurationEvidence.click();
  await expect(page.getByRole('tab', { name: 'Summary', exact: true })).toHaveAttribute('aria-selected', 'true');
  await expect.poll(() => scope(page).get('healthSection')).toBe('summary');
  await assertDatabaseScope(page, 6);
  const selectedConfigurationRun = page.locator('li[data-configuration-run="22222222-2222-4222-8222-222222222222"]');
  await expect(selectedConfigurationRun).toHaveCount(1);
  await expect(selectedConfigurationRun).toHaveAttribute('tabindex', '-1');
  await expect(selectedConfigurationRun).toBeFocused();
  await expect(selectedConfigurationRun).toContainText('cost threshold for parallelism');
  await page.reload();
  await expect(page.getByRole('tab', { name: 'Summary', exact: true })).toHaveAttribute('aria-selected', 'true');
  await assertDatabaseScope(page, 6);
  await expect(selectedConfigurationRun).toBeFocused();
  await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
  await page.getByLabel('From (UTC)', { exact: true }).fill('2026-09-26T09:00');
  await page.getByLabel('To (UTC)', { exact: true }).fill('2026-09-26T11:00');
  await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  await assertHistoricalScope(page, first, '2026-09-26T09:00:00.000Z', '2026-09-26T11:00:00.000Z');
  await assertDatabaseScope(page, 6);
  await expect.poll(() => scope(page).get('healthSection')).toBe('summary');
  await expect(page.getByRole('tab', { name: 'Summary', exact: true })).toHaveAttribute('aria-selected', 'true');
});

test('failed plan comparison refresh removes old leads and reports unavailable coverage honestly', async ({ page }) => {
  await timelineFixture(page);
  await page.goto(href('health'));
  await expect(planTimelineButtons(page)).toHaveCount(2);
  await page.route(`**/${first}/query-performance/regressions?*`, route => route.fulfill({ status: 503, json: {} }));
  await page.locator('.page-actions').getByRole('button', { name: /Refresh/ }).click();
  await expect(planTimelineButtons(page)).toHaveCount(0);
  const timeline = investigationTimeline(page);
  await timeline.getByText('Timeline coverage', { exact: true }).click();
  await expect(timeline).toContainText(/plan.*unavailable/i);
  await expect(timeline.getByRole('button', { name: /^Align charts: Deadlock/ })).toHaveCount(1);
  await page.unroute(`**/${first}/query-performance/regressions?*`);
  await page.locator('.page-actions').getByRole('button', { name: /Refresh/ }).click();
  await expect(planTimelineButtons(page)).toHaveCount(2);
});

test('delayed old-target plan comparisons never reappear in the new server timeline', async ({ page }) => {
  await timelineFixture(page);
  let release, requested = false, continued = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/${first}/query-performance/regressions?*`, async route => { requested = true; await gate; await route.continue().catch(() => {}); continued = true; });
  try {
    await page.goto(href('health'));
    await expect.poll(() => requested).toBe(true);
    await page.getByRole('combobox', { name: 'Target server', exact: true }).selectOption(second);
    await expect(planTimelineButtons(page)).toHaveCount(2);
    await expect(planTimelineButtons(page).first()).toHaveAccessibleName(/query 777777777777/);
    await expect(planTimelineButtons(page).last()).toHaveAccessibleName(/query 888888888888/);
  } finally { release(); }
  await expect.poll(() => continued).toBe(true);
  await expect(investigationTimeline(page).getByRole('button', { name: /Plan regression comparison.*query (bbbbbbbbbbbb|dddddddddddd)/ })).toHaveCount(0);
  await assertHistoricalScope(page, second);
});

test('timeline plan comparisons clear during a window read and reject its delayed result after another rewind', async ({ page }) => {
  await timelineFixture(page);
  await page.goto(href('health'));
  await expect(planTimelineButtons(page)).toHaveCount(2);
  let release, requested = false, continued = false;
  const gate = new Promise(resolve => { release = resolve; });
  await page.route(`**/${first}/query-performance/regressions?*`, async route => {
    const start = new URL(route.request().url()).searchParams.get('fromUtc');
    if (Date.parse(start) === Date.parse('2026-09-26T09:00:00Z')) { requested = true; await gate; await route.continue().catch(() => {}); continued = true; }
    else await route.continue();
  });
  const editWindow = async (start, end) => {
    await page.getByRole('button', { name: 'Edit UTC range', exact: true }).click();
    await page.getByLabel('From (UTC)', { exact: true }).fill(start);
    await page.getByLabel('To (UTC)', { exact: true }).fill(end);
    await page.getByRole('button', { name: 'Apply range', exact: true }).click();
  };
  try {
    await editWindow('2026-09-26T09:00', '2026-09-26T11:00');
    await expect.poll(() => requested).toBe(true);
    await expect(planTimelineButtons(page)).toHaveCount(0);
    await editWindow('2026-09-26T10:00', '2026-09-26T11:00');
    await expect(planTimelineButtons(page)).toHaveCount(2);
  } finally { release(); }
  await expect.poll(() => continued).toBe(true);
  for (const button of await planTimelineButtons(page).all()) {
    const row = button.locator('xpath=ancestor::li');
    const at = await row.locator('time').getAttribute('datetime');
    expect(Date.parse(at)).toBeGreaterThanOrEqual(Date.parse('2026-09-26T10:00:00Z'));
    expect(Date.parse(at)).toBeLessThan(Date.parse('2026-09-26T11:00:00Z'));
    const params = new URLSearchParams((await row.getByRole('link').getAttribute('href')).split('?')[1]);
    expect(Date.parse(params.get('from'))).toBe(Date.parse('2026-09-26T10:00:00Z'));
    expect(Date.parse(params.get('to'))).toBe(Date.parse('2026-09-26T11:00:00Z'));
  }
});
