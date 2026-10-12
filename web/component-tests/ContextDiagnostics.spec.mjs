import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const origin = 'http://127.0.0.1:4328';
const alpha = '11111111-1111-4111-8111-000000000001';
const beta = '11111111-1111-4111-8111-000000000002';
const categories = ['Lock', 'I/O', 'CPU/signal', 'Memory', 'Parallelism', 'Log', 'Other'];
const longDatabaseName = 'SYNTHETIC_' + 'D'.repeat(118);
const contexts = new WeakMap();
const waits = page => page.getByRole('region', { name: 'Wait categories over time', exact: true });
const options = page => page.getByRole('region', { name: 'Database option changes', exact: true });
const chart = page => waits(page).locator('svg.overview-chart');
const refresh = page => page.getByRole('button', { name: 'Refresh diagnostics', exact: true });
const target = page => page.getByRole('combobox', { name: 'Selected server', exact: true });
const windowPicker = page => page.getByRole('combobox', { name: 'Selected diagnostic window', exact: true });
const firstCommit = async page => JSON.parse(await page.getByRole('status', { name: 'First committed diagnostic context', exact: true }).textContent());
async function json(route, value, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
}
function nativeWait(item, value = item.target === alpha ? 111 : 222) {
  const points = [];
  const clock = value => {
    const parts = /^(.*T\d{2}:\d{2}:\d{2})(?:\.(\d{1,6}))?Z$/.exec(value);
    return BigInt(Date.parse(`${parts[1]}Z`)) * 1000n + BigInt((parts[2] ?? '').padEnd(6, '0'));
  };
  const first = clock(item.fromUtc) / 300_000_000n * 300_000_000n;
  const last = (clock(item.toUtc) - 1n) / 300_000_000n * 300_000_000n;
  for (let bucket = first; bucket <= last; bucket += 300_000_000n) for (const category of categories)
    points.push({ bucketStartUtc: new Date(Number(bucket / 1000n)).toISOString(), category, waitMilliseconds: `${category === 'Lock' ? value : 0}`,
      runCount: 2, missingSummaryRuns: 0, partialRuns: 0, incomparableTypes: 0, comparableTypes: category === 'Lock' ? 1 : 0 });
  return { instanceId: item.target, fromUtc: item.fromUtc, toUtc: item.toUtc,
    repositoryTimeUtc: '2026-01-15T13:45:00.123456Z', points };
}
function nativeOptions(item, longNames = false) {
  const observed = Date.parse(item.fromUtc) + Math.min(600000, (Date.parse(item.toUtc) - Date.parse(item.fromUtc)) / 2);
  return { targetId: item.target, fromUtc: item.fromUtc, toUtc: item.toUtc,
    semantics: 'detected_between_consecutive_complete_inventory_runs', items: [7, 8].map(databaseId => ({
      databaseId, databaseName: longNames && databaseId === 7 ? longDatabaseName
        : `SYNTHETIC ${item.target === alpha ? 'Alpha' : 'Beta'} database ${databaseId}`,
      optionKey: 'recovery_model', previousValue: 'simple', currentValue: 'full',
      previousObservedAtUtc: new Date(Date.parse(item.fromUtc) - 60000).toISOString(),
      observedAtUtc: new Date(observed).toISOString(), detectedAtUtc: new Date(observed).toISOString(),
      collectionRunId: '33333333-3333-4333-8333-000000000001' })) };
}
function hold(status = 200, transform = value => value) {
  let announce, release, complete;
  const started = new Promise(resolve => { announce = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  const finished = new Promise(resolve => { complete = resolve; });
  return { started, async finish() { release(); await finished; }, async handle(route, item) {
    announce(route.request()); await gate;
    try { await json(route, status === 200 ? transform(item.kind === 'waits' ? nativeWait(item) : nativeOptions(item))
      : { detail: 'SECRET_LATE_PROVIDER <script>window.contextInjected=true</script>' }, status); }
    catch (failure) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw failure; }
    finally { complete(); }
    return true;
  } };
}

test.beforeEach(async ({ page }) => {
  const context = { reads: [], unexpected: [], handler: undefined, longNames: false }; contexts.set(page, context);
  page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async () => { context.unexpected.push(`${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/.test(url.pathname)) return route.continue();
    const match = /^\/api\/v1\/observation-targets\/([^/]+)\/(activity\/waits\/trend|health\/database-option-changes)$/.exec(url.pathname);
    if (!match || ![alpha, beta].includes(match[1]) || request.method() !== 'GET') return reject();
    const kind = match[2].startsWith('activity') ? 'waits' : 'options';
    const keys = kind === 'waits' ? ['fromUtc', 'toUtc'] : ['fromUtc', 'limit', 'toUtc'];
    if ([...url.searchParams.keys()].sort().join('|') !== keys.sort().join('|') ||
        kind === 'options' && url.searchParams.get('limit') !== '100') return reject();
    const item = { kind, target: match[1], fromUtc: url.searchParams.get('fromUtc'), toUtc: url.searchParams.get('toUtc') };
    if (!Number.isFinite(Date.parse(item.fromUtc)) || Date.parse(item.fromUtc) >= Date.parse(item.toUtc)) return reject();
    context.reads.push(item);
    if (context.handler && await context.handler(route, item)) return;
    await json(route, kind === 'waits' ? nativeWait(item) : nativeOptions(item, context.longNames));
  });
});
test.afterEach(async ({ page }) => {
  expect(contexts.get(page).unexpected, 'Only registered local native APIs; external/unexpected traffic and runtime errors are refused').toEqual([]);
});
async function mount(page) {
  await page.goto('/component-tests/context-diagnostics.html');
  await expect(chart(page)).toBeVisible();
  await expect(options(page)).toContainText(contexts.get(page).longNames ? longDatabaseName : 'SYNTHETIC Alpha database 7');
}
async function holdRefresh(page, status = 200) {
  const waiting = hold(status), optionRead = hold(status);
  contexts.get(page).handler = (route, item) => (item.kind === 'waits' ? waiting : optionRead).handle(route, item);
  await refresh(page).click();
  return { waiting, optionRead, requests: await Promise.all([waiting.started, optionRead.started]) };
}
async function finishBoth(pending) { await Promise.all([pending.waiting.finish(), pending.optionRead.finish()]); }
async function abandonBoth(page, pending, change) {
  const aborted = pending.requests.map(request => page.waitForEvent('requestfailed', { predicate: value => value === request }));
  contexts.get(page).handler = undefined; await change(); await Promise.all(aborted); await finishBoth(pending);
}

test('a held same-window poll retains the mounted wait chart and discloses its snapshot', async ({ page }) => {
  await mount(page); const original = await chart(page).elementHandle();
  const pending = await holdRefresh(page);
  await expect(waits(page).getByRole('status')).toContainText('Refreshing wait categories');
  await expect(waits(page)).toContainText('previous snapshot in this same window');
  expect(await original.evaluate(element => element.isConnected)).toBe(true);
  expect(await chart(page).evaluate((element, previous) => element === previous, original)).toBe(true);
  await expect(options(page).getByRole('status')).toHaveText('Loading option history…');
  await finishBoth(pending); await expect(waits(page).getByRole('status')).toHaveCount(0);
  await expect(chart(page)).toBeVisible(); await expect(options(page)).toContainText('SYNTHETIC Alpha database 7');
  expect(contexts.get(page).reads.filter(item => item.kind === 'options')).toHaveLength(2);
});

test('transient refresh failures disclose retained wait evidence and custom-window Refresh retries both APIs', async ({ page }) => {
  await mount(page); const pending = await holdRefresh(page, 503); await finishBoth(pending);
  await expect(waits(page).getByRole('alert')).toContainText('Showing previously loaded wait categories in this same window');
  await expect(chart(page)).toBeVisible(); await expect(options(page).getByRole('alert')).toHaveText('Option history is unavailable. Try again.');
  await expect(options(page)).not.toContainText('SYNTHETIC Alpha database 7');
  contexts.get(page).handler = undefined; await refresh(page).focus(); await page.keyboard.press('Enter');
  await expect(waits(page).getByRole('alert')).toHaveCount(0); await expect(options(page).getByRole('alert')).toHaveCount(0);
  await expect(options(page)).toContainText('SYNTHETIC Alpha database 7');
  expect(contexts.get(page).reads.filter(item => item.kind === 'waits')).toHaveLength(3);
  expect(contexts.get(page).reads.filter(item => item.kind === 'options')).toHaveLength(3);
  await expect(page.locator('body')).not.toContainText('SECRET_LATE_PROVIDER');
});

test('a newer refresh in the same window owns the results after an older poll is cancelled', async ({ page }) => {
  await mount(page); const pending = await holdRefresh(page);
  const aborted = pending.requests.map(request => page.waitForEvent('requestfailed', { predicate: value => value === request }));
  contexts.get(page).handler = async (route, item) => {
    const body = item.kind === 'waits' ? nativeWait(item, 333) : nativeOptions(item);
    if (item.kind === 'options') body.items.forEach(row => { row.currentValue = 'bulk_logged'; });
    await json(route, body); return true;
  };
  await refresh(page).click(); await Promise.all(aborted);
  await expect(options(page)).toContainText('bulk_logged'); await expect(waits(page)).toContainText('333');
  await finishBoth(pending); await expect(options(page)).toContainText('bulk_logged'); await expect(waits(page)).toContainText('333');
  await expect(waits(page).getByRole('alert')).toHaveCount(0); await expect(options(page).getByRole('alert')).toHaveCount(0);
});

for (const change of ['target', 'window']) test(`changed ${change} hides evidence on the first commit and refuses old receipts`, async ({ page }) => {
  await mount(page); const pending = await holdRefresh(page);
  await abandonBoth(page, pending, () => change === 'target' ? target(page).selectOption(beta) : windowPicker(page).selectOption('second'));
  const commit = await firstCommit(page);
  expect(commit.waitChart).toBe(false); expect(commit.optionRows).toBe(0); expect(commit.waitError).toBe(false); expect(commit.optionError).toBe(false);
  expect(commit.target).toBe(change === 'target' ? beta : alpha);
  if (change === 'window') expect(commit.fromUtc).toBe('2026-01-15T12:30:00.123456Z');
  await expect(chart(page)).toBeVisible(); await expect(options(page)).toContainText(`SYNTHETIC ${change === 'target' ? 'Beta' : 'Alpha'} database 7`);
  await expect(waits(page).getByRole('alert')).toHaveCount(0); await expect(options(page).getByRole('alert')).toHaveCount(0);
  if (change === 'target') await expect(options(page)).not.toContainText('SYNTHETIC Alpha');
  else await expect(waits(page).locator('time').first()).toHaveAttribute('datetime', '2026-01-15T12:30:00.123456Z');
});

test('old context errors are hidden before effects and late failures cannot replace a newer success', async ({ page }) => {
  const context = contexts.get(page); context.handler = async route => { await json(route, { detail: 'SECRET_PROVIDER' }, 503); return true; };
  await page.goto('/component-tests/context-diagnostics.html');
  await expect(waits(page).getByRole('alert')).toBeVisible(); await expect(options(page).getByRole('alert')).toBeVisible();
  context.handler = undefined; await target(page).selectOption(beta);
  const changedErrorContext = await firstCommit(page);
  expect(changedErrorContext.waitError).toBe(false); expect(changedErrorContext.optionError).toBe(false);
  await expect(chart(page)).toBeVisible(); await expect(options(page)).toContainText('SYNTHETIC Beta database 7');
  const pending = await holdRefresh(page, 503);
  await abandonBoth(page, pending, () => target(page).selectOption(alpha));
  const commit = await firstCommit(page); expect(commit.waitError).toBe(false); expect(commit.optionError).toBe(false);
  await expect(chart(page)).toBeVisible(); await expect(options(page)).toContainText('SYNTHETIC Alpha database 7');
  await expect(waits(page).getByRole('alert')).toHaveCount(0); await expect(options(page).getByRole('alert')).toHaveCount(0);
});

test('microsecond-mismatched returned bounds are refused, and an explicit retry accepts the exact native window', async ({ page }) => {
  await mount(page); const context = contexts.get(page);
  context.handler = async (route, item) => {
    const body = item.kind === 'waits' ? nativeWait(item) : nativeOptions(item);
    body.toUtc = item.toUtc.replace('.123456Z', '.123457Z'); await json(route, body); return true;
  };
  await windowPicker(page).selectOption('second');
  await expect(waits(page).getByRole('alert')).toContainText('invalid response');
  await expect(options(page).getByRole('alert')).toHaveText('Option history is unavailable. Try again.');
  await expect(chart(page)).toHaveCount(0);
  context.handler = undefined; await refresh(page).click(); await expect(chart(page)).toBeVisible();
  await expect(options(page)).toContainText('SYNTHETIC Alpha database 7');
  await expect(waits(page).locator('time').nth(1)).toHaveAttribute('datetime', '2026-01-15T13:30:00.123456Z');
});

test('unsupported precise windows immediately invalidate old evidence and prevent unsupported native reads', async ({ page }) => {
  await mount(page); const context = contexts.get(page), originalWaitReads = context.reads.filter(item => item.kind === 'waits').length;
  await windowPicker(page).selectOption('waitLimit');
  expect((await firstCommit(page)).waitChart).toBe(false); await expect(chart(page)).toHaveCount(0);
  await expect(waits(page)).toContainText('up to 24 hours'); await expect(options(page)).toContainText('SYNTHETIC Alpha database 7');
  expect(context.reads.filter(item => item.kind === 'waits')).toHaveLength(originalWaitReads);
  const beforeAll = context.reads.length; await windowPicker(page).selectOption('allLimit');
  expect((await firstCommit(page)).optionRows).toBe(0); await expect(options(page)).toContainText('up to seven days');
  await expect(options(page).locator('li')).toHaveCount(0); expect(context.reads).toHaveLength(beforeAll);
  await windowPicker(page).selectOption('first'); await expect(chart(page)).toBeVisible();
  await expect(options(page)).toContainText('SYNTHETIC Alpha database 7');
});

test('unmount cancels both native reads and refuses late failures after remount', async ({ page }) => {
  await mount(page); const pending = await holdRefresh(page, 503);
  await abandonBoth(page, pending, () => page.getByRole('button', { name: 'Unmount diagnostics', exact: true }).click());
  await expect(waits(page)).toHaveCount(0); await expect(options(page)).toHaveCount(0);
  await page.getByRole('button', { name: 'Mount diagnostics', exact: true }).click();
  await expect(chart(page)).toBeVisible(); await expect(options(page)).toContainText('SYNTHETIC Alpha database 7');
  await expect(waits(page).getByRole('alert')).toHaveCount(0); await expect(options(page).getByRole('alert')).toHaveCount(0);
});

for (const status of [401, 403]) test(`an explicit ${status} authorization refusal clears previously retained wait data`, async ({ page }) => {
  await mount(page); contexts.get(page).handler = async (route, item) => {
    if (item.kind !== 'waits') return false; await json(route, { detail: 'SECRET_PROVIDER' }, status); return true;
  };
  await refresh(page).click(); await expect(waits(page).getByRole('alert')).toContainText('No wait trend was loaded for this context');
  await expect(chart(page)).toHaveCount(0); await expect(waits(page)).not.toContainText('Showing previously loaded');
  await expect(options(page)).toContainText('SYNTHETIC Alpha database 7');
});

test('keyboard context links and marker alignment preserve the selected native window and database filter', async ({ page }) => {
  await mount(page); await page.getByRole('combobox', { name: 'Database context', exact: true }).selectOption('8');
  await expect(options(page)).toContainText('SYNTHETIC Alpha database 8'); await expect(options(page)).not.toContainText('SYNTHETIC Alpha database 7');
  const link = page.getByRole('link', { name: 'Open selected wait history', exact: true });
  const href = await link.getAttribute('href'), parameters = new URLSearchParams(href.slice(href.indexOf('?') + 1));
  expect(href.startsWith('#/waits?')).toBe(true); expect(parameters.get('target')).toBe(alpha); expect(parameters.get('databaseId')).toBe('8');
  expect(parameters.get('from')).toBe('2026-01-15T12:00:00.000000Z'); expect(parameters.get('to')).toBe('2026-01-15T13:00:00.000000Z');
  await link.focus(); await page.keyboard.press('Enter'); await expect(page).toHaveURL(new RegExp('databaseId=8'));
  await page.getByRole('button', { name: 'Align charts to recorded marker', exact: true }).focus(); await page.keyboard.press('Enter');
  await expect(page.getByRole('status', { name: 'Shared crosshair UTC', exact: true })).toHaveText('2026-01-15T12:15:00.000Z');
  await expect(waits(page).locator('.chart-crosshair')).toHaveCount(1);
  const positions = await chart(page).evaluate(element => [191, 477].map(x => {
    const point = element.createSVGPoint(); point.x = x; point.y = 100;
    const screen = point.matrixTransform(element.getScreenCTM()); return { x: screen.x, y: screen.y };
  }));
  await page.mouse.move(positions[0].x, positions[0].y); await page.mouse.down(); await page.mouse.move(positions[1].x, positions[1].y); await page.mouse.up();
  await expect(page.getByRole('status', { name: 'Diagnostic window selections', exact: true })).toHaveText('1');
  await expect.poll(() => contexts.get(page).reads.filter(item => item.kind === 'waits').length).toBe(2);
  await expect(waits(page).getByRole('status')).toHaveCount(0);
  const requested = contexts.get(page).reads.filter(item => item.kind === 'waits').at(-1);
  const ticks = value => {
    const parts = /^(.*T\d{2}:\d{2}:\d{2})(?:\.(\d{1,6}))?Z$/.exec(value);
    return BigInt(Date.parse(`${parts[1]}Z`)) * 1000n + BigInt((parts[2] ?? '').padEnd(6, '0'));
  };
  for (const [actual, expected] of [[requested.fromUtc, '2026-01-15T12:15:00.000000Z'], [requested.toUtc, '2026-01-15T12:45:00.000000Z']]) {
    const difference = ticks(actual) - ticks(expected);
    expect(Number(difference < 0n ? -difference : difference), 'Physical SVG coordinate round-trip stays within one millisecond').toBeLessThanOrEqual(1000);
  }
  // The native fixture echoes the requested bounds; the displayed receipt must retain them exactly.
  await expect(waits(page).locator('time').nth(0)).toHaveAttribute('datetime', requested.fromUtc);
  await expect(waits(page).locator('time').nth(1)).toHaveAttribute('datetime', requested.toUtc);
  const selected = await firstCommit(page);
  expect(selected.fromUtc).toBe(requested.fromUtc); expect(selected.toUtc).toBe(requested.toUtc);
});

for (const width of [320, 390, 768, 1440]) test(`retained diagnostic evidence remains accessible in dark layout at ${width}px`, async ({ page }, info) => {
  contexts.get(page).longNames = true;
  await page.setViewportSize({ width, height: 1000 }); await mount(page);
  const pending = hold(); contexts.get(page).handler = (route, item) => item.kind === 'waits' ? pending.handle(route, item) : false;
  await refresh(page).click(); await pending.started;
  await expect(waits(page)).toContainText('previous snapshot in this same window');
  const name = options(page).locator('li strong').first(); await expect(name).toHaveText(longDatabaseName);
  await expect(options(page).getByRole('status')).toHaveCount(0);
  const textBounds = await name.evaluate(element => Array.from(element.getClientRects(), rect => ({ left: rect.left, right: rect.right })));
  const optionBounds = await options(page).boundingBox();
  for (const bounds of textBounds) {
    expect(bounds.left).toBeGreaterThanOrEqual(optionBounds.x - 1);
    expect(bounds.right).toBeLessThanOrEqual(optionBounds.x + optionBounds.width + 1);
  }
  await waits(page).getByText('View chart values', { exact: true }).focus(); await page.keyboard.press('Enter');
  const region = waits(page).getByRole('region', { name: 'Chart observation values', exact: true });
  await expect(region).toHaveAttribute('tabindex', '0'); await page.keyboard.press('Tab'); await expect(region).toBeFocused();
  const timestampLines = await region.locator('td[title]').first().evaluate(element => {
    const range = document.createRange(); range.selectNodeContents(element); return range.getClientRects().length;
  });
  expect(timestampLines, 'The native observation timestamp remains readable on one line').toBe(1);
  const focused = await region.evaluate(element => ({ style: getComputedStyle(element).outlineStyle, width: parseFloat(getComputedStyle(element).outlineWidth) }));
  expect(focused.style).not.toBe('none'); expect(focused.width).toBeGreaterThanOrEqual(2);
  if (await region.evaluate(element => element.scrollWidth > element.clientWidth)) {
    await page.keyboard.press('ArrowRight'); await expect.poll(() => region.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`context-diagnostics-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-context-search/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
  await region.evaluate(element => { element.scrollLeft = 0; element.blur(); });
  await page.evaluate(() => window.scrollTo({ top: 0, left: 0, behavior: 'instant' })); await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
  await page.screenshot({ path: `${directory}/context-diagnostics-${width}.png`, fullPage: true }); await pending.finish();
});
