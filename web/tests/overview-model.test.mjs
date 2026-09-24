import test from 'node:test';
import assert from 'node:assert/strict';
import {readOverviewScope,overviewHref,issueHref,overviewWindow,aggregateValue,rankedIssues,overviewEvidenceFooter,overviewResourceObservationText} from '../src/features/overview/overviewModel.ts';
import {readRoute} from '../src/dashboardModel.ts';
const now=Date.parse('2026-09-05T12:00:00Z');
test('scope, comparison and fixed UTC ranges survive drill-down and URL reload',()=>{
 const scope={target:'server-one',range:'custom',from:'2026-09-01T00:00:00Z',to:'2026-09-02T00:00:00Z',compare:true};
 assert.deepEqual(readOverviewScope(overviewHref(scope,'activity')),scope);
 assert.equal(readOverviewScope(overviewHref(scope,'overview','')).target,'');
 assert.deepEqual(overviewWindow(scope,now),{fromUtc:new Date(scope.from).toISOString(),toUtc:new Date(scope.to).toISOString()});
});
test('fleet issue drill-down carries its own target, selected window, and observation time',()=>{
 const scope={target:'',range:'custom',from:'2026-09-01T00:00:00Z',to:'2026-09-02T00:00:00Z',compare:true};
 const at='2026-09-01T11:30:00Z';
 const href=issueHref(scope,'server-two','activity',at);
 assert.deepEqual(readOverviewScope(href),{...scope,target:'server-two'});
 assert.deepEqual(readRoute(href),{page:'activity',target:'server-two',activityAtUtc:at});
 const alerts=issueHref(scope,'server-two','alerts',at);
 assert.equal(readRoute(alerts).activityAtUtc,undefined);
});
test('all servers defaults to 24h and windows reject invalid and future inputs',()=>{
 const scope=readOverviewScope('#/overview');assert.equal(scope.target,'');assert.equal(scope.range,'24h');
 assert.equal(overviewWindow(scope,now).fromUtc,'2026-09-04T12:00:00.000Z');
 for(const [from,to] of [['invalid','invalid'],['2026-09-06','2026-09-05'],['2026-07-01','2026-09-01'],['2026-09-05','2026-09-06']]) assert.throws(()=>overviewWindow({...scope,range:'custom',from,to},now));
});
const evidence=(value,state='current')=>({activeAlerts:{value,state},blockedSessions:{value,state},deadlocks:{value,state}});
test('counts cover ten servers and more than five alerts, with unknown and stale distinct from zero',()=>{
 assert.equal(aggregateValue(Array.from({length:10},()=>evidence(9)),'activeAlerts').text,'90');
 assert.equal(aggregateValue([evidence(0)],'activeAlerts').text,'0');
 assert.equal(aggregateValue([evidence(null,'unavailable')],'activeAlerts').text,'—');
 assert.equal(aggregateValue([evidence(4),evidence(15,'stale')],'blockedSessions').text,'4+');
 assert.equal(aggregateValue([evidence(4,'partial')],'activeAlerts').text,'4+');
});
test('attention ranks by impact then duration instead of inventory order',()=>{
 const snapshot={evidence:[{issues:Array.from({length:8},(_,i)=>({server:`SQL${i}`,priority:i===7?0:2,observedAtUtc:'2026-09-05T01:00:00Z'}))}]};
 const issues=rankedIssues(snapshot);assert.equal(issues.length,5);assert.equal(issues[0].server,'SQL7');
});
test('resource wording distinguishes selected-window observations and source snapshots',()=>{
 const refreshed='2026-09-05T12:00:00Z';
 const host={label:'host.volume.free_bytes · mount=/data',state:'observed',observedAtUtc:'2026-09-05T10:30:15Z'};
 const wait={label:'Wait · LCK_M_X',state:'stale',observedAtUtc:'2026-09-05T10:30:15Z'};
 assert.match(overviewResourceObservationText(host,refreshed),/^Latest in selected window · observed · Observation age: 1h 29m · 2026-09-05 10:30:15/);
 assert.match(overviewResourceObservationText(wait,refreshed),/^Latest source snapshot · stale · 2026-09-05 10:30:15/);
 assert.doesNotMatch(overviewResourceObservationText(wait,refreshed),/selected window|Observation age/);
 const footer=overviewEvidenceFooter({fromUtc:'2026-09-04T12:00:00Z',toUtc:refreshed,refreshedAtUtc:refreshed});
 assert.match(footer,/Host, storage, and replication metric rows show the latest observation inside the selected window/);
 assert.match(footer,/observation age is measured against refresh time 2026-09-05T12:00:00Z/);
});
