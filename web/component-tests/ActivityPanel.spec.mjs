import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { activityFixture, alpha, beta, baselineRunId, blockingHistoryItem, cursor,
  fromUtc, toUtc, unavailableLivePage, wait, waitHistoryItem } from './activityFixture.mjs';

const sessions = new WeakMap();
const supporting = page => page.locator('details.supporting-evidence').filter({ has: page.locator('summary').filter({ hasText: 'Open supporting waits, blocking, and history evidence' }) });
const blockingHistory = page => page.getByRole('region', { name: 'Blocking history', exact: true });
const waitHistory = page => page.getByRole('region', { name: 'Server wait history', exact: true });
const json = (route, value) => route.fulfill({ status: 200, contentType: 'application/json',
  headers: { 'Cache-Control': 'no-store' }, body: JSON.stringify(value) });
const responsePage = (fixture, kind, url) => ({ ...fixture[kind],
  ...(kind.endsWith('/history') ? { fromUtc: url.searchParams.get('fromUtc'), toUtc: url.searchParams.get('toUtc') } : {}) });

test.beforeEach(async ({ page }) => {
  const session = { unexpected: [], requests: [], cancelled: [],
    fixtures: new Map([[alpha, activityFixture(alpha)], [beta, activityFixture(beta)]]), intercept: undefined };
  sessions.set(page, session);
  page.on('pageerror', error => session.unexpected.push(`Page error: ${error.message}`));
  page.on('console', message => {
    if (message.text().includes('Encountered two children with the same key')) session.unexpected.push(message.text());
  });
  page.on('requestfailed', request => session.cancelled.push(request.url()));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = () => {
      session.unexpected.push(`${request.method()} ${url.href}`); return route.abort();
    };
    if (url.origin !== 'http://127.0.0.1:4328') return reject();
    if (!/^\/api(?:\/|$)/.test(url.pathname)) return route.continue();
    session.requests.push(url);
    if (request.method() !== 'GET') return reject();
    const match = /^\/api\/v1\/observation-targets\/([^/]+)\/activity\/(sessions|requests|waits|blocking\/current|blocking\/history|waits\/history|live|live\/history)$/.exec(url.pathname);
    const fixture = match && session.fixtures.get(match[1]);
    if (!fixture) return reject();
    const [, id, kind] = match;
    // Only these registered native sources are allowed. No API route reaches Vite's proxy.
    if (kind === 'live/history') return json(route, []);
    if (kind === 'live') return json(route, unavailableLivePage());
    if (url.searchParams.get('limit') !== (kind === 'blocking/current' ? '256' : '25')) return reject();
    if (kind.endsWith('/history') && (!url.searchParams.has('fromUtc') || !url.searchParams.has('toUtc'))) return reject();
    if (session.intercept && await session.intercept(route, url, id, kind)) return;
    if (url.searchParams.has('cursor')) return reject();
    return json(route, responsePage(fixture, kind, url));
  });
  await page.addInitScript(() => {
    localStorage.setItem('sqlobserver.timeDisplayMode', 'utc');
    // Keep custom windows inside session retention without changing timer behavior.
    Date.now = () => Date.parse('2026-09-30T14:00:00.000Z');
  });
});

test.afterEach(async ({ page }) => {
  expect(sessions.get(page).unexpected, 'Unexpected API/external traffic was aborted before proxy/backend').toEqual([]);
});

async function open(page) {
  await page.goto('/component-tests/activity.html');
  await expect(page.getByRole('heading', { name: 'Activity evidence for Alpha' })).toBeVisible();
  await page.getByText('Open supporting waits, blocking, and history evidence', { exact: true }).click();
}

// A held real network response allows assertions during loading and after a
// context abort; delivery errors are accepted only after observed cancellation.
function holdResponse(page, predicate, value) {
  const session = sessions.get(page);
  let release, started, delivered, deliveryError;
  const held = new Promise(resolve => { release = resolve; });
  const seen = new Promise(resolve => { started = resolve; });
  const finished = new Promise(resolve => { delivered = resolve; });
  session.intercept = async (route, url, id, kind) => {
    if (!predicate(url, id, kind)) return false;
    started(); await held;
    try { await json(route, value); }
    catch (error) { deliveryError = error; }
    finally { delivered(); }
    return true;
  };
  return { seen, async finish(cancelled = false) {
    release(); await finished;
    if (deliveryError && !cancelled) throw deliveryError;
  } };
}

