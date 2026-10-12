export * from '../tools/server-lifecycle-history-fixture.mjs';
import {serverLifecycleHistoryBackend,serverLifecycleFixtureBinding} from '../tools/server-lifecycle-history-fixture.mjs';
export async function installServerLifecycleHistoryFixture(page,{origin='http://127.0.0.1:4328',backend=serverLifecycleHistoryBackend(),handler}={}) {
  const session={backend,requests:[],reads:[],errors:[],handler};
  await page.route('**/api/v1/observation-targets/*/lifecycle/history**',async route=>{
    const request=route.request(),url=new URL(request.url());
    if(url.origin!==origin||request.method()!=='GET'){session.errors.push('Unexpected '+request.method()+' '+url.href);return route.abort();}
    try {
      const read=serverLifecycleFixtureBinding(url);session.requests.push(request);session.reads.push(read);
      if(session.handler&&await session.handler(route,read,session))return;
      await route.fulfill({status:200,contentType:'application/json',headers:{'cache-control':'private, no-store'},body:JSON.stringify(read.selected?backend.detail(read.binding,read.selected):backend.read(read.binding,read.cursor))});
    }catch(error){await route.fulfill({status:error.status??503,contentType:'application/json',headers:{'cache-control':'private, no-store'},body:'{"detail":"PRIVATE_PROVIDER_ERROR"}'});}
  });
  return session;
}
