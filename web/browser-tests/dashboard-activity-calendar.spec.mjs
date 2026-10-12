import {test,expect} from '@playwright/test';

const origin='http://127.0.0.1:4338';
const target='11111111-1111-4111-8111-000000000001';
const from='2026-09-26T11:00:00.123456Z',to='2026-09-26T12:00:00.654321Z';
const scope=page=>new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
const sessions=page=>page.locator('.activity-capture-screen');
const picker=page=>sessions(page).getByLabel(/Exact snapshot \(/);
test.beforeEach(async({page})=>{
 await page.clock.install({time:new Date('2026-09-26T12:30:00Z')});
 await page.context().addCookies(['dashboardRefactor','sessionSelectionFixture','databaseScopeFixture'].map(name=>({name,value:'1',url:origin})));
 await page.addInitScript(()=>localStorage.setItem('sqlobserver.timeDisplayMode','utc'));
 await page.goto('/#/activity?'+new URLSearchParams({target,range:'custom',from,to,databaseId:'5'}));
 await sessions(page).getByText('History & capture selection', {exact:true}).click();
 await expect(picker(page)).toHaveValue('2026-09-26T11:31');
});
test('calendar selects an exact retained capture and preserves scope through reload',async({page})=>{
 await expect(picker(page)).toHaveAttribute('type','datetime-local');
 await expect(sessions(page).getByRole('combobox',{name:/Exact snapshot/})).toHaveCount(0);
 await picker(page).fill('2026-09-26T11:30');
 await expect.poll(()=>scope(page).get('sessionSnapshot')).toBe('88888888-8888-4888-8888-888888888888');
 expect(scope(page).get('sessionObserved')).toBe('2026-09-26T11:30:00.123456Z');
 expect(scope(page).get('from')).toBe(from);expect(scope(page).get('to')).toBe(to);expect(scope(page).get('databaseId')).toBe('5');
 await page.reload();await sessions(page).getByText('History & capture selection', {exact:true}).click();await expect(picker(page)).toHaveValue('2026-09-26T11:30');
 await expect(sessions(page)).toContainText('Selected capture pinned');
 await sessions(page).getByRole('button',{name:'Next minute',exact:true}).click();
 await expect.poll(()=>scope(page).get('sessionSnapshot')).toBe('99999999-9999-4999-8999-999999999999');
 await expect(picker(page)).toHaveValue('2026-09-26T11:31');
});
test('calendar gaps and invalid dates never substitute another capture',async({page})=>{
 await picker(page).fill('2026-09-26T11:30');
 await expect.poll(()=>scope(page).get('sessionSnapshot')).toBe('88888888-8888-4888-8888-888888888888');
 const before=page.url();await picker(page).fill('2026-09-26T13:00');
 await expect(picker(page)).toHaveAttribute('aria-invalid','true');
 await expect(sessions(page)).toContainText('The selected capture has not changed.');
 expect(page.url()).toBe(before);
 await picker(page).fill('2026-09-26T11:29');
 await expect(sessions(page)).toContainText('No successful snapshot was captured in this minute.');
 expect(scope(page).has('sessionSnapshot')).toBe(false);
 await expect(sessions(page).getByRole('button',{name:/Inspect /})).toHaveCount(0);
 await expect(picker(page)).toHaveAttribute('aria-invalid','false');
});
for(const width of [390,1440])test(`calendar is labeled, keyboard reachable and fits at ${width}px`,async({page},info)=>{
 await page.setViewportSize({width,height:1000});
 await picker(page).focus();await expect(picker(page)).toBeFocused();
 await expect(picker(page)).toHaveAttribute('min','2026-09-26T11:00');
 await expect(picker(page)).toHaveAttribute('max','2026-09-26T12:00');
 await page.getByRole('button',{name:'Show local time',exact:true}).click();
 await expect(picker(page)).toHaveValue('2026-09-26T07:31');
 await expect(picker(page)).toHaveAccessibleName('Exact snapshot (America/New_York)');
 const before=page.url(); await picker(page).fill('2026-09-26T07:30');
 await expect.poll(()=>scope(page).get('sessionObserved')).toBe('2026-09-26T11:30:00.123456Z');
 expect(scope(page).get('from')).toBe(from); expect(scope(page).get('to')).toBe(to); expect(page.url()).not.toBe(before);
 await page.getByRole('button',{name:'Show UTC time',exact:true}).click();
 await expect(picker(page)).toHaveValue('2026-09-26T11:30');
 expect(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth)).toBeLessThanOrEqual(1);
 await picker(page).scrollIntoViewIfNeeded();
 await sessions(page).screenshot({path:info.outputPath(`activity-calendar-${width}.png`)});
});
