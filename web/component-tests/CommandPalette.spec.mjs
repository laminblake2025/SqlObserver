import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const origin = 'http://127.0.0.1:4328';
const alpha = '11111111-1111-4111-8111-000000000001', beta = '11111111-1111-4111-8111-000000000002';
const from = '2026-10-02T10:00:00.123456Z', to = '2026-10-02T11:00:00.123456Z';
const changedFrom = '2026-10-02T09:00:00.123456Z', changedTo = '2026-10-02T10:00:00.123456Z';
const cursor = 'c3ludGhldGljLWRhdGFiYXNlLXBhZ2Uy';
const fingerprint = 'a'.repeat(64), run = '99999999-9999-4999-8999-000000000001';
const alertId = 'aaaaaaaa-aaaa-4aaa-8aaa-000000000002';
const longName = 'SYNTHETIC_' + 'D'.repeat(118);
const inertName = '<img src="https://unexpected.invalid/database" onerror="window.paletteInjected=true">';
const contexts = new WeakMap();
const palette = page => page.getByRole('dialog', { name: 'Jump to evidence', exact: true });
const databaseResults = page => palette(page).getByRole('group', { name: 'Matching databases', exact: true });
const queryResult = page => palette(page).getByRole('button', { name: /^Open query history/ });
const params = page => new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
const reads = (page, kind) => contexts.get(page).requests.filter(item => item.kind === kind);
async function json(route, value, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
}
function target(id, name) {
  return { instanceId: id, instanceKey: name.toLowerCase(), displayName: `SYNTHETIC ${name}`, host: `${name.toLowerCase()}.invalid`, tcpPort: 1433,
    authenticationMode: 'windows_integrated_service_identity', encryptionMode: 'mandatory_validated', lifecycle: 'active', capabilityStatus: 'supported',
    capabilityReasons: [], configurationRevision: 1, discoveryRequestedAtUtc: from, lastDiscoveryAtUtc: from };
}
function collector() {
  return { collectorId: 'database.health', manifestVersion: 1, outputSchemaVersion: 1, state: 'current', reason: 'none', lastExecutionOutcome: 'succeeded',
    circuitState: 'closed', durationMilliseconds: 1, retryCount: 0, sourceRows: 25, outputRows: 25, insertedRows: 25, duplicateRows: 0, rejectedRows: 0,
    responseBytes: 5000, persistedBytes: 5000, sampleLossKind: null, minimumLostItems: null, lossCountIsExact: null, minimumLostBytes: null,
    hasVisibilityGap: false, scheduledAtUtc: from, lastAttemptAtUtc: from, lastSuccessAtUtc: from, nextDueAtUtc: to };
}
function database(id, name) {
  return { databaseId: id, name, state: 'online', recoveryModel: 'full', userAccess: 'multi_user', isReadOnly: false,
    compatibilityLevel: 160, logReuseWaitDescription: null, observedAtUtc: from, collector: collector() };
}
function nativeValue(item, context) {
  const name = item.id === beta ? 'Beta' : 'Alpha';
  if (item.kind === 'servers') return { items: [target(alpha, 'Alpha'), target(beta, 'Beta')], nextCursor: null };
  if (item.kind === 'alerts') return { snapshotUtc: to, nextCursor: null, items: [{ targetId: beta, targetName: 'SYNTHETIC Beta', alertId,
    ruleId: 'bbbbbbbb-bbbb-4bbb-8bbb-000000000001', ruleName: 'SYNTHETIC blocking alert', state: 'firing', firstObservedUtc: from, firedUtc: from,
    deliverySuppressed: false, value: 2, reason: null }] };
  if (item.kind === 'databases') {
    const firstName = context.long ? longName : context.inert ? inertName : `SYNTHETIC ${name} Orders`;
    const first = [database(5, firstName), database(6, `SYNTHETIC ${name} Warehouse`)];
    const items = context.emptyBeta && item.id === beta ? [] : item.cursor ? [database(30, `SYNTHETIC ${name} ledger`)]
      : context.paged ? [...first, ...Array.from({ length: 23 }, (_, index) => database(index + 7, `SYNTHETIC ${name} database ${index + 7}`))] : first;
    const summary = { ...collector(), sourceRows: items.length, outputRows: items.length, insertedRows: items.length };
    return { instanceId: item.id, repositoryTimeUtc: to, collector: summary, items: items.map(row => ({ ...row, collector: summary })),
      nextCursor: context.paged && !item.cursor && items.length ? cursor : null };
  }
  if (item.kind === 'catalog') return { targetId: item.id, snapshotUtc: to, source: 'query_store', sourceState: 'read_write', coverage: 'complete', fresh: true,
    truncated: false, contentAvailable: false, databaseStatuses: [], databaseCatalog: context.missingBeta && item.id === beta
      ? [{ databaseId: 6, databaseName: 'SYNTHETIC Beta Warehouse' }]
      : [{ databaseId: 5, databaseName: `SYNTHETIC ${name} Orders` }, { databaseId: 6, databaseName: `SYNTHETIC ${name} Warehouse` }] };
  if (item.kind === 'history') return { targetId: item.id, databaseId: item.databaseId, queryFingerprint: fingerprint, snapshotUtc: item.toUtc,
    fromUtc: item.fromUtc, toUtc: item.toUtc, nextCursor: null, items: [{ databaseId: item.databaseId, queryFingerprint: fingerprint, planFingerprint: null,
      source: 'query_store', sourceState: 'read_write', semantics: 'query_store_interval', cpuMilliseconds: 25, durationMilliseconds: 50, executions: 1,
      intervalStartUtc: item.fromUtc, intervalEndUtc: item.toUtc, coverage: 'complete', fresh: true, truncated: false, contentAvailable: false,
      collectionRunId: run, observationKey: 'b'.repeat(32) }] };
  throw new Error(`Unregistered palette DTO ${item.kind}`);
}
function registered(url) {
  const keys = [...url.searchParams.keys()];
  if (new Set(keys).size !== keys.length) return;
  if (url.pathname === '/api/v1/observation-targets' && keys.join('|') === 'limit' && url.searchParams.get('limit') === '100') return { kind: 'servers' };
  if (url.pathname === '/api/v1/alerts/active' && keys.join('|') === 'limit' && url.searchParams.get('limit') === '100') return { kind: 'alerts' };
  const match = /^\/api\/v1\/observation-targets\/([^/]+)\/(.+)$/.exec(url.pathname);
  if (!match || ![alpha, beta].includes(match[1])) return;
  const id = match[1], suffix = match[2];
  if (suffix === 'health/databases' && keys.every(key => ['limit', 'cursor'].includes(key)) && url.searchParams.get('limit') === '25'
    && (!url.searchParams.has('cursor') || url.searchParams.get('cursor') === cursor)) return { kind: 'databases', id, cursor: url.searchParams.get('cursor') };
  if (suffix === 'query-performance/status' && keys.length === 0) return { kind: 'catalog', id };
  const history = /^query-performance\/databases\/(5|6)\/history\/([a]{64})$/.exec(suffix);
  if (history && keys.length === 3 && keys.every(key => ['fromUtc', 'toUtc', 'limit'].includes(key)) && url.searchParams.get('limit') === '200') {
    const fromUtc = url.searchParams.get('fromUtc'), toUtc = url.searchParams.get('toUtc');
    if ([`${from}|${to}`, `${changedFrom}|${changedTo}`].includes(`${fromUtc}|${toUtc}`)) return { kind: 'history', id, databaseId: Number(history[1]), fromUtc, toUtc };
  }
}
function held(predicate, status = 200) {
  let announce, release, complete;
  const started = new Promise(resolve => { announce = resolve; }), gate = new Promise(resolve => { release = resolve; }), finished = new Promise(resolve => { complete = resolve; });
  return { started, async finish() { release(); await finished; }, async handle(route, item, context) {
    if (!predicate(item)) return false;
    const value = nativeValue(item, context); announce(route.request()); await gate;
    try { await json(route, status === 200 ? value : { detail: '<script>SECRET_LATE_DATABASE</script>' }, status); }
    catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
    finally { complete(); } return true;
  } };
}
test.beforeEach(async ({ page }) => {
  const context = { requests: [], unexpected: [], handler: undefined, paged: false, long: false, inert: false, emptyBeta: false, missingBeta: false };
  contexts.set(page, context);
  page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.clock.setFixedTime(new Date('2026-10-02T12:00:00Z'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async () => { context.unexpected.push(`${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/.test(url.pathname)) return route.continue();
    const item = registered(url); if (request.method() !== 'GET' || !item) return reject(); context.requests.push(item);
    if (context.handler && await context.handler(route, item, context)) return;
    await json(route, nativeValue(item, context));
  });
});
test.afterEach(async ({ page }) => expect(contexts.get(page).unexpected, 'Only finite native synthetic API reads; no external traffic or page errors').toEqual([]));
async function mount(page) { await page.goto('/component-tests/command-palette.html'); await page.getByRole('button', { name: 'Open evidence search', exact: true }).focus(); await page.keyboard.press('Control+k'); await expect(palette(page)).toBeVisible(); }
async function category(page, name) { await palette(page).getByRole('button', { name, exact: true }).click(); }
async function databases(page) { await category(page, 'Databases'); await expect(databaseResults(page).getByRole('button').first()).toBeVisible(); }
async function cancelled(page, request, action) { const aborted = page.waitForEvent('requestfailed', { predicate: value => value === request }); await action(); await aborted; }
function expectWindow(parameters, start = from, end = to) { expect(parameters.get('range')).toBe('custom'); expect(parameters.get('from')).toBe(start); expect(parameters.get('to')).toBe(end); expect(parameters.get('compare')).toBe('1'); }

test('Ctrl+K, arrow keys and Enter navigate an authorized server with the exact shared window; Escape restores focus', async ({ page }) => {
  await mount(page); const search = palette(page).getByRole('searchbox', { name: 'Search servers', exact: true }); await expect(search).toBeFocused();
  await expect(palette(page).getByRole('button', { name: /^SYNTHETIC Alpha / })).toBeVisible(); await page.keyboard.press('ArrowDown');
  await expect(palette(page).getByRole('button', { name: /^SYNTHETIC Alpha / })).toBeFocused(); await page.keyboard.press('ArrowDown');
  await expect(palette(page).getByRole('button', { name: /^SYNTHETIC Beta / })).toBeFocused(); await page.keyboard.press('Enter');
  await expect(palette(page)).not.toBeVisible(); expect(params(page).get('target')).toBe(beta); expectWindow(params(page));
  await page.keyboard.press('Control+k'); await expect(search).toBeFocused(); await page.keyboard.press('Escape');
  await expect(palette(page)).not.toBeVisible(); await expect(page.getByRole('button', { name: 'Open evidence search', exact: true })).toBeFocused();
});

test('database name or ID search and keyboard selection retain the originating server and exact scope', async ({ page }) => {
  await mount(page); await databases(page); const search = palette(page).getByRole('searchbox', { name: 'Search databases', exact: true });
  await search.fill('warehouse'); await expect(databaseResults(page).getByRole('button')).toHaveCount(1);
  await search.fill('6'); await expect(databaseResults(page)).toContainText('SYNTHETIC Alpha Warehouse'); await page.keyboard.press('ArrowDown');
  await page.keyboard.press('ArrowUp'); await expect(search).toBeFocused(); await page.keyboard.press('ArrowDown'); await page.keyboard.press('Enter');
  await expect(palette(page)).not.toBeVisible(); expect(params(page).get('target')).toBe(alpha); expect(params(page).get('databaseId')).toBe('6'); expectWindow(params(page));
});

test('the first changed-target commit hides old database rows and status before passive cleanup or new receipts', async ({ page }) => {
  await mount(page); await databases(page); const context = contexts.get(page), pending = held(item => item.kind === 'databases' && item.id === beta); context.handler = pending.handle;
  await page.keyboard.press('F2'); await pending.started;
  const commit = JSON.parse(await page.getByLabel('Search context commit', { exact: true }).textContent());
  expect(commit.results).toEqual([]); expect(commit.status.join(' ')).toContain('0 loaded'); expect(commit.status.join(' ')).not.toContain('2 databases searched');
  await expect(databaseResults(page).getByRole('button')).toHaveCount(0); const url = page.url(); await page.keyboard.press('ArrowDown'); await page.keyboard.press('Enter'); expect(page.url()).toBe(url);
  await pending.finish(); await expect(databaseResults(page)).toContainText('SYNTHETIC Beta Orders'); await expect(databaseResults(page)).not.toContainText('Alpha');
  await palette(page).getByRole('searchbox').focus(); await page.keyboard.press('ArrowDown'); await page.keyboard.press('Enter');
  expect(params(page).get('target')).toBe(beta); expect(params(page).get('databaseId')).toBe('5'); expectWindow(params(page));
});

test('held database continuation is aborted on a target change and cannot append old-target rows or overwrite status', async ({ page }) => {
  const context = contexts.get(page); context.paged = true; const pending = held(item => item.kind === 'databases' && item.id === alpha && item.cursor); context.handler = pending.handle;
  await mount(page); await databases(page); const request = await pending.started;
  await expect(palette(page)).toContainText('25 loaded'); await cancelled(page, request, () => page.keyboard.press('F2'));
  await expect(palette(page)).toContainText('26 databases searched'); await pending.finish(); await expect(databaseResults(page)).not.toContainText('Alpha');
  await palette(page).getByRole('searchbox').fill('ledger'); await expect(databaseResults(page).getByRole('button')).toHaveCount(1);
  await expect(databaseResults(page)).toContainText('SYNTHETIC Beta ledger');
  expect(reads(page, 'databases').map(item => [item.id, item.cursor])).toEqual([[alpha, null], [alpha, cursor], [beta, null], [beta, cursor]]);
});

test('category changes abort database paging and late errors cannot replace current alert status or exact navigation', async ({ page }) => {
  const context = contexts.get(page); context.paged = true; const pending = held(item => item.kind === 'databases' && Boolean(item.cursor), 503); context.handler = pending.handle;
  await mount(page); await databases(page); const request = await pending.started;
  await cancelled(page, request, () => category(page, 'Current alerts')); await pending.finish();
  await expect(palette(page)).toContainText('1 current alerts searched'); await expect(palette(page)).not.toContainText('incomplete'); await expect(palette(page)).not.toContainText('SECRET_LATE_DATABASE');
  await palette(page).getByRole('searchbox', { name: 'Search current alerts', exact: true }).focus(); await page.keyboard.press('ArrowDown'); await page.keyboard.press('Enter');
  expect(new URL(page.url()).hash.split('?')[0]).toBe('#/alerts'); expect(params(page).get('alertTarget')).toBe(beta); expect(params(page).get('alert')).toBe(alertId); expectWindow(params(page));
});

test('closing and reopening starts a new search session and refuses a delayed database response from the previous one', async ({ page }) => {
  const pending = held(item => item.kind === 'databases', 503); contexts.get(page).handler = pending.handle;
  await mount(page); await category(page, 'Databases'); const request = await pending.started;
  await cancelled(page, request, () => page.keyboard.press('Escape')); await page.keyboard.press('Control+k'); await expect(palette(page)).toBeVisible();
  await expect(palette(page).getByRole('searchbox', { name: 'Search servers', exact: true })).toBeFocused(); await pending.finish();
  await expect(palette(page)).toContainText('2 authorized servers searched'); await expect(palette(page)).not.toContainText('incomplete'); contexts.get(page).handler = undefined;
  await databases(page); await expect(databaseResults(page)).toContainText('SYNTHETIC Alpha Orders'); expect(reads(page, 'databases')).toHaveLength(2);
});

test('database failures are safe and bound to their target; empty and unselected-server contexts have truthful status', async ({ page }) => {
  const context = contexts.get(page); context.handler = async (route, item) => { if (item.kind !== 'databases') return false;
    await json(route, { detail: '<img src="https://unexpected.invalid/secret"> SECRET_PROVIDER' }, 503); return true; };
  await mount(page); await category(page, 'Databases'); await expect(palette(page)).toContainText("Database search is incomplete. Try the server's Databases tab.");
  await expect(palette(page)).not.toContainText('SECRET_PROVIDER'); context.emptyBeta = true;
  const pending = held(item => item.kind === 'databases' && item.id === beta); context.handler = pending.handle; await page.keyboard.press('F2'); await pending.started;
  const commit = JSON.parse(await page.getByLabel('Search context commit', { exact: true }).textContent()); expect(commit.status.join(' ')).not.toContain('incomplete');
  await pending.finish(); await expect(palette(page)).toContainText('0 databases searched'); await expect(databaseResults(page)).toContainText('No matching databases');
  const count = reads(page, 'databases').length; await page.keyboard.press('F3'); await expect(palette(page)).toContainText('Select a server first');
  await expect(databaseResults(page).getByRole('button')).toHaveCount(0); expect(reads(page, 'databases')).toHaveLength(count);
});

test('verified query navigation and safe status are fenced to the exact shared window while its replacement is pending', async ({ page }) => {
  await mount(page); await category(page, 'Query hash'); const hash = palette(page).getByRole('searchbox', { name: 'Enter query hash', exact: true });
  await expect(palette(page).getByRole('spinbutton', { name: 'Database ID', exact: true })).toHaveValue('5'); await hash.fill(fingerprint); await expect(queryResult(page)).toBeVisible();
  const pending = held(item => item.kind === 'history' && item.fromUtc === changedFrom); contexts.get(page).handler = pending.handle;
  await page.keyboard.press('F6'); await pending.started; await expect(queryResult(page)).toHaveCount(0);
  const commit = JSON.parse(await page.getByLabel('Search context commit', { exact: true }).textContent()); expect(commit.results).toEqual([]); expect(commit.status.join(' ')).not.toContain('Matching query history found');
  await expect(palette(page)).toContainText('Checking exact query history'); await pending.finish(); await expect(queryResult(page)).toBeVisible(); await queryResult(page).click();
  expect(new URL(page.url()).hash.split('?')[0]).toBe('#/queries'); expect(params(page).get('queryFingerprint')).toBe(fingerprint); expect(params(page).get('target')).toBe(alpha);
  expect(params(page).get('databaseId')).toBe('5'); expectWindow(params(page), changedFrom, changedTo);
  expect(reads(page, 'history').map(item => [item.fromUtc, item.toUtc])).toEqual([[from, to], [changedFrom, changedTo]]);
});

test('target and workspace-database changes invalidate held query verification and require the current catalog selection', async ({ page }) => {
  const context = contexts.get(page); context.missingBeta = true; const pending = held(item => item.kind === 'history' && item.id === alpha); context.handler = pending.handle;
  await mount(page); await category(page, 'Query hash'); const database = palette(page).getByRole('spinbutton', { name: 'Database ID', exact: true }); await expect(database).toHaveValue('5');
  await palette(page).getByRole('searchbox').fill(fingerprint); const request = await pending.started; await cancelled(page, request, () => page.keyboard.press('F2')); await pending.finish();
  await expect(database).toHaveValue(''); await expect(palette(page)).toContainText("Workspace database ID 5 is absent from this server's query database catalog"); await expect(queryResult(page)).toHaveCount(0);
  await page.keyboard.press('F4'); await expect(database).toHaveValue('6'); await palette(page).getByRole('searchbox').fill(fingerprint); await expect(queryResult(page)).toBeVisible();
  await queryResult(page).click(); expect(params(page).get('target')).toBe(beta); expect(params(page).get('databaseId')).toBe('6'); expectWindow(params(page));
  expect(reads(page, 'history').map(item => [item.id, item.databaseId])).toEqual([[alpha, 5], [beta, 6]]);
});

test('an unsupported query window refuses lookup before IO and unmount aborts a held native database read', async ({ page }) => {
  await mount(page); await category(page, 'Query hash'); await expect(palette(page).getByRole('spinbutton', { name: 'Database ID', exact: true })).toHaveValue('5');
  await page.keyboard.press('Shift+F6'); await palette(page).getByRole('searchbox').fill(fingerprint);
  await expect(palette(page)).toContainText('Choose a valid time range of up to seven days'); await expect(queryResult(page)).toHaveCount(0); expect(reads(page, 'history')).toEqual([]);
  const pending = held(item => item.kind === 'databases', 503); contexts.get(page).handler = pending.handle; await category(page, 'Databases'); const request = await pending.started;
  await cancelled(page, request, () => page.keyboard.press('F8')); await pending.finish(); await expect(palette(page)).toHaveCount(0);
  await page.keyboard.press('Escape'); await page.keyboard.press('F8'); await expect(palette(page)).toBeVisible(); await expect(palette(page)).not.toContainText('SECRET_LATE_DATABASE');
});

test('native database names containing provider markup remain inert text', async ({ page }) => {
  contexts.get(page).inert = true; await mount(page); await databases(page); await expect(databaseResults(page)).toContainText(inertName);
  await expect(databaseResults(page).locator('img,script')).toHaveCount(0); expect(await page.evaluate(() => window.paletteInjected)).toBeUndefined();
});

for (const width of [320, 390, 768, 1440]) test(`native long database names support dark keyboard search without overflow at ${width}px`, async ({ page }, info) => {
  contexts.get(page).long = true; await page.setViewportSize({ width, height: 900 }); await mount(page); await databases(page);
  const dialog = palette(page), search = dialog.getByRole('searchbox', { name: 'Search databases', exact: true }); await search.focus(); await page.keyboard.press('ArrowDown');
  const result = databaseResults(page).getByRole('button', { name: `${longName} Database ID 5`, exact: true }); await expect(result).toBeFocused();
  const focus = await result.evaluate(element => ({ style: getComputedStyle(element).outlineStyle, width: parseFloat(getComputedStyle(element).outlineWidth) }));
  expect(focus.style).not.toBe('none'); expect(focus.width).toBeGreaterThanOrEqual(2); await page.keyboard.press('ArrowUp'); await expect(search).toBeFocused();
  const bounds = await dialog.boundingBox(); expect(bounds.x).toBeGreaterThanOrEqual(0); expect(bounds.x + bounds.width).toBeLessThanOrEqual(width + 1);
  for (const control of [search, result, dialog.getByRole('button', { name: 'Close search', exact: true })]) {
    const box = await control.boundingBox(); expect(box.height).toBeGreaterThanOrEqual(40); expect(box.x).toBeGreaterThanOrEqual(bounds.x);
    expect(box.x + box.width).toBeLessThanOrEqual(bounds.x + bounds.width + 1);
  }
  expect((await search.boundingBox()).width).toBeGreaterThanOrEqual(220);
  expect(await databaseResults(page).evaluate(element => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(1);
  expect(await dialog.evaluate(element => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`command-palette-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-context-search/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
  await search.evaluate(element => element.blur()); await expect(search).not.toBeFocused();
  await dialog.evaluate(element => { element.scrollTop = 0; });
  await databaseResults(page).evaluate(element => { element.scrollTop = 0; element.scrollLeft = 0; });
  await page.evaluate(() => window.scrollTo({ top: 0, left: 0, behavior: 'instant' })); await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
  await expect.poll(() => dialog.evaluate(element => element.scrollTop)).toBe(0); await page.screenshot({ path: `${directory}/command-palette-${width}.png`, fullPage: true });
  await page.keyboard.press('Escape'); await expect(dialog).not.toBeVisible(); await expect(page.getByRole('button', { name: 'Open evidence search', exact: true })).toBeFocused();
  expect(reads(page, 'history')).toEqual([]);
});
