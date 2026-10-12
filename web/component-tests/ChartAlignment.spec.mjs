import { test,expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const field=page=>page.getByRole('textbox',{name:'Dashboard inspection time (UTC)',exact:true});
const alignment=page=>page.getByRole('region',{name:'Dashboard chart alignment',exact:true});
const at='2026-09-26T10:00:00.123456Z';
const errors=new WeakMap();
test.beforeEach(async({page})=>{
 const unexpected=[];errors.set(page,unexpected);page.on('pageerror',error=>unexpected.push(error.message));
 await page.route('**/*',route=>{const url=new URL(route.request().url());if(url.origin!=='http://127.0.0.1:4328'||url.pathname.startsWith('/api/')){unexpected.push(url.href);return route.abort();}return route.continue();});
 await page.goto('/component-tests/chart-alignment.html');await expect(field(page)).toBeVisible();
});
test.afterEach(async({page})=>expect(errors.get(page)).toEqual([]));
test('exact arbitrary UTC keyboard alignment shares both charts and restores through reload/history without replacing the window',async({page})=>{
 const before=new URLSearchParams(new URL(page.url()).hash.split('?')[1]);
 await field(page).fill(at);await field(page).press('Enter');await expect(alignment(page).locator('time')).toHaveAttribute('datetime',at);
 await expect(page.locator('.chart-crosshair line')).toHaveCount(2);
 const after=new URLSearchParams(new URL(page.url()).hash.split('?')[1]);for(const[key,value]of before)expect(after.get(key)).toBe(value);
 expect(after.get('chartAt')).toBe(at);await page.reload();await expect(field(page)).toHaveValue(at);
 await page.getByRole('button',{name:'Clear dashboard alignment',exact:true}).click();await expect(page.locator('.chart-crosshair')).toHaveCount(0);
 await page.goBack();await expect(alignment(page).locator('time')).toHaveAttribute('datetime',at);await page.goForward();await expect(page.locator('.chart-crosshair')).toHaveCount(0);
});
test('invalid and end-boundary input cannot move a received marker; malformed URL stays explicit until recovery',async({page})=>{
 await field(page).fill(at);await field(page).press('Enter');
 await expect(alignment(page).locator('time')).toHaveAttribute('datetime',at);
 await expect.poll(()=>new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('chartAt')).toBe(at);
 for(const value of ['2026-09-26T12:00:00.000001Z','2026-09-26T10:00:00.1234567Z','bad']){
  await field(page).fill(value);await field(page).press('Enter');await expect(alignment(page).getByRole('alert')).toBeVisible();await expect(alignment(page).locator('time')).toHaveAttribute('datetime',at);
 }
 await page.evaluate(()=>{location.hash+='&chartAt=bad';});await expect(alignment(page)).toContainText('invalid or repeated');await expect(page.locator('.chart-crosshair')).toHaveCount(0);
 await field(page).fill(at);await field(page).press('Enter');await expect(alignment(page).getByRole('alert')).toHaveCount(0);await expect(page.locator('.chart-crosshair line')).toHaveCount(2);
});
test('out-of-window URL time remains visible without substituting an in-window marker',async({page})=>{
 await page.evaluate(()=>{location.hash+='&chartAt=2026-09-26T12:00:00.000001Z';});await expect(alignment(page)).toContainText('outside the loaded window');
 await expect(field(page)).toHaveValue('2026-09-26T12:00:00.000001Z');await expect(page.locator('.chart-crosshair')).toHaveCount(0);
});

test('native zero-offset bounds accept exact Z keyboard alignment and keep the end exclusive',async({page})=>{
 await page.getByRole('button',{name:'Use native UTC bounds',exact:true}).click();
 await field(page).fill('2026-09-26T12:00:00.000000Z');await field(page).press('Enter');
 await expect(alignment(page).locator('time')).toHaveAttribute('datetime','2026-09-26T12:00:00.000000Z');
 await expect(page.locator('.chart-crosshair line')).toHaveCount(2);
 await field(page).fill('2026-09-26T12:00:00.000001Z');await field(page).press('Enter');
 await expect(alignment(page).getByRole('alert')).toContainText('before its end');
 await expect(alignment(page).locator('time')).toHaveAttribute('datetime','2026-09-26T12:00:00.000000Z');
});

test('native event alignment remains independent of the arbitrary UTC selection',async({page})=>{
 await page.getByRole('button',{name:'Use native UTC bounds',exact:true}).click();
 await page.getByRole('button',{name:'Use native event alignment',exact:true}).click();
 await expect(alignment(page).locator('time')).toHaveAttribute('datetime','2026-09-26T10:00:00.1234560+00:00');
 await expect(page.locator('.chart-crosshair line')).toHaveCount(2);
 await expect(page.getByRole('button',{name:'Clear dashboard alignment',exact:true})).toBeDisabled();
 await field(page).fill('2026-09-26T11:00:00.654321Z');await field(page).press('Enter');
 await expect(alignment(page).locator('time')).toHaveAttribute('datetime','2026-09-26T11:00:00.654321Z');
 await page.getByRole('button',{name:'Clear dashboard alignment',exact:true}).click();
 await expect(alignment(page).locator('time')).toHaveAttribute('datetime','2026-09-26T10:00:00.1234560+00:00');
});
for(const transition of ['target ABA','revision'])test(`held pointer callback is fenced after ${transition} and does not resurrect alignment`,async({page})=>{
 await page.getByRole('button',{name:'Capture pointer callback',exact:true}).click();
 if(transition==='revision')await page.getByRole('button',{name:'Change revision',exact:true}).click();
 else {await page.getByRole('button',{name:'Change target',exact:true}).click();await page.getByRole('button',{name:'Change target',exact:true}).click();}
 await page.getByRole('button',{name:'Deliver held callback',exact:true}).click();await expect(page.locator('.chart-crosshair')).toHaveCount(0);
});
for(const width of [320,390,768,1440])test(`keyboard UTC alignment and readable shared plots fit dark layout at ${width}px`,async({page},info)=>{
 await page.setViewportSize({width,height:1000});await field(page).focus();await field(page).fill(at);await page.keyboard.press('Tab');
 const apply=page.getByRole('button',{name:'Align dashboard charts',exact:true});await expect(apply).toBeFocused();await apply.press('Enter');await expect(apply).toBeFocused();
 await expect(alignment(page).locator('time')).toHaveAttribute('datetime',at);expect(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth)).toBeLessThanOrEqual(1);
 const violations=(await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa','best-practice']).analyze()).violations;
 await info.attach(`alignment-${width}-axe`,{body:JSON.stringify(violations),contentType:'application/json'});expect(violations).toEqual([]);
});
