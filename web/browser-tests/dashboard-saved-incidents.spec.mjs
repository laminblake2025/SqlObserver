import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';
import { target, otherTarget, thread, fromUtc, toUtc, nowUtc, diagnosticId, packetIds, inertText,
  incidentHref, savedIncident, summaryPage, incidentBackend } from '../component-tests/incidentFixture.mjs';

const origin = 'http://127.0.0.1:4333';
const contexts = new WeakMap();
const denial = 'Viewer, Operator or Target Administrator access is required to read and export this saved investigation.';
const view = page => page.getByRole('region', { name: 'SYNTHETIC saved investigation', exact: true });
const generate = page => view(page).getByRole('button', { name: 'Generate investigation report', exact: true });
const html = page => view(page).getByRole('link', { name: 'Open printable investigation', exact: true });
const share = page => view(page).getByRole('link', { name: 'Open the saved window and share this investigation', exact: true });
const scope = page => Object.fromEntries(new URLSearchParams(new URL(page.url()).hash.split('?')[1]));
const refresh = page => page.locator('.page-actions').getByRole('button', { name: '↻ Refresh', exact: true });
const supportingReads = new Set(['health', 'health/databases', 'health/files', 'health/database-option-changes',
  'health/server-configuration-changes', 'health/server-configuration-snapshot', 'deadlocks', 'alerts/active',
  'activity/blocking/current', 'activity/waits/trend', 'query-performance/regressions',
  'sql-agent/failures', 'analytics/baselines', 'analytics/sql-baselines']);
async function json(route, value, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
}
function nativeTarget(id) {
  return { instanceId: id, instanceKey: id === target ? 'synthetic-alpha' : 'synthetic-beta',
    displayName: id === target ? 'SYNTHETIC Alpha' : 'SYNTHETIC Beta', host: 'sql.example.test', tcpPort: 1433,
    authenticationMode: 'windows_integrated_service_identity', encryptionMode: 'mandatory_validated',
    lifecycle: 'active', capabilityStatus: 'supported', capabilityReasons: [], configurationRevision: 1,
    discoveryRequestedAtUtc: '2026-09-25T12:00:00Z', lastDiscoveryAtUtc: '2026-09-25T12:01:00Z' };
}
test.beforeEach(async ({ page }) => {
  await page.clock.install({ time: new Date(nowUtc) });
  const context = { backend: incidentBackend(), requests: [], accessReads: [], unexpected: [], role: 'viewer', handler: undefined };
  contexts.set(page, context);
  page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.context().addCookies([{ name: 'dashboardRefactor', value: '1', url: origin }, { name: 'chartReferenceRole', value: 'viewer', url: origin }]);
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    const reject = async () => { context.unexpected.push(`${request.method()} ${url.href}`); await route.abort(); };
    if (url.origin !== origin) return reject();
    if (!/^\/api(?:\/|$)/.test(url.pathname)) return route.continue();
    if (url.pathname === '/api/v1/me' && request.method() === 'GET') {
      const id = url.searchParams.get('targetId'); context.accessReads.push({ targetId: id, role: context.role });
      if (context.role === 'unverified') return json(route, { detail: 'SECRET_ACCESS_PROVIDER' }, 503);
      return json(route, { active: true, targetId: id, grantedRoles: ['Viewer'], allTargetRoles: [], targetRoles: id ? ['Viewer'] : [] });
    }
    if (url.pathname === '/api/v1/observation-targets' && request.method() === 'GET')
      return json(route, { items: [nativeTarget(target), nativeTarget(otherTarget)], nextCursor: null });
    const direct = /^\/api\/v1\/observation-targets\/([^/]+)$/.exec(url.pathname);
    if (direct && [target, otherTarget].includes(direct[1]) && request.method() === 'GET' && !url.search) return json(route, nativeTarget(direct[1]));
    const match = /^\/api\/v1\/observation-targets\/([^/]+)\/(incidents\/saved(?:\/([^/]+))?|reports)$/.exec(url.pathname);
    if (match && [target, otherTarget].includes(match[1])) {
      const action = match[2] === 'reports' && request.method() === 'POST' && !url.search ? 'report'
        : match[3] && request.method() === 'GET' && !url.search ? 'read'
        : match[2] === 'incidents/saved' && request.method() === 'GET' && url.searchParams.get('limit') === '20'
          && [...url.searchParams.keys()].every(key => ['limit', 'cursor'].includes(key)) ? 'list' : undefined;
      if (!action) return reject();
      const item = { action, id: match[1], thread: match[3], url, ...(action === 'report' ? { body: request.postDataJSON() } : {}) };
      context.requests.push(item);
      if (action === 'report') {
        expect(item.body).toEqual({ reportKind: 'saved-investigation', operationId: expect.stringMatching(/^[0-9a-f-]{36}$/i), threadId: thread });
        expect(item.id).toBe(target);
      }
      if (context.handler && await context.handler(route, item, context)) return;
      if (action === 'report') return json(route, context.backend.acceptReport(item.id, item.body));
      if (action === 'list') return json(route, summaryPage(item.id, url.searchParams.has('cursor')));
      const saved = context.backend.incidents.get(`${item.id}:${item.thread}`);
      return json(route, saved ?? {}, saved ? 200 : 404);
    }
    // Registered supporting dashboard reads stay within the synthetic-only
    // fixture server, which has no real backend proxy. Unknown APIs fail closed.
    const supporting = /^\/api\/v1\/observation-targets\/([^/]+)\/(.+)$/.exec(url.pathname);
    if (request.method() === 'GET' && (url.pathname === '/api/v1/overview'
      || supporting && [target, otherTarget].includes(supporting[1]) && supportingReads.has(supporting[2]))) return route.continue();
    return reject();
  });
});
test.afterEach(async ({ page }) => {
  expect(contexts.get(page).unexpected, 'No external traffic, unregistered mutations or runtime errors').toEqual([]);
});
async function open(page, scenario = 'rich', hash = incidentHref()) {
  contexts.get(page).backend.incidents.set(`${target}:${thread}`, savedIncident(scenario));
  await page.goto(`/${hash}`); await expect(view(page)).toBeVisible(); await expect(generate(page)).toBeEnabled();
}
async function expectScope(page) {
  await expect.poll(() => scope(page)).toMatchObject({ target, incident: thread, range: 'custom', from: fromUtc, to: toUtc, databaseId: '5', compare: '1' });
}

