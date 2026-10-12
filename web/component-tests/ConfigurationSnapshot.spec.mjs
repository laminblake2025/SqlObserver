import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const alpha = '11111111-1111-4111-8111-000000000001';
const beta = '11111111-1111-4111-8111-000000000002';
const contexts = new WeakMap();
const panel = page => page.getByRole('region', { name: 'Current server settings', exact: true });
const change = page => panel(page).getByRole('button', { name: 'Enable configuration collection', exact: true });
const collectionAction = page => panel(page).getByRole('button', { name: /^(?:Enable|Disable) configuration collection$/u });
const reload = page => panel(page).getByRole('button', { name: 'Reload current state', exact: true });
async function json(route, value, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
}
function snapshot(target, context) {
  return { targetId: target, targetRevision: target === alpha ? 1 : 4,
    runId: '22222222-2222-4222-8222-222222222222', observedAtUtc: '2026-10-02T12:00:00.123456Z', serverMajorVersion: 16,
    collectorEnabled: context.enabled.get(target), lastOutcome: 'succeeded', coverage: 'latest_complete_scan',
    items: [{ configurationId: 1, configurationName: `SYNTHETIC ${target === alpha ? 'Alpha' : 'Beta'} ${'maximum server memory '.repeat(6)}`, sourceType: 'int', configuredValue: '2048', inUseValue: '2048', isDynamic: true, isAdvanced: true }], nextCursor: null };
}
function applied(item) {
  return { targetId: item.target, targetRevision: item.body.expectedTargetRevision, collectorEnabled: item.body.enabled, scheduleRevision: 2 };
}
function held(context) {
  let announce, release, complete;
  const started = new Promise(resolve => { announce = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  const finished = new Promise(resolve => { complete = resolve; });
  return { started, async finish() { release(); await finished; }, async handle(route, item) {
    announce(route.request()); await gate;
    try { context.enabled.set(item.target, item.body.enabled); await json(route, applied(item)); }
    catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
    finally { complete(); }
    return true;
  } };
}
test.beforeEach(async ({ page }) => {
  const context = { unexpected: [], reads: [], accessReads: [], accessMode: 'administrator', mutations: [], enabled: new Map([[alpha, false], [beta, true]]), mutationHandler: undefined, readHandler: undefined };
  contexts.set(page, context); page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async () => { context.unexpected.push(`${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== 'http://127.0.0.1:4328') return reject();
    if (!/^\/api(?:\/|$)/u.test(url.pathname)) return route.continue();
    if (url.pathname === '/api/v1/me' && request.method() === 'GET') {
      const target = url.searchParams.get('targetId');
      if (!context.enabled.has(target) || url.search !== `?targetId=${target}`) return reject();
      context.accessReads.push(target);
      if (context.accessMode === 'failed') return json(route, { detail: 'SECRET_ACCESS_PROVIDER' }, 503);
      const roles = [context.accessMode === 'viewer' ? 'Viewer' : 'TargetAdministrator'];
      return json(route, { active: true, grantedRoles: roles, allTargetRoles: [],
        targetId: context.accessMode === 'foreign' ? beta : target, targetRoles: roles });
    }
    const match = /^\/api\/v1\/observation-targets\/([^/]+)\/health\/server-configuration-(snapshot|collection)$/u.exec(url.pathname);
    if (!match || !context.enabled.has(match[1])) return reject();
    if (match[2] === 'snapshot' && request.method() === 'GET' && url.search === '?limit=100') {
      context.reads.push(match[1]);
      if (context.readHandler && await context.readHandler(route, match[1])) return;
      return json(route, snapshot(match[1], context));
    }
    if (match[2] !== 'collection' || request.method() !== 'PUT' || url.search) return reject();
    const body = request.postDataJSON();
    if (Object.keys(body).sort().join('|') !== 'enabled|expectedEnabled|expectedTargetRevision' ||
      body.expectedTargetRevision !== (match[1] === alpha ? 1 : 4) || typeof body.expectedEnabled !== 'boolean' || typeof body.enabled !== 'boolean') return reject();
    const item = { target: match[1], body }; context.mutations.push(item);
    if (context.mutationHandler && await context.mutationHandler(route, item)) return;
    context.enabled.set(item.target, body.enabled); await json(route, applied(item));
  });
  await page.goto('/component-tests/configuration.html'); await expect(change(page)).toBeEnabled();
});
test.afterEach(async ({ page }) => {
  expect(contexts.get(page).unexpected, 'Only registered native local configuration reads/mutations are permitted').toEqual([]);
});
async function start(page) {
  const pending = held(contexts.get(page)); contexts.get(page).mutationHandler = pending.handle;
  await change(page).click(); const request = await pending.started;
  await expect(panel(page).getByRole('button', { name: 'Updating…', exact: true })).toBeDisabled();
  return { pending, request };
}
async function abortOn(page, request, action) {
  const aborted = page.waitForEvent('requestfailed', { predicate: value => value === request }); await action(); await aborted;
}
test('Enter dispatches exact native compare-and-set and reloads the accepted current state', async ({ page }) => {
  await change(page).focus(); await page.keyboard.press('Enter');
  await expect(panel(page).getByRole('button', { name: 'Disable configuration collection', exact: true })).toBeEnabled();
  expect(contexts.get(page).mutations).toEqual([{ target: alpha, body: { expectedTargetRevision: 1, expectedEnabled: false, enabled: true } }]);
  expect(contexts.get(page).reads).toEqual([alpha, alpha]);
});
test('held mutation rejects duplicate direct button dispatch and can be canceled without rollback claims', async ({ page }) => {
  const context = contexts.get(page), pending = held(context); context.mutationHandler = pending.handle;
  await change(page).evaluate(button => {
    button.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    button.dispatchEvent(new MouseEvent('click', { bubbles: true }));
  });
  const request = await pending.started;
  await expect(panel(page).getByRole('button', { name: 'Updating…', exact: true })).toBeDisabled();
  expect(contexts.get(page).mutations).toHaveLength(1);
  contexts.get(page).enabled.set(alpha, true);
  await abortOn(page, request, () => panel(page).getByRole('button', { name: 'Cancel configuration change' }).click());
  await pending.finish(); await expect(panel(page).getByRole('alert')).toContainText('does not roll back');
  await expect(change(page)).toBeDisabled(); expect(contexts.get(page).reads).toHaveLength(1);
  await reload(page).click(); await expect(panel(page).getByRole('button', { name: 'Disable configuration collection', exact: true })).toBeEnabled();
  expect(contexts.get(page).mutations).toHaveLength(1);
});
test('target switch fences an abandoned Alpha receipt and does not reload or mutate Beta', async ({ page }) => {
  const { pending, request } = await start(page);
  await abortOn(page, request, () => page.getByRole('combobox', { name: 'Target', exact: true }).selectOption(beta));
  await expect(panel(page).getByRole('button', { name: 'Disable configuration collection', exact: true })).toBeEnabled();
  await pending.finish(); expect(contexts.get(page).reads).toEqual([alpha, beta]);
  await expect(panel(page).getByRole('alert')).toHaveCount(0); expect(contexts.get(page).mutations).toHaveLength(1);
  await page.getByRole('combobox', { name: 'Target', exact: true }).selectOption(alpha);
  await expect(collectionAction(page)).toBeDisabled(); await expect(reload(page)).toBeEnabled();
});
test('authority loss aborts the request; regrant retains the readback lock rather than reviving old status', async ({ page }) => {
  const { pending, request } = await start(page);
  await abortOn(page, request, () => page.getByRole('button', { name: 'Remove configuration authority' }).click());
  await pending.finish(); await expect(change(page)).toHaveCount(0);
  await expect(panel(page)).toContainText('Target Administrator access on this server unlocks');
  await page.getByRole('button', { name: 'Restore configuration authority' }).click();
  await expect(collectionAction(page)).toBeDisabled(); await expect(reload(page)).toBeEnabled();
  await reload(page).click(); await expect(collectionAction(page)).toBeEnabled();
});
test('workspace refresh aborts a held mutation; automatic GET cannot clear uncertain recovery', async ({ page }) => {
  const { pending, request } = await start(page);
  await abortOn(page, request, () => page.getByRole('button', { name: 'Refresh workspace' }).click());
  await pending.finish(); await expect(collectionAction(page)).toBeDisabled();
  await expect(panel(page).getByRole('alert')).toContainText('outcome is unknown');
  expect(contexts.get(page).reads).toEqual([alpha, alpha]);
  await reload(page).click(); await expect(collectionAction(page)).toBeEnabled();
});
test('closing aborts a held mutation and reopening makes a fresh authoritative read', async ({ page }) => {
  const { pending, request } = await start(page);
  await abortOn(page, request, () => page.getByRole('button', { name: 'Close configuration panel' }).click());
  await pending.finish(); await expect(panel(page)).toHaveCount(0); expect(contexts.get(page).reads).toEqual([alpha]);
  await page.getByRole('button', { name: 'Open configuration panel' }).click();
  await expect(panel(page).getByRole('button', { name: 'Disable configuration collection', exact: true })).toBeEnabled(); expect(contexts.get(page).reads).toEqual([alpha, alpha]);
});
test('a held configuration GET cannot publish Alpha settings or clear Beta loading after target change', async ({ page }) => {
  const context = contexts.get(page); let announce, release, complete;
  const started = new Promise(resolve => { announce = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  const finished = new Promise(resolve => { complete = resolve; });
  context.readHandler = async (route, target) => {
    if (target !== alpha) return false;
    const original = snapshot(alpha, context); announce(route.request()); await gate;
    try { await json(route, original); }
    catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
    finally { complete(); }
    return true;
  };
  await page.getByRole('button', { name: 'Refresh workspace' }).click(); const request = await started;
  await abortOn(page, request, () => page.getByRole('combobox', { name: 'Target', exact: true }).selectOption(beta));
  await expect(panel(page)).toContainText('SYNTHETIC Beta'); await expect(panel(page)).not.toContainText('SYNTHETIC Alpha');
  release(); await finished;
  await expect(panel(page)).toContainText('SYNTHETIC Beta'); await expect(panel(page)).not.toContainText('SYNTHETIC Alpha');
  expect(context.reads).toEqual([alpha, alpha, beta]); expect(context.mutations).toHaveLength(0);
});
for (const status of [403, 409, 429, 503]) test(`native ${status} refuses another mutation until explicit current-state readback`, async ({ page }) => {
  const context = contexts.get(page); context.mutationHandler = async route => { await json(route, { detail: 'SECRET_PROVIDER <script>window.injected=true</script>' }, status); return true; };
  await change(page).click(); await expect(panel(page).getByRole('alert')).toContainText(/[Rr]eload current state/u);
  if (status === 403) await expect(change(page)).toHaveCount(0); else await expect(change(page)).toBeDisabled();
  await expect(panel(page)).not.toContainText('SECRET_PROVIDER'); expect(context.mutations).toHaveLength(1);
  await page.getByRole('button', { name: 'Refresh workspace' }).click(); await expect(reload(page)).toBeEnabled();
  if (status === 403) await expect(change(page)).toHaveCount(0); else await expect(change(page)).toBeDisabled();
  context.mutationHandler = undefined; await reload(page).click(); await expect(change(page)).toBeEnabled();
  await change(page).click(); await expect(panel(page).getByRole('button', { name: 'Disable configuration collection', exact: true })).toBeEnabled();
  expect(context.mutations[1].body).toEqual(context.mutations[0].body);
});
test('malformed accepted receipt and failed readback cannot release recovery or expose foreign state', async ({ page }) => {
  const context = contexts.get(page); context.mutationHandler = async (route, item) => { await json(route, { ...applied(item), targetId: beta }); return true; };
  await change(page).click(); await expect(panel(page).getByRole('alert')).toContainText('outcome is unknown');
  context.readHandler = async route => { await json(route, snapshot(beta, context)); return true; };
  await reload(page).click(); await expect(panel(page).getByRole('alert').first()).toContainText('Current settings are unavailable');
  await expect(change(page)).toHaveCount(0);
  context.readHandler = undefined; await reload(page).click(); await expect(change(page)).toBeEnabled();
});
test('rejected administration needs fresh exact-target authority plus valid settings, despite a stale true parent flag', async ({ page }) => {
  const context = contexts.get(page);
  context.mutationHandler = async route => { await json(route, {}, 403); return true; };
  await change(page).click(); await expect(panel(page).getByRole('alert')).toContainText('Target Administrator access');
  await expect(change(page)).toHaveCount(0); expect(context.accessReads).toEqual([]);
  await page.getByRole('button', { name: 'Refresh workspace' }).click(); await expect(reload(page)).toBeEnabled();
  expect(context.accessReads).toEqual([]);
  for (const mode of ['viewer', 'foreign', 'failed']) {
    context.accessMode = mode; const before = context.accessReads.length;
    await reload(page).click();
    await expect.poll(() => context.accessReads.length).toBe(before + 1);
    await expect(reload(page)).toBeEnabled(); await expect(change(page)).toHaveCount(0);
    await expect(panel(page).getByRole('alert')).toContainText('Target Administrator access');
  }
  context.accessMode = 'administrator'; context.readHandler = async route => { await json(route, {}, 503); return true; };
  await reload(page).click(); await expect(panel(page).getByRole('alert').first()).toContainText('Current settings are unavailable');
  await expect(change(page)).toHaveCount(0); expect(context.mutations).toHaveLength(1);
  context.readHandler = undefined; context.mutationHandler = undefined;
  await reload(page).click(); await expect(change(page)).toBeEnabled();
  expect(context.accessReads).toEqual([alpha, alpha, alpha, alpha, alpha]);
  await change(page).click(); await expect(panel(page).getByRole('button', { name: 'Disable configuration collection', exact: true })).toBeEnabled();
  expect(context.mutations).toHaveLength(2);
});
test('Rewind and display preference preserve latest-source truth and do not cancel a current collection change', async ({ page }) => {
  const { pending } = await start(page);
  await page.getByRole('button', { name: 'Rewind workspace' }).click();
  await page.getByRole('combobox', { name: 'Time display', exact: true }).selectOption('local');
  await expect(panel(page)).toContainText('follows the server now, even when the timeline is rewound');
  await expect(panel(page).getByRole('button', { name: 'Updating…', exact: true })).toBeDisabled();
  expect(contexts.get(page).reads).toEqual([alpha]);
  await pending.finish(); await expect(panel(page).getByRole('button', { name: 'Disable configuration collection', exact: true })).toBeEnabled();
});
for (const width of [320, 390, 768, 1440]) test(`configuration recovery is dark, keyboard accessible and bounded at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 });
  contexts.get(page).mutationHandler = async route => { await json(route, {}, 503); return true; };
  await change(page).focus(); await page.keyboard.press('Enter'); await expect(reload(page)).toBeEnabled();
  await reload(page).focus(); await expect(reload(page)).toBeFocused();
  expect(await reload(page).evaluate(element => getComputedStyle(element).outlineStyle)).not.toBe('none');
  const region = panel(page).getByRole('region', { name: 'Latest server configuration values', exact: true });
  await page.keyboard.press('Tab'); await region.focus(); await expect(region).toBeFocused();
  const focus = await region.evaluate(element => ({ style: getComputedStyle(element).outlineStyle, width: parseFloat(getComputedStyle(element).outlineWidth) }));
  expect(focus.style).not.toBe('none'); expect(focus.width).toBeGreaterThanOrEqual(2);
  const geometry = await region.evaluate(element => ({ scrollWidth: element.scrollWidth, clientWidth: element.clientWidth }));
  if (geometry.scrollWidth > geometry.clientWidth) {
    await page.keyboard.press('ArrowRight'); await expect.poll(() => region.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
  } else {
    expect(geometry.scrollWidth).toBeLessThanOrEqual(geometry.clientWidth);
    const tableBounds = await region.getByRole('table').boundingBox(), regionBounds = await region.boundingBox();
    expect(tableBounds.width).toBeLessThanOrEqual(regionBounds.width + 1);
    expect(tableBounds.x).toBeGreaterThanOrEqual(regionBounds.x - 1);
  }
  const bounds = await region.boundingBox(); expect(bounds.x).toBeGreaterThanOrEqual(0); expect(bounds.x + bounds.width).toBeLessThanOrEqual(width + 1);
  await expect(region.getByRole('cell', { name: '2048', exact: true })).toHaveCount(2);
  await expect(region.getByRole('cell', { name: 'int', exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`configuration-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-context-search/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
  await region.evaluate(element => { element.scrollLeft = 0; element.blur(); }); await page.evaluate(() => scrollTo({ top: 0, left: 0, behavior: 'instant' }));
  await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
  const path = `${directory}configuration-recovery-${width}.png`; await page.screenshot({ path, fullPage: true });
  await info.attach(`configuration-${width}`, { path, contentType: 'image/png' });
  await reload(page).focus(); await page.keyboard.press('Enter'); await expect(change(page)).toBeEnabled();
});
