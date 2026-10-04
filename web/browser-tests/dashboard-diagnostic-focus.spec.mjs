import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { mkdir } from 'node:fs/promises';
import { resolve } from 'node:path';

const origin = 'http://127.0.0.1:4322';
const target = '11111111-1111-4111-8111-000000000001';
const from = '2026-10-02T08:00:00.000Z', to = '2026-10-02T12:00:00.000Z';
const screenshots = resolve('..', 'artifacts', 'dashboard-diagnostic-focus-2026-10-04', 'screenshots');
const runs = new WeakMap();
const href = surface => '/#/' + surface + '?' + new URLSearchParams({ target, range: 'custom', from, to, compare: '1' });
const scope = page => Object.fromEntries(new URLSearchParams(new URL(page.url()).hash.split('?')[1]));

test.beforeEach(async ({ page }) => {
  const run = { requests: [], errors: [], pending: new Set() }; runs.set(page, run);
  await page.clock.setFixedTime(new Date('2026-10-03T12:00:00Z'));
  await page.context().addCookies(['dashboardRefactor', 'nativeInsightsFixture', 'queryRowTrendFixture', 'databaseScopeFixture'].map(name => ({ name, value: '1', url: origin })).concat([
    { name: 'chartReferenceRole', value: 'viewer', url: origin },
  ]));
  page.on('pageerror', error => run.errors.push(error.message));
  page.on('request', request => {
    if (!new URL(request.url()).pathname.startsWith('/api/')) return;
    run.requests.push({ method: request.method(), url: request.url() }); run.pending.add(request);
  });
  page.on('requestfinished', request => run.pending.delete(request));
  page.on('requestfailed', request => run.pending.delete(request));
});
test.afterEach(async ({ page }) => {
  expect(runs.get(page).errors).toEqual([]);
  expect(runs.get(page).requests.every(request => request.method === 'GET'), 'Focus and disclosure browsing never mutate native evidence').toBe(true);
});

async function settled(page) {
  await expect.poll(() => runs.get(page).pending.size).toBe(0);
  await page.evaluate(() => new Promise(resolveFrame => requestAnimationFrame(() => requestAnimationFrame(resolveFrame))));
}
async function noPageOverflow(page) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
}
async function capture(page, name) {
  await mkdir(screenshots, { recursive: true });
  const retained = await page.evaluate(async () => {
    const active = document.activeElement;
    const navigation = document.querySelector('nav[aria-label="Main navigation"]');
    const localScroll = navigation?.scrollLeft;
    window.scrollTo({ top: 0, behavior: 'instant' });
    await new Promise(resolveFrame => requestAnimationFrame(() => requestAnimationFrame(resolveFrame)));
    return { focus: document.activeElement === active, localScroll: navigation?.scrollLeft === localScroll, top: window.scrollY };
  });
  expect(retained).toEqual({ focus: true, localScroll: true, top: 0 });
  await page.screenshot({ path: resolve(screenshots, name + '.png'), fullPage: true });
}
async function paintedFocus(control, clippingSelector, forced, expectedOffset) {
  await expect(control).toBeFocused();
  await expect.poll(() => control.evaluate((element, selector) => {
    const owner = element.closest(selector), style = getComputedStyle(element);
    const box = element.getBoundingClientRect(), parent = owner.getBoundingClientRect();
    const width = parseFloat(style.outlineWidth), offset = parseFloat(style.outlineOffset), reach = width + offset;
    const ring = { left: box.left - reach, right: box.right + reach, top: box.top - reach, bottom: box.bottom + reach };
    const viewport = { left: parent.left + owner.clientLeft, top: parent.top + owner.clientTop,
      right: parent.left + owner.clientLeft + owner.clientWidth, bottom: parent.top + owner.clientTop + owner.clientHeight };
    return { focusVisible: element.matches(':focus-visible'), width, offset, style: style.outlineStyle,
      clipped: { left: Math.max(0, viewport.left - ring.left), right: Math.max(0, ring.right - viewport.right),
        top: Math.max(0, viewport.top - ring.top), bottom: Math.max(0, ring.bottom - viewport.bottom) } };
  }, clippingSelector)).toMatchObject({ focusVisible: true, width: 3, offset: expectedOffset, style: 'solid',
    clipped: { left: 0, right: 0, top: 0, bottom: 0 } });
  const geometry = await control.evaluate((element, forcedColors) => {
    const box = element.getBoundingClientRect(), style = getComputedStyle(element), probe = document.createElement('span');
    probe.style.color = forcedColors ? 'Highlight' : 'var(--focus)'; element.append(probe);
    const expectedColor = getComputedStyle(probe).color; probe.remove();
    return { width: box.width, height: box.height, color: style.outlineColor, expectedColor };
  }, forced);
  expect(geometry.width).toBeGreaterThanOrEqual(44); expect(geometry.height).toBeGreaterThanOrEqual(44);
  expect(geometry.color).toBe(geometry.expectedColor);
}