test('renders historical edge, per-row freshness and partial evidence as inert text with real time formatting', async ({ page }) => {
  const injected = '<img src="https://invalid.example/activity" onerror="window.activityInjected=true">';
  const fixture = sessions.get(page).fixtures.get(alpha);
  fixture['blocking/history'].items = [blockingHistoryItem({ waitType: injected })];
  fixture['blocking/history'].evidence = null; // Exercise row evidence fallback in the history header.
  await open(page);
  const history = blockingHistory(page);
  await expect(history).toContainText('engine.blocking: stale; partial — partial/loss evidence.');
  await expect(history.locator('tbody tr').first().locator('td')).toHaveText([
    /12:45:00 UTC/, '71', '63', injected, '2/9007199254740993', '2', 'root_missing (stale/partial)',
  ]);
  await expect(history.getByRole('columnheader', { name: 'Observed UTC', exact: true })).toBeVisible();
  await expect(history.locator('img, script')).toHaveCount(0);
  expect(await page.evaluate(() => window.activityInjected)).toBeUndefined();
  await page.getByRole('combobox', { name: 'Time display', exact: true }).selectOption('local');
  await expect(history.getByRole('columnheader', { name: 'Observed local', exact: true })).toBeVisible();
  await expect(history.locator('tbody tr td').first()).toContainText('08:45:00 EDT');
});

test('keeps reset and missing-baseline totals while excluding their deltas from the wait chart and disclosing loss', async ({ page }) => {
  const fixture = sessions.get(page).fixtures.get(alpha);
  const reset = { waitType: 'PAGEIOLATCH_SH', resetDetected: true, waitTimeMillisecondsDelta: null,
    waitingTasksDelta: null, signalWaitTimeMillisecondsDelta: null };
  const missing = { waitType: 'RESOURCE_SEMAPHORE', baselineAvailable: false, waitTimeMillisecondsDelta: null,
    waitingTasksDelta: null, signalWaitTimeMillisecondsDelta: null };
  fixture.waits.items = [wait(), wait(reset), wait(missing), wait({ waitType: 'SLEEP_TASK' })];
  fixture['waits/history'].items = [waitHistoryItem(), waitHistoryItem(reset), waitHistoryItem(missing)];
  await open(page);
  const chart = page.getByRole('region', { name: 'Wait categories', exact: true });
  await expect(chart).toContainText('Lock · 1 type');
  await expect(chart).toContainText('75 ms');
  await expect(chart.locator('meter')).toHaveCount(1);
  await expect(chart).toContainText('1 idle type omitted; 2 types without a comparable baseline or after a reset.');
  const raw = page.getByRole('region', { name: 'Server waits observations', exact: true });
  await expect(raw.getByRole('row').filter({ hasText: 'PAGEIOLATCH_SH' })).toContainText('reset');
  await expect(raw.getByRole('row').filter({ hasText: 'SLEEP_TASK' })).toBeVisible();
  const history = waitHistory(page);
  await expect(history.getByRole('row').filter({ hasText: 'PAGEIOLATCH_SH' }).locator('td')).toHaveText([
    /12:45:00 UTC/, 'PAGEIOLATCH_SH', '9007199254740993', '9007199254740995', '—', '—', 'reset · stale/partial · partial',
  ]);
  await expect(history.getByRole('row').filter({ hasText: 'RESOURCE_SEMAPHORE' })).toContainText('no baseline · stale/partial · partial');
  await expect(supporting(page)).toContainText('source_row_limit: at least 3 item(s), 12 byte(s) (minimum)');
  await expect(supporting(page)).toContainText(`Baseline run: ${baselineRunId}`);
});

