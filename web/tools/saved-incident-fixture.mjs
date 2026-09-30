// Loopback-only synthetic UI exercise. This is not an authorization/storage test.
import { randomUUID } from "node:crypto";
const saved=new Map(); const observations=[];
const send=(response,value,status=200)=>{response.writeHead(status,{"content-type":"application/json","cache-control":"no-store"});response.end(JSON.stringify(value));};
const cookie=(request,key)=>request.headers.cookie?.split(";").map(value=>value.trim()).find(value=>value.startsWith(`${key}=`))?.slice(key.length+1);

export async function handleSavedIncidentFixture(url,response,request,targets) {
  if (url.pathname==="/__incident_fixture/observations") { send(response,observations); return true; }
  if (url.pathname==="/__incident_fixture") {
    const role=url.searchParams.get("role")??"viewer",mode=url.searchParams.get("mode")??"normal";
    if (!["viewer","operator"].includes(role)||!["normal","uncertain","empty","truncated"].includes(mode)) {send(response,{},400);return true;}
    response.writeHead(302,{"set-cookie":[`incidentRole=${role}; SameSite=Strict; Path=/`,`incidentMode=${mode}; SameSite=Strict; Path=/`],location:"/"});response.end();return true;
  }
  if (url.pathname==="/api/v1/me") {
    const targetId=url.searchParams.get("targetId");const roles=cookie(request,"incidentRole")==="operator"?["Viewer","Operator"]:["Viewer"];
    send(response,{active:true,targetId,grantedRoles:roles,allTargetRoles:roles,targetRoles:targetId?roles:[]});return true;
  }
  const route=/^\/api\/v1\/observation-targets\/([^/]+)\/incidents\/saved(?:\/([^/]+))?\/?$/.exec(url.pathname);
  if (!route) return false;
  const targetId=route[1],threadId=route[2];
  if (!targets.some(target=>target.instanceId===targetId)) {send(response,{},404);return true;}
  if (request.method==="GET"&&threadId) {
    const record=[...saved.values()].find(value=>value.result.targetId===targetId&&value.result.threadId===threadId);
    send(response,record?.result??{},record?200:404);return true;
  }
  if (request.method!=="POST"||threadId) {send(response,{},405);return true;}
  if (cookie(request,"incidentRole")!=="operator") {send(response,{},403);return true;}
  let body="";
  for await(const chunk of request) {body+=chunk.toString("utf8");if(Buffer.byteLength(body)>16384){send(response,{},413);return true;}}
  const command=JSON.parse(body); observations.push({targetId,body});
  const existing=saved.get(command.operationId);
  if (existing) {send(response,existing.result,existing.body===body&&existing.result.targetId===targetId?200:409);return true;}
  const mode=cookie(request,"incidentMode");
  const result={targetId,targetRevision:command.expectedTargetRevision,threadId:randomUUID(),operationId:command.operationId,
    fromUtc:command.fromUtc,toUtc:command.toUtc,savedAtUtc:new Date().toISOString(),title:command.title,notes:command.notes,
    evidenceTruncated:mode==="truncated",evidence:mode==="empty"?[]:[{
      packetId:randomUUID(),occurredAtUtc:command.fromUtc,evidenceKind:"synthetic_blocking",
      sourceRunId:null,sourceSha256:"a".repeat(64),identitySha256:"b".repeat(64),sourceCutoffSha256:"c".repeat(64),
      sourceCutoffUtc:null,visibilityState:"partial",confidence:null,evidence:{description:"SYNTHETIC queued lock observation",blockedSessions:3},
    }]};
  saved.set(command.operationId,{body,result});
  send(response,mode==="uncertain"?{}:result,mode==="uncertain"?503:200);return true;
}