for (const width of [320, 390, 768, 1440]) for (const forced of [false, true])
  test(`diagnostic navigation, query controls and Waits insets preserve ${forced ? 'forced-color' : 'dark'} focus at ${width}px`, async ({ page }, info) => {
    await page.setViewportSize({ width, height: 1000 });
    await page.emulateMedia({ forcedColors: forced ? 'active' : 'none', colorScheme: 'dark' });
    await page.goto(href('queries'));
    const tabs = page.getByRole('tablist', { name: 'Query views', exact: true });
    await expect(tabs).toBeVisible(); await settled(page);
    const navigation = page.getByRole('navigation', { name: 'Main navigation', exact: true });
    if (forced) {
      const background = await page.locator('.sidebar').evaluate(element => {
        const probe = document.createElement('span');
        probe.style.backgroundColor = 'Canvas'; element.append(probe);
        const expected = getComputedStyle(probe).backgroundColor;
        probe.remove();
        return { actual: getComputedStyle(element).backgroundColor, expected };
      });
      expect(background.actual).toBe(background.expected);
      expect(background.actual).not.toBe('rgba(0, 0, 0, 0)');
      expect(background.actual).not.toBe('transparent');
    }
    const links = navigation.getByRole('link');
    expect(await links.count()).toBeGreaterThan(2);
    const before = page.url(), readCount = runs.get(page).requests.length;
    await page.getByRole('link', { name: 'Skip to content', exact: true }).focus();
    await page.keyboard.press('Tab');
    await paintedFocus(links.first(), width <= 960 ? 'nav' : '.sidebar', forced, width <= 960 ? -3 : 3);
    await capture(page, `diagnostic-${forced ? 'forced' : 'dark'}-${width}-nav-first`);
    for (let index = 1; index < await links.count(); index++) await page.keyboard.press('Tab');
    await paintedFocus(links.last(), width <= 960 ? 'nav' : '.sidebar', forced, width <= 960 ? -3 : 3);
    await capture(page, `diagnostic-${forced ? 'forced' : 'dark'}-${width}-nav-last`);
    if (width <= 960) {
      expect(await navigation.evaluate(element => element.scrollWidth > element.clientWidth)).toBe(true);
      expect(await navigation.evaluate(element => element.scrollLeft + element.clientWidth)).toBeGreaterThanOrEqual(await navigation.evaluate(element => element.scrollWidth - 1));
    }
    for (let index = 1; index < await links.count(); index++) await page.keyboard.press('Shift+Tab');
    await paintedFocus(links.first(), width <= 960 ? 'nav' : '.sidebar', forced, width <= 960 ? -3 : 3);
    expect(page.url()).toBe(before); expect(runs.get(page).requests).toHaveLength(readCount);

    const queryTabs = tabs.getByRole('tab');
    await queryTabs.first().focus(); await page.keyboard.press('Home');
    for (const [key, index] of [['Home', 0], ['End', 2], ['Home', 0], ['ArrowRight', 1], ['ArrowRight', 2], ['ArrowRight', 0], ['ArrowLeft', 2], ['Home', 0]]) {
      await page.keyboard.press(key); await expect(queryTabs.nth(index)).toHaveAttribute('aria-selected', 'true');
      await paintedFocus(queryTabs.nth(index), '[role="tablist"]', forced, -5);
      expect(scope(page)).toMatchObject({ target, range: 'custom', from, to, compare: '1' });
      await expect.poll(() => scope(page).queryView ?? 'top').toBe(['top', 'regressed', 'cache'][index]);
    }
    await capture(page, `diagnostic-${forced ? 'forced' : 'dark'}-${width}-query-tabs`);
    const trends = page.getByRole('group', { name: /^Ranking trend:/ });
    await expect(trends.first()).toBeVisible();
    await trends.first().scrollIntoViewIfNeeded();
    const inspect = trends.first().getByRole('button', { name: 'Inspect trend', exact: true });
    await expect(inspect).toBeVisible();
    const inspectBox = await inspect.boundingBox(); expect(inspectBox.width).toBeGreaterThanOrEqual(44); expect(inspectBox.height).toBeGreaterThanOrEqual(44);
    const coverage = trends.first().locator('summary').filter({ hasText: 'Trend values and coverage' });
    await expect(coverage).toBeVisible();
    const coverageBox = await coverage.boundingBox(); expect(coverageBox.width).toBeGreaterThanOrEqual(44); expect(coverageBox.height).toBeGreaterThanOrEqual(44);
    await settled(page);
    const trendUrl = page.url(), trendReads = runs.get(page).requests.length;
    await coverage.focus(); await coverage.press('Enter');
    await expect(coverage.locator('..')).toHaveAttribute('open');
    await coverage.press('Space'); await expect(coverage.locator('..')).not.toHaveAttribute('open');
    expect(page.url()).toBe(trendUrl); expect(runs.get(page).requests).toHaveLength(trendReads);
    await noPageOverflow(page);
    if (!forced) {
      const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations;
      await info.attach(`queries-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
    }

    await page.goto(href('waits'));
    await expect(page.getByRole('heading', { name: /^Waits for / })).toBeVisible();
    const bare = page.locator('.waits-evidence-panel'); await expect(bare).toHaveCount(3);
    for (const panel of await bare.all()) {
      const geometry = await panel.evaluate(element => {
        const style = getComputedStyle(element), box = element.getBoundingClientRect(), heading = element.querySelector('h3').getBoundingClientRect();
        return { left: parseFloat(style.paddingLeft), right: parseFloat(style.paddingRight), inset: heading.left - box.left,
          width: box.width, clientWidth: element.clientWidth, scrollWidth: element.scrollWidth };
      });
      expect(geometry.left).toBe(width <= 560 ? 14 : 20); expect(geometry.right).toBe(geometry.left);
      expect(geometry.inset).toBeGreaterThanOrEqual(geometry.left); expect(geometry.scrollWidth - geometry.clientWidth).toBeLessThanOrEqual(1);
    }
    const captured = page.getByRole('region', { name: 'Captured sessions by server wait category', exact: true });
    await expect(captured).toBeVisible();
    expect(await captured.evaluate(element => element.classList.contains('waits-evidence-panel'))).toBe(false);
    expect(await captured.evaluate(element => parseFloat(getComputedStyle(element).paddingLeft))).toBe(20);
    const category = page.getByRole('combobox', { name: 'Query Store category', exact: true });
    await category.focus(); await page.keyboard.press('Tab'); await page.keyboard.press('Shift+Tab'); await expect(category).toBeFocused();
    await noPageOverflow(page); await capture(page, `diagnostic-${forced ? 'forced' : 'dark'}-${width}-waits-insets`);
    if (!forced) {
      const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations;
      await info.attach(`waits-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
    }
    // The fourth bare panel is Live-only; cover it without changing retained-window expectations above.
    await page.goto('/#/waits?' + new URLSearchParams({ target, range: '1h' }));
    const current = page.getByRole('region', { name: 'Current server waits', exact: true });
    await expect(current).toBeVisible(); await expect(page.locator('.waits-evidence-panel')).toHaveCount(4);
    const currentInset = await current.evaluate(element => {
      const style = getComputedStyle(element), bounds = element.getBoundingClientRect();
      return { padding: parseFloat(style.paddingLeft), inset: element.querySelector('h3').getBoundingClientRect().left - bounds.left };
    });
    expect(currentInset.padding).toBe(width <= 560 ? 14 : 20);
    expect(currentInset.inset).toBeGreaterThanOrEqual(currentInset.padding); await noPageOverflow(page);
  });
