import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import {mkdir} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';

const origin='http://127.0.0.1:4322';
const target='11111111-1111-4111-8111-000000000002';
const screens=[['fleet','overview'],['server','health'],['activity','activity'],['queries','queries'],['deadlocks','deadlocks'],['health-checks','operations'],['alerts','alerts'],['resources','resources'],['waits','waits']];

function closedQuery(url,keys){
 for(const key of url.searchParams.keys()){
  expect(keys,`Unregistered query key for ${url.pathname}`).toContain(key);
  expect(url.searchParams.getAll(key),`Repeated query key for ${url.pathname}`).toHaveLength(1);
 }
}
function queryWindow(url){
 const ticks=value=>{
  const match=/^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(?:Z|\+00:00)$/.exec(value??'');
  expect(match,`Invalid UTC bound for ${url.pathname}`).not.toBeNull();
  expect(match[2]?.[6]??'0').toBe('0');
  const seconds=Date.parse(match[1]+'Z');expect(Number.isFinite(seconds)).toBe(true);
  return BigInt(seconds)*1000n+BigInt((match[2]??'').slice(0,6).padEnd(6,'0'));
 };
 const from=ticks(url.searchParams.get('fromUtc')),to=ticks(url.searchParams.get('toUtc'));
 expect(to>from).toBe(true);expect(to-from<=7n*86400000000n).toBe(true);
}
function registeredQuery(url){
 const route=/^\/api\/v1\/observation-targets\/([^/]+)\/(query-performance\/(?:status|top|waits\/top|regressions|cache\/top)|resources\/volumes)$/.exec(url.pathname);
 if(!route)return;
 expect(route[1]).toBe(target);
 const surface=route[2];
 if(surface==='resources/volumes'){
  closedQuery(url,['limit']);expect(url.searchParams.get('limit')).toBe('25');return;
 }
 const ranking=surface==='query-performance/top'||surface==='query-performance/cache/top';
 closedQuery(url,['fromUtc','toUtc',...(surface==='query-performance/status'?[]:['limit']),...(ranking?['metric']:[]),...(surface==='query-performance/waits/top'?['category']:[])]);
 queryWindow(url);
 if(surface!=='query-performance/status')expect(url.searchParams.get('limit')).toBe(surface==='query-performance/top'?'200':'25');
 if(ranking)expect(['cpu','duration','executions','logical_reads','writes','rows']).toContain(url.searchParams.get('metric'));
 if(url.searchParams.has('category'))expect(url.searchParams.get('category')).toMatch(/^(?:[0-9]|1[0-9]|20)$/);
}
async function registeredLegacyNoData(route,url,reads){
 const match=/^\/api\/v1\/observation-targets\/([^/]+)\/(backups|availability-groups\/(?:replicas|databases))$/.exec(url.pathname);
 if(!match)return false;
 expect(route.request().method()).toBe('GET');expect(match[1]).toBe(target);
 closedQuery(url,['limit']);expect(url.searchParams.get('limit')).toBe('50');
 // Explicit native NoData projection: no collection receipt, observation, rows,
 // or continuation. This does not treat the server's generic {}404 as evidence.
 const body={targetId:target,targetRevision:1,runId:null,evidence:null,observedAtUtc:new Date().toISOString(),state:'NoData',items:[],hasMore:false,nextCursor:null,truncated:false,
  ...(match[2].startsWith('availability-groups/')?{visibilityScope:'PrimaryAllKnown'}:{})};
 expect(body).toMatchObject({runId:null,evidence:null,state:'NoData',items:[],hasMore:false,nextCursor:null});
 reads.push(match[2]);
 await route.fulfill({status:200,contentType:'application/json',headers:{'cache-control':'no-store'},body:JSON.stringify(body)});return true;
}

async function open(page,surface){
 await page.context().addCookies([{name:'dashboardRefactor',value:'1',url:origin},{name:'chartReferenceRole',value:'viewer',url:origin},
  ...(['health','queries','waits'].includes(surface)?[{name:'timelineFixture',value:'1',url:origin}]:[]),
  ...(['queries','waits'].includes(surface)?[{name:'queryRowTrendFixture',value:'1',url:origin}]:[]),
  ...(surface==='resources'?[{name:'resourceInspectionFixture',value:'1',url:origin},{name:'sqlVolumeFixture',value:'1',url:origin}]:[])]);
 await page.goto('/#/'+surface+'?'+new URLSearchParams({target,range:'1h',...(surface==='queries'?{queryView:'cache'}:{})}));
 if(surface==='alerts')await expect(page.getByRole('heading',{name:'Alert triage',exact:true})).toBeVisible();
 else await expect(page.getByRole('combobox',{name:'Time range',exact:true})).toHaveValue('1h');
 await page.waitForLoadState('networkidle');
 if(surface==='deadlocks')await expect(page.getByRole('img',{name:'Deadlock relationship diagram'})).toBeVisible();
 if(surface==='queries')await expect(page.getByRole('region',{name:'Plan cache interval ranking',exact:true})).toContainText('000000001000');
 if(surface==='resources')await expect(page.getByRole('region',{name:'Per-NUMA page life expectancy',exact:true}).locator('.overview-chart')).toHaveCount(2);
 if(surface==='waits')await expect(page.getByRole('region',{name:'Wait categories over time',exact:true}).getByRole('img',{name:/^Server wait time by category/})).toHaveCount(1);
 if(surface==='operations')await expect(page.getByRole('region',{name:'Backup freshness assessments',exact:true})).toBeVisible();
 await expect(page.locator('main')).toBeVisible();
}

