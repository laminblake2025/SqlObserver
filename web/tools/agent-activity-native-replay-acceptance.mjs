// Finite original native source replay through real repository/root HTTP/compiled UI.
// Test authentication only; no API interception or continuous-deployment claim.
import assert from 'node:assert/strict';
import {mkdir,readFile,writeFile} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
import {chromium,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const required=name=>{const value=process.env[`SQLOBSERVER_AGENT_NATIVE_REPLAY_${name}`];assert.ok(value,`Missing native replay input ${name}`);return value;};
const origin=new URL(required('API_URL'));
const mode=required('MODE');assert.ok(['native','synthetic-rehearsal'].includes(mode));const synthetic=mode==='synthetic-rehearsal';
assert.ok(['127.0.0.1','localhost','[::1]'].includes(origin.hostname),'Acceptance must use the isolated loopback host');
assert.equal(origin.protocol,'http:');
const target=required('TARGET_ID'),revision=required('TARGET_REVISION'),run=required('RUN_ID');
const from=required('FROM_UTC'),to=required('TO_UTC');
const expectedText=await readFile(required('EXPECTED_CAPTURE_PATH'),'utf8');
assert.ok(Buffer.byteLength(expectedText)<=530000,'Expected public capture must be bounded');
const expected=JSON.parse(expectedText);
assert.ok(expected.jobs.length>=0&&expected.jobs.length<=512);
assert.ok(expected.before.service.status===1&&expected.after.service.status===1,'Recorded native lane requires stopped Agent source');
assert.ok(['ServiceNotRunning','SourceTruncated'].includes(expected.gap));
assert.ok(expected.jobs.every(job=>job.candidateState==='Unknown'));
const folder=fileURLToPath(new URL(`../../artifacts/dashboard-agent-native-replay-acceptance-2026-10-07/${mode}-browser-${new Date().toISOString().replaceAll(':','-')}/`,import.meta.url));
await mkdir(folder,{recursive:true});
await writeFile(folder+'expected-public-capture.json',expectedText);
const browser=await chromium.launch();
const context=await browser.newContext({locale:'en-US',timezoneId:'America/New_York',extraHTTPHeaders:{'X-Agent-Native-Replay-Identity':'viewer'}});
await context.addCookies([{name:'dashboardRefactor',value:'1',url:origin.href}]);
await context.addInitScript(()=>localStorage.setItem('sqlobserver.timeDisplayMode','utc'));
await context.tracing.start({screenshots:true,snapshots:true,sources:true});
const page=await context.newPage(),errors=[],responses=[],externalRequests=[];
page.on('pageerror',error=>errors.push(error.message));
page.on('request',request=>{const url=new URL(request.url());if(url.origin!==origin.origin&&!request.url().startsWith('data:'))externalRequests.push(request.url());});
page.on('response',response=>{const url=new URL(response.url());if(url.pathname.includes('/sql-agent/activity/observations'))responses.push({url:response.url(),method:response.request().method(),status:response.status()});});
const panel=()=>page.getByRole('region',{name:'SQL Agent activity observations',exact:true});
const selected=()=>panel().getByRole('region',{name:'Selected Agent activity receipt',exact:true});
const url=pin=>{const query=new URLSearchParams({target,check:'agent',range:'custom',from,to});if(pin){query.set('agentActivityRun',pin);query.set('agentActivityRevision',revision);}return new URL('/#/operations?'+query,origin).href;};
const flags=value=>{assert.equal(value.productionAdmitted,false);assert.equal(value.serviceSessionAssociationQualified,false);assert.equal(value.nativeQualification,'Absent');};
async function disclose(text){const summary=panel().getByText(text,{exact:true});if(!await summary.evaluate(element=>element.parentElement.open))await summary.press('Enter');}
try{
 const query=new URLSearchParams({targetRevision:revision,fromUtc:from,toUtc:to,sampleTargetRevision:revision});
 const path=`/api/v1/observation-targets/${target}/sql-agent/activity/observations`;
 const exactResponse=await context.request.get(new URL(`${path}/${run}?${query}`,origin).href);
 assert.equal(exactResponse.status(),200);const exact=await exactResponse.json();assert.equal(exact.runId,run);assert.deepEqual(exact.capture,expected);flags(exact);flags(exact.capture);
 assert.ok(!/enrollmentId|evidenceSha256|expectedWindowsSid|definitionSha256|publicCertificateSha256|elapsedSeconds|currentStep/iu.test(JSON.stringify(exact)));
 await writeFile(folder+'exact-public-response.json',JSON.stringify(exact,null,2));
 const listQuery=new URLSearchParams({targetRevision:revision,fromUtc:from,toUtc:to,limit:'100'});
 const listedResponse=await context.request.get(new URL(`${path}?${listQuery}`,origin).href);
 assert.equal(listedResponse.status(),200);const listed=await listedResponse.json();assert.equal(listed.coverage.runCount,1);assert.equal(listed.items.length,1);assert.equal(listed.items[0].runId,run);assert.ok(!/jobId|enrollment/iu.test(JSON.stringify(listed)));flags(listed);
 const collection=await context.request.get(new URL(`/api/v1/observation-targets/${target}/sql-agent/activity-observations/collection?targetRevision=${revision}`,origin).href);
 assert.equal(collection.status(),200);assert.equal((await collection.json()).collectorEnabled,false,'Original enrollment revoked while historical evidence remains readable');
 for(const width of [320,1440]){
  await page.setViewportSize({width,height:1000});await page.goto(url(run),{waitUntil:'domcontentloaded'});
  await expect(selected()).toContainText(run,{timeout:20000});
  if(expected.gap==='ServiceNotRunning')await expect(selected()).toContainText('Agent service was not running');
  await disclose('Exact Agent source headers');await expect(selected()).toContainText(expected.before.sourceObservedAtUtc);await expect(selected()).toContainText(expected.after.sourceObservedAtUtc);
  await disclose('Browse jobs from selected Agent activity receipt');
  const scroll=selected().getByRole('region',{name:'Jobs from selected Agent activity receipt',exact:true});
  const rows=scroll.locator('tbody tr');await expect(rows).toHaveCount(expected.jobs.length);
  const renderedRows=await rows.evaluateAll(elements=>elements.map(element=>element.textContent));
  for(const job of expected.jobs){
   const matches=renderedRows.filter(text=>text.includes(job.jobId));assert.equal(matches.length,1);assert.ok(matches[0].includes('Unknown'));
   for(const key of ['requestedLocal','queuedLocal','startExecutionLocal','stopExecutionLocal','lastExecutedStepLocal'])if(job[key]!==null)assert.ok(matches[0].includes(job[key]),`Original ${key} retained in job row`);
  }
  if(expected.jobs.length===0)await expect(selected()).toContainText('This exact capture reported zero jobs.');
  await scroll.press('ArrowRight');await expect(scroll).toBeFocused();
  if(width===320)await expect.poll(()=>scroll.evaluate(element=>element.scrollLeft)).toBeGreaterThan(0);
  assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth)<=1,'Operations must fit the viewport');
  const accessibility=await new AxeBuilder({page}).include('.agent-activity-history').withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa']).analyze();assert.deepEqual(accessibility.violations,[]);
  await page.screenshot({path:folder+`native-replay-agent-${width}.png`,fullPage:false});await page.reload({waitUntil:'domcontentloaded'});
  await expect(selected()).toContainText(run);
  assert.equal(new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('agentActivityRun'),run);
  assert.equal(new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('agentActivityRevision'),revision);
 }
 await page.goto(url(),{waitUntil:'domcontentloaded'});await expect(selected()).toContainText(run);await expect(selected()).toContainText('Default: newest completed attempt');
 const missing=run==='99999999-9999-4999-8999-999999999999'?'88888888-8888-4888-8888-888888888888':'99999999-9999-4999-8999-999999999999';
 await page.goto(url(missing),{waitUntil:'domcontentloaded'});await expect(selected()).toContainText(missing);
 await expect(selected()).toContainText('This exact run and sample revision is not retained. No replacement is selected.');
 await expect(selected().getByText('Browse jobs from selected Agent activity receipt',{exact:true})).toHaveCount(0);
 await expect(selected().getByText('Exact Agent source headers',{exact:true})).toHaveCount(0);
 assert.deepEqual(errors,[]);assert.deepEqual(externalRequests,[]);
 const observed=new Set();
 for(const response of responses){
  const request=new URL(response.url);assert.equal(request.origin,origin.origin);assert.equal(response.method,'GET');assert.equal(response.status,200);
  const kind=request.pathname===path?'list':request.pathname===`${path}/${run}`?'originalExact':request.pathname===`${path}/${missing}`?'missingExact':null;
  assert.ok(kind,'Every observed Agent read must use the original target and an expected exact identity');
  const fields={targetRevision:revision,fromUtc:from,toUtc:to,...(kind==='list'?{limit:'100'}:{sampleTargetRevision:revision})};
  assert.deepEqual([...request.searchParams.keys()].sort(),Object.keys(fields).sort(),'Only the exact expected query fields are permitted');
  for(const [key,value] of Object.entries(fields))assert.deepEqual(request.searchParams.getAll(key),[value],`Original ${key} must be sent exactly once`);
  observed.add(kind);
 }
 for(const kind of ['list','originalExact','missingExact'])assert.ok(observed.has(kind),`Built UI must issue the ${kind} Agent request`);
 const result={passed:true,synthetic,originalAuthorizationPreserved:!synthetic,nativeSourceProofQualified:false,target,revision,originalRunId:run,retainedJobs:expected.jobs.length,widths:[320,1440],actualAgentHttpResponses:responses.length,
  trafficProof:{listObserved:true,originalExactObserved:true,missingExactObserved:true,exactQueryBindingsVerified:true,allObservedAgentResponses200:true},
  noApiInterception:true,enrollmentRevokedCollectionDisabled:true,sourceCapturePreserved:true,reloadPreservedExactRun:true,missingExactRunHasNoFallback:true,
  scopedAxeViolations:0,productionCollectorInvokedAgain:false,repositoryResolverNativeQualified:false,continuousDeploymentQualified:false,
  nativeQualification:'Absent',productionAdmitted:false,serviceSessionAssociationQualified:false,
  scope:synthetic?'Explicit synthetic source rehearsal -> restricted sealed canonical repository -> production root HTTP and compiled Operations; no native artifact proof'
   :'Finite recorded native source -> restricted sealed canonical repository -> production root HTTP and compiled Operations; test authentication only'};
 await writeFile(folder+'result.json',JSON.stringify(result,null,2));console.log(JSON.stringify({...result,evidenceDirectory:folder}));
}catch(error){await page.screenshot({path:folder+'failure.png',fullPage:true}).catch(()=>{});await writeFile(folder+'failure.json',JSON.stringify({error:String(error),pageErrors:errors,responses,externalRequests},null,2));throw error;}
finally{await context.tracing.stop({path:folder+'trace.zip'});await browser.close();}
