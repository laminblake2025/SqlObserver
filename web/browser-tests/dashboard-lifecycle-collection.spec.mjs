import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
const alpha='11111111-1111-4111-8111-000000000001';
const panel=p=>p.getByRole('region',{name:'Server lifecycle collection',exact:true});
for(const width of [320,1440])test(`dashboard reader sees default-off lifecycle control without management authority at ${width}`,async({page})=>{
 await page.setViewportSize({width,height:1000});await page.context().addCookies([{name:'dashboardRefactor',value:'1',url:'http://127.0.0.1:4432'},{name:'chartReferenceRole',value:'viewer',url:'http://127.0.0.1:4432'}]);
 await page.goto('/#/health?'+new URLSearchParams({target:alpha,range:'custom',from:'2026-10-02T10:00:00Z',to:'2026-10-02T11:00:00Z',healthSection:'summary'}));
 await expect(panel(page)).toContainText('Collection disabled');await expect(panel(page).getByRole('button',{name:'Enable lifecycle collection',exact:true})).toHaveCount(0);await expect(page.getByRole('region',{name:'Server lifecycle history',exact:true})).toBeVisible();
 expect(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth)).toBeLessThanOrEqual(1);
 const axe=await new AxeBuilder({page}).include('.lifecycle-collection-control').withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa']).analyze();expect(axe.violations).toEqual([]);
});
test('dashboard administrator toggles lifecycle configuration independently of retained history',async({page})=>{
 await page.context().addCookies([{name:'dashboardRefactor',value:'1',url:'http://127.0.0.1:4432'},{name:'chartReferenceRole',value:'admin',url:'http://127.0.0.1:4432'}]);
 await page.goto('/#/health?'+new URLSearchParams({target:alpha,range:'1h',healthSection:'summary'}));await expect(panel(page)).toContainText('Collection disabled');
 await panel(page).getByRole('button',{name:'Enable lifecycle collection',exact:true}).click();await expect(panel(page)).toContainText('Collection enabled');await expect(page.getByRole('region',{name:'Server lifecycle history',exact:true})).toBeVisible();
 await panel(page).getByRole('button',{name:'Disable lifecycle collection',exact:true}).click();await expect(panel(page)).toContainText('Collection disabled');
});
