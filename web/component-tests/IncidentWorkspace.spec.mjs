import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { target, otherTarget, thread, fromUtc, toUtc, nowUtc, diagnosticId, sourceRunId,
  tombstone, inertText, packetIds, incidentHref, savedIncident, summaryPage, incidentBackend } from './incidentFixture.mjs';

const origin = 'http://127.0.0.1:4328';
const contexts = new WeakMap();
const denial = 'Viewer, Operator or Target Administrator access is required to read and export this saved investigation.';
const view = page => page.getByRole('region', { name: /^SYNTHETIC (?:saved|other) investigation$/ });
const generate = page => view(page).getByRole('button', { name: 'Generate investigation report', exact: true });
const html = page => view(page).getByRole('link', { name: 'Open printable investigation', exact: true });
const raw = (page, index) => view(page).getByRole('region', { name: `Saved evidence packet ${packetIds[index]}`, exact: true });
const articles = page => view(page).getByRole('article');
async function json(route, value, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
}
function heldReport() {
  let start, release, complete;
  const started = new Promise(resolve => { start = resolve; }), gate = new Promise(resolve => { release = resolve; }),
    finished = new Promise(resolve => { complete = resolve; });
  return { started, async finish() { release(); await finished; }, async handle(route, item, context) {
    if (item.action !== 'report') return false;
    const receipt = context.backend.acceptReport(item.id, item.body); start(route.request()); await gate;
    try { await json(route, receipt); }
    catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
    finally { complete(); }
    return true;
  } };
}
test.beforeEach(async ({ page }) => {
  await page.clock.install({ time: new Date(nowUtc) });
  const context = { backend: incidentBackend(), requests: [], unexpected: [], handler: undefined };
  contexts.set(page, context);
  page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async () => { context.unexpected.push(`${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/.test(url.pathname)) return route.continue();
    const match = /^\/api\/v1\/observation-targets\/([^/]+)\/(incidents\/saved(?:\/([^/]+))?|reports)$/.exec(url.pathname);
    if (!match || ![target, otherTarget].includes(match[1])) return reject();
    const verb = request.method();
    const action = match[2] === 'reports' && verb === 'POST' && !url.search ? 'report'
      : match[3] && verb === 'GET' && !url.search ? 'read'
      : match[2] === 'incidents/saved' && verb === 'GET' && url.searchParams.get('limit') === '20'
        && [...url.searchParams.keys()].every(key => ['limit', 'cursor'].includes(key)) ? 'list'
      : match[2] === 'incidents/saved' && verb === 'POST' && !url.search ? 'save' : undefined;
    if (!action) return reject();
    const item = { action, id: match[1], thread: match[3], url,
      ...(['report', 'save'].includes(action) ? { body: request.postDataJSON() } : {}) };
    context.requests.push(item);
    if (action === 'report') {
      expect(item.body).toEqual({ reportKind: 'saved-investigation', operationId: expect.stringMatching(/^[0-9a-f-]{36}$/i), threadId: expect.any(String) });
      expect(context.backend.incidents.has(`${item.id}:${item.body.threadId}`)).toBe(true);
    }
    if (context.handler && await context.handler(route, item, context)) return;
    if (action === 'report') return json(route, context.backend.acceptReport(item.id, item.body));
    if (action === 'save') return json(route, context.backend.acceptSave(item.id, item.body));
    if (action === 'list') return json(route, summaryPage(item.id, item.url.searchParams.has('cursor')));
    const saved = context.backend.incidents.get(`${item.id}:${item.thread}`);
    return json(route, saved ?? {}, saved ? 200 : 404);
  });
});
test.afterEach(async ({ page }) => {
  expect(contexts.get(page).unexpected, 'Only registered local native APIs may run; no external traffic or runtime errors').toEqual([]);
});
async function mount(page, scenario = 'rich', hash = incidentHref()) {
  contexts.get(page).backend.incidents.set(`${target}:${thread}`, savedIncident(scenario));
  await page.goto(`/component-tests/incidents.html${hash}`);
  await expect(view(page)).toBeVisible();
}
async function openDisclosure(page, article, label, key = 'Enter') {
  const summary = article.getByText(label, { exact: true }); await summary.focus(); await page.keyboard.press(key);
  await expect(summary.locator('..')).toHaveAttribute('open', '');
}
const workspaceSummary = page => page.locator('summary').filter({ hasText: /^Investigation workspace$/ });
async function openWorkspace(page) {
  const summary = workspaceSummary(page);
  await summary.focus(); await page.keyboard.press('Enter');
  await expect(summary.locator('..')).toHaveAttribute('open', '');
}
async function expectExports(page, id, runId) {
  await expect(html(page)).toHaveAttribute('href', `/api/v1/observation-targets/${id}/reports/${runId}/html`);
  await expect(html(page)).toHaveAttribute('target', '_blank'); await expect(html(page)).toHaveAttribute('rel', 'noreferrer');
  await expect(view(page).getByRole('link', { name: 'Download saved details CSV', exact: true }))
    .toHaveAttribute('href', `/api/v1/observation-targets/${id}/reports/${runId}/investigation.csv`);
  await expect(view(page).getByRole('link', { name: 'Download saved evidence CSV', exact: true }))
    .toHaveAttribute('href', `/api/v1/observation-targets/${id}/reports/${runId}/saved-evidence.csv`);
}

test('native health references and full provenance show recorded identities without invented diagnostic facts or links', async ({ page }) => {
  await mount(page);
  await expect(articles(page)).toHaveCount(4);
  await expect(view(page).getByRole('region', { name: 'Evidence overview', exact: true }))
    .toContainText('Recorded packet coverage: 1 complete · 1 partial · 1 unavailable · 1 unsupported.');
  const complete = articles(page).nth(0), partial = articles(page).nth(1);
  await expect(complete).toContainText('Diagnostic event'); await expect(complete).toContainText(diagnosticId);
  await expect(complete).toContainText('Confidence 13%. This is a recorded score, not a probability of root cause.');
  await expect(complete.getByRole('link')).toHaveCount(0);
  await expect(complete).not.toContainText('CPU percentage');
  await openDisclosure(page, complete, 'Saved packet provenance');
  for (const label of ['Source run', 'Source cutoff']) {
    await expect(complete.getByText(label, { exact: true }).locator('..').locator('dd')).toHaveText('Not recorded');
  }
  for (const digest of ['a'.repeat(64), 'b'.repeat(64), 'c'.repeat(64)]) await expect(complete.locator('.saved-evidence-provenance')).toContainText(digest);
  await openDisclosure(page, partial, 'Saved packet provenance');
  await expect(partial.locator('.saved-evidence-provenance')).toContainText(sourceRunId);
  await expect(partial.locator('.saved-evidence-provenance')).toContainText('2026-09-29T12:25:00.654321Z');
  await expect(partial).toContainText(tombstone); await expect(partial).toContainText('The producer marked this packet as truncated.');
  await expect(view(page)).not.toContainText('This capture reached its size or packet limit.');
  expect(contexts.get(page).requests.filter(item => item.action !== 'read')).toHaveLength(0);
});

test('unknown inner schemas preserve opaque raw HTML as inert text and never fabricate source summaries', async ({ page }) => {
  await mount(page, 'inert');
  const article = articles(page).first();
  await expect(article).toContainText("This packet's schema or metadata could not be recognized. Its saved raw evidence remains available.");
  await expect(article.getByRole('heading', { name: 'Recorded source references', exact: true })).toHaveCount(0);
  await expect(article.getByRole('link')).toHaveCount(0);
  await openDisclosure(page, article, 'Raw saved evidence', 'Space');
  await expect(raw(page, 3)).toContainText(JSON.stringify(inertText).slice(1, -1)); await expect(view(page).locator('.incident-notes')).toHaveText(inertText);
  await expect(view(page).locator('img, script')).toHaveCount(0);
  expect(await page.evaluate(() => window.incidentInjected)).toBeUndefined();
  await expect(article).toContainText('Recorded packet coverage: unsupported');
});

test('empty, partial, unavailable and globally truncated captures retain separate truthful coverage disclosures', async ({ page }) => {
  const context = contexts.get(page);
  await mount(page, 'empty');
  await expect(view(page)).toContainText('No analytics evidence packets existed for this window when it was saved. This does not mean the server was healthy.');
  await expect(articles(page)).toHaveCount(0);
  context.backend.incidents.set(`${target}:${thread}`, savedIncident('partial')); await page.reload();
  await expect(articles(page)).toHaveCount(2);
  await expect(view(page)).toContainText('Recorded packet coverage: 0 complete · 1 partial · 1 unavailable · 0 unsupported.');
  await expect(articles(page).nth(1)).toContainText('No source references were recorded in this packet.');
  await expect(articles(page).nth(1)).toContainText('Its retained references do not establish complete evidence.');
  context.backend.incidents.set(`${target}:${thread}`, savedIncident('truncated')); await page.reload();
  await expect(view(page)).toContainText('This capture reached its size or packet limit.');
  await expect(view(page)).toContainText('not the entire window.');
  await expect(view(page)).toContainText('The producer marked this packet as truncated.');
  expect(context.requests.every(item => item.action === 'read')).toBe(true);
});

test('the saved exact microsecond window and share link remain frozen while shared scope, clock and display change', async ({ page }) => {
  await mount(page, 'known');
  const share = () => view(page).getByRole('link', { name: 'Open the saved window and share this investigation', exact: true });
  const selected = () => share().getAttribute('href').then(href => Object.fromEntries(new URLSearchParams(href.split('?')[1])));
  await expect.poll(selected).toMatchObject({ target, incident: thread, range: 'custom', from: fromUtc, to: toUtc, databaseId: '5', compare: '1' });
  // Settle the route-owned re-read before advancing the transport timeout clock.
  const reread = page.waitForResponse(response => response.request().method() === 'GET' && new URL(response.url()).pathname === `/api/v1/observation-targets/${target}/incidents/saved/${thread}`);
  await page.getByRole('button', { name: 'Shift shared window', exact: true }).click();
  const response = await reread; expect(await response.finished()).toBeNull();
  await expect(view(page)).toBeVisible();
  await expect.poll(() => new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('from')).toBe('2026-09-29T11:00:00.111111Z');
  await expect.poll(selected).toMatchObject({ from: fromUtc, to: toUtc });
  await page.clock.fastForward(60_000); await page.getByRole('button', { name: 'Refresh workspace context', exact: true }).click();
  await page.getByRole('combobox', { name: 'Time display', exact: true }).selectOption('local');
  await expect(view(page)).toContainText('EDT');
  await page.getByRole('combobox', { name: 'Time display', exact: true }).selectOption('utc');
  await expect.poll(selected).toMatchObject({ from: fromUtc, to: toUtc, target, incident: thread });
  await share().click();
  await expect.poll(() => Object.fromEntries(new URLSearchParams(new URL(page.url()).hash.split('?')[1])))
    .toMatchObject({ from: fromUtc, to: toUtc, target, incident: thread });
  expect(contexts.get(page).requests.every(item => item.action === 'read')).toBe(true);
});

for (const failure of ['lost', 'invalid']) test(`${failure} report receipt preserves one native operation across uncertain retry and guards duplicate pending activation`, async ({ page }) => {
  const context = contexts.get(page); let start, release, first = true;
  const started = new Promise(resolve => { start = resolve; }), gate = new Promise(resolve => { release = resolve; });
  context.handler = async (route, item) => {
    if (item.action !== 'report') return false;
    const receipt = context.backend.acceptReport(item.id, item.body);
    if (first) {
      first = false; start(); await gate;
      await json(route, failure === 'lost' ? { detail: 'SECRET_UNCERTAIN <script>window.incidentInjected=true</script>' }
        : { ...receipt, targetRevision: 99 }, failure === 'lost' ? 503 : 200);
    } else await json(route, receipt);
    return true;
  };
  await mount(page); await generate(page).focus(); await page.keyboard.press('Enter'); await started;
  await expect(view(page).getByRole('button', { name: 'Generating report…', exact: true })).toBeDisabled();
  await page.keyboard.press('Enter'); await page.keyboard.press('Enter');
  expect(context.requests.filter(item => item.action === 'report')).toHaveLength(1); await expect(html(page)).toHaveCount(0);
  release(); await expect(view(page).getByRole('alert')).toBeVisible();
  await expect(view(page)).not.toContainText('SECRET_UNCERTAIN');
  await view(page).getByRole('button', { name: 'Retry report generation', exact: true }).click();
  const attempts = context.requests.filter(item => item.action === 'report');
  await expect(html(page)).toBeVisible(); expect(attempts).toHaveLength(2); expect(attempts[1].body).toEqual(attempts[0].body);
  expect(context.backend.operations.size).toBe(1);
  await expectExports(page, target, [...context.backend.operations.values()][0].receipt.runId);
});

test('at the exact 24-hour report expiry stale exports disappear and renewal uses one fresh operation across failure and retry', async ({ page }) => {
  const expiryStart = '2026-10-02T12:01:00.000Z';
  await page.clock.pauseAt(new Date(expiryStart)); contexts.get(page).backend.now = expiryStart;
  await mount(page); await generate(page).click(); await expect(html(page)).toBeVisible();
  const context = contexts.get(page), first = [...context.backend.operations.values()][0];
  await expectExports(page, target, first.receipt.runId);
  await page.clock.fastForward(24 * 3600 * 1000 - 1); await expect(html(page)).toBeVisible();
  await page.clock.fastForward(1);
  await expect(view(page)).toContainText('This report expired. Generate a new report from the same saved investigation.');
  await expect(html(page)).toHaveCount(0);
  await expect(view(page).getByRole('link', { name: /Download saved (details|evidence) CSV/ })).toHaveCount(0);
  expect(context.backend.operations.size).toBe(1);
  context.backend.now = first.receipt.expiresAtUtc; let uncertain = true;
  context.handler = async (route, item) => {
    if (item.action !== 'report') return false;
    const receipt = context.backend.acceptReport(item.id, item.body);
    if (uncertain) { uncertain = false; await json(route, { detail: 'SECRET_RENEWAL' }, 503); } else await json(route, receipt);
    return true;
  };
  await view(page).getByRole('button', { name: 'Generate new investigation report', exact: true }).click();
  await expect(view(page).getByRole('alert')).toBeVisible(); await expect(html(page)).toHaveCount(0);
  const attempts = context.requests.filter(item => item.action === 'report');
  expect(attempts).toHaveLength(2); expect(attempts[1].body.operationId).not.toBe(attempts[0].body.operationId);
  await view(page).getByRole('button', { name: 'Retry report generation', exact: true }).click();
  await expect(html(page)).toBeVisible();
  const all = context.requests.filter(item => item.action === 'report'); expect(all[2].body).toEqual(all[1].body);
  expect(context.backend.operations.size).toBe(2);
  await expectExports(page, target, [...context.backend.operations.values()][1].receipt.runId);
});

test('Viewer-to-unverified read loss hides saved data and aborts a pending report before a fresh permitted read', async ({ page }) => {
  const held = heldReport(), context = contexts.get(page); context.handler = held.handle;
  await mount(page); await generate(page).click(); const request = await held.started;
  const aborted = page.waitForEvent('requestfailed', { predicate: candidate => candidate === request });
  await page.getByRole('combobox', { name: 'Read access', exact: true }).selectOption('unverified'); await aborted;
  await expect(page.getByText(denial, { exact: true })).toBeVisible(); await expect(view(page)).toHaveCount(0);
  await expect(page.locator('body')).not.toContainText('Synthetic retained notes.'); await held.finish();
  await expect(page.getByRole('link', { name: 'Open printable investigation', exact: true })).toHaveCount(0);
  const reads = context.requests.filter(item => item.action === 'read').length;
  await page.getByRole('button', { name: 'Refresh workspace context', exact: true }).click();
  expect(context.requests.filter(item => item.action === 'read')).toHaveLength(reads);
  context.handler = undefined;
  await page.getByRole('combobox', { name: 'Read access', exact: true }).selectOption('viewer');
  await expect(view(page)).toBeVisible(); await expect(generate(page)).toBeEnabled(); await expect(html(page)).toHaveCount(0);
  expect(context.requests.filter(item => item.action === 'read')).toHaveLength(reads + 1);
});

for (const change of ['target', 'unmount']) test(`a report receipt delivered after ${change} cannot expose stale exports or errors`, async ({ page }) => {
  const held = heldReport(); contexts.get(page).handler = held.handle;
  await mount(page); await generate(page).click(); const request = await held.started;
  const aborted = page.waitForEvent('requestfailed', { predicate: candidate => candidate === request });
  if (change === 'target') await page.getByRole('combobox', { name: 'Server context', exact: true }).selectOption(otherTarget);
  else await page.getByRole('button', { name: 'Unmount incident workspace', exact: true }).click();
  await aborted; await held.finish();
  await expect(page.getByRole('link', { name: 'Open printable investigation', exact: true })).toHaveCount(0);
  if (change === 'target') {
    await expect(view(page)).toHaveAccessibleName('SYNTHETIC other investigation');
    await expect(generate(page)).toBeEnabled(); await expect(view(page).getByRole('alert')).toHaveCount(0);
  } else await expect(view(page)).toHaveCount(0);
});

test('saved list paging retains its native snapshot and a stale cursor requires explicit refresh without a substituted investigation', async ({ page }) => {
  const context = contexts.get(page); let stale = true;
  context.handler = async (route, item) => {
    if (item.action !== 'list' || !item.url.searchParams.has('cursor') || !stale) return false;
    stale = false; await json(route, { detail: 'SECRET_CURSOR' }, 409); return true;
  };
  await page.goto(`/component-tests/incidents.html${incidentHref(target, null)}`);
  await openWorkspace(page);
  await page.getByRole('button', { name: 'Saved investigations', exact: true }).click();
  await expect(page.locator('.incident-summary-list > li')).toHaveCount(20);
  await page.getByRole('button', { name: 'Older investigations', exact: true }).click();
  await expect(page.getByRole('alert')).toHaveText('Saved investigations changed. Refresh the list to start again.');
  await expect(page.getByRole('link', { name: 'SYNTHETIC saved page 20', exact: true })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Retry loading investigations', exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'Refresh investigations', exact: true }).click();
  await expect(page.locator('.incident-summary-list > li')).toHaveCount(20);
  await page.getByRole('button', { name: 'Older investigations', exact: true }).click();
  await expect(page.getByRole('link', { name: 'SYNTHETIC saved page 20', exact: true })).toBeVisible();
  const reads = context.requests.filter(item => item.action === 'list');
  expect(reads.map(item => item.url.searchParams.get('cursor'))).toEqual([null, 'native.snapshot', null, 'native.snapshot']);
  const link = page.getByRole('link', { name: 'SYNTHETIC saved page 20', exact: true });
  expect(Object.fromEntries(new URLSearchParams((await link.getAttribute('href')).split('?')[1]))).toMatchObject({ target, from: fromUtc, to: toUtc, range: 'custom' });
  await expect(page.locator('body')).not.toContainText('SECRET_CURSOR');
});

test('direct saved-view props cannot expose title, notes, evidence or actions before read access is verified', async ({ page }) => {
  await page.goto('/component-tests/incidents.html?mode=view&read=denied');
  await expect(page.getByText(denial, { exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'SYNTHETIC direct saved view', exact: true })).toHaveCount(0);
  await expect(page.locator('body')).not.toContainText('PRIVATE_DIRECT_SAVED_NOTES');
  await expect(page.getByRole('button', { name: 'Generate investigation report', exact: true })).toHaveCount(0);
  expect(contexts.get(page).requests).toHaveLength(0);
  await page.getByRole('combobox', { name: 'Read access', exact: true }).selectOption('viewer');
  await expect(page.getByRole('heading', { name: 'SYNTHETIC direct saved view', exact: true })).toBeVisible();
  await expect(page.locator('.incident-notes')).toHaveText('PRIVATE_DIRECT_SAVED_NOTES');
  await page.getByRole('combobox', { name: 'Read access', exact: true }).selectOption('unverified');
  await expect(page.locator('body')).not.toContainText('PRIVATE_DIRECT_SAVED_NOTES');
  expect(contexts.get(page).requests).toHaveLength(0);
});

async function draftWorkspace(page) {
  await page.goto(`/component-tests/incidents.html${incidentHref(target, null)}`);
  await page.getByRole('combobox', { name: 'Save access', exact: true }).selectOption('operator');
  await expect(workspaceSummary(page).locator('..')).not.toHaveAttribute('open', '');
  await openWorkspace(page);
  await page.getByRole('button', { name: 'Open incident', exact: true }).press('Enter');
  await expect(page.getByRole('textbox', { name: 'Incident title', exact: true })).toBeFocused();
}
async function closeWorkspace(page) {
  await workspaceSummary(page).focus(); await page.keyboard.press('Enter');
  await expect(workspaceSummary(page).locator('..')).not.toHaveAttribute('open', '');
}

test('the optional workspace exposes the role hint without fetching or changing the shared URL', async ({ page }) => {
  await page.goto(`/component-tests/incidents.html${incidentHref(target, null)}`);
  const hash = new URL(page.url()).hash;
  await expect(workspaceSummary(page).locator('..')).not.toHaveAttribute('open', '');
  await expect(page.getByText('Operator or Target Administrator access unlocks saving an incident for this server.', { exact: true })).toBeHidden();
  await openWorkspace(page);
  await expect(page.getByText('Operator or Target Administrator access unlocks saving an incident for this server.', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Saved investigations', exact: true })).toBeVisible();
  await closeWorkspace(page); await page.getByRole('button', { name: 'Refresh workspace context', exact: true }).click();
  expect(new URL(page.url()).hash).toBe(hash); expect(contexts.get(page).requests).toHaveLength(0);
});

test('closing the workspace preserves a frozen draft through context refresh and a shared-window change', async ({ page }) => {
  await draftWorkspace(page);
  const title = page.getByRole('textbox', { name: 'Incident title', exact: true });
  await title.fill('SYNTHETIC retained draft'); await page.getByRole('textbox', { name: 'Notes', exact: true }).fill('Keep these exact notes.');
  const frozen = await page.locator('.incident-form > p').first().textContent();
  await closeWorkspace(page);
  await expect(page.getByText(/^Frozen incident draft:/)).toBeVisible();
  await page.getByRole('button', { name: 'Refresh workspace context', exact: true }).click();
  await page.getByRole('button', { name: 'Shift shared window', exact: true }).click();
  await expect(workspaceSummary(page).locator('..')).not.toHaveAttribute('open', '');
  await openWorkspace(page); await expect(title).toHaveValue('SYNTHETIC retained draft');
  await expect(page.getByRole('textbox', { name: 'Notes', exact: true })).toHaveValue('Keep these exact notes.');
  await expect(page.locator('.incident-form > p').first()).toHaveText(frozen);
  await page.getByRole('button', { name: 'Close draft', exact: true }).press('Enter');
  await expect(page.getByRole('button', { name: 'Open incident', exact: true })).toBeFocused();
  await expect(workspaceSummary(page).locator('..')).toHaveAttribute('open', '');
  expect(contexts.get(page).requests).toHaveLength(0);
});

test('a collapsed pending save remains owned and its uncertain retry preserves the exact original command', async ({ page }) => {
  const context = contexts.get(page); let started, release, finished;
  const seen = new Promise(done => { started = done; }), held = new Promise(done => { release = done; }), delivered = new Promise(done => { finished = done; });
  context.handler = async (route, item) => {
    if (item.action !== 'save' || context.requests.filter(item => item.action === 'save').length !== 1) return false;
    context.backend.acceptSave(item.id, item.body); started(route.request()); await held;
    await json(route, { detail: 'PRIVATE_SAVE_ERROR' }, 503); finished(); return true;
  };
  await draftWorkspace(page);
  await page.getByRole('textbox', { name: 'Incident title', exact: true }).fill('SYNTHETIC exact retry');
  await page.getByRole('textbox', { name: 'Notes', exact: true }).fill('Frozen notes.');
  await page.getByRole('button', { name: 'Save incident', exact: true }).press('Enter');
  const request = await seen;
  await closeWorkspace(page);
  await expect(page.getByRole('status').filter({ hasText: 'Saving the frozen incident draft.' })).toBeVisible();
  await page.getByRole('button', { name: 'Refresh workspace context', exact: true }).click();
  expect(request.failure()).toBeNull(); expect(context.requests.filter(item => item.action === 'save')).toHaveLength(1);
  release(); await delivered;
  await expect(page.getByRole('alert')).toHaveText('The incident service is temporarily unavailable. Retry the same save to check its outcome.');
  await expect(page.getByRole('button', { name: 'Retry same save', exact: true })).toHaveCount(0);
  await openWorkspace(page);
  await expect(page.getByRole('textbox', { name: 'Incident title', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Retry same save', exact: true }).press('Enter');
  await expect(page.getByRole('heading', { name: 'SYNTHETIC exact retry', exact: true })).toBeVisible();
  const saves = context.requests.filter(item => item.action === 'save'); expect(saves).toHaveLength(2); expect(saves[1].body).toEqual(saves[0].body);
  expect(saves[0].body).toMatchObject({ expectedTargetRevision: 7, fromUtc, toUtc, notes: 'Frozen notes.' });
  await expect(workspaceSummary(page)).toHaveCount(0); await expect(page.locator('body')).not.toContainText('PRIVATE_SAVE_ERROR');
});

test('collapsing the outer workspace preserves a pending saved-list continuation and exposes its failure for explicit recovery', async ({ page }) => {
  const context = contexts.get(page); let started, release, finished, fail = true;
  const seen = new Promise(done => { started = done; }), gate = new Promise(done => { release = done; }), delivered = new Promise(done => { finished = done; });
  context.handler = async (route, item) => {
    if (item.action !== 'list' || !item.url.searchParams.has('cursor') || !fail) return false;
    fail = false; started(route.request()); await gate; await json(route, {}, 503); finished(); return true;
  };
  await page.goto(`/component-tests/incidents.html${incidentHref(target, null)}`); await openWorkspace(page);
  await page.getByRole('button', { name: 'Saved investigations', exact: true }).click();
  await expect(page.locator('.incident-summary-list > li')).toHaveCount(20);
  await page.getByRole('button', { name: 'Older investigations', exact: true }).click(); const request = await seen;
  await closeWorkspace(page);
  await expect(page.getByRole('status').filter({ hasText: 'Loading saved investigations…' })).toBeVisible();
  expect(request.failure()).toBeNull();
  release(); await delivered;
  await expect(page.getByRole('alert')).toHaveText('Saved investigations are temporarily unavailable. Open the investigation workspace for recovery controls.');
  await openWorkspace(page);
  await page.getByRole('button', { name: 'Retry loading investigations', exact: true }).click();
  await expect(page.getByRole('link', { name: 'SYNTHETIC saved page 20', exact: true })).toBeVisible();
  await closeWorkspace(page); await page.getByRole('button', { name: 'Refresh workspace context', exact: true }).click(); await openWorkspace(page);
  await expect(page.getByRole('link', { name: 'SYNTHETIC saved page 20', exact: true })).toBeVisible();
  expect(context.requests.filter(item => item.action === 'list').map(item => item.url.searchParams.get('cursor'))).toEqual([null, 'native.snapshot', 'native.snapshot']);
});

for (const state of ['invalid', 'missing', 'denied']) test(`a ${state} selected investigation stays discoverable outside the optional workspace`, async ({ page }) => {
  const hash = state === 'invalid' ? incidentHref(target, 'invalid') : incidentHref(target, 'ffffffff-ffff-4fff-8fff-ffffffffffff');
  await page.goto(`/component-tests/incidents.html${state === 'denied' ? '?read=denied' : ''}${hash}`);
  await expect(workspaceSummary(page)).toHaveCount(0);
  await expect(page.getByText(state === 'invalid' ? 'The incident link is invalid. Open a saved investigation using its original link.'
    : state === 'denied' ? denial : 'This saved investigation was not found. Automatically correlated incidents remain available in Analytics.', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Close investigation', exact: true })).toBeVisible();
  for (const button of await page.locator('.incident-workspace').getByRole('button').all()) {
    expect((await button.boundingBox()).height, 'Direct recovery actions remain touch accessible outside the optional workspace').toBeGreaterThanOrEqual(44);
  }
  await expect(view(page)).toHaveCount(0);
  expect(contexts.get(page).requests.map(item => item.action)).toEqual(state === 'missing' ? ['read'] : []);
});

test('an invalid shared window stays visible outside the closed workspace and cannot create a draft', async ({ page }) => {
  await page.goto(`/component-tests/incidents.html${incidentHref(target, null, { from: toUtc, to: fromUtc })}`);
  await page.getByRole('combobox', { name: 'Save access', exact: true }).selectOption('operator');
  await expect(workspaceSummary(page).locator('..')).not.toHaveAttribute('open', '');
  await expect(page.getByRole('status').filter({ hasText: 'Select a window of at most seven days.' })).toBeVisible();
  await openWorkspace(page); await expect(page.getByRole('button', { name: 'Open incident', exact: true })).toBeDisabled();
  expect(contexts.get(page).requests).toHaveLength(0);
});

test('opening a draft retires a held saved-list owner without leaving a collapsed loading announcement', async ({ page }) => {
  const context = contexts.get(page); let started, release, finished;
  const seen = new Promise(done => { started = done; }), gate = new Promise(done => { release = done; }), delivered = new Promise(done => { finished = done; });
  context.handler = async (route, item) => {
    if (item.action !== 'list') return false;
    started(route.request()); await gate;
    try { await json(route, summaryPage(item.id)); } catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
    finished(); return true;
  };
  await page.goto(`/component-tests/incidents.html${incidentHref(target, null)}`);
  await page.getByRole('combobox', { name: 'Save access', exact: true }).selectOption('operator'); await openWorkspace(page);
  await page.getByRole('button', { name: 'Saved investigations', exact: true }).click(); const request = await seen;
  await page.getByRole('button', { name: 'Open incident', exact: true }).click();
  await expect(page.getByRole('textbox', { name: 'Incident title', exact: true })).toBeFocused();
  await expect.poll(() => request.failure()?.errorText).toContain('ERR_ABORTED');
  await closeWorkspace(page); await expect(page.getByText(/^Frozen incident draft:/)).toBeVisible();
  await expect(page.getByRole('status').filter({ hasText: 'Loading saved investigations' })).toHaveCount(0);
  release(); await delivered;
  await expect(page.getByRole('status').filter({ hasText: 'Loading saved investigations' })).toHaveCount(0);
  await openWorkspace(page); await expect(page.getByRole('textbox', { name: 'Incident title', exact: true })).toBeVisible();
  expect(context.requests.map(item => item.action)).toEqual(['list']);
});

for (const width of [320, 390, 768, 1440]) test(`the compact investigation disclosure preserves keyboard focus and a draft at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 }); await draftWorkspace(page);
  await page.getByRole('textbox', { name: 'Incident title', exact: true }).fill(`SYNTHETIC draft ${width}`);
  await closeWorkspace(page); await expect(workspaceSummary(page)).toBeFocused();
  const focus = await workspaceSummary(page).evaluate(element => ({ style: getComputedStyle(element).outlineStyle, width: parseFloat(getComputedStyle(element).outlineWidth) }));
  expect(focus.style).not.toBe('none'); expect(focus.width).toBeGreaterThanOrEqual(2);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`incident-disclosure-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-context-frame/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
  await page.screenshot({ path: `${directory}/incident-disclosure-${width}.png`, fullPage: true });
  await openWorkspace(page); await expect(page.getByRole('textbox', { name: 'Incident title', exact: true })).toHaveValue(`SYNTHETIC draft ${width}`);
  expect(contexts.get(page).requests).toHaveLength(0);
});

async function audit(page, info, name) {
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`${name}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-saved-incidents/screenshots', import.meta.url)); await mkdir(directory, { recursive: true });
  await page.evaluate(() => window.scrollTo(0, 0)); await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
  await page.screenshot({ path: `${directory}/${name}.png`, fullPage: true });
}
for (const width of [320, 390, 768, 1440]) test(`populated saved evidence, provenance and unknown raw disclosure remain readable and keyboard accessible at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 900 }); await mount(page, 'truncated');
  const complete = articles(page).nth(0), partial = articles(page).nth(1), unknown = articles(page).nth(3);
  await openDisclosure(page, complete, 'Saved packet provenance'); await openDisclosure(page, partial, 'Saved packet provenance', 'Space');
  await openDisclosure(page, unknown, 'Raw saved evidence');
  const region = raw(page, 3); await region.focus(); await expect(region).toBeFocused();
  const focus = await region.evaluate(element => ({ width: getComputedStyle(element).outlineWidth, style: getComputedStyle(element).outlineStyle }));
  expect(focus.style).not.toBe('none'); expect(parseFloat(focus.width)).toBeGreaterThanOrEqual(2);
  expect(await region.evaluate(element => element.scrollHeight > element.clientHeight)).toBe(true);
  await page.keyboard.press('ArrowDown'); await expect.poll(() => region.evaluate(element => element.scrollTop)).toBeGreaterThan(0);
  for (const item of [complete, partial, unknown, region]) {
    const box = await item.boundingBox(); expect(box).not.toBeNull(); expect(box.width).toBeGreaterThanOrEqual(150);
    expect(box.x).toBeGreaterThanOrEqual(0); expect(box.x + box.width).toBeLessThanOrEqual(width + 1);
  }
  await expect(region).toContainText(JSON.stringify(inertText).slice(1, -1)); await expect(view(page).locator('img, script')).toHaveCount(0);
  await audit(page, info, `saved-evidence-mounted-${width}`);
  await generate(page).click(); await expect(html(page)).toBeVisible();
  await audit(page, info, `saved-evidence-exports-${width}`);
});