test('a saved URL exposes recorded packet evidence and shares its original exact window through reload and history without implicit reports', async ({ page }) => {
  await open(page, 'rich', incidentHref(target, thread, { from: '2026-09-28T08:00:00.111111Z', to: '2026-09-28T12:00:00.222222Z' }));
  const complete = view(page).getByRole('article').first();
  await expect(complete).toContainText('Diagnostic event'); await expect(complete).toContainText(diagnosticId);
  await complete.getByText('Saved packet provenance', { exact: true }).focus(); await page.keyboard.press('Enter');
  await expect(complete.locator('.saved-evidence-provenance')).toContainText('Identity digest');
  expect(Object.fromEntries(new URLSearchParams((await share(page).getAttribute('href')).split('?')[1])))
    .toMatchObject({ target, incident: thread, range: 'custom', from: fromUtc, to: toUtc, databaseId: '5', compare: '1' });
  await share(page).click(); await expectScope(page);
  await page.reload(); await expect(view(page)).toBeVisible(); await expectScope(page);
  await page.goBack(); await expect(view(page)).toBeVisible();
  await expect.poll(() => scope(page).from).toBe('2026-09-28T08:00:00.111111Z');
  await page.goForward(); await expectScope(page);
  const context = contexts.get(page);
  expect(context.requests.filter(item => item.action === 'report')).toHaveLength(0);
  expect(context.requests.filter(item => item.action === 'read').every(item => item.id === target && item.thread === thread)).toBe(true);
});

test('the saved investigation report retries one target-bound native operation and exports the verified saved revision', async ({ page }) => {
  const context = contexts.get(page); let uncertain = true;
  context.handler = async (route, item) => {
    if (item.action !== 'report') return false;
    const receipt = context.backend.acceptReport(item.id, item.body);
    if (uncertain) { uncertain = false; await json(route, { detail: 'SECRET_REPORT_PROVIDER' }, 503); } else await json(route, receipt);
    return true;
  };
  await open(page); await generate(page).focus(); await page.keyboard.press('Enter');
  await expect(view(page).getByRole('alert')).toBeVisible(); await expect(html(page)).toHaveCount(0);
  await expect(view(page)).not.toContainText('SECRET_REPORT_PROVIDER');
  await view(page).getByRole('button', { name: 'Retry report generation', exact: true }).click();
  await expect(html(page)).toBeVisible();
  const attempts = context.requests.filter(item => item.action === 'report');
  expect(attempts).toHaveLength(2); expect(attempts[1].body).toEqual(attempts[0].body); expect(context.backend.operations.size).toBe(1);
  const receipt = [...context.backend.operations.values()][0].receipt;
  expect(receipt.targetRevision).toBe(7);
  await expect(html(page)).toHaveAttribute('href', `/api/v1/observation-targets/${target}/reports/${receipt.runId}/html`);
  await expect(html(page)).toHaveAttribute('target', '_blank'); await expect(html(page)).toHaveAttribute('rel', 'noreferrer');
  await expect(view(page).getByRole('link', { name: 'Download saved evidence CSV', exact: true }))
    .toHaveAttribute('href', `/api/v1/observation-targets/${target}/reports/${receipt.runId}/saved-evidence.csv`);
  await expectScope(page);
});

