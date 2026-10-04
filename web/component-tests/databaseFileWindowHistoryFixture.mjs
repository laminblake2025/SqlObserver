import {databaseFileWindowHistoryBackend,fileHistoryAlpha,fileHistoryBeta} from '../tools/database-file-window-history-fixture.mjs';
export async function fileHistoryJson(route,value,status=200){await route.fulfill({status,contentType:'application/json',headers:{'cache-control':'private, no-store'},body:JSON.stringify(value)});}
export function fileHistoryHeld(){let start,release,finish;const started=new Promise(r=>start=r),gate=new Promise(r=>release=r),done=new Promise(r=>finish=r);return{started,async finish(){release();await done;},async handle(route,value,status=200){start(route.request());await gate;try{await fileHistoryJson(route,value,status);}catch(error){if(!route.request().failure()?.errorText.includes('ERR_ABORTED'))throw error;}finally{finish();}return true;}};}
export async function installDatabaseFileWindowHistoryFixture(page,{origin='http://127.0.0.1:4328',backend=databaseFileWindowHistoryBackend()}={}){
 const session={backend,reads:[],errors:[],handler:undefined};
 await page.route('**/*',async route=>{const request=route.request(),url=new URL(request.url());const reject=()=>{session.errors.push(`${request.method()} ${url.href}`);return route.abort();};if(url.origin!==origin)return reject();if(!url.pathname.startsWith('/api/'))return route.continue();
  const match=/^\/api\/v1\/observation-targets\/([^/]+)\/resources\/files\/history(?:\/([^/]+))?$/.exec(url.pathname);
  if(!match||request.method()!=='GET'||![fileHistoryAlpha,fileHistoryBeta].includes(match[1])||[...url.searchParams.keys()].some(key=>!['targetRevision','sampleTargetRevision','fromUtc','toUtc','limit','cursor'].includes(key))||!(match[2]?['100','50']:['50']).includes(url.searchParams.get('limit')))return reject();
  const binding={targetId:match[1],targetRevision:Number(url.searchParams.get('targetRevision')),fromUtc:url.searchParams.get('fromUtc'),toUtc:url.searchParams.get('toUtc')},selection=match[2]?{runId:match[2],sampleTargetRevision:Number(url.searchParams.get('sampleTargetRevision'))}:undefined,cursor=url.searchParams.get('cursor')??undefined;
  const read={binding,selection,cursor,limit:Number(url.searchParams.get('limit')),url:url.href};session.reads.push(read);
  if(session.handler&&await session.handler(route,read))return;
  try{return fileHistoryJson(route,selection?backend.snapshot(binding,selection,cursor,read.limit):backend.read(binding,cursor,read.limit));}catch(error){return fileHistoryJson(route,{code:'fixture_context_changed'},error.status??400);}
 });page.on('pageerror',error=>session.errors.push(error.message));return session;
}
