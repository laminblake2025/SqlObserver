import { expect } from '@playwright/test';

// Legacy evidence suites inspect the retained supporting surfaces through the
// same disclosures available to an operator in the redesigned workspace.
export async function expandDisclosure(page, label) {
  const summary = page.locator('summary').filter({ hasText: new RegExp(`^${label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`) });
  await expect(summary).toHaveCount(1);
  if (!await summary.locator('..').evaluate(element => element.open)) await summary.click();
}

export async function revealHealthSupport(page) {
  await expandDisclosure(page, 'Latest health, databases & configuration');
  const resources = page.locator('summary').filter({ hasText: /^Resource charts, wait history & baselines$/ });
  if (await resources.count()) await expandDisclosure(page, 'Resource charts, wait history & baselines');
}
