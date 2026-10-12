import assert from 'node:assert/strict';
import test from 'node:test';
import { getDeadlocks } from '../src/features/deadlocks/deadlockApi.ts';

test('deadlock pagination keeps the selected UTC range and native cursor', async () => {
  const original = globalThis.fetch;
  const target = '11111111-1111-4111-8111-111111111111';
  const window = {fromUtc:'2026-09-20T01:00:00.000Z',toUtc:'2026-09-21T01:00:00.000Z'};
  const urls = [];
  const cursor = Buffer.from(JSON.stringify([target, '2026-09-20T12:00:00Z', '33333333-3333-4333-8333-333333333333', window.toUtc, window.fromUtc, window.toUtc]), 'utf8').toString('base64');
  try {
    globalThis.fetch = async url => {
      urls.push(new URL(String(url),'https://lab.invalid'));
      return new Response(JSON.stringify({targetId:target,repositoryTimeUtc:window.toUtc,items:[],nextCursor:null}),{headers:{'content-type':'application/json'}});
    };
    await getDeadlocks(target,new AbortController().signal,undefined,window);
    await getDeadlocks(target,new AbortController().signal,cursor,window);
    for (const url of urls) {
      assert.equal(url.searchParams.get('fromUtc'),window.fromUtc);
      assert.equal(url.searchParams.get('toUtc'),window.toUtc);
      assert.equal(url.searchParams.get('limit'),'25');
    }
    assert.equal(urls[0].searchParams.has('cursor'),false);
    assert.equal(urls[1].searchParams.get('cursor'),cursor);
  } finally {globalThis.fetch = original;}
});