test('losing verified Viewer read access during a report hides the saved packet and fences its late receipt until a fresh read', async ({ page }) => {
  const context = contexts.get(page); let start, release, complete;
  const started = new Promise(resolve => { start = resolve; }), gate = new Promise(resolve => { release = resolve; }),
    finished = new Promise(resolve => { complete = resolve; });
  context.handler = async (route, item) => {
    if (item.action !== 'report') return false;
    const receipt = context.backend.acceptReport(item.id, item.body); start(route.request()); await gate;
    try { await json(route, receipt); }
    catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
    finally { complete(); }
    return true;
  };
  await open(page); await generate(page).click(); const request = await started;
  const cancelled = page.waitForEvent('requestfailed', { predicate: candidate => candidate === request });
  context.role = 'unverified'; await refresh(page).click(); await cancelled;
  await expect(page.getByText(denial, { exact: true })).toBeVisible(); await expect(view(page)).toHaveCount(0);
  await expect(page.locator('body')).not.toContainText('Synthetic retained notes.');
  await expect(page.locator('body')).not.toContainText('SECRET_ACCESS_PROVIDER');
  release(); await finished; await expect(page.getByRole('link', { name: 'Open printable investigation', exact: true })).toHaveCount(0);
  const reads = context.requests.filter(item => item.action === 'read').length;
  await expect(refresh(page)).toBeEnabled(); await refresh(page).click(); await expect(refresh(page)).toBeEnabled();
  expect(context.requests.filter(item => item.action === 'read')).toHaveLength(reads);
  context.role = 'viewer'; context.handler = undefined;
  await refresh(page).click(); await expect(view(page)).toBeVisible(); await expect(generate(page)).toBeEnabled();
  await expect(html(page)).toHaveCount(0); expect(context.requests.filter(item => item.action === 'read')).toHaveLength(reads + 1);
  expect(context.requests.filter(item => item.action === 'report')).toHaveLength(1);
});

for (const width of [320, 390, 768, 1440]) test(`saved packet references, provenance and opaque JSON are dark, accessible and readable in the App at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 }); await open(page, 'truncated');
  const articles = view(page).getByRole('article'), complete = articles.nth(0), partial = articles.nth(1), unknown = articles.nth(3);
  for (const article of [complete, partial]) {
    const disclosure = article.getByText('Saved packet provenance', { exact: true }); await disclosure.focus(); await page.keyboard.press('Enter');
    await expect(disclosure.locator('..')).toHaveAttribute('open', '');
  }
  const rawDisclosure = unknown.getByText('Raw saved evidence', { exact: true }); await rawDisclosure.focus(); await page.keyboard.press('Space');
  const raw = view(page).getByRole('region', { name: `Saved evidence packet ${packetIds[3]}`, exact: true });
  await raw.focus(); await expect(raw).toBeFocused();
  const focus = await raw.evaluate(element => ({ width: getComputedStyle(element).outlineWidth, style: getComputedStyle(element).outlineStyle }));
  expect(focus.style).not.toBe('none'); expect(parseFloat(focus.width)).toBeGreaterThanOrEqual(2);
  expect(await raw.evaluate(element => element.scrollHeight > element.clientHeight)).toBe(true);
  await page.keyboard.press('ArrowDown'); await expect.poll(() => raw.evaluate(element => element.scrollTop)).toBeGreaterThan(0);
  for (const item of [complete, partial, unknown, raw]) {
    const box = await item.boundingBox(); expect(box).not.toBeNull(); expect(box.width).toBeGreaterThanOrEqual(150);
    expect(box.x).toBeGreaterThanOrEqual(0); expect(box.x + box.width).toBeLessThanOrEqual(width + 1);
  }
  await expect(raw).toContainText(JSON.stringify(inertText).slice(1, -1)); await expect(view(page).locator('img, script')).toHaveCount(0);
  expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`saved-packets-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
  const directory = resolve('../artifacts/dashboard-saved-incidents/screenshots'); await mkdir(directory, { recursive: true });
  await page.evaluate(() => window.scrollTo(0, 0)); await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
  await page.screenshot({ path: resolve(directory, `saved-evidence-workspace-${width}.png`), fullPage: true });
  await expectScope(page); expect(contexts.get(page).requests.filter(item => item.action === 'report')).toHaveLength(0);
});
