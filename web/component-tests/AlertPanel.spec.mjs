import { expect, test } from '@playwright/test';

const target = '11111111-1111-4111-8111-111111111111';
const otherTarget = '44444444-4444-4444-8444-444444444444';
const firstId = '22222222-2222-4222-8222-222222222222';
const secondId = '55555555-5555-4555-8555-555555555555';
const missingId = '99999999-9999-4999-8999-999999999999';
const snapshotUtc = '2026-08-24T00:00:00Z';
const cursor = 'Y3Vyc29yMQ==';
const contexts = new WeakMap();
const alert = (overrides = {}) => ({ alertId: firstId, ruleId: '33333333-3333-4333-8333-333333333333',
  ruleName: 'CPU high', state: 'firing', firstObservedUtc: snapshotUtc, value: null,
  deliverySuppressed: false, ...overrides });
const pageData = (items, overrides = {}) => ({ targetId: target, snapshotUtc, nextCursor: null, items, ...overrides });
const detail = (page, name = 'CPU high') => page.getByRole('complementary', { name, exact: true });
const readPath = id => `/api/v1/observation-targets/${id}/alerts/active`;
const acknowledgePath = (id, alertId) => `/api/v1/observation-targets/${id}/alerts/${alertId}/acknowledge`;

function isRead(route, id = target, nextCursor) {
  const request = route.request();
  const url = new URL(request.url());
  return request.method() === 'GET' && url.pathname === readPath(id)
    && url.search === `?limit=100${nextCursor ? `&cursor=${encodeURIComponent(nextCursor)}` : ''}`;
}
function isAcknowledge(route, id = target, alertId = firstId) {
  const url = new URL(route.request().url());
  return route.request().method() === 'POST'
    && url.pathname === acknowledgePath(id, alertId) && url.search === '';
}
async function json(route, body, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(body) });
}
function deferred() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
}
// Hold a real intercepted response until the test has changed context. The
// browser still owns fetch/AbortSignal and the production transport/parser run.
function heldResponse(response) {
  const started = deferred();
  const released = deferred();
  const completed = deferred();
  return {
    started: started.promise,
    release: async () => { released.resolve(); await completed.promise; },
    handle: async route => {
      started.resolve(route.request());
      await released.promise;
      try { await route.fulfill(response); }
      catch (error) {
        if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error;
      } finally { completed.resolve(); }
    },
  };
}

test.beforeEach(async ({ page }) => {
  const context = { unexpected: [], requests: [], handler: undefined };
  contexts.set(page, context);
  page.on('pageerror', error => context.unexpected.push(`Page error: ${error.message}`));
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.origin !== 'http://127.0.0.1:4328') {
      context.unexpected.push(`External request: ${url.href}`);
      await route.abort();
    } else if (/^\/api(?:\/|$)/.test(url.pathname)) {
      context.requests.push(route.request());
      if (!context.handler || !await context.handler(route)) {
        context.unexpected.push(`Unmocked API: ${route.request().method()} ${url.href}`);
        await route.abort();
      }
    } else {
      await route.continue();
    }
  });
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
});
test.afterEach(async ({ page }) => {
  expect(contexts.get(page).unexpected, 'All API traffic must be fulfilled locally, with no external requests or page errors').toEqual([]);
});
async function mount(page, handler, selectedId) {
  contexts.get(page).handler = handler;
  const hash = selectedId ? `#/alerts?target=${target}&alert=${selectedId}` : `#/alerts?target=${target}`;
  await page.goto(`/component-tests/alerts.html${hash}`);
  await expect(page.getByRole('heading', { name: 'Alert panel harness' })).toBeVisible();
}

