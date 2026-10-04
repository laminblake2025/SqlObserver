import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {tempDbHistoryBackend,tempDbHistoryReadBinding,tempDbHistoryJson} from './tempDbWindowHistoryFixture.mjs';
import { target, otherTarget, fromUtc, toUtc, requestPath, routeHash, fixture } from './operationsFixture.mjs';
import { nativeValue } from './analyticsWorkflowFixture.mjs';

const origin = 'http://127.0.0.1:4328';
const sessions = new WeakMap();
const screenshots = fileURLToPath(new URL('../../artifacts/dashboard-agent-activity-source-2026-10-04/screenshots/', import.meta.url));
const panel = page => page.getByRole('region', { name: 'Health checks', exact: true });
const categories = page => panel(page).getByRole('tablist', { name: 'Health check categories', exact: true });
const tab = (page, name) => categories(page).getByRole('tab', { name, exact: true });

function key(path) {
  const url = new URL(path, origin);
  return `${url.pathname}?${new URLSearchParams([...url.searchParams].sort(([a], [b]) => a.localeCompare(b)))}`;
}

test.beforeEach(async ({ page }) => {
  const session = { registry: new Map(), requests: [], unexpected: [], tempdb:tempDbHistoryBackend() };
  sessions.set(page, session);
  for (const id of [target, otherTarget]) {
    for (const [kind, body] of Object.entries(fixture(id))) session.registry.set(key(requestPath(kind, id)), body);
    for (const surface of ['replication/status', 'replication/evidence']) {
      const query = new URLSearchParams({ fromUtc, toUtc, limit: '200' });
      session.registry.set(key(`/api/v1/observation-targets/${id}/analytics/${surface}?${query}`),
        nativeValue({ targetId: id, surface, fromUtc, toUtc, limit: 200 }, { generation: 1, revision: 4, total: 0 }));
    }
  }
  page.on('pageerror', error => session.unexpected.push(`Page error: ${error.message}`));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    if (url.origin !== origin) {
      session.unexpected.push(`External request: ${request.method()} ${url.href}`);
      return route.abort();
    }
    if (!/^\/api(?:\/|$)/.test(url.pathname)) return route.continue();
    const historyRead=tempDbHistoryReadBinding(url);if(historyRead){if(request.method()!=='GET'){session.unexpected.push('Non-read TempDB history request');return route.abort();}session.requests.push(request);const b=session.tempdb;return tempDbHistoryJson(route,historyRead.selection?b.snapshot(historyRead.binding,historyRead.selection,historyRead.cursor,historyRead.limit):b.read(historyRead.binding,historyRead.cursor,historyRead.limit));}
    const body = request.method() === 'GET' && session.registry.get(key(url.href));
    if (!body) {
      session.unexpected.push(`Unregistered API: ${request.method()} ${url.href}`);
      return route.abort();
    }
    session.requests.push(request);
    return route.fulfill({ status: 200, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(body) });
  });
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
});

test.afterEach(async ({ page }) => {
  const session = sessions.get(page);
  expect(session.unexpected, 'Unknown API/external requests are aborted; page errors fail this suite').toEqual([]);
  expect(session.requests.every(request => request.method() === 'GET'), 'Tab navigation remains read-only').toBe(true);
});

async function selected(page, name, check) {
  await expect(tab(page, name)).toBeFocused();
  await expect(tab(page, name)).toHaveAttribute('aria-selected', 'true');
  await expect.poll(() => page.evaluate(() => Object.fromEntries(new URLSearchParams(location.hash.split('?')[1])))).toEqual({
    target, databaseId: '7', range: 'custom', from: fromUtc, to: toUtc, compare: '1', check,
  });
}

async function paintedFocus(page, name, forcedColors) {
  const focused = tab(page, name);
  await expect(focused).toBeFocused();
  // Measure the painted outline, including offset, against the clipping scrollport.
  // Focus identity alone would pass with the old outward outline that the scroller cuts off.
  await expect.poll(() => focused.evaluate(element => {
    const owner = element.closest('[role="tablist"]');
    const bounds = element.getBoundingClientRect(), parent = owner.getBoundingClientRect();
    const style = getComputedStyle(element);
    const width = parseFloat(style.outlineWidth), offset = parseFloat(style.outlineOffset);
    const outward = width + offset;
    const ring = { left: bounds.left - outward, right: bounds.right + outward,
      top: bounds.top - outward, bottom: bounds.bottom + outward };
    const viewport = { left: parent.left + owner.clientLeft, top: parent.top + owner.clientTop,
      right: parent.left + owner.clientLeft + owner.clientWidth, bottom: parent.top + owner.clientTop + owner.clientHeight };
    const clipped = { left: Math.max(0, viewport.left - ring.left), right: Math.max(0, ring.right - viewport.right),
      top: Math.max(0, viewport.top - ring.top), bottom: Math.max(0, ring.bottom - viewport.bottom) };
    return { focusVisible: element.matches(':focus-visible'), outlineWidth: width, outlineOffset: offset,
      outlineStyle: style.outlineStyle, outlineColor: style.outlineColor,
      width: bounds.width, height: bounds.height, overflowX: getComputedStyle(owner).overflowX,
      ring, viewport, clipped, clippedPixels: Math.max(...Object.values(clipped)),
      scrollLeft: owner.scrollLeft, remaining: owner.scrollWidth - owner.clientWidth - owner.scrollLeft,
      overflows: owner.scrollWidth > owner.clientWidth + 1 };
  })).toMatchObject({ focusVisible: true, outlineWidth: 3, outlineOffset: -5, outlineStyle: 'solid',
    clipped: { left: 0, right: 0, top: 0, bottom: 0 }, clippedPixels: 0 });
  const geometry = await focused.evaluate(element => {
    const owner = element.closest('[role="tablist"]'), style = getComputedStyle(element), bounds = element.getBoundingClientRect();
    return { width: bounds.width, height: bounds.height, color: style.outlineColor,
      scrollLeft: owner.scrollLeft, remaining: owner.scrollWidth - owner.clientWidth - owner.scrollLeft,
      overflows: owner.scrollWidth > owner.clientWidth + 1 };
  });
  expect(geometry.width).toBeGreaterThanOrEqual(44);
  expect(geometry.height).toBeGreaterThanOrEqual(44);
  expect(geometry.color).not.toBe('rgba(0, 0, 0, 0)');
  const expectedColor = await focused.evaluate((element, forced) => {
    const probe = document.createElement('span');
    probe.style.color = forced ? 'Highlight' : 'var(--focus)';
    element.append(probe);
    const color = getComputedStyle(probe).color;
    probe.remove();
    return color;
  }, forcedColors);
  expect(geometry.color, 'The painted indicator uses the theme focus token or forced-colors Highlight').toBe(expectedColor);
  return geometry;
}

