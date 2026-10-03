export * from '../tools/sql-volume-history-fixture.mjs';
import {volumeHistoryBackend,historyBinding} from '../tools/sql-volume-history-fixture.mjs';
export async function installSqlVolumeHistoryFixture(page,{origin='http://127.0.0.1:4328',backend=volumeHistoryBackend(),handler}={}) {
 const session={backend,requests:[],reads:[],errors:[],handler};
 await page.route('**/api/v1/observation-targets/*/resources/volumes/**',async route=>{
  const request=route.request(),url=new URL(request.url());if(url.origin!==origin||request.method()!=='GET'||!/^\/api\/v1\/observation-targets\/[^/]+\/resources\/volumes\/(?:history|[a-f0-9]{64}\/history)$/u.test(url.pathname)){session.errors.push('Unexpected '+request.method()+' '+url.href);return route.abort();}
  try{const binding=historyBinding(url),cursor=url.searchParams.get('cursor')??undefined,item={binding,cursor,source:binding.selected?'selected':'catalog'};
   session.requests.push(request);session.reads.push(item);if(session.handler&&await session.handler(route,item,session))return;
   await route.fulfill({status:200,contentType:'application/json',headers:{'cache-control':'private, no-store'},body:JSON.stringify(backend.read(binding,cursor))});
  }catch(error){await route.fulfill({status:error.status??503,contentType:'application/json',body:JSON.stringify({detail:'PRIVATE_PROVIDER_ERROR'})});}
 });return session;
}