async function keyboardFocus(page){
 await page.keyboard.press('Tab');
 const skip=page.getByRole('link',{name:'Skip to content',exact:true});
 await expect(skip).toBeFocused();await expect(skip).toBeVisible();await skip.press('Enter');await expect(page.locator('main')).toBeFocused();
 const refresh=page.getByRole('button',{name:'↻ Refresh',exact:true});
 // Reach the real control from the skip target using sequential keyboard navigation.
 // A programmatic .focus() would miss keyboard modality and focus traversal defects.
 for(let step=0;step<80&&!await refresh.evaluate(e=>e===document.activeElement);step++)await page.keyboard.press('Tab');
 await expect(refresh).toBeFocused();await refresh.scrollIntoViewIfNeeded();
 const painted=await refresh.evaluate(e=>{
  const style=getComputedStyle(e),box=e.getBoundingClientRect();
  return{visible:e.matches(':focus-visible'),width:parseFloat(style.outlineWidth),style:style.outlineStyle,color:style.outlineColor,x:box.x,y:box.y,right:box.right,bottom:box.bottom,widthPx:box.width,heightPx:box.height};
 });
 expect(painted.visible).toBe(true);expect(painted.width).toBeGreaterThanOrEqual(2);expect(painted.style).not.toBe('none');expect(painted.color).not.toBe('rgba(0, 0, 0, 0)');
 expect(painted.widthPx).toBeGreaterThanOrEqual(44);expect(painted.heightPx).toBeGreaterThanOrEqual(44);
 expect(painted.x).toBeGreaterThanOrEqual(0);expect(painted.right).toBeLessThanOrEqual((await page.viewportSize()).width+1);expect(painted.y).toBeGreaterThanOrEqual(0);expect(painted.bottom).toBeLessThanOrEqual((await page.viewportSize()).height+1);
 return painted;
}

for(const width of [320,1440])for(const[name,surface]of screens)test(`${name}: forced colors preserve accessible evidence and keyboard focus at ${width}px`,async({page},info)=>{
 const errors=[],unknown=[],registeredNoData=[];
 page.on('pageerror',error=>errors.push(error.message));
 page.on('response',response=>{const url=new URL(response.url());if(url.origin===origin&&url.pathname.startsWith('/api/')&&response.status()===404)unknown.push('Unregistered API: '+url.pathname);});
 await page.route('**/*',async route=>{const request=route.request(),url=new URL(request.url());if(url.origin!==origin){unknown.push('External request: '+url.origin);return route.abort();}if(url.pathname.startsWith('/api/')&&request.method()!=='GET'){unknown.push('Unexpected API method: '+request.method());return route.abort();}registeredQuery(url);if(await registeredLegacyNoData(route,url,registeredNoData))return;return route.continue();});
 await page.emulateMedia({forcedColors:'active',colorScheme:'light'});await page.setViewportSize({width,height:1000});await open(page,surface);
 expect(await page.evaluate(()=>matchMedia('(forced-colors: active)').matches)).toBe(true);
 // Forced-colors legitimately computes color-scheme as "light dark". The test
 // audits actual browser system colors rather than insisting on a dark string.
 const painted=await keyboardFocus(page);
 expect(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth),'Wide evidence must scroll locally, not widen the document').toBeLessThanOrEqual(1);
 const axe=await new AxeBuilder({page}).withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa','best-practice']).analyze();
 await info.attach('forced-colors-accessibility',{body:JSON.stringify(axe.violations,null,2),contentType:'application/json'});await info.attach('forced-colors-keyboard-focus',{body:JSON.stringify(painted,null,2),contentType:'application/json'});
 expect(axe.violations,JSON.stringify(axe.violations.map(v=>({id:v.id,nodes:v.nodes.map(n=>n.target)})))).toEqual([]);expect(errors).toEqual([]);expect(unknown).toEqual([]);
 if(surface==='operations')expect([...new Set(registeredNoData)].sort()).toEqual(['availability-groups/databases','availability-groups/replicas','backups']);
 const folder=fileURLToPath(new URL('../../artifacts/dashboard-tempdb-history-2026-10-04/screenshots/forced-screens/',import.meta.url));await mkdir(folder,{recursive:true});
 const focusPath=folder+`${name}-${width}-forced-focus.png`;await page.screenshot({path:focusPath});await info.attach(`${name}-${width}-forced-focus`,{path:focusPath,contentType:'image/png'});
 await page.evaluate(()=>{document.activeElement?.blur();scrollTo({top:0,left:0,behavior:'instant'});});const path=folder+`${name}-${width}-forced.png`;await page.screenshot({path,fullPage:true});await info.attach(`${name}-${width}-forced`,{path,contentType:'image/png'});
});
