import assert from 'node:assert/strict';
import test from 'node:test';
import { planCacheIntervalTrend, formatCacheIntervalRate } from '../src/features/queries/planCacheIntervalTrendModel.ts';
import { prepareObservationChart, observationSegments } from '../src/components/observationChartModel.ts';
const fromUtc='2026-09-28T00:00:00Z',toUtc='2026-09-29T00:00:00Z';
const base={fromUtc,toUtc,items:[]};
const row=(start,end,changes={})=>({cacheEntryFingerprint:'a'.repeat(64),sourceEpoch:'b'.repeat(64),transition:'comparable',
  intervalFromUtc:start,intervalToUtc:end,observedAtUtc:end,cpuMilliseconds:'0.00100000000000000000',durationMilliseconds:'0.002',
  executions:'3',logicalReads:'6',writes:'9',rows:'12',captureComplete:true,...changes});
const values=trend=>trend.series.flatMap(series=>series.items.filter(item=>item.value!==null).map(item=>item.value));

test('rates use full microsecond interval lengths rather than capture cadence or rounded milliseconds',()=>{
  const first=row('2026-09-28T12:00:00.000001Z','2026-09-28T12:00:00.000101Z');
  const second=row(first.intervalToUtc,'2026-09-28T12:00:00.000301Z');
  const trend=planCacheIntervalTrend({...base,items:[second,first]},'cpu');
  assert.deepEqual(values(trend),[10,10,5,5]);
  assert.equal(trend.plottedIntervals,2);
  for(const [metric,expected] of [['duration',20],['executions',30000],['logical_reads',60000],['writes',90000],['rows',120000]])
    assert.deepEqual(values(planCacheIntervalTrend({...base,items:[first]},metric)),[expected,expected]);
});
test('touching intervals remain separate paths without a vertical interpolation or a missing-state zero',()=>{
  const first=row('2026-09-28T12:00:00Z','2026-09-28T12:00:01Z',{executions:'3'});
  const second=row(first.intervalToUtc,'2026-09-28T12:00:02Z',{executions:'5'});
  const reset={...first,transition:'reset',executions:null,intervalFromUtc:null,intervalToUtc:null};
  const boundary={...first,transition:'window_boundary',executions:null,intervalFromUtc:'2026-09-27T23:59:59Z'};
  const trend=planCacheIntervalTrend({...base,items:[second,reset,boundary,first]},'executions');
  const chart=prepareObservationChart(trend.series,fromUtc,toUtc);
  assert.deepEqual(observationSegments(chart.series[0].items).map(segment=>segment.map(point=>point.value)),[[3,3],[5,5]]);
  assert.equal(trend.unavailableComparisons,2); assert.equal(trend.plottedIntervals,2);
});
test('cache entries and server epochs stay separate instead of adding their rates together',()=>{
  const sample=row('2026-09-28T12:00:00Z','2026-09-28T12:00:01Z');
  const trend=planCacheIntervalTrend({...base,items:[sample,{...sample,sourceEpoch:'c'.repeat(64)},
    {...sample,cacheEntryFingerprint:'d'.repeat(64)}]},'executions');
  assert.equal(trend.series.length,3); assert.equal(new Set(trend.series.map(series=>series.id)).size,3);
  assert.deepEqual(values(trend),[3,3,3,3,3,3]);
});
test('tiny nonzero rates stay nonzero while unrepresentable rates remain in exact evidence',()=>{
  const sample=row('2026-09-28T00:00:00Z','2026-10-04T23:59:59Z');
  const trend=planCacheIntervalTrend({...base,toUtc:'2026-10-05T00:00:00Z',items:[sample]},'cpu');
  assert.deepEqual(values(trend),[1.65344e-9,1.65344e-9]); assert.equal(formatCacheIntervalRate(values(trend)[0]),'1.65344e-9');
  const huge=row('2026-09-28T12:00:00Z','2026-09-28T12:00:01Z',{executions:'9007199254740993'});
  const original=structuredClone(huge);
  const missing=planCacheIntervalTrend({...base,items:[huge]},'executions');
  assert.equal(missing.precisionOmissions,1); assert.deepEqual(missing.series,[]); assert.deepEqual(huge,original);
  const divided=planCacheIntervalTrend({...base,items:[{...huge,intervalToUtc:'2026-09-28T12:00:10Z',observedAtUtc:'2026-09-28T12:00:10Z'}]},'executions');
  assert.deepEqual(values(divided),[9.0072e14,9.0072e14]);
  assert.deepEqual(values(planCacheIntervalTrend({...base,items:[{...huge,executions:'0'}]},'executions')),[0,0]);
});
test('source boundaries, invalid exact counters and timestamp precision cannot create a rate',()=>{
  const sample=row('2026-09-28T12:00:00Z','2026-09-28T12:00:01Z');
  for(const changes of [{intervalFromUtc:sample.intervalToUtc},{intervalFromUtc:'2026-09-27T23:59:59Z'},
    {intervalFromUtc:'2026-09-28T12:00:00.0000001Z'},{cpuMilliseconds:'0.0001'},
    {cpuMilliseconds:'0.000000000000000000000001'},{intervalToUtc:'2026-09-28T12:00:00.500Z'}]){
    const trend=planCacheIntervalTrend({...base,items:[{...sample,...changes}]},'cpu');
    assert.equal(trend.invalidIntervals,1); assert.equal(trend.plottedIntervals,0);
  }
});
test('plot bounds are explicit and do not hide retained table evidence',()=>{
  const sample=row('2026-09-28T12:00:00Z','2026-09-28T12:00:01Z');
  const items=Array.from({length:301},(_,index)=>({...sample,cacheEntryFingerprint:String(index%11).padStart(64,'0')}));
  const trend=planCacheIntervalTrend({...base,items},'cpu');
  assert.equal(trend.hiddenSamples,1); assert.equal(trend.hiddenEntries,1); assert.equal(trend.series.length,10);
  assert.equal(items.length,301); assert.ok(trend.series.every(series=>series.items.length<=900));
  assert.throws(()=>planCacheIntervalTrend(base,'unknown'));
});
