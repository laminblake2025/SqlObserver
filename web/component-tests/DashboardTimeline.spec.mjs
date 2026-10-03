import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const unexpectedRequests = new WeakMap();
const rail = page => page.getByRole('img', { name: 'Timeline range selection with loaded event markers', exact: true });
const count = page => page.getByRole('status', { name: 'Selection callback count', exact: true });
const timeline = page => page.getByRole('region', { name: 'Investigation timeline', exact: true });
const eventBrowser = page => timeline(page).getByText(/^Browse loaded timeline events \(\d+\)$/).locator('xpath=parent::details');
async function browseEvents(page) {
  const browser = eventBrowser(page);
  if (!(await browser.evaluate(element => element.open))) await browser.locator('summary').click();
  await expect(timeline(page).getByRole('region', { name: 'Loaded timeline event list', exact: true })).toBeVisible();
}
test.beforeEach(async ({ page }) => {
  const unexpected = []; unexpectedRequests.set(page, unexpected);
  page.on('pageerror', error => unexpected.push(`Page error: ${error.message}`));
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.origin !== 'http://127.0.0.1:4328' || /^\/api(?:\/|$)/u.test(url.pathname)) {
      unexpected.push(`${route.request().method()} ${url.href}`); await route.abort();
    } else await route.continue();
  });
  await page.addInitScript(() => localStorage.setItem('sqlobserver.timeDisplayMode', 'utc'));
  await page.goto('/component-tests/timeline.html');
  await expect(page.getByRole('heading', { name: 'Dashboard timeline harness' })).toBeVisible();
});
test.afterEach(async ({ page }) => {
  expect(unexpectedRequests.get(page), 'No API/external requests or runtime errors are permitted').toEqual([]);
});
async function point(page, fraction) {
  const element = rail(page); await element.scrollIntoViewIfNeeded();
  return element.evaluate((svg, part) => {
    const bounds = svg.getBoundingClientRect();
    return { x: bounds.left + svg.clientLeft + svg.clientWidth * (48 + 572 * part) / 650, y: bounds.top + bounds.height / 2 };
  }, fraction);
}
async function begin(page, start, end) {
  const from = await point(page, start), to = await point(page, end);
  await page.mouse.move(from.x, from.y); await page.mouse.down();
  await page.mouse.move(to.x, to.y, { steps: 6 });
  await expect(timeline(page).locator('.dashboard-timeline-range-preview')).toHaveCount(1);
}
for (const [start, end] of [[.2, .8], [.8, .2]]) {
  test(`pointer selects ordered shared bounds from ${start} to ${end}`, async ({ page }) => {
    await begin(page, start, end);
    await expect(timeline(page).getByText(/Selected preview:/)).toBeVisible();
    await page.mouse.up(); await expect(count(page)).toHaveText('1');
    const from = await page.getByRole('status', { name: 'Shared from UTC', exact: true }).textContent();
    const to = await page.getByRole('status', { name: 'Shared to UTC', exact: true }).textContent();
    expect(Math.abs(Date.parse(from) - Date.parse('2026-01-15T12:12:00Z'))).toBeLessThan(15_000);
    expect(Math.abs(Date.parse(to) - Date.parse('2026-01-15T12:48:00Z'))).toBeLessThan(15_000);
    const query = new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
    expect(query.get('from')).toBe(from); expect(query.get('to')).toBe(to);
    expect(query.get('databaseId')).toBe('7'); expect(query.get('compare')).toBe('1');
    expect(query.get('target')).toBe('11111111-1111-4111-8111-111111111111');
    await expect(timeline(page).locator('.dashboard-timeline-range-preview')).toHaveCount(0);
  });
}
test('cancel, click and subminute gestures do not select a window', async ({ page }) => {
  await begin(page, .2, .8);
  await rail(page).dispatchEvent('pointercancel', { pointerId: 1, pointerType: 'mouse' });
  await page.mouse.up(); await expect(count(page)).toHaveText('0');
  await begin(page, .5, .501); await page.mouse.up(); await expect(count(page)).toHaveText('0');
  const middle = await point(page, .5); await page.mouse.click(middle.x, middle.y);
  await expect(count(page)).toHaveText('0');
});
test('a foreign pointer cannot replace or cancel the captured timeline owner', async ({ page }) => {
  await rail(page).evaluate(svg => svg.addEventListener('pointerdown', event => { svg.dataset.testOwner = String(event.pointerId); }, { once: true }));
  await begin(page, .2, .8);
  const svg = rail(page), owner = Number(await svg.getAttribute('data-test-owner'));
  const preview = timeline(page).locator('.dashboard-timeline-range-preview');
  const received = await preview.evaluate(element => [element.getAttribute('x'), element.getAttribute('width')]);
  const foreign = { pointerId: owner + 100, pointerType: 'touch', isPrimary: true, button: 0, buttons: 1, ...await point(page, .5) };
  for (const type of ['pointerdown', 'pointermove', 'pointercancel', 'lostpointercapture', 'pointerup']) {
    await svg.dispatchEvent(type, { ...foreign, clientX: foreign.x, clientY: foreign.y });
    await expect(preview).toHaveCount(1);
    expect(await preview.evaluate(element => [element.getAttribute('x'), element.getAttribute('width')])).toEqual(received);
    await expect(count(page)).toHaveText('0');
  }
  await page.mouse.up(); await expect(count(page)).toHaveText('1'); await expect(preview).toHaveCount(0);
  for (const [name, utc] of [['Shared from UTC', '2026-01-15T12:12:00Z'], ['Shared to UTC', '2026-01-15T12:48:00Z']]) {
    await expect.poll(async () => Math.abs(Date.parse(await page.getByRole('status', { name, exact: true }).textContent()) - Date.parse(utc))).toBeLessThan(15_000);
  }
});
test('an exact loaded-window change cancels a held gesture and resets keyboard drafts', async ({ page }) => {
  await page.getByLabel('Timeline from UTC', { exact: true }).fill('2026-01-15T12:10:00.123456Z');
  await begin(page, .2, .8);
  // Trigger the real harness control without releasing the captured pointer first.
  await page.getByRole('button', { name: 'Advance loaded window' }).evaluate(button => button.click());
  await expect(timeline(page).locator('.dashboard-timeline-range-preview')).toHaveCount(0);
  await page.mouse.up(); await expect(count(page)).toHaveText('0');
  await expect(page.getByLabel('Timeline from UTC', { exact: true })).toHaveValue('2026-01-15T12:01:00.000001Z');
});
test('unmount cancels captured gesture without a late shared callback', async ({ page }) => {
  await begin(page, .2, .8);
  await page.getByRole('button', { name: 'Unmount timeline' }).evaluate(button => button.click());
  await page.mouse.up(); await expect(count(page)).toHaveText('0');
  await page.getByRole('button', { name: 'Mount timeline' }).click();
  await expect(timeline(page).locator('.dashboard-timeline-range-preview')).toHaveCount(0);
});
test('keyboard Enter publishes exact microsecond bounds and rejects invalid/outside/reversed ranges', async ({ page }) => {
  const from = page.getByLabel('Timeline from UTC', { exact: true });
  const to = page.getByLabel('Timeline to UTC', { exact: true });
  for (const [first, last] of [['invalid', '2026-01-15T12:50:00Z'], ['2026-01-15T11:59:00Z', '2026-01-15T12:50:00Z'], ['2026-01-15T12:50:00Z', '2026-01-15T12:10:00Z']]) {
    await from.fill(first); await to.fill(last); await to.press('Enter');
    await expect(timeline(page).getByRole('alert')).toContainText('within the loaded timeline window');
    await expect(count(page)).toHaveText('0');
  }
  await from.fill('2026-01-15T12:10:00.123456Z'); await to.fill('2026-01-15T12:50:00.654321Z'); await to.press('Enter');
  await expect(count(page)).toHaveText('1');
  await expect(page.getByRole('status', { name: 'Shared from UTC', exact: true })).toHaveText('2026-01-15T12:10:00.123456Z');
  await expect(page.getByRole('status', { name: 'Shared to UTC', exact: true })).toHaveText('2026-01-15T12:50:00.654321Z');
});
test('native event alignment/category/evidence survives the range rail and display preference', async ({ page }) => {
  await expect(timeline(page).locator('.dashboard-timeline-rail-marker')).toHaveCount(2);
  await browseEvents(page);
  const event = timeline(page).getByRole('button', { name: 'Align charts: Recorded deadlock' });
  await event.press('Enter'); await expect(event).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByRole('status', { name: 'Shared crosshair UTC', exact: true })).toHaveText('2026-01-15T12:15:00.000001Z');
  await expect(timeline(page).getByRole('link', { name: 'Open evidence: Recorded deadlock' })).toHaveAttribute('href', /#\/deadlocks\?target=/);
  await page.getByRole('combobox', { name: 'Time display', exact: true }).selectOption('local');
  await expect(page.getByLabel('Timeline from UTC', { exact: true })).toHaveValue('2026-01-15T12:00:00.000001Z');
  await timeline(page).getByLabel('Timeline event category').selectOption('agent_failure');
  await expect(timeline(page).locator('.dashboard-timeline-rail-marker')).toHaveCount(1);
  await expect(page.getByRole('status', { name: 'Shared crosshair UTC', exact: true })).toHaveText('None');
  await expect(count(page)).toHaveText('0');
});
test('raw events are folded by default and keyboard disclosure never changes the shared window', async ({ page }) => {
  const browser = eventBrowser(page), summary = browser.locator('summary');
  await expect(browser).not.toHaveAttribute('open', '');
  await expect(rail(page)).toBeVisible();
  await expect(page.getByLabel('Timeline from UTC', { exact: true })).toBeVisible();
  await expect(timeline(page).getByRole('button', { name: 'Align charts: Recorded deadlock', exact: true })).toHaveCount(0);
  const hash = new URL(page.url()).hash;
  await summary.focus(); await page.keyboard.press('Enter');
  await expect(summary).toBeFocused(); await expect(browser).toHaveAttribute('open', '');
  await page.keyboard.press('Tab');
  const list = timeline(page).getByRole('region', { name: 'Loaded timeline event list', exact: true });
  await expect(list).toBeFocused(); await page.keyboard.press('Tab');
  await expect(timeline(page).getByRole('button', { name: 'Align charts: Recorded deadlock', exact: true })).toBeFocused();
  await summary.focus(); await page.keyboard.press('Enter');
  await expect(browser).not.toHaveAttribute('open', ''); await expect(summary).toBeFocused();
  await page.keyboard.press('Tab'); await expect(timeline(page).getByText('Timeline coverage', { exact: true })).toBeFocused();
  expect(new URL(page.url()).hash).toBe(hash); await expect(count(page)).toHaveText('0');
});
test('restored selection stays compact until its explicit opener focuses the exact event', async ({ page }) => {
  await page.getByRole('button', { name: 'Restore deadlock selection', exact: true }).click();
  await expect(eventBrowser(page)).not.toHaveAttribute('open', '');
  await expect(timeline(page).getByRole('link', { name: 'Open selected event evidence', exact: true })).toHaveAttribute('href', /#\/deadlocks\?target=/);
  const show = timeline(page).getByRole('button', { name: 'Show selected event in timeline list', exact: true });
  await show.focus(); await page.keyboard.press('Enter');
  const selected = timeline(page).getByRole('button', { name: 'Align charts: Recorded deadlock', exact: true });
  await expect(selected).toBeFocused(); await expect(selected).toHaveAttribute('aria-pressed', 'true');
  await expect(count(page)).toHaveText('0');
});
for (const mode of ['missing', 'invalid']) test(`${mode} restored selection is discoverable without expanding raw rows`, async ({ page }) => {
  await page.getByRole('button', { name: `Restore ${mode} selection`, exact: true }).click();
  await expect(eventBrowser(page)).not.toHaveAttribute('open', '');
  await expect(timeline(page)).toContainText(mode === 'missing' ? 'not present in the loaded evidence' : 'invalid for this server and window');
  await expect(timeline(page).getByRole('link', { name: 'Open selected event evidence', exact: true })).toHaveCount(0);
  await expect(timeline(page).getByRole('button', { name: 'Clear timeline alignment', exact: true })).toBeVisible();
  await expect(count(page)).toHaveText('0');
});
test('same-context receipts and equivalent UTC preserve the browser while a true window or target resets it', async ({ page }) => {
  await browseEvents(page);
  await page.getByRole('button', { name: 'Refresh loaded events', exact: true }).click();
  await expect(eventBrowser(page)).toHaveAttribute('open', '');
  await page.getByRole('button', { name: 'Use equivalent UTC spelling', exact: true }).click();
  await expect(eventBrowser(page)).toHaveAttribute('open', '');
  await page.getByRole('button', { name: 'Advance loaded window', exact: true }).click();
  await expect(eventBrowser(page)).not.toHaveAttribute('open', '');
  await browseEvents(page); await page.getByRole('button', { name: 'Replace timeline target', exact: true }).click();
  await expect(eventBrowser(page)).not.toHaveAttribute('open', '');
  await expect(count(page)).toHaveText('0');
});
for (const width of [320, 390, 768, 1440]) {
  test(`dark keyboard range selection remains accessible at ${width}px`, async ({ page }, info) => {
    await page.setViewportSize({ width, height: 1000 });
    const input = page.getByLabel('Timeline from UTC', { exact: true }); await input.focus();
    await expect(input).toBeFocused();
    expect(await input.evaluate(element => getComputedStyle(element).outlineStyle)).not.toBe('none');
    expect(await page.evaluate(() => getComputedStyle(document.documentElement).colorScheme)).toBe('dark');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    for (const control of [input, page.getByLabel('Timeline to UTC', { exact: true }), page.getByRole('button', { name: 'Apply timeline window' })]) {
      const bounds = await control.boundingBox(); expect(bounds.x).toBeGreaterThanOrEqual(0); expect(bounds.x + bounds.width).toBeLessThanOrEqual(width);
    }
    const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations;
    await info.attach(`timeline-${width}-axe`, { body: JSON.stringify(violations, null, 2), contentType: 'application/json' }); expect(violations).toEqual([]);
    const directory = fileURLToPath(new URL('../../artifacts/dashboard-fleet-timeline/screenshots/', import.meta.url)); await mkdir(directory, { recursive: true });
    await input.evaluate(element => element.blur()); await page.evaluate(() => scrollTo({ top: 0, behavior: 'instant' }));
    await expect.poll(() => page.evaluate(() => scrollY)).toBe(0);
    const path = `${directory}dashboard-timeline-${width}.png`; await page.screenshot({ path, fullPage: true });
    await info.attach(`timeline-${width}`, { path, contentType: 'image/png' });
    await input.fill('2026-01-15T12:10:00.123456Z');
    await page.getByLabel('Timeline to UTC', { exact: true }).fill('2026-01-15T12:50:00.654321Z');
    await page.getByLabel('Timeline to UTC', { exact: true }).press('Tab');
    await expect(page.getByRole('button', { name: 'Apply timeline window' })).toBeFocused();
    await page.keyboard.press('Enter'); await expect(count(page)).toHaveText('1');
  });
}
