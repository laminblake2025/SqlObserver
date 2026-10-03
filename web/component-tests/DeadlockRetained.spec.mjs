import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { origin, target, otherTarget, fromUtc, toUtc, routeHash, nativePage, nativeDetail, respond, hold } from './deadlockRetainedFixture.mjs';

const sessions = new WeakMap();
const cards = page => page.getByRole('complementary', { name: 'Captured deadlock events' }).locator('button[aria-controls="selected-deadlock-detail"]');
const detail = page => page.getByRole('region', { name: 'Selected deadlock detail', exact: true });
const first = page => page.getByRole('button', { name: 'First page', exact: true });
const previous = page => page.getByRole('button', { name: 'Previous', exact: true });
const next = page => page.getByRole('button', { name: 'Next', exact: true });
const latest = page => page.getByRole('button', { name: 'Return to latest deadlock events', exact: true });
const retry = page => page.getByRole('button', { name: 'Retry deadlock read', exact: true });
const cancel = page => page.getByRole('button', { name: 'Cancel deadlock read', exact: true });
const refresh = page => page.getByRole('button', { name: 'Refresh context', exact: true });
const listReads = page => sessions.get(page).reads.filter(item => item.kind === 'list');
const params = page => new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
const renderedIds = page => cards(page).evaluateAll(elements => elements.map(element => element.textContent));

