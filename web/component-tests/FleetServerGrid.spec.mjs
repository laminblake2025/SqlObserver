import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const origin = 'http://127.0.0.1:4328';
const alpha = '11111111-1111-4111-8111-000000000001';
const beta = '11111111-1111-4111-8111-000000000002';
const longName = `SYNTHETIC-${'LongServerName'.repeat(30)}`;
const contexts = new WeakMap();
const form = page => page.getByRole('form', { name: 'Server grouping labels', exact: true });
const environment = page => form(page).getByRole('textbox', { name: 'Server environment', exact: true });
const tags = page => form(page).getByRole('textbox', { name: 'Server tags', exact: true });
const save = page => form(page).getByRole('button', { name: 'Save labels', exact: true });
const callbacks = page => page.getByRole('status', { name: 'Grouping refresh callbacks', exact: true });
const edit = (page, name = 'SYNTHETIC Alpha') => page.getByRole('button', { name: `Edit grouping for ${name}`, exact: true });
const reload = (page, name = 'SYNTHETIC Alpha') => page.getByRole('button', { name: `Reload server labels for ${name}`, exact: true });
async function json(route, value, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
}
function receipt(item) {
  return { instanceId: item.target, environment: item.body.environment?.toLowerCase() ?? null,
    tags: item.body.tags.map(value => value.toLowerCase()).sort(), groupingRevision: item.body.expectedRevision + 1 };
}
function held(status = 200) {
  let announce, release, complete;
  const started = new Promise(resolve => { announce = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  const finished = new Promise(resolve => { complete = resolve; });
  return { started, async finish() { release(); await finished; }, async handle(route, item) {
    announce(route.request()); await gate;
    try {
      await json(route, status === 200 ? receipt(item) : { detail: 'SECRET_LATE_PROVIDER <script>window.injected=true</script>' }, status);
    } catch (failure) {
      if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw failure;
    } finally { complete(); }
    return true;
  } };
}

test.beforeEach(async ({ page }) => {
  const context = { mutations: [], unexpected: [], handler: undefined, revisions: new Map([[alpha, 1], [beta, 4]]) };
  contexts.set(page, context);
  page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async () => { context.unexpected.push(`${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/.test(url.pathname)) return route.continue();
    const match = /^\/api\/v1\/observation-targets\/([^/]+)\/grouping$/.exec(url.pathname);
    if (!match || !context.revisions.has(match[1]) || request.method() !== 'PUT' || url.search) return reject();
    const body = request.postDataJSON();
    if (Object.keys(body).sort().join('|') !== 'environment|expectedRevision|tags' ||
        body.expectedRevision !== context.revisions.get(match[1]) ||
        !(body.environment === null || typeof body.environment === 'string') || !Array.isArray(body.tags)) return reject();
    const item = { target: match[1], body }; context.mutations.push(item);
    if (context.handler && await context.handler(route, item)) return;
    await json(route, receipt(item));
  });
});
test.afterEach(async ({ page }) => {
  expect(contexts.get(page).unexpected, 'All native mutations are local; no unexpected APIs, external requests or runtime errors').toEqual([]);
});
async function mount(page) {
  await page.goto('/component-tests/fleet-grid.html');
  await expect(page.getByRole('heading', { name: 'Server fleet', exact: true })).toBeVisible();
  await expect(edit(page)).toBeEnabled();
}
async function open(page, name) {
  await edit(page, name).click(); await expect(form(page)).toBeVisible();
}
async function startHeld(page, status = 200) {
  const pending = held(status); contexts.get(page).handler = pending.handle;
  await mount(page); await open(page); await environment(page).fill('east-prod');
  await save(page).click(); const request = await pending.started;
  await expect(form(page).getByRole('button', { name: 'Saving…', exact: true })).toBeDisabled();
  return { pending, request };
}
async function cancelRequest(page, request, change) {
  const aborted = page.waitForEvent('requestfailed', { predicate: value => value === request });
  await change(); await aborted;
}

test('native grouping save works with Enter and normalizes the accepted labels', async ({ page }) => {
  await mount(page); await open(page);
  await environment(page).fill('  EAST-PROD  '); await tags(page).fill('West, critical');
  await tags(page).focus(); await page.keyboard.press('Enter');
  await expect(form(page)).toHaveCount(0); await expect(callbacks(page)).toHaveText('1');
  expect(contexts.get(page).mutations).toEqual([{ target: alpha,
    body: { expectedRevision: 1, environment: 'EAST-PROD', tags: ['West', 'critical'] } }]);
});

test('an abandoned Alpha receipt cannot close or replace Beta’s newer pending editor', async ({ page }) => {
  const first = await startHeld(page), context = contexts.get(page);
  await cancelRequest(page, first.request, () => edit(page, 'SYNTHETIC Beta').click());
  await expect(form(page)).toContainText('SYNTHETIC Beta'); await environment(page).fill('beta-draft');
  const next = held(); context.handler = next.handle;
  await save(page).click(); await next.started;
  await first.pending.finish();
  await expect(form(page)).toContainText('SYNTHETIC Beta'); await expect(environment(page)).toHaveValue('beta-draft');
  await expect(form(page).getByRole('button', { name: 'Saving…', exact: true })).toBeDisabled();
  await expect(callbacks(page)).toHaveText('0'); await expect(form(page).getByRole('alert')).toHaveCount(0);
  await next.finish(); await expect(form(page)).toHaveCount(0); await expect(callbacks(page)).toHaveText('1');
  expect(context.mutations.map(item => [item.target, item.body.expectedRevision])).toEqual([[alpha, 1], [beta, 4]]);
});

test('unmount aborts a save and refuses its late error or refresh callback', async ({ page }) => {
  const first = await startHeld(page, 503);
  await cancelRequest(page, first.request, () => page.getByRole('button', { name: 'Unmount server grid', exact: true }).click());
  await first.pending.finish(); await expect(form(page)).toHaveCount(0); await expect(callbacks(page)).toHaveText('0');
  await page.getByRole('button', { name: 'Mount server grid', exact: true }).click();
  await open(page); await expect(environment(page)).toHaveValue('production'); await expect(form(page).getByRole('alert')).toHaveCount(0);
});

test('rapid repeated Enter submissions issue only one native mutation', async ({ page }) => {
  const pending = held(); contexts.get(page).handler = pending.handle;
  await mount(page); await open(page); await environment(page).fill('east-prod');
  await environment(page).focus(); await page.keyboard.press('Enter'); await pending.started;
  await page.keyboard.press('Enter'); await page.keyboard.press('Enter');
  await expect(form(page).getByRole('button', { name: 'Saving…', exact: true })).toBeDisabled();
  expect(contexts.get(page).mutations).toHaveLength(1);
  await pending.finish(); await expect(callbacks(page)).toHaveText('1');
});

test('a routine same-revision overview refresh preserves the unfinished draft', async ({ page }) => {
  await mount(page); await open(page); await environment(page).fill('draft-east'); await tags(page).fill('draft, investigation');
  await page.getByRole('button', { name: 'Refresh same revision', exact: true }).click();
  await expect(environment(page)).toHaveValue('draft-east'); await expect(tags(page)).toHaveValue('draft, investigation');
  await save(page).click(); await expect(callbacks(page)).toHaveText('1');
  expect(contexts.get(page).mutations[0].body).toEqual({ expectedRevision: 1, environment: 'draft-east', tags: ['draft', 'investigation'] });
});

test('a newer native grouping revision cancels the old attempt and requires an explicit fresh editor', async ({ page }) => {
  const first = await startHeld(page), context = contexts.get(page); context.revisions.set(alpha, 2);
  await cancelRequest(page, first.request, () => page.getByRole('button', { name: 'Publish Alpha revision 2', exact: true }).click());
  await first.pending.finish(); await expect(form(page)).toHaveCount(0); await expect(callbacks(page)).toHaveText('0');
  await expect(page.getByRole('status').filter({ hasText: /^Server labels changed\./ })).toContainText('Open Edit grouping');
  await expect(edit(page)).toBeDisabled(); await reload(page).click(); await expect(callbacks(page)).toHaveText('1');
  await expect(page.getByRole('status').filter({ hasText: /^The draft was discarded\./ })).toContainText('open Edit grouping after the refreshed overview arrives');
  await open(page); await expect(environment(page)).toHaveValue('remote-east'); await expect(tags(page)).toHaveValue('remote');
  context.handler = undefined; await environment(page).fill('confirmed-revision'); await save(page).click();
  await expect(callbacks(page)).toHaveText('2'); expect(context.mutations[1].body.expectedRevision).toBe(2);
});

test('conflict locks saving until explicit readback, and a fresh editor uses the new revision', async ({ page }) => {
  const context = contexts.get(page); context.handler = async route => {
    await json(route, { detail: 'SECRET_CONFLICT <img src=https://invalid.example>' }, 409); return true;
  };
  await mount(page); await open(page); await environment(page).fill('old-draft'); await save(page).click();
  await expect(form(page).getByRole('alert')).toContainText('changed elsewhere'); await expect(save(page)).toBeDisabled();
  await expect(form(page)).not.toContainText('SECRET_CONFLICT');
  await form(page).getByRole('button', { name: 'Reload server labels', exact: true }).click();
  await expect(form(page)).toHaveCount(0); await expect(callbacks(page)).toHaveText('1');
  context.revisions.set(alpha, 2); context.handler = undefined;
  await page.getByRole('button', { name: 'Publish Alpha revision 2', exact: true }).click();
  await open(page); await expect(environment(page)).toHaveValue('remote-east'); await save(page).click();
  await expect(callbacks(page)).toHaveText('2'); expect(context.mutations.map(item => item.body.expectedRevision)).toEqual([1, 2]);
});

test('permission removal aborts pending changes, and regrant does not resurrect a draft', async ({ page }) => {
  const first = await startHeld(page, 503);
  await cancelRequest(page, first.request, () => page.getByRole('button', { name: 'Remove Alpha authority', exact: true }).click());
  await first.pending.finish(); await expect(form(page)).toHaveCount(0); await expect(edit(page)).toHaveCount(0);
  await expect(callbacks(page)).toHaveText('0');
  await page.getByRole('button', { name: 'Restore Alpha authority', exact: true }).click();
  await expect(form(page)).toHaveCount(0); await expect(edit(page)).toBeDisabled(); await reload(page).click();
  await expect(callbacks(page)).toHaveText('1'); await expect(edit(page)).toBeEnabled();
  await open(page); await expect(environment(page)).toHaveValue('production'); await expect(form(page).getByRole('alert')).toHaveCount(0);
});

test('pending Cancel is usable, aborts the request and requires explicit target readback', async ({ page }) => {
  const first = await startHeld(page);
  const cancel = form(page).getByRole('button', { name: 'Cancel', exact: true }); await expect(cancel).toBeEnabled();
  await cancelRequest(page, first.request, () => cancel.click()); await first.pending.finish();
  await expect(form(page)).toHaveCount(0); await expect(callbacks(page)).toHaveText('0'); await expect(edit(page)).toBeDisabled();
  await expect(reload(page)).toBeEnabled(); await reload(page).focus(); await page.keyboard.press('Enter');
  await expect(callbacks(page)).toHaveText('1'); await expect(edit(page)).toBeEnabled();
  await open(page); await expect(environment(page)).toHaveValue('production');
});

test('an indeterminate save failure refuses a blind retry and keeps provider text inert', async ({ page }) => {
  const context = contexts.get(page); context.handler = async route => {
    await json(route, { detail: 'SECRET_PROVIDER <script>window.injected=true</script>' }, 503); return true;
  };
  await mount(page); await open(page); await save(page).click();
  await expect(form(page).getByRole('alert')).toContainText('outcome is unknown'); await expect(save(page)).toBeDisabled();
  await expect(form(page)).not.toContainText('SECRET_PROVIDER'); expect(context.mutations).toHaveLength(1);
  expect(await page.evaluate(() => window.injected)).toBeUndefined();
  await form(page).getByRole('button', { name: 'Reload server labels', exact: true }).click();
  await expect(form(page)).toHaveCount(0); await expect(callbacks(page)).toHaveText('1');
});

for (const width of [320, 390, 768, 1440]) test(`long grouping labels support keyboard, dark layout and accessible scrolling at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 900 }); await mount(page);
  await page.getByRole('button', { name: 'Use long native labels', exact: true }).click(); await open(page, longName);
  const editor = form(page), bounds = await editor.boundingBox();
  await expect(environment(page)).toHaveValue('e'.repeat(64)); await expect(tags(page)).toHaveValue(`${'t'.repeat(64)}, ${'x'.repeat(64)}`);
  for (const control of [environment(page), tags(page), save(page), editor.getByRole('button', { name: 'Cancel', exact: true })]) {
    const box = await control.boundingBox(); expect(box.height).toBeGreaterThanOrEqual(40);
    expect(box.x).toBeGreaterThanOrEqual(bounds.x - 1); expect(box.x + box.width).toBeLessThanOrEqual(bounds.x + bounds.width + 1);
  }
  for (const control of [environment(page), tags(page)]) expect((await control.boundingBox()).width).toBeGreaterThanOrEqual(190);
  await environment(page).focus(); await page.keyboard.press('Tab'); await expect(tags(page)).toBeFocused();
  const focus = await tags(page).evaluate(element => ({ style: getComputedStyle(element).outlineStyle, width: parseFloat(getComputedStyle(element).outlineWidth) }));
  expect(focus.style).not.toBe('none'); expect(focus.width).toBeGreaterThanOrEqual(2);
  await page.keyboard.press('Tab'); await expect(save(page)).toBeFocused(); await page.keyboard.press('Tab');
  const cancel = editor.getByRole('button', { name: 'Cancel', exact: true }); await expect(cancel).toBeFocused();
  const region = page.getByRole('region', { name: 'Server health grid', exact: true });
  await expect(region).toHaveAttribute('tabindex', '0'); await region.focus(); await page.keyboard.press('ArrowRight');
  await expect.poll(() => region.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`fleet-grouping-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-fleet-timeline/screenshots/', import.meta.url));
  await mkdir(directory, { recursive: true }); await region.evaluate(element => { element.scrollLeft = 0; element.blur(); });
  await page.evaluate(() => window.scrollTo({ top: 0, left: 0, behavior: 'instant' }));
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
  await page.screenshot({ path: `${directory}/fleet-grouping-${width}.png`, fullPage: true });
  await cancel.focus(); await page.keyboard.press('Enter'); await expect(form(page)).toHaveCount(0);
  expect(contexts.get(page).mutations).toEqual([]);
});