test('acknowledge is available only to an operator selecting a firing alert', async ({ page }) => {
  const states = ['firing', 'normal', 'pending', 'acknowledged', 'resolved'];
  const items = states.map((state, index) => alert({ state, ruleName: `${state} rule`,
    alertId: `${index + 2}2222222-2222-4222-8222-222222222222` }));
  await mount(page, async route => {
    if (!isRead(route)) return false;
    await json(route, pageData(items)); return true;
  });
  await expect(detail(page, 'firing rule')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Acknowledge', exact: true })).toHaveCount(0);
  await expect(detail(page, 'firing rule')).toContainText('An Operator or Target Administrator role');
  await page.getByLabel('Operator permission').check();
  await expect(detail(page, 'firing rule').getByRole('button', { name: 'Acknowledge', exact: true })).toBeEnabled();
  await page.getByRole('combobox', { name: 'State', exact: true }).selectOption('all');
  for (const state of states.slice(1)) {
    await page.getByRole('combobox', { name: 'State', exact: true }).selectOption('all');
    await page.getByRole('button', { name: `${state} rule`, exact: true }).click();
    // A URL selection resets the production filter to active. Terminal states
    // remain an explicit hidden selection until the user asks to reveal it.
    if (state === 'normal' || state === 'resolved') await page.getByRole('button', { name: 'Show selected alert' }).click();
    await expect(detail(page, `${state} rule`).getByRole('status')).toContainText(state);
    await expect(page.getByRole('button', { name: 'Acknowledge', exact: true })).toHaveCount(0);
  }
  await page.getByRole('button', { name: 'firing rule', exact: true }).click();
  await expect(detail(page, 'firing rule').getByRole('button', { name: 'Acknowledge', exact: true })).toBeEnabled();
  expect(contexts.get(page).requests.map(request => request.method())).toEqual(['GET']);
});

test('failed acknowledgement retries the same operation and a bodyless 204 records review', async ({ page }) => {
  const operations = [];
  const success = heldResponse({ status: 204 });
  await mount(page, async route => {
    if (isRead(route)) { await json(route, pageData([alert()])); return true; }
    if (!isAcknowledge(route)) return false;
    operations.push(route.request().postDataJSON());
    if (operations.length === 1) await json(route, {}, 503);
    else await success.handle(route);
    return true;
  });
  await expect(detail(page)).toBeVisible();
  await expect(detail(page)).toContainText('Collector health value unavailable');
  await page.getByLabel('Operator permission').check();
  await page.getByRole('button', { name: 'Acknowledge', exact: true }).click();
  await expect(page.getByRole('status').filter({ hasText: 'The alert could not be acknowledged.' })).toBeVisible();
  await expect(detail(page).getByRole('status')).toContainText('firing');
  await page.getByRole('button', { name: 'Acknowledge', exact: true }).click();
  await success.started;
  await expect(page.getByRole('button', { name: 'Acknowledging…', exact: true })).toBeDisabled();
  expect(operations).toHaveLength(2);
  expect(operations[0]).toEqual(operations[1]);
  expect(Object.keys(operations[0])).toEqual(['operationId']);
  expect(operations[0].operationId).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
  await success.release();
  await expect(detail(page).getByRole('status')).toContainText('acknowledged');
  await expect(page.getByRole('status').filter({ hasText: 'Acknowledgement recorded. Monitoring continues' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Acknowledge', exact: true })).toHaveCount(0);
  await expect(detail(page)).toContainText('Not returned by this alert state');
});

test('a selected resolved or filtered alert remains explicit until the user clears filters', async ({ page }) => {
  await mount(page, async route => {
    if (!isRead(route)) return false;
    await json(route, pageData([alert(), alert({ alertId: secondId, ruleName: 'Resolved health', state: 'resolved' })]));
    return true;
  }, secondId);
  await expect(page.getByText('The selected alert is hidden by the current filters.', { exact: false })).toBeVisible();
  await expect(detail(page)).toHaveCount(0);
  await page.getByRole('button', { name: 'Show selected alert' }).click();
  await expect(detail(page, 'Resolved health').getByRole('status')).toContainText('resolved');
  await page.getByLabel('Filter by rule identifier').fill('no-such-rule');
  await expect(detail(page, 'Resolved health')).toHaveCount(0);
  await expect(page.getByText('No alert rows match the loaded target-scoped evidence.', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Show selected alert' }).click();
  await expect(page.getByLabel('Filter by rule identifier')).toHaveValue('');
  await expect(detail(page, 'Resolved health')).toBeVisible();
  await expect(page.getByRole('button', { name: 'CPU high', exact: true })).toHaveAttribute('aria-pressed', 'false');
  expect(contexts.get(page).requests).toHaveLength(1);
});

test('a missing URL selection searches server pages without substituting another alert', async ({ page }) => {
  await mount(page, async route => {
    if (isRead(route)) { await json(route, pageData([alert()], { nextCursor: cursor })); return true; }
    if (isRead(route, target, cursor)) {
      await json(route, pageData([alert({ alertId: secondId, ruleName: 'Memory high' })])); return true;
    }
    return false;
  }, missingId);
  await expect(page.getByText('The requested alert is not present in the bounded current server alerts.', { exact: false })).toBeVisible();
  await expect(page.getByRole('complementary')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'CPU high', exact: true })).toHaveAttribute('aria-pressed', 'false');
  await expect(page.getByRole('button', { name: 'Memory high', exact: true })).toHaveAttribute('aria-pressed', 'false');
  await expect(page.getByRole('button', { name: 'Load more', exact: true })).toBeDisabled();
  expect(contexts.get(page).requests).toHaveLength(2);
  await page.getByRole('button', { name: 'Memory high', exact: true }).click();
  await expect(detail(page, 'Memory high')).toBeVisible();
  expect(contexts.get(page).requests).toHaveLength(2);
});

test('pagination is disabled while pending and rejects changed snapshots before retry', async ({ page }) => {
  let reads = 0;
  const next = heldResponse({ status: 200, contentType: 'application/json',
    body: JSON.stringify(pageData([alert({ alertId: secondId, ruleName: 'Inconsistent memory' })], { snapshotUtc: '2026-08-24T00:01:00Z' })) });
  await mount(page, async route => {
    if (isRead(route)) {
      reads++;
      await json(route, pageData([alert()], { nextCursor: reads === 1 ? cursor : null })); return true;
    }
    if (isRead(route, target, cursor)) { await next.handle(route); return true; }
    return false;
  });
  await expect(detail(page)).toBeVisible();
  await page.getByRole('button', { name: 'Load more', exact: true }).click();
  await next.started;
  await expect(page.getByRole('button', { name: 'Loading…', exact: true })).toBeDisabled();
  expect(contexts.get(page).requests).toHaveLength(2);
  await next.release();
  await expect(page.getByRole('alert')).toContainText('Alert snapshot changed while paging. Refresh the server alerts.');
  await expect(page.getByRole('button', { name: 'Inconsistent memory', exact: true })).toHaveCount(0);
  await expect(page.getByText('1 shown · 1 loaded', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(page.getByRole('alert')).toHaveCount(0);
  await expect(detail(page)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Load more', exact: true })).toBeDisabled();
  expect(reads).toBe(2);
});

test('malformed target evidence reports an accessible error and a retry replaces it', async ({ page }) => {
  let reads = 0;
  const first = heldResponse({ status: 200, contentType: 'application/json',
    body: JSON.stringify(pageData([alert()], { targetId: otherTarget })) });
  await mount(page, async route => {
    if (!isRead(route)) return false;
    reads++;
    if (reads === 1) await first.handle(route);
    else await json(route, pageData([alert({ value: 0 })]));
    return true;
  }, firstId);
  await first.started;
  await expect(page.getByRole('status').filter({ hasText: 'Loading target-scoped alerts…' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Load more', exact: true })).toBeDisabled();
  await first.release();
  await expect(page.getByRole('alert')).toContainText('Alerts response is invalid.');
  await expect(page.getByRole('complementary')).toHaveCount(0);
  await expect(page.getByText('Selected alert evidence is unavailable.', { exact: false })).toBeVisible();
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(detail(page)).toBeVisible();
  await expect(page.getByRole('alert')).toHaveCount(0);
  await expect(detail(page).locator('.alert-observed strong')).toHaveText('0');
  await expect(page.getByRole('status').filter({ hasText: 'Loading target-scoped alerts…' })).toHaveCount(0);
  expect(reads).toBe(2);
});

test('changing server context aborts pending evidence and excludes its late response', async ({ page }) => {
  const old = heldResponse({ status: 200, contentType: 'application/json', body: JSON.stringify(pageData([alert()])) });
  await mount(page, async route => {
    if (isRead(route)) { await old.handle(route); return true; }
    if (isRead(route, otherTarget)) {
      await json(route, pageData([alert({ alertId: secondId, ruleName: 'Reporting memory' })], { targetId: otherTarget })); return true;
    }
    return false;
  });
  const request = await old.started;
  const cancelled = page.waitForEvent('requestfailed', { predicate: failed => failed === request });
  await page.getByLabel('Server context').selectOption(otherTarget);
  await cancelled;
  await expect(detail(page, 'Reporting memory')).toContainText('Reporting SQL');
  await old.release();
  await expect(page.getByRole('button', { name: 'CPU high', exact: true })).toHaveCount(0);
  await expect(detail(page, 'Reporting memory').getByRole('status')).toContainText('firing');
  await expect(page.getByRole('alert')).toHaveCount(0);
  expect(contexts.get(page).requests).toHaveLength(2);
});

test('changing the selected alert aborts acknowledgement without applying its late success', async ({ page }) => {
  const old = heldResponse({ status: 204 });
  await mount(page, async route => {
    if (isRead(route)) {
      await json(route, pageData([alert(), alert({ alertId: secondId, ruleName: 'Memory high' })])); return true;
    }
    if (isAcknowledge(route)) { await old.handle(route); return true; }
    return false;
  });
  await expect(detail(page)).toBeVisible();
  await page.getByLabel('Operator permission').check();
  await page.getByRole('button', { name: 'Acknowledge', exact: true }).click();
  const request = await old.started;
  const cancelled = page.waitForEvent('requestfailed', { predicate: failed => failed === request });
  await page.getByRole('button', { name: 'Memory high', exact: true }).click();
  await cancelled;
  await expect(detail(page, 'Memory high').getByRole('button', { name: 'Acknowledge', exact: true })).toBeEnabled();
  await old.release();
  await expect(detail(page, 'Memory high').getByRole('status')).toContainText('firing');
  await expect(page.getByRole('status').filter({ hasText: 'Acknowledgement recorded.' })).toHaveCount(0);
  await page.getByRole('button', { name: 'CPU high', exact: true }).click();
  await expect(detail(page).getByRole('status')).toContainText('firing');
  await expect(detail(page).getByRole('button', { name: 'Acknowledge', exact: true })).toBeEnabled();
  expect(contexts.get(page).requests.map(request => request.method())).toEqual(['GET', 'POST']);
});
