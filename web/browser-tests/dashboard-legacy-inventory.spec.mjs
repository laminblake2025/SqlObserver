import { test, expect } from '@playwright/test';
import { evidence, database } from '../component-tests/healthFixture.mjs';

const origin = 'http://127.0.0.1:4432';
const target = '11111111-1111-4111-8111-000000000002';
async function installLegacyInventory(page, invalid = false) {
  await page.context().addCookies([{name:'dashboardRefactor',value:'1',url:origin},
    {name:'chartReferenceRole',value:'viewer',url:origin}]);
  const latest = evidence(target);
  latest.databases.items = Array.from({length:9}, (_, index) => {
    const row = database(index + 1, `SYNTHETIC legacy database ${index + 1}`);
    delete row.logReuseWaitDescription;
    return row;
  });
  if (invalid) latest.databases.items[0].logReuseWaitDescription = false;
  await page.route(`**/api/v1/observation-targets/${target}/health{,/**}`, async route => {
    const suffix = new URL(route.request().url()).pathname.split('/health')[1];
    const value = suffix === '' ? latest.target : suffix === '/databases' ? latest.databases
      : suffix === '/files' ? latest.files : null;
    if (!value) return route.fallback();
    expect(route.request().method()).toBe('GET');
    await route.fulfill({status:200,contentType:'application/json',headers:{'cache-control':'no-store'},json:value});
  });
}

for (const width of [390,1440]) test(`legacy inventory loads on Deadlocks, survives reload, and reports uncollected log wait at ${width}px`, async ({page}, info) => {
  await page.setViewportSize({width,height:1000});
  await installLegacyInventory(page);
  const failures = []; page.on('pageerror', error => failures.push(error.message));
  await page.goto(`/#/deadlocks?target=${target}&range=24h`);
  const inventory = page.locator('.database-scope-status');
  const choices = page.getByRole('combobox',{name:'Workspace database',exact:true});
  await expect(inventory).toHaveText('Loaded inventory · 9 databases loaded.');
  await expect(choices.locator('option')).toHaveCount(10);
  await choices.selectOption('5');
  await expect(page).toHaveURL(/databaseId=5/);
  await page.reload();
  await expect(inventory).toHaveText('Loaded inventory · 9 databases loaded.');
  await expect(choices).toHaveValue('5');
  await page.screenshot({path:info.outputPath(`legacy-inventory-${width}.png`),fullPage:true});
  await page.goto(`/#/health?target=${target}&range=24h&databaseId=5&healthSection=databases`);
  const health = page.getByRole('region',{name:'Server diagnostics for SYNTHETIC SQL 02',exact:true})
    .getByRole('region',{name:'Database health',exact:true});
  await expect(health).toContainText('SYNTHETIC legacy database 5');
  await expect(health.getByText('Not collected',{exact:true})).toHaveCount(9);
  const selected = health.getByRole('article').filter({has:page.getByRole('heading',{name:'SYNTHETIC legacy database 5',exact:true})});
  await expect(selected.getByText('Not collected',{exact:true})).toHaveCount(1);
  expect(failures).toEqual([]);
});

test('an explicit malformed log reuse wait still rejects inventory safely', async ({page}) => {
  await installLegacyInventory(page,true);
  await page.goto(`/#/deadlocks?target=${target}&range=24h`);
  await expect(page.locator('.database-scope-status')).toContainText('Database inventory unavailable. Health evidence returned an invalid or mismatched response.');
  await expect(page.getByRole('combobox',{name:'Workspace database',exact:true}).locator('option')).toHaveCount(1);
});
