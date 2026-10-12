import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const origin = 'http://127.0.0.1:4328';
const target = '11111111-1111-4111-8111-000000000001';
const from = '2026-10-02T10:00:00.123456Z', to = '2026-10-02T11:00:00.123456Z';
const fingerprint = 'a'.repeat(64), run = '99999999-9999-4999-8999-000000000001';
const contexts = new WeakMap();
const dialog = page => page.getByRole('dialog', { name: 'Jump to evidence', exact: true });
const queryResult = page => dialog(page).getByRole('button', { name: /^Open query history/ });
const parameters = page => new URLSearchParams(new URL(page.url()).hash.split('?')[1]);

function server(index) {
  return { instanceId: index === 1 ? target : `11111111-1111-4111-8111-${String(index).padStart(12, '0')}`,
    instanceKey: `synthetic-${String(index).padStart(2, '0')}`, displayName: `SYNTHETIC ${String(index).padStart(2, '0')} keyboard server`,
    host: `synthetic-${index}.invalid`, tcpPort: 1433, authenticationMode: 'windows_integrated_service_identity',
    encryptionMode: 'mandatory_validated', lifecycle: 'active', capabilityStatus: 'supported', capabilityReasons: [],
    configurationRevision: 1, discoveryRequestedAtUtc: from, lastDiscoveryAtUtc: from };
}
function nativeResponse(url) {
  const entries = [...url.searchParams.entries()];
  if (new Set(entries.map(([name]) => name)).size !== entries.length) return;
  if (url.pathname === '/api/v1/observation-targets' && entries.length === 1 && url.searchParams.get('limit') === '100')
    return { kind: 'servers', value: { items: Array.from({ length: 18 }, (_, index) => server(index + 1)), nextCursor: null } };
  if (url.pathname === `/api/v1/observation-targets/${target}/query-performance/status` && entries.length === 0)
    return { kind: 'catalog', value: { targetId: target, snapshotUtc: to, source: 'query_store', sourceState: 'read_write',
      coverage: 'complete', fresh: true, truncated: false, contentAvailable: false, databaseStatuses: [],
      databaseCatalog: [{ databaseId: 5, databaseName: 'SYNTHETIC Orders' }] } };
  if (url.pathname === `/api/v1/observation-targets/${target}/query-performance/databases/5/history/${fingerprint}` && entries.length === 3
    && url.searchParams.get('fromUtc') === from && url.searchParams.get('toUtc') === to && url.searchParams.get('limit') === '200')
    return { kind: 'history', value: { targetId: target, databaseId: 5, queryFingerprint: fingerprint, snapshotUtc: to,
      fromUtc: from, toUtc: to, nextCursor: null, items: [{ databaseId: 5, queryFingerprint: fingerprint, planFingerprint: null,
        source: 'query_store', sourceState: 'read_write', semantics: 'query_store_interval', cpuMilliseconds: 25, durationMilliseconds: 50,
        executions: 1, intervalStartUtc: from, intervalEndUtc: to, coverage: 'complete', fresh: true, truncated: false,
        contentAvailable: false, collectionRunId: run, observationKey: 'b'.repeat(32) }] } };
}

test.beforeEach(async ({ page }) => {
  const context = { reads: [], unexpected: [] }; contexts.set(page, context);
  page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.clock.setFixedTime(new Date('2026-10-02T12:00:00Z'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    if (url.origin === origin && !/^\/api(?:\/|$)/u.test(url.pathname)) return route.continue();
    const response = url.origin === origin && request.method() === 'GET' ? nativeResponse(url) : undefined;
    if (!response) { context.unexpected.push(`${request.method()} ${url.href}`); return route.abort(); }
    context.reads.push({ kind: response.kind, url: url.href, itemCount: response.value.items?.length });
    await route.fulfill({ status: 200, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, json: response.value });
  });
});
test.afterEach(async ({ page }) => expect(contexts.get(page).unexpected, 'Only fixed native-shaped synthetic GETs; no external traffic or runtime errors').toEqual([]));

async function open(page) {
  await page.goto('/component-tests/command-palette.html');
  await page.getByRole('button', { name: 'Open evidence search', exact: true }).focus();
  await page.keyboard.press('Control+k'); await expect(dialog(page)).toBeVisible();
}
async function verifiedQuery(page) {
  await dialog(page).getByRole('button', { name: 'Query hash', exact: true }).click();
  await expect(dialog(page).getByRole('spinbutton', { name: 'Database ID', exact: true })).toHaveValue('5');
  const search = dialog(page).getByRole('searchbox', { name: 'Enter query hash', exact: true });
  await search.fill(fingerprint); await expect(queryResult(page)).toBeVisible();
  await expect(dialog(page).getByRole('status')).toContainText('Matching query history found.');
  return search;
}

test('verified query uses ArrowDown and ArrowUp to return to search before Enter navigates the exact shared scope', async ({ page }) => {
  await open(page); const search = await verifiedQuery(page), before = page.url();
  await search.focus(); await search.press('ArrowDown'); await expect(queryResult(page)).toBeFocused();
  await page.keyboard.press('ArrowUp'); await expect(search).toBeFocused(); expect(page.url()).toBe(before);
  await search.press('ArrowDown'); await expect(queryResult(page)).toBeFocused();
  await page.keyboard.press('ArrowDown'); await expect(queryResult(page)).toBeFocused(); expect(page.url()).toBe(before);
  await page.keyboard.press('Enter'); await expect(dialog(page)).not.toBeVisible();
  expect(new URL(page.url()).hash.split('?')[0]).toBe('#/queries');
  expect(Object.fromEntries(parameters(page))).toMatchObject({ target, databaseId: '5', range: 'custom', from, to, compare: '1', queryFingerprint: fingerprint });
  expect(contexts.get(page).reads.filter(read => read.kind === 'history')).toHaveLength(1);
});