test('pages blocking and wait histories within the selected native window and restores their first pages', async ({ page }) => {
  const session = sessions.get(page), fixture = session.fixtures.get(alpha);
  fixture['blocking/history'].nextCursor = cursor; fixture['waits/history'].nextCursor = cursor;
  session.intercept = async (route, url, _id, kind) => {
    if (!url.searchParams.has('cursor')) return false;
    expect(url.searchParams.get('cursor')).toBe(cursor);
    expect(url.searchParams.get('fromUtc')).toBe(fromUtc); expect(url.searchParams.get('toUtc')).toBe(toUtc);
    const items = kind === 'blocking/history' ? [blockingHistoryItem({ blockedSessionId: 82 })]
      : [waitHistoryItem({ waitType: 'WRITELOG' })];
    await json(route, { ...responsePage(fixture, kind, url), items, nextCursor: null }); return true;
  };
  await open(page);
  for (const [region, older, newer, first, marker] of [
    [blockingHistory(page), 'Older history', 'Newer history', 'First history page', '82'],
    [waitHistory(page), 'Older waits', 'Newer waits', 'First wait history page', 'WRITELOG'],
  ]) {
    await expect(region.getByRole('button', { name: first, exact: true })).toBeDisabled();
    await region.getByRole('button', { name: older, exact: true }).click();
    await expect(region.getByRole('status')).toHaveText('Page 2 · 1 observations · End of this window');
    await expect(region.getByRole('cell', { name: marker, exact: true })).toBeVisible();
    await expect(region.getByRole('button', { name: older, exact: true })).toBeDisabled();
    await region.getByRole('button', { name: newer, exact: true }).click();
    await expect(region.getByRole('status')).toHaveText('Page 1 · 1 observations');
    await expect(region.getByRole('cell', { name: marker, exact: true })).toHaveCount(0);
  }
  expect(session.requests.filter(url => url.searchParams.has('cursor')).map(url => url.search)).toEqual([
    `?limit=25&fromUtc=${encodeURIComponent(fromUtc)}&toUtc=${encodeURIComponent(toUtc)}&cursor=history%2B%2F%3Dnext`,
    `?limit=25&fromUtc=${encodeURIComponent(fromUtc)}&toUtc=${encodeURIComponent(toUtc)}&cursor=history%2B%2F%3Dnext`,
  ]);
});

test('shows bounded loading, isolates a sanitized failed section, and recovers through refresh', async ({ page }) => {
  const session = sessions.get(page);
  let release, started;
  const held = new Promise(resolve => { release = resolve; });
  const seen = new Promise(resolve => { started = resolve; });
  let fail = true;
  session.intercept = async (route, _url, _id, kind) => {
    if (kind !== 'waits' || !fail) return false;
    started(); await held;
    await route.fulfill({ status: 503, contentType: 'text/html', body: '<script>window.activityInjected=true</script> password=synthetic-secret' });
    return true;
  };
  await open(page); await seen;
  await expect(supporting(page).getByText('Loading bounded activity evidence…', { exact: true })).toBeVisible();
  release();
  await expect(supporting(page).getByRole('status').filter({ hasText: 'Waits:' })).toHaveText(
    'Waits: The activity request failed safely. Refresh to retry this section.');
  await expect(blockingHistory(page).getByRole('cell', { name: '71', exact: true })).toBeVisible();
  await expect(supporting(page).getByRole('heading', { name: 'Server waits', exact: true })).toHaveCount(0);
  await expect(page.locator('body')).not.toContainText('synthetic-secret');
  expect(await page.evaluate(() => window.activityInjected)).toBeUndefined();
  fail = false;
  await page.getByRole('button', { name: 'Refresh activity evidence', exact: true }).click();
  await expect(supporting(page).getByRole('heading', { name: 'Server waits', exact: true })).toBeVisible();
  await expect(supporting(page).getByRole('status').filter({ hasText: 'Waits:' })).toHaveCount(0);
});

test('retains each history page on sanitized paging failure and allows retry', async ({ page }) => {
  const session = sessions.get(page), fixture = session.fixtures.get(alpha);
  fixture['blocking/history'].nextCursor = cursor; fixture['waits/history'].nextCursor = cursor;
  let fail = true;
  session.intercept = async (route, url, _id, kind) => {
    if (!url.searchParams.has('cursor')) return false;
    if (fail) await route.fulfill({ status: 504, contentType: 'text/plain', body: 'provider password=synthetic-secret' });
    else await json(route, { ...responsePage(fixture, kind, url), nextCursor: null,
      items: kind === 'blocking/history' ? [blockingHistoryItem({ blockedSessionId: 82 })] : [waitHistoryItem({ waitType: 'WRITELOG' })] });
    return true;
  };
  await open(page);
  for (const [region, button, firstMarker, recoveredMarker] of [
    [blockingHistory(page), 'Older history', '71', '82'], [waitHistory(page), 'Older waits', 'LCK_M_X', 'WRITELOG'],
  ]) {
    fail = true;
    await region.getByRole('button', { name: button, exact: true }).click();
    await expect(region.getByRole('alert')).toHaveText('The activity request exceeded its execution limit. The displayed page is unchanged. Retry using the navigation buttons.');
    await expect(region.getByRole('cell', { name: firstMarker, exact: true })).toBeVisible();
    await expect(region.getByRole('status')).toHaveText('Page 1 · 1 observations');
    await expect(page.locator('body')).not.toContainText('synthetic-secret');
    fail = false;
    await region.getByRole('button', { name: button, exact: true }).click();
    await expect(region.getByRole('cell', { name: recoveredMarker, exact: true })).toBeVisible();
    await expect(region.getByRole('alert')).toHaveCount(0);
  }
});

