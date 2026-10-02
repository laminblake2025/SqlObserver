import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { target, otherTarget, firstId, secondId, retainedId, firstAt, secondAt, retainedAt,
  fromUtc, toUtc, cursor, providerText, summary, secondSummary, eventPage, eventDetail, routeHash } from './deadlockFixture.mjs';

const contexts = new WeakMap();
const origin = 'http://127.0.0.1:4328';
const listPath = id => `/api/v1/observation-targets/${id}/deadlocks`;
const detailPath = (id, eventId) => `${listPath(id)}/${eventId}`;
const detail = page => page.getByRole('region', { name: 'Selected deadlock detail', exact: true });
const eventCards = page => page.getByRole('complementary', { name: 'Captured deadlock events' }).locator('button[aria-controls="selected-deadlock-detail"]');
const activityLink = page => detail(page).getByRole('link', { name: 'View activity at event time' });

function isList(route, id = target, nextCursor) {
  const request = route.request();
  const url = new URL(request.url());
  const expected = { limit: '25', fromUtc, toUtc, ...(nextCursor === undefined ? {} : { cursor: nextCursor }) };
  return request.method() === 'GET' && url.pathname === listPath(id)
    && url.searchParams.size === Object.keys(expected).length
    && Object.entries(expected).every(([key, value]) => url.searchParams.get(key) === value);
}
function isDetail(route, eventId = firstId, id = target) {
  const url = new URL(route.request().url());
  return route.request().method() === 'GET' && url.pathname === detailPath(id, eventId) && url.search === '';
}
async function json(route, body, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(body) });
}
function deferred() {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
}
// Keep native fetch and AbortSignal intact; only delay the registered API response.
function held(body) {
  const started = deferred(), released = deferred(), completed = deferred();
  return {
    started: started.promise,
    release: async () => { released.resolve(); await completed.promise; },
    handle: async route => {
      started.resolve(route.request());
      await released.promise;
      try { await json(route, body); }
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
    if (url.origin !== origin) {
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
  expect(contexts.get(page).unexpected, 'API traffic must be fulfilled locally; external traffic and page errors fail the test').toEqual([]);
});
async function mount(page, handler, selection, displayName) {
  contexts.get(page).handler = handler;
  const query = displayName ? `?${new URLSearchParams({ displayName })}` : '';
  await page.goto(`/component-tests/deadlocks.html${query}${routeHash(target, selection)}`);
  await expect(page.getByRole('heading', { name: 'Deadlock panel harness' })).toBeVisible();
}
async function expectLink(page, eventId, at, id = target) {
  await expect(activityLink(page)).toBeVisible();
  await expect.poll(() => activityLink(page).getAttribute('href')).toContain(`event=${eventId}`);
  const href = await activityLink(page).getAttribute('href');
  const url = new URL(href, origin);
  expect(url.hash.split('?')[0]).toBe('#/activity');
  expect(Object.fromEntries(new URLSearchParams(url.hash.split('?')[1]))).toEqual({
    target: id, databaseId: '7', range: 'custom', from: fromUtc, to: toUtc, compare: '1', at, event: eventId,
  });
}

test('selecting an event loads its exact native detail and carries event provenance into Activity', async ({ page }) => {
  const reads = [];
  const second = secondSummary();
  await mount(page, async route => {
    if (isList(route)) { await json(route, eventPage()); return true; }
    if (isDetail(route)) { reads.push(firstId); await json(route, eventDetail()); return true; }
    if (isDetail(route, secondId)) {
      reads.push(secondId);
      await json(route, eventDetail(second, {
        participants: [{ sessionId: 61, isVictim: false }, { sessionId: 62, isVictim: true }],
        relations: [{ waiterSessionId: 62, blockerSessionId: 61, resourceCategory: 'page', lockMode: 'S' }],
      })); return true;
    }
    return false;
  });
  await expectLink(page, firstId, firstAt);
  await expect(eventCards(page).nth(0)).toHaveAttribute('aria-pressed', 'true');
  await eventCards(page).nth(1).click();
  await expectLink(page, secondId, secondAt);
  await expect(eventCards(page).nth(0)).toHaveAttribute('aria-pressed', 'false');
  await expect(eventCards(page).nth(1)).toHaveAttribute('aria-pressed', 'true');
  const cells = detail(page).getByRole('table').getByRole('row').nth(1).getByRole('cell');
  await expect(cells).toHaveText(['62', '61', 'page', 'S']);
  await expect(detail(page).getByRole('img', { name: 'Deadlock relationship diagram' })).toContainText('62→61');
  await expect(detail(page)).toContainText('SQL Server selected session 62 as the victim.');
  expect(reads).toEqual([firstId, secondId]);
  // This verifies link provenance only, not automatic destination capture selection.
});

test('provider text stays inert while truncated detail displays only available known evidence', async ({ page }) => {
  const truncated = summary({ parseTruncated: true, participantCount: 1, relationCount: 0,
    rawXml: providerText, unknownProviderField: 'UNREVIEWED_PROVIDER_SECRET' });
  await mount(page, async route => {
    if (isList(route)) { await json(route, eventPage([truncated])); return true; }
    if (isDetail(route)) {
      await json(route, eventDetail(truncated, {
        participants: [{ sessionId: 73, isVictim: false, providerName: providerText }],
        relations: [], rawXml: providerText, unknownProviderField: 'UNREVIEWED_PROVIDER_SECRET',
      })); return true;
    }
    return false;
  }, undefined, providerText);
  await expect(detail(page)).toContainText('Truncated capture');
  await expect(detail(page)).toContainText('they do not infer missing participants');
  await expect(detail(page)).toContainText('1 participants · 0 relationships');
  await expect(detail(page).getByRole('table')).toContainText('No lock relationships were reported.');
  await expect(detail(page)).toContainText('Victim: not identified');
  await expect(page.locator('.screen-subtitle')).toHaveText(`Captured events and lock relationships for ${providerText}`);
  await expect(page.locator('img, [onerror]')).toHaveCount(0);
  await expect(detail(page)).not.toContainText('UNREVIEWED_PROVIDER_SECRET');
  await expect(detail(page)).not.toContainText(providerText);
  expect(await page.evaluate(() => window.deadlockInjected)).toBeUndefined();
});

test('an off-page retained event keeps its exact microsecond occurrence and rejects a same-ID mismatch', async ({ page }) => {
  const selected = summary({ eventId: retainedId, occurredAtUtc: retainedAt, fingerprint: 'c'.repeat(64) });
  let reads = 0;
  await mount(page, async route => {
    if (isList(route)) { await json(route, eventPage([summary()])); return true; }
    if (isDetail(route, retainedId)) {
      reads++;
      await json(route, eventDetail(reads === 2 ? { ...selected, occurredAtUtc: '2026-08-24T00:30:00.123457Z' } : selected));
      return true;
    }
    return false;
  }, selected);
  await expectLink(page, retainedId, retainedAt);
  await expect(detail(page)).toContainText('not on the loaded event page');
  await expect(eventCards(page).nth(0)).toHaveAttribute('aria-pressed', 'false');
  await page.getByRole('button', { name: 'Refresh context' }).click();
  await expect(detail(page).getByRole('alert')).toContainText('No other event has been selected.');
  await expect(activityLink(page)).toHaveCount(0);
  await expect(detail(page).getByRole('table')).toHaveCount(0);
  await expect(eventCards(page).nth(0)).toHaveAttribute('aria-pressed', 'false');
  await page.getByRole('button', { name: 'Refresh context' }).click();
  await expectLink(page, retainedId, retainedAt);
  expect(reads).toBe(3);
});

test('bounded paging retains the selected event and a failed list refresh retries the first page', async ({ page }) => {
  let initialReads = 0;
  const next = held(eventPage([secondSummary()]));
  await mount(page, async route => {
    if (isList(route)) {
      initialReads++;
      await json(route, eventPage([summary()], { nextCursor: cursor }), initialReads === 2 ? 503 : 200);
      return true;
    }
    if (isList(route, target, cursor)) { await next.handle(route); return true; }
    if (isDetail(route)) { await json(route, eventDetail()); return true; }
    return false;
  }, summary());
  await expectLink(page, firstId, firstAt);
  await page.getByRole('button', { name: 'Next', exact: true }).click();
  await next.started;
  for (const name of ['First page', 'Previous', 'Next']) await expect(page.getByRole('button', { name, exact: true })).toBeDisabled();
  await next.release();
  await expect(eventCards(page)).toHaveCount(1);
  await expect(eventCards(page).nth(0)).toHaveAttribute('aria-pressed', 'false');
  await expect(page.getByRole('button', { name: 'Previous', exact: true })).toBeEnabled();
  await expect(detail(page)).toContainText('not on the loaded event page');
  await expectLink(page, firstId, firstAt);
  await page.getByRole('button', { name: 'Refresh context' }).click();
  await expect(page.getByRole('alert')).toContainText('Deadlock evidence is unavailable. Refresh to retry the event list.');
  await expect(eventCards(page)).toHaveCount(0);
  await page.getByRole('button', { name: 'Refresh context' }).click();
  await expect(eventCards(page).nth(0)).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('button', { name: 'Previous', exact: true })).toBeDisabled();
  await expect(page.getByRole('alert')).toHaveCount(0);
  expect(initialReads).toBe(3);
});

test('the native reader rejects valid JSON above its byte bound for list and detail then refresh recovers', async ({ page }) => {
  const padding = 'x'.repeat(256 * 1024 + 1);
  let lists = 0, details = 0;
  await mount(page, async route => {
    if (isList(route)) {
      lists++;
      await json(route, eventPage([summary()], lists === 1 ? { unknownPadding: padding } : {})); return true;
    }
    if (isDetail(route)) {
      details++;
      await json(route, eventDetail(summary(), details === 1 ? { unknownPadding: padding } : {})); return true;
    }
    return false;
  }, summary());
  // These bodies remain below the deadlock parser's 1 MiB limit and are valid
  // JSON. Rejection exercises the transport's 256 KiB deadlock reader boundary.
  await expect(page.getByRole('alert').filter({ hasText: 'Activity evidence returned an invalid response.' })).toBeVisible();
  await expect(detail(page).getByRole('alert')).toContainText('No other event has been selected.');
  await expect(activityLink(page)).toHaveCount(0);
  await expect(eventCards(page)).toHaveCount(0);
  await page.getByRole('button', { name: 'Refresh context' }).click();
  await expectLink(page, firstId, firstAt);
  await expect(page.getByRole('alert')).toHaveCount(0);
  expect([lists, details]).toEqual([2, 2]);
});

test('changing server context cancels pending list and detail and excludes both late responses', async ({ page }) => {
  const oldList = held(eventPage());
  const oldDetail = held(eventDetail());
  await mount(page, async route => {
    if (isList(route)) { await oldList.handle(route); return true; }
    if (isDetail(route)) { await oldDetail.handle(route); return true; }
    if (isList(route, otherTarget)) {
      await json(route, eventPage([secondSummary()], { targetId: otherTarget })); return true;
    }
    if (isDetail(route, secondId, otherTarget)) { await json(route, eventDetail(secondSummary())); return true; }
    return false;
  }, summary());
  const requests = await Promise.all([oldList.started, oldDetail.started]);
  const cancelled = requests.map(request => page.waitForEvent('requestfailed', { predicate: failed => failed === request }));
  await page.getByLabel('Server context').selectOption(otherTarget);
  await Promise.all(cancelled);
  await expectLink(page, secondId, secondAt, otherTarget);
  await oldList.release();
  await oldDetail.release();
  await expect(page.locator('.screen-subtitle')).toContainText('Reporting SQL');
  await expect(eventCards(page)).toHaveCount(1);
  await expect(detail(page)).not.toContainText('a'.repeat(64));
  await expectLink(page, secondId, secondAt, otherTarget);
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('changing event selection cancels pending detail and a late first event cannot replace the selected event', async ({ page }) => {
  const old = held(eventDetail());
  await mount(page, async route => {
    if (isList(route)) { await json(route, eventPage()); return true; }
    if (isDetail(route)) { await old.handle(route); return true; }
    if (isDetail(route, secondId)) { await json(route, eventDetail(secondSummary())); return true; }
    return false;
  });
  const request = await old.started;
  await expect(detail(page).getByRole('status')).toContainText('Loading selected event participants');
  const cancelled = page.waitForEvent('requestfailed', { predicate: failed => failed === request });
  await eventCards(page).nth(1).click();
  await cancelled;
  await expectLink(page, secondId, secondAt);
  await old.release();
  await expect(detail(page)).not.toContainText('a'.repeat(64));
  await expectLink(page, secondId, secondAt);
  await expect(eventCards(page).nth(1)).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('alert')).toHaveCount(0);
});

test('cross-target list evidence fails accessibly and refresh retries the same target before selecting detail', async ({ page }) => {
  let reads = 0;
  await mount(page, async route => {
    if (isList(route)) {
      reads++;
      await json(route, eventPage([summary()], { targetId: reads === 1 ? otherTarget : target })); return true;
    }
    if (isDetail(route)) { await json(route, eventDetail()); return true; }
    return false;
  });
  await expect(page.getByRole('alert')).toContainText('Deadlock response is invalid. Refresh to retry the event list.');
  await expect(eventCards(page)).toHaveCount(0);
  await expect(activityLink(page)).toHaveCount(0);
  expect(contexts.get(page).requests).toHaveLength(1);
  await page.getByRole('button', { name: 'Refresh context' }).click();
  await expectLink(page, firstId, firstAt);
  await expect(page.getByRole('alert')).toHaveCount(0);
  expect(reads).toBe(2);
});

test('keeps deadlock detail accessible and responsive at 320 and 1440 pixels', async ({ page }, info) => {
  await mount(page, async route => {
    if (isList(route)) { await json(route, eventPage()); return true; }
    if (isDetail(route)) { await json(route, eventDetail()); return true; }
    return false;
  });
  await expectLink(page, firstId, firstAt);
  const directory = fileURLToPath(new URL('../../artifacts/dashboard-components/screenshots/', import.meta.url));
  await mkdir(directory, { recursive: true });
  for (const width of [320, 1440]) {
    await page.setViewportSize({ width, height: 1000 });
    await activityLink(page).focus();
    await expect(activityLink(page)).toBeFocused();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
    await info.attach(`deadlock-panel-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
    expect(violations).toEqual([]);
    const path = `${directory}deadlock-panel-${width}.png`;
    await page.screenshot({ path, fullPage: true });
    await info.attach(`deadlock-panel-${width}`, { path, contentType: 'image/png' });
  }
});
