import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const attempts = new WeakMap();
const evidence = page => page.getByRole('region', { name: 'SQL scheduler CPU evidence', exact: true });
const summary = page => evidence(page).locator('summary');
async function mount(page, scenario) {
  await page.goto('/component-tests/compact-evidence.html?scenario=' + scenario);
  await expect(page.getByRole('heading', { name: 'Compact evidence harness', exact: true })).toBeVisible();
}
test.beforeEach(async ({ page }) => {
  const unexpected = []; attempts.set(page, unexpected);
  page.on('pageerror', error => unexpected.push(error.message));
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.origin !== 'http://127.0.0.1:4328' || /^\/api(?:\/|$)/u.test(url.pathname)) {
      unexpected.push(route.request().method() + ' ' + url.href); await route.abort();
    } else await route.continue();
  });
});
test.afterEach(async ({ page }) => expect(attempts.get(page), 'No API/external traffic or runtime errors').toEqual([]));

for (const scenario of ['unavailable', 'disabled', 'unsupported']) test(scenario + ' source with missing buckets offers quiet recovery guidance and preserves unknown coverage', async ({ page }) => {
  await mount(page, scenario); await expect(evidence(page).locator('details')).not.toHaveAttribute('open', '');
  await summary(page).click();
  await expect(evidence(page)).toContainText(scenario + ' · 0 observed of 2 loaded sample buckets · 2 missing');
  await expect(evidence(page)).toContainText('Check the selected window, collection settings and target access, then refresh');
  await expect(evidence(page)).toContainText('Refresh cannot recreate missing historical samples');
  await expect(evidence(page)).toContainText('They do not establish continuous coverage, freshness, or a health verdict');
  if (scenario === 'disabled') await expect(evidence(page)).toContainText('Target Administrator access is required to enable disabled collection');
  if (scenario === 'unsupported') await expect(evidence(page)).toContainText('refreshing alone does not add source support');
});
for (const scenario of ['unavailable-received', 'stale']) test(scenario + ' preserves received numeric observations including zero while offering recovery', async ({ page }) => {
  await mount(page, scenario); await summary(page).click();
  await expect(evidence(page)).toContainText((scenario === 'stale' ? 'stale' : 'unavailable') + ' · 2 observed of 2 loaded sample buckets');
  await expect(evidence(page)).toContainText('Received observations remain visible');
  await expect(evidence(page)).toContainText('Check the selected window, collection settings and target access, then refresh');
});
test('complete source retains compact coverage without unnecessary recovery guidance', async ({ page }) => {
  await mount(page, 'complete'); await summary(page).click();
  await expect(evidence(page)).toContainText('complete · 2 observed of 2 loaded sample buckets');
  await expect(evidence(page)).not.toContainText('Check the selected window');
  await expect(evidence(page)).toContainText('a health verdict');
});
test('absent source retains its collection and target access recovery guidance', async ({ page }) => {
  await mount(page, 'absent'); await summary(page).click();
  await expect(evidence(page)).toContainText('No source series was returned for this chart. Check collection and target access, then refresh');
});
for (const width of [320, 390, 768, 1440]) test('keyboard recovery disclosure remains accessible and contained at ' + width, async ({ page }) => {
  await page.setViewportSize({ width, height: 900 }); await mount(page, 'disabled');
  await page.keyboard.press('Tab'); await expect(summary(page)).toBeFocused(); await page.keyboard.press('Enter');
  await expect(evidence(page).locator('details')).toHaveAttribute('open', '');
  await expect(evidence(page)).toContainText('Target Administrator access');
  expect(await summary(page).evaluate(node => getComputedStyle(node).outlineStyle)).not.toBe('none');
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  expect((await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'best-practice']).analyze()).violations).toEqual([]);
  await page.keyboard.press('Enter'); await expect(evidence(page).locator('details')).not.toHaveAttribute('open', '');
});