test.beforeEach(async ({ page }) => {
  const session = { reads: [], unexpected: [], summaries: new Map(), handler: undefined }; sessions.set(page, session);
  page.on('pageerror', error => session.unexpected.push(`Page error: ${error.message}`));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async () => { session.unexpected.push(`${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/u.test(url.pathname)) return route.continue();
    const match = /^\/api\/v1\/observation-targets\/([^/]+)\/deadlocks(?:\/([^/]+))?$/u.exec(url.pathname);
    if (!match || ![target, otherTarget].includes(match[1]) || request.method() !== 'GET') return reject();
    const kind = match[2] ? 'detail' : 'list';
    let value;
    if (kind === 'list') {
      const keys = [...url.searchParams.keys()].sort();
      if (keys.join('|') !== ['fromUtc', 'limit', 'toUtc', ...(url.searchParams.has('cursor') ? ['cursor'] : [])].sort().join('|') ||
          url.searchParams.get('limit') !== '25' || [...new Set(keys)].length !== keys.length) return reject();
      value = nativePage(url, match[1]);
      for (const item of value.items) session.summaries.set(item.eventId, item);
    } else {
      if (url.search || !session.summaries.has(match[2])) return reject();
      value = nativeDetail(session.summaries.get(match[2]));
    }
    const item = { kind, target: match[1], eventId: match[2], cursor: url.searchParams.get('cursor'),
      fromUtc: url.searchParams.get('fromUtc'), toUtc: url.searchParams.get('toUtc'), value, url: url.href, request };
    session.reads.push(item);
    if (session.handler && await session.handler(route, item)) return;
    await respond(route, value);
  });
});
test.afterEach(async ({ page }) => {
  expect(sessions.get(page).unexpected, 'Only registered native GETs are allowed; external traffic and page errors fail').toEqual([]);
});
async function mount(page, hash = routeHash()) {
  await page.goto(`/component-tests/deadlocks.html${hash}`);
  await expect(cards(page)).toHaveCount(25);
  await expect(detail(page).getByRole('link', { name: 'View activity at event time' })).toBeVisible();
}
async function selectFirst(page) {
  const row = listReads(page).at(-1).value.items[0];
  await cards(page).first().click();
  await expect.poll(() => params(page).get('deadlockEvent')).toBe(row.eventId);
  await expect(page.getByRole('button', { name: 'Clear deadlock selection', exact: true })).toBeVisible();
  await expect(detail(page)).toHaveAttribute('aria-busy', 'false');
  await expect(detail(page)).toContainText(row.fingerprint);
  return row;
}
async function holdList(page, action, status = 200, continuationOnly = false) {
  const pending = hold(status);
  sessions.get(page).handler = (route, item) => item.kind === 'list' && (!continuationOnly || item.cursor !== null)
    ? pending.handle(route, item.value) : false;
  await action(); const request = await pending.started; return { pending, request };
}
async function abandon(page, held, action) {
  const aborted = page.waitForEvent('requestfailed', { predicate: request => request === held.request });
  sessions.get(page).handler = undefined; await action(); await aborted; await held.pending.finish();
}

test('First, Previous and already visited Next restore native pages locally and retain the exact selected event', async ({ page }) => {
  await mount(page); const selected = await selectFirst(page), original = await renderedIds(page);
  const reads = listReads(page).length; await next(page).click();
  await expect.poll(() => listReads(page).length).toBe(reads + 1); await expect(previous(page)).toBeEnabled();
  const second = await renderedIds(page); expect(second).not.toEqual(original);
  await expect(detail(page)).toContainText(selected.fingerprint); expect(params(page).get('deadlockEvent')).toBe(selected.eventId);
  await first(page).click(); expect(await renderedIds(page)).toEqual(original);
  await next(page).click(); expect(await renderedIds(page)).toEqual(second);
  await previous(page).click(); expect(await renderedIds(page)).toEqual(original);
  expect(listReads(page)).toHaveLength(reads + 1);
  await refresh(page).click(); expect(listReads(page)).toHaveLength(reads + 1);
  await expect(detail(page)).toContainText(selected.fingerprint);
});

test('moving Live refresh keeps an owned continuation and its received window', async ({ page }) => {
  await page.clock.install({ time: new Date('2026-08-24T01:00:00Z') });
  await mount(page, `#/deadlocks?${new URLSearchParams({ target, range: '1h', databaseId: '7' })}`);
  const selected = await selectFirst(page), receipt = listReads(page)[0];
  await page.clock.runFor(50000);
  const held = await holdList(page, () => next(page).click(), 200, true);
  await page.clock.runFor(10000); await refresh(page).click();
  expect(listReads(page)).toHaveLength(2); expect(held.request.failure()).toBeNull();
  await held.pending.finish(); await expect(previous(page)).toBeEnabled();
  expect(listReads(page)[1].fromUtc).toBe(receipt.fromUtc); expect(listReads(page)[1].toUtc).toBe(receipt.toUtc);
  await expect(detail(page)).toContainText(selected.fingerprint);
  sessions.get(page).handler = undefined; await latest(page).click();
  await expect.poll(() => listReads(page).length).toBe(3);
  expect(listReads(page)[2].toUtc).not.toBe(receipt.toUtc);
});

test('failed latest read preserves visited pages and retries its exact request before replacement', async ({ page }) => {
  await mount(page); await selectFirst(page); await next(page).click(); await expect(previous(page)).toBeEnabled();
  const visited = await renderedIds(page);
  const held = await holdList(page, () => latest(page).click(), 503); await held.pending.finish();
  await expect(retry(page)).toBeVisible(); expect(await renderedIds(page)).toEqual(visited);
  const failed = listReads(page).at(-1); sessions.get(page).handler = undefined;
  await retry(page).click(); await expect(first(page)).toBeDisabled();
  expect(listReads(page).at(-1).url).toBe(failed.url); await expect(retry(page)).toHaveCount(0);
  await expect(cards(page)).toHaveCount(25); await expect(page.locator('body')).not.toContainText('SECRET_PROVIDER_FAILURE');
});

test('failed continuation preserves exact selected detail and can retry without another first-page GET', async ({ page }) => {
  await mount(page); const selected = await selectFirst(page), original = await renderedIds(page);
  const held = await holdList(page, () => next(page).click(), 503, true); await held.pending.finish();
  await expect(retry(page)).toBeVisible(); expect(await renderedIds(page)).toEqual(original);
  await expect(detail(page)).toContainText(selected.fingerprint);
  const failed = listReads(page).at(-1); sessions.get(page).handler = undefined;
  await retry(page).click(); await expect(previous(page)).toBeEnabled();
  expect(listReads(page).at(-1).url).toBe(failed.url);
  expect(listReads(page).filter(item => item.cursor === null)).toHaveLength(1);
});

test('a continuation with a changed collected cutoff cannot advance received evidence', async ({ page }) => {
  await mount(page); const selected = await selectFirst(page), original = await renderedIds(page);
  sessions.get(page).handler = async (route, item) => {
    if (item.kind !== 'list' || !item.cursor) return false;
    await respond(route, { ...item.value, repositoryTimeUtc: new Date(Date.parse(item.value.repositoryTimeUtc) + 1000).toISOString() }); return true;
  };
  await next(page).click(); await expect(retry(page)).toBeVisible(); await expect(cancel(page)).toHaveCount(0);
  expect(await renderedIds(page)).toEqual(original); await expect(first(page)).toBeDisabled();
  await expect(detail(page)).toContainText(selected.fingerprint);
  const failed = listReads(page).at(-1); sessions.get(page).handler = undefined;
  await retry(page).click(); await expect(previous(page)).toBeEnabled(); expect(listReads(page).at(-1).url).toBe(failed.url);
});

for (const operation of ['latest', 'continuation']) test(`cancelled ${operation} read retains received evidence and exact retry ownership`, async ({ page }) => {
  await mount(page); await selectFirst(page); const original = await renderedIds(page);
  const held = await holdList(page, () => operation === 'latest' ? latest(page).click() : next(page).click(), 200, operation === 'continuation');
  await abandon(page, held, () => cancel(page).click());
  expect(await renderedIds(page)).toEqual(original); await expect(retry(page)).toBeVisible();
  const cancelled = listReads(page).at(-1); await retry(page).click();
  await expect(cancel(page)).toHaveCount(0); expect(listReads(page).at(-1).url).toBe(cancelled.url);
});

test('a newer automatic read aborts the older poll and exclusively owns replacement', async ({ page }) => {
  await mount(page); const held = await holdList(page, () => refresh(page).click());
  await abandon(page, held, () => refresh(page).click());
  await expect(cards(page)).toHaveCount(25); await expect(cancel(page)).toHaveCount(0);
  expect(listReads(page)).toHaveLength(3); await expect(page.getByRole('alert')).toHaveCount(0);
});

for (const change of ['target', 'revision', 'window', 'unmount']) test(`changing ${change} cancels pending continuation and discards the old investigation`, async ({ page }) => {
  await mount(page); await selectFirst(page); const selected = params(page).get('deadlockEvent');
  const held = await holdList(page, () => next(page).click(), 200, true);
  await abandon(page, held, async () => {
    if (change === 'target') await page.getByRole('combobox', { name: 'Server context', exact: true }).selectOption(otherTarget);
    else if (change === 'revision') await page.getByRole('button', { name: 'Advance target revision', exact: true }).click();
    else if (change === 'window') await page.getByRole('combobox', { name: 'History window', exact: true }).selectOption('second');
    else await page.getByRole('button', { name: 'Unmount deadlock panel', exact: true }).click();
  });
  if (change === 'unmount') {
    await expect(detail(page)).toHaveCount(0); await page.getByRole('button', { name: 'Mount deadlock panel', exact: true }).click();
  }
  await expect(cards(page)).toHaveCount(25); await expect(first(page)).toBeDisabled();
  if (change === 'target' || change === 'window') expect(params(page).get('deadlockEvent')).not.toBe(selected);
  expect(listReads(page).at(-1).cursor).toBeNull();
});

test('target ABA round trip cannot revive an abandoned continuation or old page trail', async ({ page }) => {
  await mount(page); const held = await holdList(page, () => next(page).click(), 200, true);
  await abandon(page, held, () => page.getByRole('combobox', { name: 'Server context', exact: true }).selectOption(otherTarget));
  await expect(cards(page)).toHaveCount(25);
  await page.getByRole('combobox', { name: 'Server context', exact: true }).selectOption(target);
  await expect(cards(page)).toHaveCount(25); await expect(first(page)).toBeDisabled();
  expect(listReads(page).at(-1).target).toBe(target); expect(listReads(page).at(-1).cursor).toBeNull();
});

for (const status of [401, 403]) for (const source of ['list', 'detail']) test(`definitive ${status} ${source} refusal clears all protected event and detail receipts`, async ({ page }) => {
  await mount(page); const selected = await selectFirst(page);
  sessions.get(page).handler = async (route, item) => {
    if (item.kind !== source) return false;
    await respond(route, { detail: 'SECRET_ACCESS_PROVIDER' }, status); return true;
  };
  if (source === 'list') await latest(page).click(); else await refresh(page).click();
  await expect(cards(page)).toHaveCount(0); await expect(detail(page).getByRole('table')).toHaveCount(0);
  await expect(detail(page)).not.toContainText(selected.fingerprint);
  expect(params(page).get('deadlockEvent')).toBe(selected.eventId);
  await expect(page.locator('body')).not.toContainText('SECRET_ACCESS_PROVIDER');
  sessions.get(page).handler = undefined; await latest(page).click(); await expect(cards(page)).toHaveCount(25);
});

test('detail denial synchronously invalidates a pending successful list read and permits only explicit recovery', async ({ page }) => {
  await mount(page); const selected = await selectFirst(page), pending = hold(), deniedDetail = hold(401);
  sessions.get(page).handler = async (route, item) => {
    if (item.kind === 'list') return pending.handle(route, item.value);
    return deniedDetail.handle(route, { detail: 'SECRET_LATE_ACCESS' });
  };
  await refresh(page).click(); const [request] = await Promise.all([pending.started, deniedDetail.started]);
  await deniedDetail.finish();
  await expect(cards(page)).toHaveCount(0); await expect(detail(page).getByRole('table')).toHaveCount(0);
  await expect.poll(() => request.failure()?.errorText ?? '').toContain('ERR_ABORTED');
  await pending.finish(); await expect(cards(page)).toHaveCount(0);
  await expect(detail(page)).not.toContainText(selected.fingerprint);
  const reads = sessions.get(page).reads.length; sessions.get(page).handler = undefined;
  await refresh(page).click(); expect(sessions.get(page).reads).toHaveLength(reads);
  await expect(cards(page)).toHaveCount(0); await expect(detail(page).getByRole('table')).toHaveCount(0);
  expect(params(page).get('deadlockEvent')).toBe(selected.eventId);
  await latest(page).click(); await expect(cards(page)).toHaveCount(25);
  await expect(detail(page)).toContainText(selected.fingerprint);
  await expect(page.locator('body')).not.toContainText('SECRET_LATE_ACCESS');
});

test('pending selected-detail read survives refresh without duplicate GETs or loss of matching received detail', async ({ page }) => {
  await mount(page); const selected = await selectFirst(page), pending = hold();
  sessions.get(page).handler = (route, item) => item.kind === 'detail' ? pending.handle(route, item.value) : false;
  await refresh(page).click(); const request = await pending.started;
  const reads = sessions.get(page).reads.filter(item => item.kind === 'detail').length;
  await expect(detail(page)).toContainText(selected.fingerprint);
  await refresh(page).click(); expect(request.failure()).toBeNull();
  expect(sessions.get(page).reads.filter(item => item.kind === 'detail')).toHaveLength(reads);
  await pending.finish(); await expect(page.getByRole('button', { name: 'Cancel deadlock detail', exact: true })).toHaveCount(0);
  await expect(detail(page)).toContainText(selected.fingerprint); expect(params(page).get('deadlockEvent')).toBe(selected.eventId);
});

test('cancelled selected-detail read retains matching evidence and retries its exact occurrence explicitly', async ({ page }) => {
  await mount(page); const selected = await selectFirst(page), pending = hold();
  sessions.get(page).handler = (route, item) => item.kind === 'detail' ? pending.handle(route, item.value) : false;
  await refresh(page).click(); const request = await pending.started;
  await abandon(page, { request, pending }, () => page.getByRole('button', { name: 'Cancel deadlock detail', exact: true }).click());
  await expect(detail(page)).toContainText(selected.fingerprint);
  const retryDetail = page.getByRole('button', { name: 'Retry deadlock detail', exact: true }); await expect(retryDetail).toBeVisible();
  const cancelled = sessions.get(page).reads.filter(item => item.kind === 'detail').at(-1);
  const count = sessions.get(page).reads.filter(item => item.kind === 'detail').length;
  await refresh(page).click(); expect(sessions.get(page).reads.filter(item => item.kind === 'detail')).toHaveLength(count);
  await retryDetail.click(); await expect(retryDetail).toHaveCount(0);
  expect(sessions.get(page).reads.filter(item => item.kind === 'detail').at(-1).url).toBe(cancelled.url);
  await expect(detail(page)).toContainText(selected.fingerprint); expect(params(page).get('deadlockEvent')).toBe(selected.eventId);
});

test('detail occurrence mismatch keeps the exact URL selection and never substitutes another event', async ({ page }) => {
  await mount(page); const rows = listReads(page)[0].value.items, selected = rows[1];
  sessions.get(page).handler = async (route, item) => {
    if (item.kind !== 'detail' || item.eventId !== selected.eventId) return false;
    await respond(route, { ...item.value, summary: { ...item.value.summary, occurredAtUtc: rows[0].occurredAtUtc } }); return true;
  };
  await cards(page).nth(1).click(); await expect(detail(page).getByRole('alert')).toContainText('No other event has been selected');
  expect(params(page).get('deadlockEvent')).toBe(selected.eventId);
  await expect(detail(page).getByRole('table')).toHaveCount(0);
  await next(page).click(); await expect(previous(page)).toBeEnabled();
  expect(params(page).get('deadlockEvent')).toBe(selected.eventId);
});

test('native zero-offset DateTimeOffset timestamps preserve exact microsecond selected occurrence and continuation', async ({ page }) => {
  const nativeUtc = value => value.replace(/(?:\.(\d{1,7}))?Z$/u, (_, fraction = '') => `.${fraction.padEnd(7, '0')}+00:00`);
  const convertSummary = summary => ({ ...summary, occurredAtUtc: nativeUtc(summary.occurredAtUtc), collectedAtUtc: nativeUtc(summary.collectedAtUtc) });
  sessions.get(page).handler = async (route, item) => {
    await respond(route, item.kind === 'list' ? { ...item.value, repositoryTimeUtc: nativeUtc(item.value.repositoryTimeUtc),
      items: item.value.items.map(convertSummary) } : { ...item.value, summary: convertSummary(item.value.summary) });
    return true;
  };
  await mount(page); const selected = await selectFirst(page);
  await expect(detail(page)).toContainText(selected.fingerprint);
  const occurrence = params(page).get('deadlockAt'); expect(occurrence).toMatch(/Z$/u);
  expect(Date.parse(occurrence)).toBe(Date.parse(selected.occurredAtUtc));
  const activityHref = await detail(page).getByRole('link', { name: 'View activity at event time' }).getAttribute('href');
  const activity = new URLSearchParams(activityHref.split('?')[1]);
  expect(activity.get('at')).toMatch(/Z$/u); expect(Date.parse(activity.get('at'))).toBe(Date.parse(occurrence));
  expect(Object.fromEntries(activity)).toMatchObject({ target, event: selected.eventId, databaseId: '7',
    range: 'custom', from: fromUtc, to: toUtc, compare: '1' });
  await next(page).click(); await expect(previous(page)).toBeEnabled();
  await expect(detail(page)).toContainText(selected.fingerprint); expect(params(page).get('deadlockAt')).toBe(occurrence);
});

for (const width of [320, 390, 768, 1440]) test(`retained deadlock evidence is accessible in dark layout at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 }); await mount(page); await selectFirst(page); await next(page).click();
  await expect(previous(page)).toBeEnabled(); await previous(page).focus(); await page.keyboard.press('Enter');
  await expect(first(page)).toBeDisabled(); await next(page).focus(); await page.keyboard.press('Enter');
  await expect(previous(page)).toBeEnabled(); await expect(detail(page).getByRole('table')).toBeVisible();
  const receivedRows = page.getByRole('region', { name: 'Received deadlock event rows', exact: true });
  await receivedRows.focus(); await expect(receivedRows).toBeFocused();
  const rowBounds = await receivedRows.boundingBox(); expect(rowBounds.height).toBeLessThanOrEqual(width <= 480 ? 420 : 600);
  expect(await receivedRows.evaluate(element => element.scrollHeight > element.clientHeight)).toBe(true);
  await page.keyboard.press('ArrowDown'); await expect.poll(() => receivedRows.evaluate(element => element.scrollTop)).toBeGreaterThan(0);
  await receivedRows.evaluate(element => { element.scrollTop = 0; });
  const table = detail(page).getByRole('region', { name: 'Deadlock relationship table', exact: true });
  await table.focus(); await expect(table).toBeFocused();
  const focus = await table.evaluate(element => ({ style: getComputedStyle(element).outlineStyle, width: parseFloat(getComputedStyle(element).outlineWidth) }));
  expect(focus.style).not.toBe('none'); expect(focus.width).toBeGreaterThanOrEqual(2);
  if (await table.evaluate(element => element.scrollWidth > element.clientWidth)) {
    await page.keyboard.press('ArrowRight'); await expect.poll(() => table.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`deadlock-retained-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
  expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-deadlock-retained/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
  await table.evaluate(element => { element.scrollLeft = 0; element.blur(); });
  await page.evaluate(() => window.scrollTo({ top: 0, left: 0, behavior: 'instant' }));
  await page.screenshot({ path: `${directory}/deadlock-retained-${width}.png`, fullPage: true });
});
