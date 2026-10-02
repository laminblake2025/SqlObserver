import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const initialFrom = '2026-01-15T12:00:00.000Z';
const initialTo = '2026-01-15T13:00:00.000Z';
const attempts = new WeakMap();
const primary = page => page.getByRole('region', { name: 'Primary chart', exact: true });
const companion = page => page.getByRole('region', { name: 'Companion chart', exact: true });
const result = (page, name) => page.getByRole('status', { name, exact: true });

async function expectCrosshairNear(page, utc) {
  // Browser geometry can round subpixel pointer positions; allow under one
  // second while still checking the callback's actual UTC timestamp.
  await expect.poll(async () => Math.abs(Date.parse(await result(page, 'Shared crosshair UTC').textContent()) - Date.parse(utc))).toBeLessThan(1000);
}

test.beforeEach(async ({ page }) => {
  const unexpected = [];
  attempts.set(page, unexpected);
  page.on('pageerror', error => unexpected.push(`Page error: ${error.message}`));
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.origin !== 'http://127.0.0.1:4328' || /^\/api(?:\/|$)/.test(url.pathname)) {
      unexpected.push(`${route.request().method()} ${url.href}`);
      await route.abort();
    } else {
      await route.continue();
    }
  });
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.goto('/component-tests/index.html');
  await expect(page.getByRole('heading', { name: 'Observation chart harness' })).toBeVisible();
});

test.afterEach(async ({ page }) => {
  expect(attempts.get(page), 'Harness must make no API/external requests and have no runtime errors').toEqual([]);
});

// Position the pointer between the actual rendered first/last observations, so
// the test does not duplicate the component's viewBox-to-time implementation.
async function pointAt(chart, fraction) {
  await chart.getByRole('img').scrollIntoViewIfNeeded();
  const markers = chart.locator('circle.chart-marker');
  const first = await markers.first().boundingBox();
  const last = await markers.last().boundingBox();
  if (!first || !last) throw new Error('Expected mounted visible chart observations.');
  return {
    x: first.x + first.width / 2 + fraction * (last.x + last.width / 2 - first.x - first.width / 2),
    y: (first.y + first.height / 2 + last.y + last.height / 2) / 2,
  };
}

async function drag(page, chart, from, to) {
  const start = await pointAt(chart, from);
  const end = await pointAt(chart, to);
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await page.mouse.move(end.x, end.y, { steps: 6 });
  await page.mouse.up();
}

