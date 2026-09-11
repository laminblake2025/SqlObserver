import test from 'node:test';
import assert from 'node:assert/strict';
import { startLiveRefresh } from '../src/features/activity/liveRefresh.ts';
const settle = () => new Promise(resolve => setImmediate(resolve));
test('live refresh has one request in flight and cancellation discards scheduling', async () => {
  const controller = new AbortController(); let resolve; let calls = 0; const pending = [];
  startLiveRefresh(() => { calls++; return new Promise(r => { resolve = r; }); }, () => assert.fail(), controller.signal, true,
    (action, delay) => { pending.push({action,delay}); return pending.length; }, () => {});
  assert.equal(calls,1); assert.equal(pending.length,0);
  await settle(); assert.equal(calls,1); resolve(); await settle(); assert.equal(pending[0].delay,10000);
  pending.shift().action(); assert.equal(calls,2);
  controller.abort(); resolve(); await settle(); assert.equal(pending.length,0);
});
test('error backoff is bounded, success resets cadence, history never repeats', async () => {
  const controller = new AbortController(); const pending = []; let errors=0; let failing=true;
  startLiveRefresh(async () => { if(failing) throw Error('unavailable'); }, () => errors++, controller.signal,true,
    (action,delay) => { pending.push({action,delay}); return pending.length; }, () => {});
  await settle(); assert.equal(pending[0].delay,20000);
  pending.shift().action(); await settle(); assert.equal(pending[0].delay,40000);
  pending.shift().action(); await settle(); assert.equal(pending[0].delay,60000); assert.equal(errors,3);
  failing=false; pending.shift().action(); await settle(); assert.equal(pending[0].delay,10000); controller.abort();
  let historyCalls=0; startLiveRefresh(async () => {historyCalls++;},()=>assert.fail(),new AbortController().signal,false,()=>assert.fail(),()=>{});
  await settle(); assert.equal(historyCalls,1);
});