test('aborts the previous target snapshot and prevents late evidence from replacing the new target', async ({ page }) => {
  const session = sessions.get(page);
  session.fixtures.get(beta)['blocking/history'].items = [blockingHistoryItem({ blockedSessionId: 87 })];
  const held = holdResponse(page, (_url, id, kind) => id === alpha && kind === 'blocking/history',
    session.fixtures.get(alpha)['blocking/history']);
  await open(page); await held.seen;
  await expect(supporting(page).getByText('Loading bounded activity evidence…', { exact: true })).toBeVisible();
  await page.getByRole('combobox', { name: 'Selected server', exact: true }).selectOption('beta');
  await expect(page.getByRole('heading', { name: 'Activity evidence for Beta' })).toBeVisible();
  await expect(blockingHistory(page).getByRole('cell', { name: '87', exact: true })).toBeVisible();
  await expect.poll(() => session.cancelled.filter(url => url.includes(`/${alpha}/activity/blocking/history`)).length).toBe(1);
  await held.finish(true);
  await expect(blockingHistory(page).getByRole('cell', { name: '87', exact: true })).toBeVisible();
  await expect(blockingHistory(page).getByRole('cell', { name: '71', exact: true })).toHaveCount(0);
  await expect(supporting(page).getByRole('alert')).toHaveCount(0);
});

test('aborts an old history navigation when the window changes and resets paging to the new window', async ({ page }) => {
  const session = sessions.get(page), fixture = session.fixtures.get(alpha);
  fixture['blocking/history'].nextCursor = cursor;
  await open(page);
  await expect(blockingHistory(page).getByRole('cell', { name: '71', exact: true })).toBeVisible();
  const held = holdResponse(page, (url, _id, kind) => kind === 'blocking/history' && url.searchParams.has('cursor'),
    { ...fixture['blocking/history'], items: [blockingHistoryItem({ blockedSessionId: 82 })], nextCursor: null });
  await blockingHistory(page).getByRole('button', { name: 'Older history', exact: true }).click(); await held.seen;
  await expect(blockingHistory(page)).toHaveAttribute('aria-busy', 'true');
  await expect(blockingHistory(page).getByRole('button', { name: 'Older history', exact: true })).toBeDisabled();
  fixture['blocking/history'].items = [blockingHistoryItem({ blockedSessionId: 94, observedAtUtc: '2026-09-30T11:45:00.000Z' })];
  fixture['blocking/history'].nextCursor = null;
  await page.getByRole('combobox', { name: 'History window', exact: true }).selectOption('earlier');
  await expect(blockingHistory(page).getByRole('cell', { name: '94', exact: true })).toBeVisible();
  await expect(blockingHistory(page).getByRole('status')).toHaveText('Page 1 · 1 observations · End of this window');
  await expect.poll(() => session.cancelled.filter(url => url.includes('cursor=')).length).toBe(1);
  await held.finish(true);
  await expect(blockingHistory(page).getByRole('cell', { name: '82', exact: true })).toHaveCount(0);
  await expect(blockingHistory(page).getByRole('cell', { name: '94', exact: true })).toBeVisible();
  expect(session.requests.filter(url => url.pathname.endsWith('/blocking/history') && !url.searchParams.has('cursor')).at(-1).searchParams.get('fromUtc')).toBe('2026-09-30T11:00:00.000Z');
});