async function capture(page, name) {
  await mkdir(screenshots, { recursive: true });
  await page.screenshot({ path: `${screenshots}/${name}.png`, fullPage: true });
}

for (const width of [320, 390, 768, 1440]) {
  for (const forcedColors of [false, true]) {
    test(`Operations tab focus stays painted inside the scroller at ${width}${forcedColors ? ' in forced colors' : ' in dark theme'}`, async ({ page }) => {
      await page.setViewportSize({ width, height: 1000 });
      await page.emulateMedia({ colorScheme: 'dark', forcedColors: forcedColors ? 'active' : 'none' });
      await page.goto(`/component-tests/operations.html${routeHash(target, 'backups')}`);
      await expect(page.getByRole('heading', { name: 'Operations panel harness', exact: true })).toBeVisible();
      await expect(panel(page).getByRole('region', { name: 'Backup freshness assessments', exact: true })).toContainText('no database verdicts are available for this received assessment');

      // Enter the roving tablist from the preceding real keyboard control.
      await page.getByRole('button', { name: 'Refresh context', exact: true }).focus();
      await page.keyboard.press('Tab');
      await selected(page, 'Backups', 'backups');
      const first = await paintedFocus(page, 'Backups', forcedColors);
      expect(first.scrollLeft).toBeLessThanOrEqual(1);
      if (width <= 390) expect(first.overflows, 'The narrow test must exercise real clipping, not a fitting tab row').toBe(true);
      await capture(page, `operations-tab-focus-${width}-${forcedColors ? 'forced-colors-' : ''}first`);

      await page.keyboard.press('End');
      await selected(page, 'Replication', 'replication');
      const last = await paintedFocus(page, 'Replication', forcedColors);
      if (last.overflows) {
        expect(last.scrollLeft).toBeGreaterThan(0);
        expect(last.remaining).toBeLessThanOrEqual(1);
      }
      await expect(panel(page).getByRole('region', { name: 'Replication status', exact: true })).toContainText('Received page 1');
      await expect(panel(page).getByRole('region', { name: 'Replication evidence', exact: true })).toContainText('Received page 1');
      await capture(page, `operations-tab-focus-${width}-${forcedColors ? 'forced-colors-' : ''}last`);

      await page.keyboard.press('ArrowLeft');
      await selected(page, 'Availability groups', 'availability');
      await paintedFocus(page, 'Availability groups', forcedColors);
      await page.keyboard.press('ArrowRight');
      await selected(page, 'Replication', 'replication');
      await paintedFocus(page, 'Replication', forcedColors);
      await page.keyboard.press('ArrowRight');
      await selected(page, 'Backups', 'backups');
      await paintedFocus(page, 'Backups', forcedColors);
      await page.keyboard.press('ArrowLeft');
      await selected(page, 'Replication', 'replication');
      await paintedFocus(page, 'Replication', forcedColors);
      await page.keyboard.press('Home');
      await selected(page, 'Backups', 'backups');
      const home = await paintedFocus(page, 'Backups', forcedColors);
      expect(home.scrollLeft).toBeLessThanOrEqual(1);
      await page.keyboard.press('ArrowRight');
      await selected(page, 'SQL Agent', 'agent');
      await paintedFocus(page, 'SQL Agent', forcedColors);
      await page.keyboard.press('ArrowRight');
      await selected(page, 'TempDB', 'tempdb');
      await paintedFocus(page, 'TempDB', forcedColors);
      await page.keyboard.press('Home');
      await selected(page, 'Backups', 'backups');
      await paintedFocus(page, 'Backups', forcedColors);
      await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      if (!forcedColors) {
        await expect(panel(page).getByRole('region', { name: 'Backup freshness assessments', exact: true })).toContainText('no database verdicts are available for this received assessment');
        const result = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze();
        expect(result.violations).toEqual([]);
      }
    });
  }
}