for (const kind of ['observation', 'overview']) {
  for (const width of [320, 1440]) test(`${kind} maps the actual visible plot bounds to the full UTC window at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 }); await page.getByLabel('Primary chart kind').selectOption(kind);
    const chart = primary(page), svg = chart.getByRole('img'); await svg.scrollIntoViewIfNeeded();
    await page.evaluate(() => window.scrollTo({ top: scrollY, behavior: 'instant' }));
    const edge = async line => line.evaluate(element => {
      const point = element.ownerSVGElement.createSVGPoint(); point.x = element.x1.baseVal.value;
      point.y = (element.y1.baseVal.value + element.y2.baseVal.value) / 2;
      const screen = point.matrixTransform(element.getScreenCTM()); return { x: screen.x, y: screen.y };
    });
    const axes = chart.locator('.chart-gridline-vertical'), start = await edge(axes.first()), end = await edge(axes.last());
    // One pixel outside the painted plot remains within SVG, and clamps to its real boundary.
    await page.mouse.move(start.x - 1, start.y); await page.mouse.down();
    await page.mouse.move(end.x + 1, end.y, { steps: 8 }); await page.mouse.up();
    await expect(result(page, 'Selection callback count')).toHaveText('1');
    await expect(result(page, 'Selected from UTC')).toHaveText(initialFrom);
    await expect(result(page, 'Selected to UTC')).toHaveText(initialTo);
  });
  for (const replacement of ['Advance chart window', 'Replace chart target']) {
    test(`${kind} cancels a captured drag when ${replacement.toLowerCase()}`, async ({ page }) => {
      await page.getByLabel('Primary chart kind').selectOption(kind);
      const chart = primary(page), start = await pointAt(chart, .2), end = await pointAt(chart, .8);
      await page.mouse.move(start.x, start.y); await page.mouse.down();
      await page.mouse.move(end.x, end.y, { steps: 4 });
      await expect(chart.locator('.chart-selection')).toHaveCount(1);
      // Change the mounted props during pointer capture, as a workspace poll/navigation can.
      await page.getByRole('button', { name: replacement, exact: true }).evaluate(button => button.click());
      await expect(chart.locator('.chart-selection')).toHaveCount(0);
      await page.mouse.up();
      await expect(result(page, 'Selection callback count')).toHaveText('0');
      await page.getByRole('button', { name: 'Reset interaction', exact: true }).click();
      const svg = chart.getByRole('img'); await svg.scrollIntoViewIfNeeded();
      await page.evaluate(() => window.scrollTo({ top: scrollY, behavior: 'instant' }));
      const bounds = await svg.boundingBox();
      await page.mouse.move(bounds.x + bounds.width * .2, bounds.y + bounds.height * .5); await page.mouse.down();
      await page.mouse.move(bounds.x + bounds.width * .8, bounds.y + bounds.height * .5, { steps: 6 }); await page.mouse.up();
      await expect(result(page, 'Selection callback count')).toHaveText('1');
    });
  }
}

test('retains a numeric zero and excludes missing samples from accessible evidence', async ({ page }) => {
  const chart = primary(page);
  await expect(chart.locator('figcaption')).toContainText('3 plotted values');
  await expect(chart.getByRole('img')).toHaveAccessibleName(/Missing samples remain gaps/);
  await expect(chart.locator('circle.chart-marker')).toHaveCount(3);
  await chart.getByText('View chart values', { exact: true }).click();
  const values = chart.getByRole('region', { name: 'Latency evidence chart values', exact: true });
  await expect(values.getByRole('row')).toHaveCount(4);
  await expect(values.getByRole('cell', { name: '0', exact: true })).toBeVisible();
  await expect(values.locator('td[title="2026-01-15T12:15:00.000Z"]')).toHaveCount(0);
  await page.getByLabel('Evidence preset').selectOption('missing');
  await expect(chart.getByText('No numeric observations in this window. Missing values are not zero.')).toBeVisible();
  await expect(chart.getByRole('img')).toHaveCount(0);
  await expect(chart.getByRole('table')).toHaveCount(0);
});

test('renders one observation and two distinct runs without inventing evidence across a gap', async ({ page }) => {
  const chart = primary(page);
  await page.getByLabel('Evidence preset').selectOption('single');
  await expect(chart.locator('figcaption')).toContainText('1 plotted values');
  await expect(chart.locator('circle.chart-marker')).toHaveCount(1);
  // Browser-computed stroke length: a solitary marker has no connecting line.
  expect(await chart.locator('path.chart-series').evaluateAll(paths => paths.map(path => path.getTotalLength()))).toEqual([0]);
  await chart.getByText('View chart values', { exact: true }).click();
  await expect(chart.getByRole('cell', { name: '7', exact: true })).toBeVisible();
  await page.getByLabel('Evidence preset').selectOption('gap');
  await expect(chart.locator('figcaption')).toContainText('4 plotted values');
  await expect(chart.locator('circle.chart-marker')).toHaveCount(4);
  await expect(chart.getByRole('row')).toHaveCount(5);
  await expect(chart.locator('td[title="2026-01-15T12:30:00.000Z"]')).toHaveCount(0);
  const runs = await chart.locator('path.chart-series').evaluateAll(paths => paths.map(path => {
    const bounds = path.getBBox();
    return { left: bounds.x, right: bounds.x + bounds.width, width: bounds.width };
  }));
  expect(runs).toHaveLength(2);
  expect(runs[1].left - runs[0].right).toBeGreaterThan(Math.max(runs[0].width, runs[1].width));
  await expect(chart.getByText('Observed values only; lines do not imply samples between points.', { exact: false })).toBeVisible();
});

test('updates reference legends and formatted values in the accessible table', async ({ page }) => {
  const chart = primary(page);
  await page.getByLabel('Show reference values').check();
  await page.getByLabel('Format values in milliseconds').check();
  await expect(chart.locator('.observation-legend')).toContainText('Expected range');
  await expect(chart.locator('.observation-legend')).toContainText('Investigation threshold');
  await expect(chart.locator('.chart-baseline-band title')).toHaveText('Expected range: 8.0 ms to 12.0 ms');
  await expect(chart.locator('.chart-threshold title')).toHaveText('Investigation threshold: 30.0 ms');
  await chart.getByText('View chart values', { exact: true }).click();
  const table = chart.getByRole('region', { name: 'Latency evidence chart values', exact: true });
  await expect(table).toHaveAttribute('tabindex', '0');
  await expect(table.getByRole('columnheader')).toHaveText(['Series', 'UTC', 'Value']);
  await expect(table.getByRole('cell', { name: '0.0 ms', exact: true })).toBeVisible();
  await expect(table.getByRole('cell', { name: '40.0 ms', exact: true })).toBeVisible();
  await page.getByLabel('Show reference values').uncheck();
  await expect(chart.locator('.observation-legend')).not.toContainText('Expected range');
  await expect(chart.locator('.chart-baseline-band, .chart-threshold')).toHaveCount(0);
});

test('shares controlled pointer time between charts and clears both on leave', async ({ page }) => {
  const first = primary(page), second = companion(page);
  const middle = await pointAt(first, .5);
  await page.mouse.move(middle.x, middle.y);
  await expectCrosshairNear(page, '2026-01-15T12:30:00.000Z');
  await expect(first.locator('.chart-nearest')).toContainText('Latency 20');
  await expect(first.locator('.chart-nearest')).toContainText('Uncollected unavailable');
  await expect(second.locator('.chart-nearest')).toContainText('Throughput 10');
  await expect(second.locator('.chart-crosshair')).toHaveCount(1);
  await expect(first.locator('.chart-crosshair text')).toHaveText(await second.locator('.chart-crosshair text').textContent());
  const later = await pointAt(second, .75);
  await page.mouse.move(later.x, later.y);
  await expectCrosshairNear(page, '2026-01-15T12:45:00.000Z');
  await expect(first.locator('.chart-crosshair')).toHaveCount(1);
  await expect(second.locator('.chart-crosshair')).toHaveCount(1);
  await page.mouse.move(0, 0);
  await expect(result(page, 'Shared crosshair UTC')).toHaveText('None');
  await expect(page.locator('.chart-crosshair, .chart-nearest')).toHaveCount(0);
});

test('selects a normalized shared time window and cancels or ignores incomplete drags', async ({ page }) => {
  const chart = primary(page);
  await drag(page, chart, .8, .2);
  await expect(result(page, 'Selection callback count')).toHaveText('1');
  const selectedFrom = Date.parse(await result(page, 'Selected from UTC').textContent());
  const selectedTo = Date.parse(await result(page, 'Selected to UTC').textContent());
  expect(Math.abs(selectedFrom - Date.parse('2026-01-15T12:12:00.000Z'))).toBeLessThan(1000);
  expect(Math.abs(selectedTo - Date.parse('2026-01-15T12:48:00.000Z'))).toBeLessThan(1000);
  await expect(chart.locator('figcaption')).toContainText('1 plotted values');
  await expect(companion(page).locator('figcaption')).toContainText('1 plotted values');
  await expect(page.locator('.chart-selection')).toHaveCount(0);
  await page.getByRole('button', { name: 'Reset interaction' }).click();
  await drag(page, chart, .5, .501);
  await expect(result(page, 'Selection callback count')).toHaveText('0');
  const start = await pointAt(chart, .2), end = await pointAt(chart, .8);
  await page.mouse.move(start.x, start.y);
  await page.mouse.down();
  await page.mouse.move(end.x, end.y, { steps: 4 });
  await expect(chart.locator('.chart-selection')).toHaveCount(1);
  // Native pointer cancellation is not exposed by Playwright's mouse API.
  // Dispatch the browser event to exercise the mounted component's cancel path.
  await chart.getByRole('img').dispatchEvent('pointercancel', { pointerId: 1, pointerType: 'mouse' });
  await page.mouse.up();
  await expect(page.locator('.chart-selection')).toHaveCount(0);
  await expect(result(page, 'Selection callback count')).toHaveText('0');
  await expect(result(page, 'Selected from UTC')).toHaveText(initialFrom);
  await expect(result(page, 'Selected to UTC')).toHaveText(initialTo);
});

test('replaces mounted chart content when props become unavailable and restores evidence', async ({ page }) => {
  const chart = primary(page);
  const middle = await pointAt(chart, .5);
  await page.mouse.move(middle.x, middle.y);
  await expect(chart.locator('.chart-nearest')).toContainText('Latency 20');
  await chart.getByText('View chart values', { exact: true }).click();
  await page.getByLabel('Evidence preset').selectOption('missing');
  await expect(chart.getByText('No numeric observations in this window. Missing values are not zero.')).toBeVisible();
  await expect(chart.locator('svg, table, .observation-legend, .chart-nearest')).toHaveCount(0);
  await expect(companion(page).getByRole('img')).toBeVisible();
  await page.getByLabel('Evidence preset').selectOption('mixed');
  await expect(chart.getByRole('img')).toBeVisible();
  await expect(chart.locator('figcaption')).toContainText('3 plotted values');
  await chart.getByText('View chart values', { exact: true }).click();
  await expect(chart.getByRole('row')).toHaveCount(4);
});

test('uses the real time provider for local and UTC chart labels without changing evidence timestamps', async ({ page }) => {
  const chart = primary(page);
  await chart.getByText('View chart values', { exact: true }).click();
  const firstTime = chart.locator(`td[title="${initialFrom}"]`);
  await expect(firstTime).toContainText('12:00:00 UTC');
  await page.getByRole('combobox', { name: 'Time display', exact: true }).selectOption('local');
  await expect(chart.locator('figcaption')).toContainText('local time');
  await expect(chart.getByRole('img')).toHaveAccessibleName(/local-time display/);
  await expect(chart.getByRole('columnheader', { name: 'Local time', exact: true })).toBeVisible();
  await expect(firstTime).toContainText('07:00:00 EST');
  const middle = await pointAt(chart, .5);
  await page.mouse.move(middle.x, middle.y);
  await expect(chart.locator('.chart-nearest')).toContainText('Latency 20 at 01/15/2026, 07:30:00 EST');
  await expect(companion(page).locator('.chart-nearest')).toContainText('Throughput 10 at 01/15/2026, 07:30:00 EST');
  await expectCrosshairNear(page, '2026-01-15T12:30:00.000Z');
  await page.getByRole('combobox', { name: 'Time display', exact: true }).selectOption('utc');
  await expect(firstTime).toContainText('12:00:00 UTC');
  await expect(chart.getByRole('columnheader', { name: 'UTC', exact: true })).toBeVisible();
});

test('keeps chart values accessible at narrow and wide widths', async ({ page }, info) => {
  const screenshotDirectory = fileURLToPath(new URL('../../artifacts/dashboard-fleet-timeline/screenshots/', import.meta.url));
  await mkdir(screenshotDirectory, { recursive: true });
  await page.getByLabel('Show reference values').check();
  await primary(page).getByText('View chart values', { exact: true }).click();
  for (const width of [320, 1440]) {
    await page.setViewportSize({ width, height: 1000 });
    await expect(primary(page).getByRole('img')).toBeVisible();
    const values = primary(page).getByRole('region', { name: 'Latency evidence chart values', exact: true });
    await values.focus();
    await expect(values).toBeFocused();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
    await info.attach(`observation-chart-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
    expect(violations).toEqual([]);
    const path = `${screenshotDirectory}observation-chart-${width}.png`;
    await values.evaluate(element => { element.scrollLeft = 0; element.blur(); });
    await page.evaluate(() => window.scrollTo({ top: 0, behavior: 'instant' }));
    await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
    await page.screenshot({ path, fullPage: true });
    await info.attach(`observation-chart-${width}`, { path, contentType: 'image/png' });
  }
});
