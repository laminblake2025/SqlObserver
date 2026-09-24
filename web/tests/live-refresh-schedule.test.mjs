import assert from 'node:assert/strict';
import test from 'node:test';
import { refreshCadence, startWorkspaceClock, wakeLiveTick } from '../src/liveRefreshSchedule.ts';

test('one workspace clock drives 10, 30, and 60 second refresh tokens', () => {
  assert.deepEqual(refreshCadence(0, 2), { slow: 0, blocking: 0 });
  assert.deepEqual(refreshCadence(0, 3), { slow: 0, blocking: 1 });
  assert.deepEqual(refreshCadence(0, 6), { slow: 1, blocking: 2 });
  assert.deepEqual(refreshCadence(1, 6), { slow: 2, blocking: 3 });
  assert.equal(wakeLiveTick(1), 6);
  assert.equal(wakeLiveTick(6), 12);
});

test('workspace clock skips hidden ticks, wakes on visibility, and disposes one timer', () => {
  let interval;
  let visible;
  let hidden = false;
  let ticks = 0;
  let wakes = 0;
  const cancelled = [];
  const host = {
    setInterval(callback, milliseconds) { assert.equal(milliseconds, 10_000); interval = callback; return 7; },
    clearInterval(timer) { cancelled.push(timer); },
    addVisibilityListener(callback) { visible = callback; },
    removeVisibilityListener(callback) { assert.equal(callback, visible); visible = undefined; },
    isHidden() { return hidden; },
  };
  const stop = startWorkspaceClock(host, () => ticks++, () => wakes++);
  interval();
  assert.equal(ticks, 1);
  hidden = true;
  interval();
  visible();
  assert.equal(ticks, 1);
  assert.equal(wakes, 0);
  hidden = false;
  visible();
  assert.equal(wakes, 1);
  stop();
  assert.deepEqual(cancelled, [7]);
  assert.equal(visible, undefined);
});
