// Synthetic responses preserve the native list envelope and cursor contract.
import { deadlockRetainedPage, deadlockRetainedDetail } from '../tools/deadlock-retained-fixture.mjs';
export { target, otherTarget, fromUtc, toUtc, routeHash } from './deadlockFixture.mjs';
export const origin = 'http://127.0.0.1:4328';
export const nativePage = (url, targetId) => deadlockRetainedPage(url, targetId);
export const nativeDetail = summary => deadlockRetainedDetail(summary);
export async function respond(route, value, status = 200) {
  await route.fulfill({ status, contentType: 'application/json', headers: { 'cache-control': 'no-store' }, body: JSON.stringify(value) });
}
export function hold(status = 200, transform = value => value) {
  let announce, release, finish;
  const started = new Promise(resolve => { announce = resolve; });
  const gate = new Promise(resolve => { release = resolve; });
  const completed = new Promise(resolve => { finish = resolve; });
  return { started, async finish() { release(); await completed; }, async handle(route, value) {
    announce(route.request()); await gate;
    try { await respond(route, status === 200 ? transform(value) : { detail: 'SECRET_PROVIDER_FAILURE' }, status); }
    catch (error) { if (!route.request().failure()?.errorText.includes('ERR_ABORTED')) throw error; }
    finally { finish(); }
    return true;
  } };
}
