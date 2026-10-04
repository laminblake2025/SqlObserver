import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { ApiRequestError } from "../../apiClient.ts";
import { getFileReceiptHistory, getFileSnapshotHistory } from "./databaseFileWindowHistoryApi.ts";
import { fileHistoryContextIdentity,fileSnapshotIdentity,validFileSnapshotSelection,type FileHistoryBinding,type FileSnapshotSelection,type FileReceiptPage,type FileSnapshotPage } from "./databaseFileWindowHistoryModel.ts";
type Source="list"|"snapshot";
export interface FileReadState<T> { readonly pages:readonly T[];readonly index:number;readonly loading:boolean;readonly error?:string;readonly errorStatus?:number;readonly limit?:number;
  readonly attempt?:{readonly cursor?:string;readonly replace:boolean;readonly index:number;readonly limit:number}; }
const emptyRead=<T,>():FileReadState<T>=>({pages:[],index:0,loading:false});
interface State { readonly list:FileReadState<FileReceiptPage>;readonly snapshot:FileReadState<FileSnapshotPage> }
const empty=():State=>({list:emptyRead(),snapshot:emptyRead()});
export function useDatabaseFileWindowHistory(binding:FileHistoryBinding|undefined,selected?:FileSnapshotSelection|null,manualRefresh=0,accessContext="") {
  const context=JSON.stringify([fileHistoryContextIdentity(binding),accessContext]),selection=fileSnapshotIdentity(selected),authority=JSON.stringify([binding?.targetId.toLowerCase(),binding?.targetRevision,accessContext]);
  const currentContext=useRef(context),currentSelection=useRef(selection),currentIntent=useRef(selected);currentContext.current=context;currentIntent.current=selected;
  const previousContext=useRef(context),previousSelection=useRef(selection),mounted=useRef(false),epoch=useRef(0),snapshotEpoch=useRef(0),owners=useRef(new Map<Source,AbortController>()),denial=useRef<string|undefined>(undefined);
  const [state,setState]=useState(empty),received=useRef(state),[denied,setDenied]=useState(false);
  const effectiveSelection=selected===null?null:selected??(previousContext.current===context?state.list.pages[0]?.items[0]:undefined);
  const effectiveIdentity=fileSnapshotIdentity(effectiveSelection);
  currentSelection.current=effectiveIdentity;
  const preferredFileLimit=useRef(100);
  const publish=(change:(old:State)=>State)=>{const next=change(received.current);received.current=next;setState(next);};
  const stop=(source?:Source)=>{if(source){owners.current.get(source)?.abort();owners.current.delete(source);}else{epoch.current++;snapshotEpoch.current++;for(const c of owners.current.values())c.abort();owners.current.clear();}};
  useLayoutEffect(()=>{mounted.current=true;return()=>{mounted.current=false;stop();};},[]);
  useLayoutEffect(()=>{if(previousContext.current===context)return;previousContext.current=context;previousSelection.current=effectiveIdentity;stop();publish(empty);if(binding&&denial.current!==undefined&&denial.current!==authority){denial.current=undefined;setDenied(false);}},[context]);
  useLayoutEffect(()=>{if(previousSelection.current===effectiveIdentity)return;previousSelection.current=effectiveIdentity;snapshotEpoch.current++;stop("snapshot");publish(old=>({...old,snapshot:emptyRead()}));},[effectiveIdentity]);
  const active=(source:Source,c:AbortController,key:string,version:number,detailVersion:number,identity:string)=>mounted.current&&denial.current===undefined&&!c.signal.aborted&&owners.current.get(source)===c&&currentContext.current===key&&epoch.current===version&&(source==="list"||snapshotEpoch.current===detailVersion&&currentSelection.current===identity);
  function refuse(failure:unknown){if(!(failure instanceof ApiRequestError)||![401,403].includes(failure.status))return false;denial.current=authority;stop();publish(empty);setDenied(true);return true;}
  async function read(source:Source,cursor?:string,replace=false,index=0,requestedLimit?:number){
    const requestedSelection=selected===null?null:selected??received.current.list.pages[0]?.items[0];
    if(!binding||!mounted.current||denial.current!==undefined||owners.current.has(source)||source==="snapshot"&&!validFileSnapshotSelection(requestedSelection))return;
    const old=received.current[source],prior=replace?[]:old.pages;
    const limit=source==="list"?50:requestedLimit??(replace?preferredFileLimit.current:old.limit??100);
    const maximumPages=source==="list"?20:10000/limit;if(prior.length>=maximumPages)return;
    const controller=new AbortController(),key=context,version=epoch.current,detailVersion=snapshotEpoch.current,identity=fileSnapshotIdentity(requestedSelection);owners.current.set(source,controller);
    publish(old=>({...old,[source]:{...old[source],loading:true,error:undefined,errorStatus:undefined,attempt:{cursor,replace,index,limit}}}));
    try{
      if(source==="list"){
        const page=await getFileReceiptHistory(binding,controller.signal,prior as readonly FileReceiptPage[],cursor);if(!active(source,controller,key,version,detailVersion,identity))return;
        const pages=[...(prior as readonly FileReceiptPage[]),page];if(pages.length===20&&page.nextCursor!==null||pages.reduce((sum,p)=>sum+p.items.length,0)>1000)throw new Error("Received file receipts reached the local browsing bound. Narrow the shared window; retained receipts remain evidence.");
        const priorDefault=received.current.list.pages[0]?.items[0],nextDefault=pages[0]?.items[0];
        if(currentIntent.current===undefined&&fileSnapshotIdentity(priorDefault)!==fileSnapshotIdentity(nextDefault)){snapshotEpoch.current++;stop("snapshot");publish(old=>({...old,snapshot:emptyRead()}));}
        publish(old=>({...old,list:{pages,index,loading:false}}));
      }else{
        const page=await getFileSnapshotHistory(binding,requestedSelection!,controller.signal,prior as readonly FileSnapshotPage[],cursor,limit);if(!active(source,controller,key,version,detailVersion,identity))return;
        const pages=[...(prior as readonly FileSnapshotPage[]),page];if(pages.length===maximumPages&&page.nextCursor!==null||pages.reduce((sum,p)=>sum+p.items.length,0)>10000)throw new Error("Received file facts reached their local read bound. Narrow the shared window; no rows have been substituted.");
        preferredFileLimit.current=limit;publish(old=>({...old,snapshot:{pages,index,loading:false,limit}}));
      }
    }catch(failure){if(!active(source,controller,key,version,detailVersion,identity)||refuse(failure))return;publish(old=>({...old,[source]:{...old[source],loading:false,errorStatus:failure instanceof ApiRequestError?failure.status:undefined,error:failure instanceof Error?failure.message:"Database file evidence is unavailable. Retry this read."}}));}
    finally{if(owners.current.get(source)===controller)owners.current.delete(source);}
  }
  useEffect(()=>{if(denial.current===undefined)void read("list",undefined,true);},[context]);
  useEffect(()=>{if(denial.current===undefined)void read("snapshot",undefined,true);},[context,effectiveIdentity]);
  const previousRefresh=useRef(manualRefresh);
  useEffect(()=>{if(previousRefresh.current===manualRefresh)return;previousRefresh.current=manualRefresh;if(denial.current===undefined){void read("list",undefined,true);void read("snapshot",undefined,true);}},[manualRefresh]);
  function navigate(source:Source,index:number){if(denial.current!==undefined||owners.current.has(source)||index<0)return;const old=received.current[source];if(old.pages[index])publish(state=>({...state,[source]:{...state[source],index}}));else if(index===old.pages.length&&old.pages.at(-1)?.nextCursor)void read(source,old.pages.at(-1)!.nextCursor!,false,index);}
  function cancel(source:Source){if(!owners.current.has(source))return;stop(source);publish(old=>({...old,[source]:{...old[source],loading:false,error:"Waiting cancelled. Received evidence remains; retry keeps the exact receipt and continuation."}}));}
  function retry(source:Source){const attempt=received.current[source].attempt;if(attempt)void read(source,attempt.cursor,attempt.replace,attempt.index,attempt.limit);}
  function retryAccess(){if(!binding||!mounted.current)return;denial.current=undefined;setDenied(false);void read("list",undefined,true);if(selected)void read("snapshot",undefined,true);}
  const visible=previousContext.current===context,visibleSelection=previousSelection.current===effectiveIdentity;
  return{contextKey:context,selectionIdentity:effectiveIdentity,list:visible?state.list:emptyRead<FileReceiptPage>(),snapshot:visible&&visibleSelection?state.snapshot:emptyRead<FileSnapshotPage>(),selected:effectiveSelection,denied,navigate,cancel,retry,refresh:(source:Source)=>void read(source,undefined,true),smallerFilePages:()=>void read("snapshot",undefined,true,0,50),retryAccess};
}
export type DatabaseFileWindowHistoryRead=ReturnType<typeof useDatabaseFileWindowHistory>;
