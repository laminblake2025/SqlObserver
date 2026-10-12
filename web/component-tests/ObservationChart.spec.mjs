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

async function plotPoint(chart, fraction) {
  await chart.getByRole('img').scrollIntoViewIfNeeded();
  const axes = chart.locator('.chart-gridline-vertical');
  const edge = line => line.evaluate(element => {
    const point = element.ownerSVGElement.createSVGPoint();
    point.x = element.x1.baseVal.value;
    point.y = (element.y1.baseVal.value + element.y2.baseVal.value) / 2;
    const screen = point.matrixTransform(element.getScreenCTM());
    return { x: screen.x, y: screen.y };
  });
  const start = await edge(axes.first()), end = await edge(axes.last());
  return { x: start.x + (end.x - start.x) * fraction, y: start.y };
}

async function observePointerOwner(svg) {
  await svg.evaluate(element => element.addEventListener('pointerdown', event => {
    element.dataset.testPointerOwner = String(event.pointerId);
  }, { once: true }));
}

async function touchTap(page, button) {
  await button.scrollIntoViewIfNeeded();
  const box = await button.boundingBox();
  expect(box.width).toBeGreaterThanOrEqual(44); expect(box.height).toBeGreaterThanOrEqual(44);
  const session = await page.context().newCDPSession(page);
  try {
    await session.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: box.x + box.width / 2, y: box.y + box.height / 2, id: 1 }] });
    await session.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  } finally { await session.detach(); }
}

async function touchScrollToEdge(page, viewport, button, direction) {
  for (let step = 0; step < 8; step++) {
    const position = await viewport.evaluate(element => ({ left: element.scrollLeft,
      remaining: element.scrollWidth - element.clientWidth - element.scrollLeft }));
    if ((direction === 'later' ? position.remaining : position.left) <= 1) break;
    await expect(button).toBeEnabled();
    await touchTap(page, button);
    if (direction === 'later') await expect.poll(() => viewport.evaluate(element => element.scrollLeft)).toBeGreaterThan(position.left);
    else await expect.poll(() => viewport.evaluate(element => element.scrollLeft)).toBeLessThan(position.left);
  }
  await expect(button).toBeDisabled();
}

async function visiblePlotGesture(viewport) {
  await viewport.scrollIntoViewIfNeeded();
  return viewport.evaluate(element => {
    const edges = [...element.querySelectorAll('.chart-gridline-vertical')], box = element.getBoundingClientRect();
    const edge = line => { const point = line.ownerSVGElement.createSVGPoint(); point.x = line.x1.baseVal.value;
      point.y = (line.y1.baseVal.value + line.y2.baseVal.value) / 2; return point.matrixTransform(line.getScreenCTM()); };
    const first = edge(edges[0]), last = edge(edges.at(-1));
    const start = Math.max(first.x + 5, box.left + 20), end = Math.min(last.x - 5, box.right - 20);
    return { start, end, y: first.y, from: (start - first.x) / (last.x - first.x), to: (end - first.x) / (last.x - first.x) };
  });
}