test('declines an overlong history window while keeping current bounded supporting evidence available', async ({ page }) => {
  const session = sessions.get(page);
  await open(page);
  await expect(blockingHistory(page).getByRole('cell', { name: '71', exact: true })).toBeVisible();
  const historicalRequests = () => session.requests.filter(url => /\/(?:blocking|waits)\/history$/.test(url.pathname)).length;
  expect(historicalRequests()).toBe(2);
  await page.getByRole('combobox', { name: 'History window', exact: true }).selectOption('long');
  await expect(supporting(page)).toContainText('Activity history supports selected windows up to 24 hours. Choose a shorter range.');
  await expect(blockingHistory(page)).toHaveCount(0);
  await expect(waitHistory(page)).toHaveCount(0);
  await expect(supporting(page).getByRole('heading', { name: 'Server waits', exact: true })).toBeVisible();
  expect(historicalRequests()).toBe(2);
});

const histories = [
  { kind: 'blocking/history', region: blockingHistory, older: 'Older history', first: 'First history page',
    latest: 'Return to latest blocking history', original: '71', next: '82', newest: '94',
    items: value => [blockingHistoryItem({ blockedSessionId: Number(value) })] },
  { kind: 'waits/history', region: waitHistory, older: 'Older waits', first: 'First wait history page',
    latest: 'Return to latest wait history', original: 'LCK_M_X', next: 'WRITELOG', newest: 'PAGEIOLATCH_SH',
    items: value => [waitHistoryItem({ waitType: value })] },
];
async function refreshHistory(page, kind) {
  const session = sessions.get(page), before = session.requests.filter(url => url.pathname.endsWith(kind) && !url.searchParams.has('cursor')).length;
  const response = page.waitForResponse(value => value.url().includes(`/activity/${kind}?`) && !new URL(value.url()).searchParams.has('cursor'));
  await page.getByRole('button', { name: 'Refresh activity evidence', exact: true }).click();
  await response;
  await expect.poll(() => session.requests.filter(url => url.pathname.endsWith(kind) && !url.searchParams.has('cursor')).length).toBe(before + 1);
}
for (const history of histories) {
  test(`${history.kind} retains loaded pages and original cursor across refresh until explicit latest`, async ({ page }) => {
    const session = sessions.get(page), fixture = session.fixtures.get(alpha), original = fixture[history.kind];
    original.nextCursor = cursor;
    session.intercept = async (route, url, _id, kind) => {
      if (!url.searchParams.has('cursor')) return false;
      expect(kind).toBe(history.kind); expect(url.searchParams.get('cursor')).toBe(cursor);
      expect(url.searchParams.get('fromUtc')).toBe(fromUtc); expect(url.searchParams.get('toUtc')).toBe(toUtc);
      await json(route, { ...responsePage(fixture, kind, url), items: history.items(history.next), nextCursor: null }); return true;
    };
    await open(page); const region = history.region(page);
    await region.getByRole('button', { name: history.older, exact: true }).click();
    await expect(region.getByRole('cell', { name: history.next, exact: true })).toBeVisible();
    fixture[history.kind] = { ...original, repositoryTimeUtc: '2026-09-30T13:05:00.000Z', items: history.items(history.newest), nextCursor: null };
    await refreshHistory(page, history.kind);
    await expect(region.getByRole('cell', { name: history.next, exact: true })).toBeVisible();
    await expect(region.getByRole('status').filter({ hasText: 'Page 2' })).toBeVisible();
    await region.getByRole('button', { name: history.first, exact: true }).click();
    // Window paging is mutable: First re-reads the retained window, rather than
    // claiming an immutable cached history snapshot.
    await expect(region.getByRole('cell', { name: history.newest, exact: true })).toBeVisible();
    expect(session.requests.filter(url => url.pathname.endsWith(history.kind) && url.searchParams.has('cursor'))).toHaveLength(1);
    await expect.poll(() => session.requests.filter(url => url.pathname.endsWith(history.kind) && !url.searchParams.has('cursor')).length).toBe(3);
    await region.getByRole('button', { name: history.latest, exact: true }).click();
    await expect(region.getByRole('cell', { name: history.newest, exact: true })).toBeVisible();
    await expect(region.getByRole('status').filter({ hasText: 'Page 1' })).toBeVisible();
    await expect(region.getByRole('cell', { name: history.original, exact: true })).toHaveCount(0);
    await expect(region.getByRole('button', { name: history.older, exact: true })).toBeDisabled();
    await expect.poll(() => session.requests.filter(url => url.pathname.endsWith(history.kind) && !url.searchParams.has('cursor')).length).toBe(4);
  });

  test(`${history.kind} refresh preserves held navigation while latest cancels and fences late history`, async ({ page }) => {
    const session = sessions.get(page), fixture = session.fixtures.get(alpha);
    fixture[history.kind].nextCursor = cursor;
    await open(page); const region = history.region(page);
    const held = holdResponse(page, (url, _id, kind) => kind === history.kind && url.searchParams.has('cursor'),
      { ...fixture[history.kind], items: history.items(history.next), nextCursor: null });
    await region.getByRole('button', { name: history.older, exact: true }).click(); await held.seen;
    fixture[history.kind] = { ...fixture[history.kind], repositoryTimeUtc: '2026-09-30T13:05:00.000Z', items: history.items(history.newest), nextCursor: null };
    await refreshHistory(page, history.kind);
    await expect(region).toHaveAttribute('aria-busy', 'true');
    expect(session.cancelled.filter(url => url.includes('cursor='))).toEqual([]);
    await expect(region.getByRole('cell', { name: history.original, exact: true })).toBeVisible();
    await region.getByRole('button', { name: history.latest, exact: true }).click();
    await expect.poll(() => session.cancelled.filter(url => url.includes('cursor=')).length).toBe(1);
    await expect(region.getByRole('cell', { name: history.newest, exact: true })).toBeVisible();
    await held.finish(true);
    await expect(region).toHaveAttribute('aria-busy', 'false');
    await expect(region.getByRole('cell', { name: history.next, exact: true })).toHaveCount(0);
    await expect(region.getByRole('cell', { name: history.newest, exact: true })).toBeVisible();
    await expect(region.getByRole('alert')).toHaveCount(0);
  });
}

