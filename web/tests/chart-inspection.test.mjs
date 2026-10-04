import test from 'node:test';
import assert from 'node:assert/strict';
import { chartInspectionUtc, chartInspectionInWindow, chartInspectionHref, readChartInspection, retainChartInspectionHref } from '../src/components/chartInspectionModel.ts';
const target='11111111-1111-4111-8111-000000000001';
const from='2026-09-26T08:00:00.000001Z',to='2026-09-26T12:00:00.000001Z';
const base=`#/health?target=${target}&range=custom&from=${from}&to=${to}&databaseId=6&compare=1&healthSection=summary&timelineEvent=finding:test&timelineAt=2026-09-26T09:00:00Z`;
test('inspection preserves exact microseconds and rejects precision loss or non-UTC forms',()=>{
 assert.equal(chartInspectionUtc('2026-09-26T10:00:00.1234560Z'),'2026-09-26T10:00:00.123456Z');
 for(const value of ['2026-09-26T10:00:00.1234567Z','2026-09-26T10:00:00+00:00','2026-02-30T10:00:00Z','2026-09-26T10:00'])assert.throws(()=>chartInspectionUtc(value));
});
test('inspection uses the exact half-open window without replacing an out-of-window request',()=>{
 assert.equal(chartInspectionInWindow(from,from,to),true);assert.equal(chartInspectionInWindow(to,from,to),false);
 assert.equal(chartInspectionInWindow('2026-09-26T08:00:00.000000Z',from,to),false);
 assert.equal(chartInspectionInWindow('2026-09-26T12:00:00.000000Z',from,to),true);
 const href=chartInspectionHref(base,to);assert.equal(readChartInspection(href),to);assert.equal(chartInspectionInWindow(readChartInspection(href),from,to),false);
});

test('native zero-offset seven-digit bounds preserve exact half-open comparison while user input remains Z-only',()=>{
 const nativeFrom='2026-09-26T08:00:00.0000010+00:00',nativeTo='2026-09-26T12:00:00.0000010+00:00';
 assert.equal(chartInspectionInWindow(from,nativeFrom,nativeTo),true);
 assert.equal(chartInspectionInWindow(nativeFrom,nativeFrom,nativeTo),true);
 assert.equal(chartInspectionInWindow('2026-09-26T12:00:00.000000Z',nativeFrom,nativeTo),true);
 assert.equal(chartInspectionInWindow(to,nativeFrom,nativeTo),false);
 assert.equal(chartInspectionInWindow(from,nativeFrom.replace('10+','17+'),nativeTo),false);
 assert.throws(()=>chartInspectionUtc(nativeFrom));
 const query=new URLSearchParams({target,range:'custom',from:nativeFrom,to:nativeTo,chartAt:'2026-09-26T10:00:00.123456Z'});
 assert.equal(readChartInspection('#/health?'+query),'2026-09-26T10:00:00.123456Z');
 const aligned=chartInspectionHref('#/health?'+query,'2026-09-26T11:00:00.654321Z');
 assert.equal(new URLSearchParams(aligned.split('?')[1]).get('from'),nativeFrom);
 assert.equal(new URLSearchParams(aligned.split('?')[1]).get('to'),nativeTo);
});
test('shareable alignment and clear preserve window database comparison and independent event identity',()=>{
 const href=chartInspectionHref(base,'2026-09-26T10:00:00.123456Z');
 const original=new URLSearchParams(base.split('?')[1]),next=new URLSearchParams(href.split('?')[1]);
 for(const[key,value]of original)assert.equal(next.get(key),value);
 assert.equal(readChartInspection(href),'2026-09-26T10:00:00.123456Z');
 assert.deepEqual([...new URLSearchParams(chartInspectionHref(href).split('?')[1])],[...original]);assert.equal(readChartInspection(base),undefined);
});
test('malformed ambiguous or contradictory URL context remains invalid rather than choosing a marker',()=>{
 for(const suffix of ['chartAt=bad','chartAt=2026-09-26T10:00:00Z&chartAt=2026-09-26T10:00:00Z','chartAt=2026-09-26T10:00:00Z&range=1h','chartAt=2026-09-26T10:00:00Z&target='+target])assert.equal(readChartInspection(base+'&'+suffix),null);
 for(const context of ['#/health?range=1h','#/overview?range=bogus','#/overview?databaseId=6','#/overview?compare=bad','#/overview?range=custom&from='+from+'&to='+from])assert.equal(readChartInspection(context+'&chartAt=2026-09-26T10:00:00Z'),null);
 assert.equal(readChartInspection('#/activity?chartAt=bad'),undefined);
 assert.equal(readChartInspection('#/overview?range=1h&chartAt=2026-09-26T10:00:00Z'),'2026-09-26T10:00:00.000Z');
});

for (const screen of ['queries','waits']) test(`${screen} alignment preserves independent selections and requires an unambiguous server context`,()=>{
 const parameters=new URLSearchParams({target,range:'custom',from,to,databaseId:'6',compare:'1',queryView:'regressed',queryFingerprint:'a'.repeat(64),planFingerprint:'b'.repeat(64),waitCategory:'3',sessionWaitCapture:'independent-selection'});
 const hash=`#/${screen}?${parameters}`,at='2026-09-26T10:00:00.123456Z';
 const aligned=chartInspectionHref(hash,at);
 assert.equal(readChartInspection(aligned),at);
 assert.deepEqual([...new URLSearchParams(chartInspectionHref(aligned).split('?')[1])],[...parameters]);
 for(const invalid of [`#/${screen}?chartAt=${at}`,aligned+'&target='+target,aligned+'&chartAt='+at,aligned+'&from='+from])assert.equal(readChartInspection(invalid),null);
 assert.equal(readChartInspection(chartInspectionHref(hash,to)),to);
 assert.equal(chartInspectionInWindow(to,from,to),false);
});

test('same-server window navigation retains exact or invalid alignment without carrying it into another context',()=>{
 const at='2026-09-26T10:00:00.123456Z',aligned=chartInspectionHref(base,at);
 const next=`#/queries?target=${target}&range=custom&from=${from}&to=${to}&databaseId=9&queryView=cache`;
 assert.equal(readChartInspection(retainChartInspectionHref(next,aligned)),at);
 const invalid=aligned+'&chartAt=bad';
 assert.deepEqual(new URLSearchParams(retainChartInspectionHref(next,invalid).split('?')[1]).getAll('chartAt'),[at,'bad']);
 for(const changed of [next.replace(target,'22222222-2222-4222-8222-000000000002'),next.replace(from,'2026-09-26T08:00:00.000002Z'),next+'&range=1h',next.replace('queries','activity'),next.replace('target='+target,'target=bad')])assert.equal(retainChartInspectionHref(changed,aligned),changed);
 const native=next.replace(from,'2026-09-26T08:00:00.0000010%2B00%3A00').replace(to,'2026-09-26T12:00:00.0000010%2B00%3A00');
 assert.equal(readChartInspection(retainChartInspectionHref(native,aligned)),at);
 const existing=chartInspectionHref(next,'2026-09-26T11:00:00.654321Z');
 assert.equal(retainChartInspectionHref(existing,aligned),existing);
});
