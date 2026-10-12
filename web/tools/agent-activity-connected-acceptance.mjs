// Opt-in synthetic source acceptance against a live disposable repository and production root HTTP.
// This script installs no API interception and makes no native/live qualification claim.
import assert from 'node:assert/strict';
import {mkdir,writeFile} from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
import {chromium,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const required=name=>{const value=process.env[`SQLOBSERVER_AGENT_CONNECTED_${name}`];assert.ok(value,`Missing connected acceptance input ${name}`);return value;};
const origin=new URL(required('API_URL'));
assert.ok(['127.0.0.1','localhost','[::1]'].includes(origin.hostname),'Acceptance must use the isolated loopback host');
assert.equal(origin.protocol,'http:');
const target=required('TARGET_ID'),revision=required('TARGET_REVISION'),success=required('SUCCESS_RUN_ID'),failed=required('FAILED_RUN_ID');
const from=required('FROM_UTC'),to=required('TO_UTC'),expected=JSON.parse(required('EXPECTED_CAPTURE_JSON'));
const folder=fileURLToPath(new URL(`../../artifacts/dashboard-agent-connected-acceptance-2026-10-07/browser-${new Date().toISOString().replaceAll(':','-')}/`,import.meta.url));
await mkdir(folder,{recursive:true});
const browser=await chromium.launch();
const context=await browser.newContext({locale:'en-US',timezoneId:'America/New_York',extraHTTPHeaders:{'X-Agent-Connected-Identity':'viewer'}});
await context.addCookies([{name:'dashboardRefactor',value:'1',url:origin.href}]);
await context.addInitScript(()=>localStorage.setItem('sqlobserver.timeDisplayMode','utc'));
await context.tracing.start({screenshots:true,snapshots:true,sources:true});
const page=await context.newPage(),errors=[],responses=[],externalRequests=[];
page.on('pageerror',error=>errors.push(error.message));
page.on('request',request=>{const url=new URL(request.url());if(url.origin!==origin.origin&&!request.url().startsWith('data:'))externalRequests.push(request.url());});
page.on('response',response=>{const url=new URL(response.url());if(url.pathname.includes('/sql-agent/activity/observations'))responses.push({url:response.url(),status:response.status()});});
const panel=()=>page.getByRole('region',{name:'SQL Agent activity observations',exact:true});
const selected=()=>panel().getByRole('region',{name:'Selected Agent activity receipt',exact:true});
const url=run=>{const query=new URLSearchParams({target,check:'agent',range:'custom',from,to});if(run){query.set('agentActivityRun',run);query.set('agentActivityRevision',revision);}return new URL('/#/operations?'+query,origin).href;};
const flags=value=>{assert.equal(value.productionAdmitted,false);assert.equal(value.serviceSessionAssociationQualified,false);assert.equal(value.nativeQualification,'Absent');};
async function disclose(text){const summary=panel().getByText(text,{exact:true});if(!await summary.evaluate(element=>element.parentElement.open))await summary.press('Enter');}
try{
 const query=new URLSearchParams({targetRevision:revision,fromUtc:from,toUtc:to,sampleTargetRevision:revision});
 const exactResponse=await context.request.get(new URL(`/api/v1/observation-targets/${target}/sql-agent/activity/observations/${success}?${query}`,origin).href);
 assert.equal(exactResponse.status(),200);const exact=await exactResponse.json();assert.equal(exact.runId,success);assert.deepEqual(exact.capture,expected);flags(exact);flags(exact.capture);
 assert.equal(exact.capture.gap,'ServiceNotRunning');assert.equal(exact.capture.jobs.length,12);assert.ok(exact.capture.jobs.every(job=>job.candidateState==='Unknown'));
 assert.equal(exact.capture.jobs[0].startExecutionLocal,'2026-11-01T01:30:00.1234567');assert.equal(exact.capture.jobs[0].stopExecutionLocal,'2026-11-01T00:30:00.1234567');
 assert.ok(!/enrollmentId|evidenceSha256|expectedWindowsSid|definitionSha256|publicCertificateSha256|elapsedSeconds|currentStep|PRIVATE_SYNTHETIC_SOURCE_REFUSAL/iu.test(JSON.stringify(exact)));
 await writeFile(folder+'exact-public-response.json',JSON.stringify(exact,null,2));
 const collection=await context.request.get(new URL(`/api/v1/observation-targets/${target}/sql-agent/activity-observations/collection?targetRevision=${revision}`,origin).href);
 assert.equal(collection.status(),200);assert.equal((await collection.json()).collectorEnabled,false,'Revocation must disable collection while historical evidence remains readable');
 for(const width of [320,1440]){
  await page.setViewportSize({width,height:1000});await page.goto(url(success),{waitUntil:'domcontentloaded'});
  await expect(selected()).toContainText(success,{timeout:20000});await expect(selected()).toContainText('Agent service was not running');
  await disclose('Exact Agent source headers');await expect(selected()).toContainText(expected.before.sourceObservedAtUtc);await expect(selected()).toContainText(expected.after.sourceObservedAtUtc);
  await disclose('Browse jobs from selected Agent activity receipt');const scroll=selected().getByRole('region',{name:'Jobs from selected Agent activity receipt',exact:true});
  await expect(scroll).toContainText('2026-11-01T01:30:00.1234567');await expect(scroll).toContainText('2026-11-01T00:30:00.1234567');await expect(scroll).toContainText('Unknown');
  await scroll.press('ArrowRight');await expect(scroll).toBeFocused();if(width===320)await expect.poll(()=>scroll.evaluate(element=>element.scrollLeft)).toBeGreaterThan(0);
  assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth-innerWidth)<=1,'Operations must fit the viewport');
  const accessibility=await new AxeBuilder({page}).include('.agent-activity-history').withTags(['wcag2a','wcag2aa','wcag21a','wcag21aa']).analyze();assert.deepEqual(accessibility.violations,[]);
  await panel().screenshot({path:folder+`connected-agent-${width}.png`});await page.reload({waitUntil:'domcontentloaded'});
  await expect(selected()).toContainText(success);await expect(selected()).toContainText('Agent service was not running');
  assert.equal(new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('agentActivityRun'),success);
  assert.equal(new URLSearchParams(new URL(page.url()).hash.split('?')[1]).get('agentActivityRevision'),revision);
 }
 await page.goto(url(),{waitUntil:'domcontentloaded'});await expect(selected()).toContainText(failed);await expect(selected()).toContainText('This collection attempt failed. No older successful observation is substituted.');
 await expect(selected()).not.toContainText('Agent service was not running');await expect(selected().getByText('Browse jobs from selected Agent activity receipt',{exact:true})).toHaveCount(0);
 const missing='99999999-9999-4999-8999-999999999999';await page.goto(url(missing),{waitUntil:'domcontentloaded'});await expect(selected()).toContainText(missing);
 await expect(selected()).toContainText('This exact run and sample revision is not retained. No replacement is selected.');await expect(selected()).not.toContainText('Agent service was not running');
 assert.deepEqual(errors,[]);assert.deepEqual(externalRequests,[]);assert.ok(responses.length>=8);assert.ok(responses.every(response=>response.status===200));
 const result={passed:true,target,revision,successRunId:success,failedRunId:failed,widths:[320,1440],actualAgentHttpResponses:responses.length,noApiInterception:true,
  enrollmentRevokedCollectionDisabled:true,sourceCapturePreserved:true,reloadPreservedExactRun:true,newestFailedAttemptHasNoOlderSuccessFallback:true,missingExactRunHasNoFallback:true,
  scopedAxeViolations:0,nativeQualification:'Absent',productionAdmitted:false,serviceSessionAssociationQualified:false,
  scope:'Synthetic source delegate and prerequisites -> production collector -> restricted sealed canonical repository -> production root HTTP and compiled Operations; test authentication only'};
 await writeFile(folder+'result.json',JSON.stringify(result,null,2));console.log(JSON.stringify({...result,evidenceDirectory:folder}));
}catch(error){await page.screenshot({path:folder+'failure.png',fullPage:true}).catch(()=>{});await writeFile(folder+'failure.json',JSON.stringify({error:String(error),pageErrors:errors,responses,externalRequests},null,2));throw error;}
finally{await context.tracing.stop({path:folder+'trace.zip'});await browser.close();}