for (const kind of ['observation', 'overview']) {
  test(`${kind} ignores right and middle mouse drags and secondary touch starts`, async ({ page }) => {
    await page.getByLabel('Primary chart kind').selectOption(kind);
    const chart = primary(page), svg = chart.getByRole('img');
    const start = await plotPoint(chart, .25), end = await plotPoint(chart, .75);
    for (const button of ['right', 'middle']) {
      await page.mouse.move(start.x, start.y); await page.mouse.down({ button });
      await page.mouse.move(end.x, end.y, { steps: 4 });
      await expect(chart.locator('.chart-selection')).toHaveCount(0);
      await page.mouse.up({ button }); await page.keyboard.press('Escape');
      await expect(result(page, 'Selection callback count')).toHaveText('0');
    }
    // Exercise a secondary PointerEvent on the mounted chart without granting capture.
    const secondary = { pointerId: 900, pointerType: 'touch', isPrimary: false, button: 0, buttons: 1,
      clientX: start.x, clientY: start.y };
    await svg.dispatchEvent('pointerdown', secondary);
    await svg.dispatchEvent('pointermove', { ...secondary, clientX: end.x });
    await svg.dispatchEvent('pointerup', { ...secondary, clientX: end.x, buttons: 0 });
    await expect(chart.locator('.chart-selection')).toHaveCount(0);
    await expect(result(page, 'Selection callback count')).toHaveText('0');
    await page.mouse.move(start.x, start.y); await page.mouse.down();
    await page.mouse.move(end.x, end.y, { steps: 4 }); await page.mouse.up();
    await expect(result(page, 'Selection callback count')).toHaveText('1');
  });

  test(`${kind} preserves the owning drag across foreign starts, movement and termination`, async ({ page }) => {
    await page.getByLabel('Primary chart kind').selectOption(kind);
    const chart = primary(page), svg = chart.getByRole('img');
    const start = await plotPoint(chart, .25), end = await plotPoint(chart, .75);
    await observePointerOwner(svg);
    await page.mouse.move(start.x, start.y); await page.mouse.down();
    await page.mouse.move(end.x, end.y, { steps: 4 });
    const owner = Number(await svg.getAttribute('data-test-pointer-owner'));
    expect(owner).toBeGreaterThan(0);
    const selection = chart.locator('.chart-selection');
    await expect(selection).toHaveCount(1);
    const preview = await selection.evaluate(element => [element.getAttribute('x'), element.getAttribute('width')]);
    const crosshair = await result(page, 'Shared crosshair UTC').textContent();
    for (const isPrimary of [false, true]) {
      const foreign = { pointerId: owner + 100, pointerType: 'touch', isPrimary, button: 0, buttons: 1,
        clientX: start.x, clientY: start.y };
      for (const type of ['pointerdown', 'pointermove', 'pointerup', 'pointercancel', 'lostpointercapture', 'pointerleave']) {
        await svg.dispatchEvent(type, foreign);
        await expect(selection).toHaveCount(1);
        expect(await selection.evaluate(element => [element.getAttribute('x'), element.getAttribute('width')])).toEqual(preview);
        await expect(result(page, 'Shared crosshair UTC')).toHaveText(crosshair);
        await expect(result(page, 'Selection callback count')).toHaveText('0');
      }
    }
    await page.mouse.up();
    await expect(result(page, 'Selection callback count')).toHaveText('1');
    await expect(chart.locator('.chart-selection')).toHaveCount(0);
    expect(Math.abs(Date.parse(await result(page, 'Selected from UTC').textContent()) - Date.parse('2026-01-15T12:15:00Z'))).toBeLessThan(1000);
    expect(Math.abs(Date.parse(await result(page, 'Selected to UTC').textContent()) - Date.parse('2026-01-15T12:45:00Z'))).toBeLessThan(1000);
  });

  for (const type of ['pointercancel', 'lostpointercapture']) test(`${kind} cancels only the owning ${type}`, async ({ page }) => {
    await page.getByLabel('Primary chart kind').selectOption(kind);
    const chart = primary(page), svg = chart.getByRole('img');
    const start = await plotPoint(chart, .25), end = await plotPoint(chart, .75);
    await observePointerOwner(svg);
    await page.mouse.move(start.x, start.y); await page.mouse.down();
    await page.mouse.move(end.x, end.y, { steps: 4 });
    await expect(chart.locator('.chart-selection')).toHaveCount(1);
    const pointerId = Number(await svg.getAttribute('data-test-pointer-owner'));
    await svg.dispatchEvent(type, { pointerId, pointerType: 'mouse', isPrimary: true });
    await expect(chart.locator('.chart-selection')).toHaveCount(0);
    await page.mouse.up();
    await expect(result(page, 'Selection callback count')).toHaveText('0');
    await expect(result(page, 'Selected from UTC')).toHaveText(initialFrom);
    await expect(result(page, 'Selected to UTC')).toHaveText(initialTo);
  });

  test(`${kind} selects a shared window with native primary touch input`, async ({ page }) => {
    await page.getByLabel('Primary chart kind').selectOption(kind);
    const chart = primary(page), start = await plotPoint(chart, .25), end = await plotPoint(chart, .75);
    const session = await page.context().newCDPSession(page);
    let touching = false;
    try {
      await session.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ ...start, id: 1 }] });
      touching = true;
      await session.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ ...end, id: 1 }] });
      await expect(chart.locator('.chart-selection')).toHaveCount(1);
      await session.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
      touching = false;
      await expect(result(page, 'Selection callback count')).toHaveText('1');
      await expect(chart.locator('.chart-selection')).toHaveCount(0);
      expect(Math.abs(Date.parse(await result(page, 'Selected from UTC').textContent()) - Date.parse('2026-01-15T12:15:00Z'))).toBeLessThan(1000);
      expect(Math.abs(Date.parse(await result(page, 'Selected to UTC').textContent()) - Date.parse('2026-01-15T12:45:00Z'))).toBeLessThan(1000);
    } finally {
      if (touching) await session.send('Input.dispatchTouchEvent', { type: 'touchCancel', touchPoints: [] });
      await session.detach();
    }
  });

  for (const width of [320, 1440]) test(`${kind} maps the actual visible plot bounds to the full UTC window at ${width}px`, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 }); await page.getByLabel('Primary chart kind').selectOption(kind);
    const chart = primary(page), svg = chart.getByRole('img'); await svg.scrollIntoViewIfNeeded();
    await page.evaluate(() => window.scrollTo({ top: scrollY, behavior: 'instant' }));
    const edge = async line => line.evaluate(element => {
      const point = element.ownerSVGElement.createSVGPoint(); point.x = element.x1.baseVal.value;
      point.y = (element.y1.baseVal.value + element.y2.baseVal.value) / 2;
      const screen = point.matrixTransform(element.getScreenCTM()); return { x: screen.x, y: screen.y };
    });
    const viewport = chart.locator('.chart-plot-scroll');
    await viewport.focus();
    await viewport.evaluate(element => { element.scrollLeft = 0; });
    const axes = chart.locator('.chart-gridline-vertical'), start = await edge(axes.first());
    // One pixel outside the painted plot remains within SVG, and clamps to its real boundary.
    await page.mouse.move(start.x - 1, start.y); await page.mouse.down();
    // Keep the captured owner while the real local viewport moves. Both endpoints are
    // painted inside the viewport when clicked, even when the full plot cannot fit.
    await viewport.evaluate(element => { element.scrollLeft = element.scrollWidth; });
    const end = await edge(axes.last());
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
      await expect(result(page, 'Selected from UTC')).toHaveText(initialFrom);
      await expect(result(page, 'Selected to UTC')).toHaveText(initialTo);
      await page.mouse.move(0, 0);
      const svg = chart.getByRole('img');
      await svg.evaluate(element => element.scrollIntoView({ block: 'center', behavior: 'instant' }));
      const freshStart = await plotPoint(chart, .2), freshEnd = await plotPoint(chart, .8);
      await page.mouse.move(freshStart.x, freshStart.y); await page.mouse.down();
      await expect(chart.locator('.chart-selection')).toHaveCount(1);
      await page.mouse.move(freshEnd.x, freshEnd.y, { steps: 6 });
      await expect(chart.locator('.chart-selection')).toHaveCount(1);
      await page.mouse.up();
      await expect(result(page, 'Selection callback count')).toHaveText('1');
      for (const [name, utc] of [['Selected from UTC', '2026-01-15T12:12:00Z'], ['Selected to UTC', '2026-01-15T12:48:00Z']]) {
        await expect.poll(async () => Math.abs(Date.parse(await result(page, name).textContent()) - Date.parse(utc))).toBeLessThan(1000);
      }
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

