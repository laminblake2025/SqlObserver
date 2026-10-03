import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { alpha, beta, labels, rowId, malicious, registered, nativeValue } from './analyticsWorkflowFixture.mjs';

const origin = 'http://127.0.0.1:4328', contexts = new WeakMap();
const owner = page => page.locator('.analytics-workflow-owner');
const button = (page, name) => owner(page).getByRole('button', { name, exact: true });
const next = page => button(page, 'Next evidence page');
const latest = page => button(page, 'Return analytics to latest');
const retry = page => button(page, 'Retry analytics read');
const receipt = (page, generation, pageNumber = 1) => Promise.all([
  expect(owner(page)).toContainText(`Generation: ${generation}`),
  expect(owner(page)).toContainText(`Received page ${pageNumber} of`)
]);
const select = (page, surface) => page.getByRole('combobox', { name: 'Evidence surface', exact: true }).selectOption(surface);
const firstCommit = async page => JSON.parse(await page.getByRole('status', { name: 'First committed analytics context' }).textContent());
const secret = 'SECRET_NATIVE_PROVIDER ' + malicious;
async function json(route, body, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(body) });
}
function held(predicate, transform = value => value, status = 200) {
  let announce, release, complete;
  const started = new Promise(resolve => { announce = resolve; }), gate = new Promise(resolve => { release = resolve; }), finished = new Promise(resolve => { complete = resolve; });
  return { started, async finish() { release(); await finished; }, async handle(route, item, context) {
    if (!predicate(item)) return false;
    // Capture at dispatch so late completion is a genuinely old receipt.
    const body = status === 200 ? transform(nativeValue(item, context)) : { detail: secret };
    announce(route.request()); await gate;
    try { await json(route, body, status); }
    catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
    finally { complete(); } return true;
  } };
}
async function abortRead(page, request, action) {
  const failed = page.waitForEvent('requestfailed', { predicate: item => item === request }); await action(); await failed;
}
test.beforeEach(async ({ page }) => {
  const context = { generation: 1, revision: 1, requests: [], unexpected: [], handler: undefined }; contexts.set(page, context);
  page.on('pageerror', error => context.unexpected.push(`Runtime error: ${error.message}`));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async reason => { context.unexpected.push(`${reason}: ${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject('External request');
    if (!/^\/api(?:\/|$)/u.test(url.pathname)) return route.continue();
    const item = registered(url); if (!item || request.method() !== 'GET') return reject('Unexpected native API');
    context.requests.push(item);
    if (context.handler && await context.handler(route, item, context)) return;
    if (item.continuation && (item.continuation.generation !== context.generation || item.continuation.targetRevision !== context.revision)) return json(route, { detail: secret }, 409);
    await json(route, nativeValue(item, context));
  });
});
test.afterEach(async ({ page }) => { expect(contexts.get(page).unexpected, 'Only registered native-shaped requests and no runtime errors').toEqual([]); });
async function mount(page, pageMode = false) {
  await page.goto(`/component-tests/analytics-workflow.html${pageMode ? '?page=1' : ''}`);
  await expect(page.getByRole('heading', { name: 'Analytics workflow harness', exact: true })).toBeVisible(); await receipt(page, 1);
}

test('received investigation pages survive polling and cached first/previous/next restore without network reads', async ({ page }) => {
  await mount(page); const context = contexts.get(page); await next(page).click(); await receipt(page, 1, 2);
  await expect(owner(page)).toContainText(rowId(1, 100)); const count = context.requests.length;
  context.generation = 2; await page.keyboard.press('F6'); await expect(owner(page)).toContainText('Retained analytics evidence');
  await receipt(page, 1, 2); expect(context.requests).toHaveLength(count);
  await button(page, 'First evidence page').click(); await receipt(page, 1, 1);
  await next(page).click(); await receipt(page, 1, 2); await button(page, 'Previous evidence page').click(); await receipt(page, 1, 1);
  expect(context.requests).toHaveLength(count);
  await latest(page).click(); await receipt(page, 2, 1); await expect(owner(page)).not.toContainText(rowId(1, 100));
  expect(context.requests.at(-1).cursor).toBeNull();
});
test('failed explicit latest retains received rows; retry repeats fresh first page and resets only on success', async ({ page }) => {
  await mount(page); await next(page).click(); await receipt(page, 1, 2); const context = contexts.get(page); context.generation = 2;
  context.handler = async (route, item) => { if (item.cursor) return false; await json(route, { detail: secret }, 503); return true; };
  await latest(page).click(); await expect(owner(page).getByRole('alert')).toBeVisible(); await receipt(page, 1, 2);
  await expect(owner(page)).toContainText(rowId(1, 100)); await expect(owner(page)).not.toContainText('SECRET_NATIVE_PROVIDER');
  const failed = context.requests.at(-1); context.handler = undefined; await retry(page).click(); await receipt(page, 2, 1);
  expect(context.requests.at(-1)).toEqual(failed); await expect(button(page, 'Previous evidence page')).toBeDisabled();
});
test('failed continuation retries the identical native cursor and does not append duplicate evidence', async ({ page }) => {
  await mount(page); const context = contexts.get(page);
  context.handler = async (route, item) => { if (!item.cursor) return false; await json(route, { detail: secret }, 503); return true; };
  await next(page).click(); await expect(owner(page).getByRole('alert')).toBeVisible(); await receipt(page, 1, 1);
  const failed = context.requests.at(-1); context.handler = undefined; await retry(page).click(); await receipt(page, 1, 2);
  expect(context.requests.at(-1)).toEqual(failed); await expect(owner(page).getByRole('table').locator('tbody tr')).toHaveCount(100);
  await expect(owner(page)).not.toContainText(rowId(1, 0));
});
test('one held navigation owns the read through refresh ticks; cancel retains receipt and retry resumes the same cursor', async ({ page }) => {
  await mount(page); const context = contexts.get(page), pending = held(item => Boolean(item.cursor)); context.handler = pending.handle;
  await next(page).click(); const request = await pending.started, count = context.requests.length;
  await expect(next(page)).toBeDisabled(); await expect(latest(page)).toBeDisabled(); await page.keyboard.press('F6');
  expect(context.requests).toHaveLength(count); await receipt(page, 1, 1);
  await abortRead(page, request, () => button(page, 'Cancel analytics read').click()); await pending.finish();
  await receipt(page, 1, 1); context.handler = undefined; await retry(page).click(); await receipt(page, 1, 2);
  expect(context.requests.at(-1).cursor).toBe(context.requests[count - 1].cursor);
});
test('idle polling replaces an older automatic read and fences its late receipt', async ({ page }) => {
  await mount(page); const context = contexts.get(page), pending = held(item => !item.cursor); context.handler = pending.handle;
  await page.keyboard.press('F6'); const request = await pending.started; context.handler = undefined; context.generation = 2;
  await abortRead(page, request, () => page.keyboard.press('F6')); await receipt(page, 2); await pending.finish(); await receipt(page, 2);
  await expect(owner(page)).not.toContainText(rowId(1, 0));
});
for (const status of [401, 403]) test(`native HTTP ${status} removes all received pages and permits explicit recovery`, async ({ page }) => {
  await mount(page); await next(page).click(); await receipt(page, 1, 2); const context = contexts.get(page);
  context.handler = async route => { await json(route, { detail: secret }, status); return true; };
  await latest(page).click(); await expect(owner(page)).toContainText('Analytics access was denied');
  await expect(owner(page).getByRole('table')).toHaveCount(0); await expect(owner(page)).not.toContainText(rowId(1, 100));
  await expect(owner(page)).not.toContainText('SECRET_NATIVE_PROVIDER'); context.handler = undefined; context.generation = 2;
  await latest(page).click(); await receipt(page, 2); await expect(button(page, 'Previous evidence page')).toBeDisabled();
});
for (const field of ['generation', 'targetRevision', 'snapshotUtc', 'cutoffUtc']) test(`continuation ${field} drift fails safely and preserves the received page`, async ({ page }) => {
  await mount(page); const context = contexts.get(page);
  context.handler = async (route, item) => {
    if (!item.cursor) return false; const value = nativeValue(item, context);
    value[field] = typeof value[field] === 'number' ? value[field] + 1 : '2026-10-03T12:00:00Z';
    await json(route, value); return true;
  };
  await next(page).click(); await expect(owner(page).getByRole('alert')).toBeVisible(); await receipt(page, 1, 1);
  await expect(owner(page)).toContainText(rowId(1, 0)); await expect(retry(page)).toBeEnabled();
});
test('an expired unread cursor preserves cached pages and fresh latest recovers the new generation', async ({ page }) => {
  await mount(page); await next(page).click(); await receipt(page, 1, 2); const context = contexts.get(page); context.generation = 2;
  await next(page).click(); await expect(owner(page).getByRole('alert')).toBeVisible(); await receipt(page, 1, 2);
  await button(page, 'Previous evidence page').click(); await receipt(page, 1, 1); await next(page).click(); await receipt(page, 1, 2);
  await latest(page).click(); await receipt(page, 2, 1);
});
test('moving live windows preserve an investigated native window until explicit latest', async ({ page }) => {
  await mount(page); await select(page, 'host/metrics'); await receipt(page, 1); await next(page).click(); await receipt(page, 1, 2);
  const context = contexts.get(page), continuation = context.requests.at(-1), count = context.requests.length;
  await page.keyboard.press('F7'); await receipt(page, 1, 2); expect(context.requests).toHaveLength(count);
  await next(page).click(); await receipt(page, 1, 3);
  expect(context.requests.at(-1).fromUtc).toBe(continuation.fromUtc); expect(context.requests.at(-1).toUtc).toBe(continuation.toUtc);
  await latest(page).click(); await receipt(page, 1, 1);
  expect(Date.parse(context.requests.at(-1).toUtc) - Date.parse(continuation.toUtc)).toBe(300000);
});
for (const change of ['target', 'revision', 'window']) test(`${change} context change hides old evidence before effects and aborts a held old continuation`, async ({ page }) => {
  await mount(page); const context = contexts.get(page), pending = held(item => Boolean(item.cursor)); context.handler = pending.handle;
  await next(page).click(); const request = await pending.started; context.handler = undefined;
  if (change === 'revision') context.revision = 2;
  await abortRead(page, request, () => page.keyboard.press(change === 'target' ? 'F2' : change === 'revision' ? 'F3' : 'F5'));
  expect((await firstCommit(page)).text).not.toContain(rowId(1, 0)); await receipt(page, 1); await pending.finish();
  await receipt(page, 1); await expect(button(page, 'Previous evidence page')).toBeDisabled();
  if (change === 'target') expect(context.requests.at(-1).targetId).toBe(beta);
  if (change === 'revision') await expect(owner(page)).toContainText('Target revision: 2');
});
test('target ABA does not allow an aborted first target read to overwrite its newer receipt', async ({ page }) => {
  await mount(page); const context = contexts.get(page), pending = held(item => item.targetId === alpha && !item.cursor); context.handler = pending.handle;
  await page.keyboard.press('F6'); const request = await pending.started; context.handler = undefined;
  await abortRead(page, request, () => page.keyboard.press('F2')); await receipt(page, 1);
  context.generation = 2; await page.keyboard.press('F2'); await receipt(page, 2); await pending.finish(); await receipt(page, 2);
  await expect(owner(page)).not.toContainText(rowId(1, 0));
});
test('unmount aborts an owned continuation and late completion cannot seed the remount', async ({ page }) => {
  await mount(page); const context = contexts.get(page), pending = held(item => Boolean(item.cursor)); context.handler = pending.handle;
  await next(page).click(); const request = await pending.started;
  await abortRead(page, request, () => page.keyboard.press('F8')); await expect(owner(page).getByRole('table')).toHaveCount(0);
  context.handler = undefined; context.generation = 2; await page.keyboard.press('F8'); await receipt(page, 2); await pending.finish(); await receipt(page, 2);
});
test('real AnalyticsPage keeps independent surface trails; leaving a held read cancels only that owner', async ({ page }) => {
  await mount(page, true); await next(page).click(); await receipt(page, 1, 2); const context = contexts.get(page), pending = held(item => item.surface === 'jobs' && Boolean(item.cursor)); context.handler = pending.handle;
  await next(page).click(); const request = await pending.started; context.handler = undefined;
  await abortRead(page, request, () => select(page, 'backfill')); await receipt(page, 1); await pending.finish();
  await expect(owner(page)).toContainText('Backfill jobs only'); await next(page).click(); await receipt(page, 1, 2);
  const count = context.requests.length; await select(page, 'jobs'); await receipt(page, 1, 2); expect(context.requests).toHaveLength(count);
  await select(page, 'backfill'); await receipt(page, 1, 2); expect(context.requests).toHaveLength(count);
});
test('category ABA cannot let a cancelled prior category read overwrite its restored trail', async ({ page }) => {
  await mount(page, true); await next(page).click(); await receipt(page, 1, 2);
  const context = contexts.get(page), pending = held(item => item.surface === 'jobs' && Boolean(item.cursor)); context.handler = pending.handle;
  await next(page).click(); const request = await pending.started; context.handler = undefined;
  await abortRead(page, request, () => select(page, 'backfill')); await receipt(page, 1);
  await select(page, 'jobs'); await receipt(page, 1, 2); await pending.finish(); await receipt(page, 1, 2);
  await expect(owner(page)).toContainText(rowId(1, 100)); await expect(owner(page)).not.toContainText(rowId(1, 200));
});
test('target-wide native denial purges retained evidence across categories without automatic denial loops', async ({ page }) => {
  await mount(page, true); await select(page, 'diagnostics/search'); await receipt(page, 1); await next(page).click(); await receipt(page, 1, 2);
  await select(page, 'jobs'); await receipt(page, 1); const context = contexts.get(page);
  context.handler = async route => { await json(route, { detail: secret }, 403); return true; };
  await latest(page).click(); await expect(owner(page)).toContainText('Analytics access was denied');
  const count = context.requests.length; await select(page, 'diagnostics/search');
  await expect(owner(page).getByRole('table')).toHaveCount(0); await expect(owner(page)).not.toContainText(rowId(1, 100));
  await expect(owner(page)).toContainText('Analytics access was denied'); expect(context.requests).toHaveLength(count);
  await page.keyboard.press('F6'); expect(context.requests).toHaveLength(count);
  context.handler = undefined; context.generation = 2; await latest(page).click(); await receipt(page, 2, 1);
});
test('legacy incidents disclose bounded native evidence without fabricated paging or provenance', async ({ page }) => {
  await mount(page); await select(page, 'incidents'); await expect(owner(page).getByRole('table')).toBeVisible();
  await expect(owner(page)).toContainText('Bounded incident list'); await expect(owner(page)).toContainText('does not provide continuation or a snapshot fence');
  for (const name of ['First evidence page', 'Previous evidence page', 'Next evidence page']) await expect(button(page, name)).toHaveCount(0);
  await expect(owner(page)).not.toContainText('Generation:'); await expect(owner(page)).not.toContainText('Target revision:');
});
test('job inventory remains truthful when native requested times predate the evidence window', async ({ page }) => {
  await mount(page, true); await expect(owner(page)).toContainText('Inventory is not filtered by the selected time range');
  await expect(owner(page)).toContainText('Records are not filtered by time'); await expect(owner(page)).toContainText('09/01/2026');
  await next(page).click(); await receipt(page, 1, 2); const item = contexts.get(page).requests.at(-1);
  expect(item.fromUtc).toBe(item.continuation.fromUtc); expect(item.toUtc).toBe(item.continuation.toUtc);
});
test('native diagnostic metadata renders inertly and unknown evidence confidence stays unavailable', async ({ page }) => {
  await mount(page); await select(page, 'diagnostics/search'); await receipt(page, 1);
  const details = owner(page).locator('details').first(); await details.getByText('Evidence details', { exact: true }).click();
  expect(JSON.parse(await details.locator('pre').textContent()).description).toBe(malicious);
  await expect(owner(page).locator('img,script')).toHaveCount(0);
  expect(await page.evaluate(() => window.analyticsInjected)).toBeUndefined(); await select(page, 'evidence-packets'); await receipt(page, 1);
  await expect(owner(page).getByRole('table').locator('tbody tr').first()).toContainText('Unavailable');
  await expect(owner(page)).not.toContainText('Evidence confidence is low');
});
test('the bounded browser investigation limit retains its final page and makes no further unread request', async ({ page }) => {
  const context = contexts.get(page); context.total = 2100; await mount(page);
  for (let number = 2; number <= 20; number++) { await next(page).click(); await receipt(page, 1, number); }
  const count = context.requests.length; await expect(next(page)).toBeDisabled();
  await expect(owner(page)).toContainText('20'); expect(context.requests).toHaveLength(count);
  await button(page, 'Previous evidence page').click(); await receipt(page, 1, 19); await next(page).click(); await receipt(page, 1, 20);
  expect(context.requests).toHaveLength(count); await latest(page).click(); await receipt(page, 1, 1);
});
for (const width of [320, 390, 768, 1440]) test(`retained analytics is dark, accessible, keyboard operable and locally bounded at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1100 }); await mount(page); await next(page).click(); await receipt(page, 1, 2);
  const context = contexts.get(page); context.handler = async route => { await json(route, { detail: secret }, 503); return true; };
  await latest(page).click(); await expect(retry(page)).toBeEnabled();
  await page.keyboard.press('Tab'); await retry(page).focus(); await expect(retry(page)).toBeFocused();
  expect(await retry(page).evaluate(element => parseFloat(getComputedStyle(element).outlineWidth))).toBeGreaterThanOrEqual(3);
  const scroll = owner(page).getByRole('region', { name: `${labels.jobs} scrollable evidence`, exact: true });
  await scroll.focus(); await page.keyboard.press('ArrowDown');
  await expect.poll(() => scroll.evaluate(element => element.scrollTop)).toBeGreaterThan(0);
  expect(await scroll.evaluate(element => parseFloat(getComputedStyle(element).outlineWidth))).toBeGreaterThanOrEqual(3);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await owner(page).evaluate(element => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(1);
  const bounds = await owner(page).boundingBox();
  for (const control of await owner(page).locator('button').all()) { const box = await control.boundingBox(); expect(box.x).toBeGreaterThanOrEqual(bounds.x - 1); expect(box.x + box.width).toBeLessThanOrEqual(bounds.x + bounds.width + 1); }
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`analytics-workflow-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  await scroll.evaluate(element => { element.scrollTop = 0; element.scrollLeft = 0; });
  await page.evaluate(() => { if (document.activeElement instanceof HTMLElement) document.activeElement.blur(); window.scrollTo({ top: 0, left: 0, behavior: 'instant' }); });
  await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-analytics-workflow/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
  await owner(page).screenshot({ path: `${directory}/analytics-workflow-${width}.png` });
});
