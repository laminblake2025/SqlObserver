import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const target = '11111111-1111-4111-8111-000000000001';
const template = { name: 'deadlocks.count', kind: 'deadlock_count_v1', metricId: null, unit: 'count',
  comparison: 'greater_than', threshold: 0, hysteresis: 0, confirmationCount: 2,
  clearConfirmationCount: 2, confirmationSeconds: 300, evaluationSeconds: 15 };

test('Deadlock rules expose counts and preserve their signal across create, retry, edit and disable', async ({ page }) => {
  const writes = [], errors = []; let item, failCreate = true;
  page.on('pageerror', error => errors.push(error.message));
  await page.context().addCookies([{ name: 'chartReferenceRole', value: 'admin', url: 'http://127.0.0.1:4321' }]);
  await page.route(`**/api/v1/observation-targets/${target}/alerts/rules**`, async route => {
    const request = route.request(), path = new URL(request.url()).pathname;
    if (request.method() === 'GET') return route.fulfill({ json: path.endsWith('/catalog')
      ? { targetId: target, digest: 'a'.repeat(64), items: [template] }
      : { targetId: target, listingStartedUtc: '2026-10-01T00:00:00Z', items: item ? [item] : [], hasMore: false, nextCursor: null } });
    const body = request.postDataJSON(); writes.push({ method: request.method(), path, body });
    if (failCreate) { failCreate = false; return route.fulfill({ status: 503, json: {} }); }
    item = { ...body, enabled: !path.endsWith('/disable'), revision: (item?.revision ?? 0) + 1, updatedAtUtc: '2026-10-01T00:00:00Z' };
    await route.fulfill({ json: { auditId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', recordedAtUtc: '2026-10-01T00:00:00Z' } });
  });
  await page.goto(`/#/rules?target=${target}`);
  const form = page.locator('form').filter({ has: page.getByRole('heading', { name: 'Create an alert rule' }) });
  await expect(form.getByRole('combobox', { name: 'Signal', exact: true })).toContainText('Deadlock count (count)');
  await expect(form.getByLabel('Deadlock threshold (count)')).toHaveValue('0');
  await expect(form.getByLabel('Clear below threshold by (count)')).toHaveValue('0');
  await form.getByLabel('Rule name').fill('deadlocks.browser');
  await form.getByLabel('Evaluate every seconds').fill('15');
  await form.getByLabel('Within seconds').fill('0');
  await form.getByRole('button', { name: 'Create rule', exact: true }).click();
  await expect(form.getByRole('alert')).toContainText('allow enough time'); expect(writes).toHaveLength(0);
  await form.getByLabel('Within seconds').fill('300');
  await form.getByLabel('Deadlock threshold (count)').fill('3');
  await form.getByRole('button', { name: 'Create rule', exact: true }).click();
  await expect(form.getByRole('alert')).toContainText('Retry the same action');
  await form.getByRole('button', { name: 'Create rule', exact: true }).click();
  await expect(page.getByText('Rule created. The list is refreshing.')).toBeVisible();
  expect(writes[1].body).toEqual(writes[0].body);
  await page.getByRole('button', { name: 'Edit rule', exact: true }).click();
  const edit = page.locator('form').filter({ has: page.getByRole('heading', { name: 'Edit deadlocks.browser' }) });
  await expect(edit.getByLabel('Deadlock threshold (count)')).toHaveValue('3');
  await edit.getByLabel('Deadlock threshold (count)').fill('4');
  await edit.getByLabel('Clear below threshold by (count)').fill('1');
  await edit.getByLabel('Clear after observations').fill('3');
  expect((await new AxeBuilder({ page }).include('.alerts-screen').analyze()).violations).toEqual([]);
  await page.screenshot({ path: '../artifacts/deadlock-alerts/deadlock-rule-editor.png', fullPage: true });
  await edit.getByRole('button', { name: 'Save rule' }).click();
  await expect(page.getByText('Rule updated. The list is refreshing.')).toBeVisible();
  await page.getByRole('button', { name: 'Disable rule', exact: true }).click();
  await expect(page.getByText('Rule disabled. The list is refreshing.')).toBeVisible();
  expect(writes.map(write => write.body.kind)).toEqual(Array(4).fill('deadlock_count_v1'));
  expect(writes[2].body).toMatchObject({ expectedRevision: 1, threshold: 4, hysteresis: 1, clearConfirmationCount: 3 });
  expect(writes[3].body).toMatchObject({ expectedRevision: 2, enabled: false, threshold: 4, clearConfirmationCount: 3 });
  expect(errors).toEqual([]);
});