for (const kind of ['observation', 'overview']) for (const width of [320, 390, 768, 1440]) test(`${kind} plot keeps readable axes and full-window touch/keyboard access at ${width}px`, async ({ page }, info) => {
  await page.setViewportSize({ width, height: 1000 });
  await page.getByRole('combobox', { name: 'Primary chart kind', exact: true }).selectOption(kind);
  await page.getByRole('checkbox', { name: 'Scrollable resource plot', exact: true }).check();
  const label = kind === 'overview' ? 'Resource evidence plot' : 'Latency evidence plot';
  const viewport = primary(page).getByRole('region', { name: label, exact: true });
  const earlier = primary(page).getByRole('button', { name: `Scroll ${label} earlier`, exact: true });
  const later = primary(page).getByRole('button', { name: `Scroll ${label} later`, exact: true });
  const svg = viewport.getByRole('img');
  await expect(svg).toBeVisible();
  await page.getByRole('button', { name: 'Replace chart target', exact: true }).focus();
  await page.keyboard.press('Tab');
  await expect(viewport).toBeFocused();
  // Ordinary plots fit their container with readable axes. Exercise deliberate
  // enlargement too, so touch panning and CTM selection retain full coverage.
  const normal = await svg.evaluate(element => { const text = element.querySelector('text'), matrix = text.getScreenCTM(); return { width: element.getBoundingClientRect().width, container: element.parentElement.clientWidth, font: parseFloat(getComputedStyle(text).fontSize) * Math.hypot(matrix.a, matrix.b) }; });
  expect(normal.width).toBeLessThanOrEqual(normal.container + 1); expect(normal.font).toBeGreaterThanOrEqual(11.5);
  await svg.evaluate(element => { element.style.width = `${element.viewBox.baseVal.width}px`; });
  const geometry = await viewport.evaluate(element => {
    const svg = element.querySelector('svg'), text = svg.querySelector('text'), matrix = text.getScreenCTM();
    const outline = getComputedStyle(element), box = svg.getBoundingClientRect();
    return { client: element.clientWidth, scroll: element.scrollWidth, svgWidth: box.width, svgHeight: box.height,
      viewWidth: svg.viewBox.baseVal.width, viewHeight: svg.viewBox.baseVal.height,
      axisFont: parseFloat(getComputedStyle(text).fontSize) * Math.hypot(matrix.a, matrix.b),
      outlineStyle: outline.outlineStyle, outlineWidth: parseFloat(outline.outlineWidth) };
  });
  expect(geometry.outlineStyle).not.toBe('none');
  expect(geometry.outlineWidth).toBeGreaterThanOrEqual(2);
  expect(geometry.svgWidth).toBeGreaterThanOrEqual(geometry.viewWidth);
  expect(geometry.axisFont).toBeGreaterThanOrEqual(11.5);
  expect(Math.abs(geometry.svgHeight - geometry.svgWidth * geometry.viewHeight / geometry.viewWidth)).toBeLessThan(1);
  if (geometry.scroll > geometry.client + 1) {
    await expect(earlier).toBeDisabled(); await expect(later).toBeEnabled();
    await touchScrollToEdge(page, viewport, later, 'later');
    await expect(later).toBeDisabled(); await expect(earlier).toBeEnabled();
    const lastAxis = await svg.locator('.chart-gridline-vertical').last().boundingBox(), rightBox = await viewport.boundingBox();
    expect(lastAxis.x).toBeGreaterThanOrEqual(rightBox.x); expect(lastAxis.x).toBeLessThanOrEqual(rightBox.x + rightBox.width);
    await touchScrollToEdge(page, viewport, earlier, 'earlier');
    await expect(earlier).toBeDisabled(); await expect(later).toBeEnabled();
    const firstAxis = await svg.locator('.chart-gridline-vertical').first().boundingBox(), leftBox = await viewport.boundingBox();
    expect(firstAxis.x).toBeGreaterThanOrEqual(leftBox.x); expect(firstAxis.x).toBeLessThanOrEqual(leftBox.x + leftBox.width);
    await expect(result(page, 'Selection callback count')).toHaveText('0');
    await expect(result(page, 'Selected from UTC')).toHaveText(initialFrom); await expect(result(page, 'Selected to UTC')).toHaveText(initialTo);
  } else { await expect(earlier).toHaveCount(0); await expect(later).toHaveCount(0); }
  if (width <= 560) {
    expect(geometry.scroll).toBeGreaterThan(geometry.client);
    await viewport.focus();
    await page.keyboard.press('ArrowRight');
    await expect.poll(() => viewport.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
    // Exercise pointer-to-window conversion after the SVG has moved within its own viewport.
    await viewport.evaluate(element => { element.scrollLeft = 150; });
    await viewport.scrollIntoViewIfNeeded();
    const gesture = await viewport.evaluate(element => {
      const edges = [...element.querySelectorAll('.chart-gridline-vertical')], box = element.getBoundingClientRect();
      const edge = line => { const point = line.ownerSVGElement.createSVGPoint(); point.x = line.x1.baseVal.value;
        point.y = (line.y1.baseVal.value + line.y2.baseVal.value) / 2; return point.matrixTransform(line.getScreenCTM()); };
      const first = edge(edges[0]), last = edge(edges.at(-1));
      const start = Math.max(first.x + 5, box.left + 20), end = Math.min(last.x - 5, box.right - 20);
      return { start, end, y: first.y, from: (start - first.x) / (last.x - first.x), to: (end - first.x) / (last.x - first.x) };
    });
    expect(gesture.end).toBeGreaterThan(gesture.start);
    await page.mouse.move(gesture.start, gesture.y); await page.mouse.down();
    await page.mouse.move(gesture.end, gesture.y, { steps: 6 }); await page.mouse.up();
    await expect(result(page, 'Selection callback count')).toHaveText('1');
    for (const [name, fraction] of [['Selected from UTC', gesture.from], ['Selected to UTC', gesture.to]]) {
      const expected = Date.parse(initialFrom) + fraction * (Date.parse(initialTo) - Date.parse(initialFrom));
      await expect.poll(async () => Math.abs(Date.parse(await result(page, name).textContent()) - expected)).toBeLessThan(1000);
    }
  }
  expect(await svg.evaluate(element => getComputedStyle(element).touchAction)).toContain('pan-y');
  await expect(viewport.locator('figcaption, table')).toHaveCount(0);
  await primary(page).getByText('View chart values', { exact: true }).click();
  await expect(primary(page).getByRole('region', { name: kind === 'overview' ? 'Chart observation values' : 'Latency evidence chart values', exact: true })).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations;
  await info.attach(`${kind}-plot-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
  expect(violations).toEqual([]);
});

for (const kind of ['observation', 'overview']) for (const width of [320, 1440]) test(`${kind} long exact axis values fit their gutter and preserve CTM selection at ${width}px`, async ({ page }) => {
  await page.setViewportSize({ width, height: 1000 });
  await page.getByRole('combobox', { name: 'Primary chart kind', exact: true }).selectOption(kind);
  await page.getByRole('combobox', { name: 'Evidence preset', exact: true }).selectOption('large');
  await page.getByRole('checkbox', { name: 'Scrollable resource plot', exact: true }).check();
  const viewport = primary(page).locator('.chart-plot-scroll'), svg = viewport.getByRole('img');
  const axes = await svg.locator('text[text-anchor="end"][x="40"]').evaluateAll(nodes => nodes.map(node => {
    const box = node.getBBox(), view = node.ownerSVGElement.viewBox.baseVal, m = node.getScreenCTM();
    return { text: node.textContent, left: box.x, right: box.x + box.width, viewLeft: view.x, viewRight: view.x + view.width,
      font: parseFloat(getComputedStyle(node).fontSize) * Math.hypot(m.a, m.b) };
  }));
  expect(axes.length).toBeGreaterThanOrEqual(3); expect(axes.some(axis => axis.text.length > 10)).toBe(true);
  for (const axis of axes) {
    expect(axis.viewLeft).toBeLessThan(0); expect(axis.left).toBeGreaterThanOrEqual(axis.viewLeft);
    // getBBox includes the font's subpixel ink overhang beyond the x=40 anchor.
    expect(axis.right).toBeLessThanOrEqual(41); expect(axis.right).toBeLessThan(48);
    expect(axis.right).toBeLessThanOrEqual(axis.viewRight); expect(axis.font).toBeGreaterThanOrEqual(11.99);
  }
  await primary(page).getByText('View chart values', { exact: true }).click();
  const values = primary(page).getByRole('region', { name: kind === 'overview' ? 'Chart observation values' : 'Latency evidence chart values', exact: true });
  for (const exact of ['1,000,000,000', '1,100,000,000', '1,200,000,000']) await expect(values.getByRole('cell', { name: kind === 'overview' ? exact + ' ms' : exact, exact: true })).toBeVisible();
  await primary(page).getByText('View chart values', { exact: true }).click();
  if (await viewport.evaluate(node => node.scrollWidth > node.clientWidth + 1)) {
    const later = primary(page).getByRole('button', { name: /^Scroll .+ later$/ });
    await touchTap(page, later); await expect.poll(() => viewport.evaluate(node => node.scrollLeft)).toBeGreaterThan(0);
  }
  await expect(result(page, 'Selection callback count')).toHaveText('0');
  const gesture = await visiblePlotGesture(viewport); expect(gesture.end).toBeGreaterThan(gesture.start);
  await page.mouse.move(gesture.start, gesture.y); await page.mouse.down(); await page.mouse.move(gesture.end, gesture.y, { steps: 6 }); await page.mouse.up();
  await expect(result(page, 'Selection callback count')).toHaveText('1');
  for (const [name, fraction] of [['Selected from UTC', gesture.from], ['Selected to UTC', gesture.to]]) {
    const expected = Date.parse(initialFrom) + fraction * (Date.parse(initialTo) - Date.parse(initialFrom));
    await expect.poll(async () => Math.abs(Date.parse(await result(page, name).textContent()) - expected)).toBeLessThan(1000);
  }
  expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
});

for (const kind of ['observation', 'overview']) test(`${kind} native touch drag after local scrolling respects cancel and replacement ownership`, async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 1000 });
  await page.getByRole('combobox', { name: 'Primary chart kind', exact: true }).selectOption(kind);
  await page.getByRole('checkbox', { name: 'Scrollable resource plot', exact: true }).check();
  const viewport = primary(page).locator('.chart-plot-scroll'), svg = viewport.getByRole('img');
  const session = await page.context().newCDPSession(page);
  let touching = false;
  try {
    for (const ending of ['cancel', 'replace', 'apply']) {
      await viewport.evaluate(element => { element.scrollLeft = 150; });
      const gesture = await visiblePlotGesture(viewport);
      expect(gesture.end).toBeGreaterThan(gesture.start);
      await session.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: gesture.start, y: gesture.y, id: 1 }] }); touching = true;
      await session.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: gesture.end, y: gesture.y, id: 1 }] });
      await expect(primary(page).locator('.chart-selection')).toHaveCount(1);
      if (ending === 'replace') {
        await page.getByRole('button', { name: 'Advance chart window', exact: true }).evaluate(button => button.click());
        await expect(primary(page).locator('.chart-selection')).toHaveCount(0);
        await expect.poll(() => viewport.evaluate(element => element.scrollLeft)).toBe(0);
      }
      await session.send('Input.dispatchTouchEvent', { type: ending === 'cancel' ? 'touchCancel' : 'touchEnd', touchPoints: [] }); touching = false;
      await expect(primary(page).locator('.chart-selection')).toHaveCount(0);
      await expect(result(page, 'Selection callback count')).toHaveText(ending === 'apply' ? '1' : '0');
      if (ending === 'cancel') {
        await expect(result(page, 'Selected from UTC')).toHaveText(initialFrom); await expect(result(page, 'Selected to UTC')).toHaveText(initialTo);
      }
      if (ending === 'replace') {
        await expect(result(page, 'Selected from UTC')).toHaveText('2026-01-15T12:10:00.000Z');
        await page.getByRole('button', { name: 'Reset interaction', exact: true }).click();
        await expect(result(page, 'Selected from UTC')).toHaveText(initialFrom);
      }
      if (ending === 'apply') for (const [name, fraction] of [['Selected from UTC', gesture.from], ['Selected to UTC', gesture.to]]) {
        const expected = Date.parse(initialFrom) + fraction * (Date.parse(initialTo) - Date.parse(initialFrom));
        await expect.poll(async () => Math.abs(Date.parse(await result(page, name).textContent()) - expected)).toBeLessThan(1000);
      }
    }
    expect(await svg.evaluate(element => getComputedStyle(element).touchAction)).toContain('pan-y');
  } finally {
    if (touching) await session.send('Input.dispatchTouchEvent', { type: 'touchCancel', touchPoints: [] });
    await session.detach();
  }
});

test('keeps chart values accessible at four dashboard widths', async ({ page }, info) => {
  const screenshotDirectory = fileURLToPath(new URL('../../artifacts/dashboard-access-navigation/screenshots/', import.meta.url));
  await mkdir(screenshotDirectory, { recursive: true });
  await page.getByLabel('Show reference values').check();
  await primary(page).getByText('View chart values', { exact: true }).click();
  for (const width of [320, 390, 768, 1440]) {
    await page.setViewportSize({ width, height: 1000 });
    await expect(primary(page).getByRole('img')).toBeVisible();
    const values = primary(page).getByRole('region', { name: 'Latency evidence chart values', exact: true });
    await primary(page).getByText('View chart values', { exact: true }).focus();
    await page.keyboard.press('Tab');
    await expect(values).toBeFocused();
    const focus = await values.evaluate(element => {
      const style = getComputedStyle(element);
      return { outlineStyle: style.outlineStyle, outlineWidth: parseFloat(style.outlineWidth) };
    });
    expect(focus.outlineStyle).not.toBe('none');
    expect(focus.outlineWidth).toBeGreaterThanOrEqual(2);
    expect(await values.locator('td[title]').first().evaluate(element => {
      const range = document.createRange(); range.selectNodeContents(element); return range.getClientRects().length;
    }), 'Observation timestamps remain readable on one line').toBe(1);
    if (await values.evaluate(element => element.scrollWidth > element.clientWidth)) {
      await page.keyboard.press('ArrowRight');
      await expect.poll(() => values.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
    }
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa', 'best-practice']).analyze()).violations;
    await info.attach(`observation-chart-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' });
    expect(violations).toEqual([]);
    const path = `${screenshotDirectory}chart-pointer-${width}.png`;
    await values.evaluate(element => { element.scrollLeft = 0; });
    await page.evaluate(() => window.scrollTo({ top: 0, behavior: 'instant' }));
    await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
    await page.screenshot({ path, fullPage: true });
    await info.attach(`observation-chart-${width}`, { path, contentType: 'image/png' });
  }
});