test('a delayed supporting aggregate cannot replace explicit latest history, while a subsequent poll can advance it', async ({ page }) => {
  const session = sessions.get(page), fixture = session.fixtures.get(alpha);
  fixture['blocking/history'].nextCursor = cursor;
  session.intercept = async (route, url, _id, kind) => {
    if (!url.searchParams.has('cursor')) return false;
    expect(kind).toBe('blocking/history'); expect(url.searchParams.get('cursor')).toBe(cursor);
    expect(url.searchParams.get('fromUtc')).toBe(fromUtc); expect(url.searchParams.get('toUtc')).toBe(toUtc);
    await json(route, { ...responsePage(fixture, kind, url), items: [blockingHistoryItem({ blockedSessionId: 82 })], nextCursor: null }); return true;
  };
  await open(page); const history = blockingHistory(page);
  await history.getByRole('button', { name: 'Older history', exact: true }).click();
  await expect(history.getByRole('cell', { name: '82', exact: true })).toBeVisible();
  fixture['blocking/history'] = { ...fixture['blocking/history'], repositoryTimeUtc: '2026-09-30T13:05:00.000Z',
    items: [blockingHistoryItem({ blockedSessionId: 94 })], nextCursor: null };
  // The history subread finishes, but getActivitySnapshot cannot publish it
  // until its separately held native Requests response finishes.
  const aggregate = holdResponse(page, (_url, _id, kind) => kind === 'requests', fixture.requests);
  await refreshHistory(page, 'blocking/history'); await aggregate.seen;
  await expect(supporting(page).getByRole('status').filter({ hasText: 'Refreshing bounded activity evidence; previously received snapshots remain displayed…' })).toBeVisible();
  await expect(history.getByRole('cell', { name: '82', exact: true })).toBeVisible();
  fixture['blocking/history'] = { ...fixture['blocking/history'], repositoryTimeUtc: '2026-09-30T13:10:00.000Z',
    items: [blockingHistoryItem({ blockedSessionId: 96 })] };
  await history.getByRole('button', { name: 'Return to latest blocking history', exact: true }).click();
  await expect(history.getByRole('cell', { name: '96', exact: true })).toBeVisible();
  await expect(history.getByRole('button', { name: 'Return to latest blocking history', exact: true })).toHaveCount(0);
  await aggregate.finish();
  await expect(supporting(page).getByRole('status').filter({ hasText: 'Refreshing bounded activity evidence; previously received snapshots remain displayed…' })).toHaveCount(0);
  await expect(supporting(page).getByRole('heading', { name: 'Server waits', exact: true })).toBeVisible();
  await expect(history.getByRole('cell', { name: '96', exact: true })).toBeVisible();
  await expect(history.getByRole('cell', { name: '94', exact: true })).toHaveCount(0);
  expect(session.cancelled.filter(url => new URL(url).pathname.endsWith('/activity/requests'))).toEqual([]);
  expect(session.requests.filter(url => url.pathname.endsWith('/blocking/history') && !url.searchParams.has('cursor'))).toHaveLength(3);

  session.intercept = undefined;
  fixture['blocking/history'] = { ...fixture['blocking/history'], repositoryTimeUtc: '2026-09-30T13:15:00.000Z',
    items: [blockingHistoryItem({ blockedSessionId: 97 })] };
  await refreshHistory(page, 'blocking/history');
  await expect(history.getByRole('cell', { name: '97', exact: true })).toBeVisible();
  await expect(history.getByRole('cell', { name: '96', exact: true })).toHaveCount(0);
  await expect(history.getByRole('status').filter({ hasText: 'Page 1' })).toBeVisible();
});

