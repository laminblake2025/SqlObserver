// Synthetic, loopback-only visual QA. Never imported by production code.
import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
import {resolve,extname,sep} from 'node:path';
const root=resolve(import.meta.dirname,'../dist');
const targets=Array.from({length:10},(_,i)=>({instanceId:`11111111-1111-4111-8111-${String(i+1).padStart(12,'0')}`,displayName:`SYNTHETIC SQL ${String(i+1).padStart(2,'0')}`,host:`sql${i+1}.invalid`,lifecycle:'active',capabilityStatus:'supported',capabilityReasons:[],configurationRevision:1,tcpPort:1433}));
const send=(res,value,status=200)=>{res.writeHead(status,{'content-type':'application/json'});res.end(JSON.stringify(value));};
createServer(async(req,res)=>{try{
 const url=new URL(req.url,'http://localhost');
 if(url.pathname==='/__fixture') {const mode=url.searchParams.get('mode');if(!['empty','partial','error','loading','ready'].includes(mode))return send(res,{},400);res.writeHead(302,{'set-cookie':`overviewMode=${mode}; SameSite=Strict; Path=/`,location:'/'});res.end();return;}
 if(url.pathname==='/api/v1/observation-targets')return send(res,{items:targets,nextCursor:null});
 if(/^\/api\/v1\/observation-targets\/[^/]+\/health\/files$/.test(url.pathname)){
  const instanceId=url.pathname.split('/')[4];
  if(!targets.some(t=>t.instanceId===instanceId))return send(res,{},404);
  const cursor=url.searchParams.get('cursor');
  if(cursor!==null&&cursor!=='synthetic-file-page-2')return send(res,{},400);
  return send(res,{instanceId,repositoryTimeUtc:new Date().toISOString(),collector:{state:'current'},nextCursor:cursor===null?'synthetic-file-page-2':null,items:cursor===null?[
   {databaseId:5,fileId:1,logicalName:'Orders_data',sizeBytes:'8589934592',readCount:'120000',writeCount:'80000',readStallMilliseconds:'480000',writeStallMilliseconds:'560000',observedAtUtc:new Date().toISOString()},
   {databaseId:5,fileId:2,logicalName:'Orders_log',sizeBytes:'2147483648',readCount:'4000',writeCount:'35000',readStallMilliseconds:'8000',writeStallMilliseconds:'175000',observedAtUtc:new Date().toISOString()},
  ]:[
   {databaseId:7,fileId:1,logicalName:'Warehouse_data',sizeBytes:'17179869184',readCount:'90000',writeCount:'24000',readStallMilliseconds:'450000',writeStallMilliseconds:'96000',observedAtUtc:new Date().toISOString()},
  ]});
 }
 if(url.pathname==='/api/v1/overview'){
  const mode=req.headers.cookie?.match(/overviewMode=(\w+)/)?.[1];
  if(mode==='error')return send(res,{},503);
  if(mode==='loading')await new Promise(r=>setTimeout(r,2500));
  const targetId=url.searchParams.get('targetId');const fromUtc=url.searchParams.get('fromUtc'),toUtc=url.searchParams.get('toUtc');
  const from=Date.parse(fromUtc),to=Date.parse(toUtc),at=new Date(to-30000).toISOString();
  const selected=mode==='empty'?[]:targets.filter(t=>!targetId||t.instanceId===targetId);
  const evidence=selected.map((t,index)=>{
   const i=targets.indexOf(t),gap=mode==='partial'&&index===0;
   const value=n=>({value:gap?null:n,state:gap?'unavailable':'current',observedAtUtc:gap?null:at});
   const issues=i===1?[{targetId:t.instanceId,server:t.displayName,title:'Sessions blocked',detail:'12 distinct sessions waiting on SQL 02',destination:'activity',priority:1,observedAtUtc:at}]:i===4?[{targetId:t.instanceId,server:t.displayName,title:'SQL Agent failures',detail:'2 jobs affected in selected window',destination:'operations',priority:2,observedAtUtc:at}]:[];
   const metric=(key,unit,base,dimension=null)=>({targetId:t.instanceId,label:t.displayName,metric:key,unit,state:gap?'partial':'observed',dimension,points:gap?[]:Array.from({length:48},(_,j)=>({timeUtc:new Date(from+(to-from)*j/48).toISOString(),value:Math.max(0,base*(1+.25*Math.sin(j/5+i))+(i===1&&j>27&&j<34?base*.7:0)),samples:10}))});
   const resource=(label,n,unit)=>({targetId:t.instanceId,server:t.displayName,label,value:gap?null:n,unit,state:gap?'unavailable':'observed',observedAtUtc:gap?null:at});
   const databaseSeries=targetId?[metric('activity.user_sessions','sessions',14+i*4,'Orders'),metric('activity.user_sessions','sessions',8+i*3,'Reporting')]:[];
   return {targetId:t.instanceId,displayName:t.displayName,collectionState:gap?'stale':'current',lastObservedUtc:at,activeAlerts:value(i===1?9:i===4?2:0),blockedSessions:value(i===1?12:0),deadlocks:{...value(i===1?3:0),state:gap?'unavailable':'observed'},issues,resources:[resource('Wait · LCK_M_X',i===1?18:2,'seconds since prior sample'),resource('host.cpu.percent',i===1?89:15+i*5,'%'),resource('SQL physical memory',8+i*2,'GiB'),resource('TempDB used',13+i*4,'%'),resource('Backup evidence',8,'records'),resource('SQL Agent failures',i===4?2:0,'observed events')],series:[metric('engine.batch_requests_per_second','batches/sec',100+i*60),metric('engine.user_connections','connections',30+i*12),...databaseSeries,metric('engine.sql_scheduler_cpu_percent','%',25+i*4),metric('engine.scheduler_runnable_tasks','tasks',2+i),metric('engine.memory_grants_pending','grants',1+i),metric('engine.process_physical_memory_bytes','GiB',8+i*2),metric('engine.os_available_memory_bytes','GiB',12+i),metric('host.cpu.percent','%',10+i*5),metric('host.memory.available_bytes','GiB',12+i),metric('blocking.sessions','blocked sessions',i===1?7:1),metric('deadlocks','events',i===1?2:0)],gaps:gap?['Synthetic collection timeout']:[]};
  });
  return send(res,{refreshedAtUtc:new Date().toISOString(),fromUtc,toUtc,targetId,targets:(mode==='empty'?[]:targets).map(t=>({targetId:t.instanceId,displayName:t.displayName,lifecycle:t.lifecycle})),excludedTargets:0,evidence});
 }
 if(url.pathname.startsWith('/api/'))return send(res,{},404);
 const path=resolve(root,url.pathname==='/'?'index.html':url.pathname.slice(1));if(!path.startsWith(root+sep))return send(res,{},404);
 const body=await readFile(path);res.writeHead(200,{'content-type':{'.html':'text/html','.js':'text/javascript','.css':'text/css'}[extname(path)]??'application/octet-stream'});res.end(body);
}catch{send(res,{},500);}}).listen(4185,'127.0.0.1',()=>console.log('Synthetic Overview QA at http://127.0.0.1:4185'));