test('Escape from a verified query result closes search and returns focus without navigating', async ({ page }) => {
  await open(page); const search = await verifiedQuery(page), before = page.url();
  await search.focus(); await search.press('ArrowDown'); await expect(queryResult(page)).toBeFocused();
  await page.keyboard.press('Escape'); await expect(dialog(page)).not.toBeVisible();
  await expect(page.getByRole('button', { name: 'Open evidence search', exact: true })).toBeFocused();
  expect(page.url()).toBe(before);
});

async function paintedFocus(result) {
  await expect(result).toBeFocused();
  const geometry = await result.evaluate(element => {
    const style = getComputedStyle(element), box = element.getBoundingClientRect();
    const region = element.closest('.command-palette-results'), parent = region.getBoundingClientRect();
    const width = parseFloat(style.outlineWidth), offset = parseFloat(style.outlineOffset);
    const reach = width + offset;
    return { outlineStyle: style.outlineStyle, width, offset, color: style.outlineColor, fill: style.backgroundColor,
      painted: { left: box.left - reach, right: box.right + reach, top: box.top - reach, bottom: box.bottom + reach },
      viewport: { left: parent.left + region.clientLeft, top: parent.top + region.clientTop,
        right: parent.left + region.clientLeft + region.clientWidth, bottom: parent.top + region.clientTop + region.clientHeight },
      scrollTop: region.scrollTop, scrollHeight: region.scrollHeight, clientHeight: region.clientHeight };
  });
  expect(geometry.outlineStyle).not.toBe('none'); expect(geometry.width).toBeGreaterThanOrEqual(2);
  expect(geometry.color).not.toMatch(/transparent|rgba\([^)]*,\s*0\)$/u);
  expect(geometry.color).not.toBe(geometry.fill);
  expect(geometry.painted.left).toBeGreaterThanOrEqual(geometry.viewport.left);
  expect(geometry.painted.right).toBeLessThanOrEqual(geometry.viewport.right);
  expect(geometry.painted.top).toBeGreaterThanOrEqual(geometry.viewport.top);
  expect(geometry.painted.bottom).toBeLessThanOrEqual(geometry.viewport.bottom);
  return geometry;
}

for (const width of [320, 390, 768, 1440]) for (const forced of [false, true])
  test(`first and last keyboard results have unclipped ${forced ? 'forced-color' : 'dark'} focus at ${width}px`, async ({ page }, info) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.emulateMedia({ forcedColors: forced ? 'active' : 'none', colorScheme: 'dark' });
    await open(page);
    const search = dialog(page).getByRole('searchbox', { name: 'Search servers', exact: true });
    const results = dialog(page).getByRole('group', { name: 'Matching servers', exact: true }).getByRole('button');
    await expect(results).toHaveCount(10); await search.focus(); await search.press('ArrowDown');
    expect(contexts.get(page).reads.filter(read => read.kind === 'servers').map(read => read.itemCount)).toEqual([18]);
    const directory = fileURLToPath(new URL('../../artifacts/dashboard-command-palette-keyboard-2026-10-04/screenshots/', import.meta.url));
    await mkdir(directory, { recursive: true });
    const first = await paintedFocus(results.first()); expect(first.scrollTop).toBe(0);
    await page.screenshot({ path: `${directory}/palette-${forced ? 'forced' : 'dark'}-${width}-first.png`, fullPage: true });
    for (let index = 1; index < 10; index++) await page.keyboard.press('ArrowDown');
    await expect(results.last()).toBeFocused();
    const last = await paintedFocus(results.last()); expect(last.scrollTop).toBeGreaterThan(0);
    expect(last.scrollTop + last.clientHeight).toBeGreaterThanOrEqual(last.scrollHeight - 1);
    for (const control of await dialog(page).locator('button,input:not([type="hidden"])').all()) {
      if (!(await control.isVisible())) continue;
      const box = await control.boundingBox(); expect(box.height).toBeGreaterThanOrEqual(44);
    }
    expect(await dialog(page).evaluate(element => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(1);
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
    if (!forced) {
      expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
      const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations;
      await info.attach(`query-keyboard-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
      expect(violations).toEqual([]);
    }
    await info.attach('painted-focus-geometry', { body: JSON.stringify({ source: 'Synthetic native GET fixtures; actual mounted keyboard focus', width, forced, first, last }), contentType: 'application/json' });
    await page.screenshot({ path: `${directory}/palette-${forced ? 'forced' : 'dark'}-${width}-last.png`, fullPage: true });
    await page.keyboard.press('ArrowDown'); await paintedFocus(results.first());
    await page.keyboard.press('ArrowUp'); await expect(search).toBeFocused();
    await page.keyboard.press('Escape'); await expect(dialog(page)).not.toBeVisible();
    await expect(page.getByRole('button', { name: 'Open evidence search', exact: true })).toBeFocused();
    expect(contexts.get(page).reads.filter(read => read.kind !== 'servers')).toEqual([]);
  });