for (const width of [320, 768, 1024, 1440]) test(`retained activity histories expose keyboard latest recovery and dark layout at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 });
  const session = sessions.get(page), fixture = session.fixtures.get(alpha);
  for (const history of histories) fixture[history.kind].nextCursor = cursor;
  session.intercept = async (route, url, _id, kind) => {
    if (!url.searchParams.has('cursor')) return false;
    const history = histories.find(item => item.kind === kind);
    if (!history || url.searchParams.get('cursor') !== cursor) throw new Error('Unexpected history continuation');
    await json(route, { ...responsePage(fixture, kind, url), items: history.items(history.next), nextCursor: null }); return true;
  };
  await open(page);
  for (const history of histories) {
    const region = history.region(page);
    await region.getByRole('button', { name: history.older, exact: true }).click();
    await expect(region.getByRole('cell', { name: history.next, exact: true })).toBeVisible();
    const latest = region.getByRole('button', { name: history.latest, exact: true });
    await region.getByRole('button', { name: history.first, exact: true }).focus();
    await page.keyboard.press('Shift+Tab');
    await expect(latest).toBeFocused();
    expect(await latest.evaluate(element => element.matches(':focus-visible'))).toBe(true);
    const box = await latest.boundingBox();
    expect(box.x).toBeGreaterThanOrEqual(0); expect(box.x + box.width).toBeLessThanOrEqual(width + 1);
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`activity-retained-history-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
  expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-components/screenshots/', import.meta.url));
  await mkdir(directory, { recursive: true });
  await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo({ top: 0, behavior: 'instant' }); });
  await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
  const path = `${directory}/activity-retained-history-${width}.png`;
  await page.screenshot({ path, fullPage: true });
  await info.attach(`activity-retained-history-${width}`, { path, contentType: 'image/png' });
  for (const history of histories) {
    const region = history.region(page);
    await region.getByRole('button', { name: history.latest, exact: true }).focus();
    await page.keyboard.press('Enter');
    await expect(region.getByRole('cell', { name: history.original, exact: true })).toBeVisible();
    await expect(region.getByRole('button', { name: history.latest, exact: true })).toHaveCount(0);
  }
});

test('keeps activity history accessible and locally scrollable at 320 and 1440 pixels', async ({ page }, info) => {
  await open(page);
  await expect(blockingHistory(page).getByRole('cell', { name: '71', exact: true })).toBeVisible();
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-components/screenshots/', import.meta.url));
  await mkdir(directory, { recursive: true });
  for (const width of [320, 1440]) {
    await page.setViewportSize({ width, height: 1000 });
    await waitHistory(page).getByRole('region', { name: 'Server wait history observations', exact: true }).focus();
    await expect(waitHistory(page).getByRole('region', { name: 'Server wait history observations', exact: true })).toBeFocused();
    const overflow = await page.evaluate(() => ({
      width: innerWidth, scrollWidth: document.documentElement.scrollWidth,
      elements: [...document.querySelectorAll('body *')].filter(element => element.getBoundingClientRect().right > innerWidth)
        .map(element => ({ tag: element.tagName, className: element.className, width: element.getBoundingClientRect().width })).slice(0, 15),
    }));
    expect(overflow.scrollWidth <= overflow.width, JSON.stringify(overflow)).toBe(true);
    const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
    await info.attach(`activity-panel-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
    expect(violations).toEqual([]);
    const path = `${directory}activity-panel-${width}.png`;
    await page.screenshot({ path, fullPage: true });
    await info.attach(`activity-panel-${width}`, { path, contentType: 'image/png' });
  }
});
