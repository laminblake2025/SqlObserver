import { test, expect } from '@playwright/test';

const first = '11111111-1111-4111-8111-000000000001';
const second = '11111111-1111-4111-8111-000000000002';
const from = '2026-09-26T08:00:00.000Z';
const to = '2026-09-26T12:00:00.000Z';
const origin = 'http://127.0.0.1:4322';
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
