import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { ApiRequestError } from "../../apiClient.ts";
import { getAlertLifecycleHistory, getAlertLifecycleEvent } from "./alertLifecycleHistoryApi.ts";
import { validLifecycleSelection, lifecycleBindingIdentity, lifecycleSelectionIdentity, type AlertLifecycleBinding, type AlertLifecycleSelection, type AlertLifecyclePage, type AlertLifecycleDetail } from "./alertLifecycleHistoryModel.ts";
interface ListState { readonly pages: readonly AlertLifecyclePage[]; readonly index: number; readonly loading: boolean; readonly error?: string;
  readonly attempt?: { readonly cursor?: string; readonly replace: boolean; readonly index: number } }
interface DetailState { readonly receipt?: AlertLifecycleDetail; readonly loading: boolean; readonly error?: string; readonly attempt?: boolean }
interface State { readonly list: ListState; readonly detail: DetailState }
const empty = (): State => ({list:{pages:[],index:0,loading:false},detail:{loading:false}});
type Source = "list" | "detail";
export function useAlertLifecycleHistory(binding: AlertLifecycleBinding | undefined, selected?: AlertLifecycleSelection | null) {
  const context = lifecycleBindingIdentity(binding), selectionKey = lifecycleSelectionIdentity(selected), authority = JSON.stringify([binding?.targetId.toLowerCase(),binding?.targetRevision]);
  const currentContext = useRef(context);currentContext.current=context;const currentSelection=useRef(selectionKey);currentSelection.current=selectionKey;
  const previousContext=useRef(context),previousSelection=useRef(selectionKey),epoch=useRef(0),detailEpoch=useRef(0),mounted=useRef(false),owners=useRef(new Map<Source,AbortController>()),denial=useRef<string|undefined>(undefined);
  const [state,setState]=useState(empty),received=useRef(state),[denied,setDenied]=useState(false);
  const publish=(update:(old:State)=>State)=>{const value=update(received.current);received.current=value;setState(value);};
  const stop=(source?:Source)=>{if(source){owners.current.get(source)?.abort();owners.current.delete(source);}else{epoch.current++;detailEpoch.current++;for(const owner of owners.current.values())owner.abort();owners.current.clear();}};
  useLayoutEffect(()=>{mounted.current=true;return()=>{mounted.current=false;stop();};},[]);
  useLayoutEffect(()=>{if(previousContext.current===context)return;previousContext.current=context;previousSelection.current=selectionKey;stop();publish(empty);if(denial.current!==undefined&&denial.current!==authority){denial.current=undefined;setDenied(false);}},[context]);
  useLayoutEffect(()=>{if(previousSelection.current===selectionKey)return;previousSelection.current=selectionKey;detailEpoch.current++;stop("detail");publish(old=>({...old,detail:{loading:false}}));},[selectionKey]);
  const active=(source:Source,controller:AbortController,captured:number,key:string,detail:number,selection:string)=>mounted.current&&denial.current===undefined&&!controller.signal.aborted&&owners.current.get(source)===controller&&epoch.current===captured&&currentContext.current===key&&(source==="list"||detailEpoch.current===detail&&currentSelection.current===selection);
  const refuse=(failure:unknown)=>{if(!(failure instanceof ApiRequestError)||![401,403].includes(failure.status))return false;denial.current=authority;stop();publish(empty);setDenied(true);return true;};
  async function readList(cursor?:string,replace=false,index=0){
    if(!binding||!mounted.current||denial.current!==undefined||owners.current.has("list"))return;const prior=replace?[]:received.current.list.pages;if(prior.length>=100)return;
    const controller=new AbortController(),captured=epoch.current,key=context,detail=detailEpoch.current,selection=selectionKey;owners.current.set("list",controller);
    publish(old=>({...old,list:{...old.list,loading:true,error:undefined,attempt:{cursor,replace,index}}}));
    try{const page=await getAlertLifecycleHistory(binding,controller.signal,prior,cursor);if(!active("list",controller,captured,key,detail,selection))return;const pages=[...prior,page];
      if(pages.length>100||pages.reduce((sum,p)=>sum+p.items.length,0)>10000||pages.length===100&&page.nextCursor!==null)throw new Error("The received alert history reached its read bound. Choose a narrower shared window; received pages remain evidence.");
      publish(old=>({...old,list:{pages,index,loading:false}}));
    }catch(failure){if(!active("list",controller,captured,key,detail,selection)||refuse(failure))return;publish(old=>({...old,list:{...old.list,loading:false,error:failure instanceof Error?failure.message:"Alert history is unavailable. Retry this exact read."}}));}
    finally{if(owners.current.get("list")===controller)owners.current.delete("list");}
  }
  async function readDetail(){
    if(!binding||!validLifecycleSelection(selected)||!mounted.current||denial.current!==undefined||owners.current.has("detail"))return;
    const controller=new AbortController(),captured=epoch.current,key=context,detail=detailEpoch.current,selection=selectionKey;owners.current.set("detail",controller);publish(old=>({...old,detail:{...old.detail,loading:true,error:undefined,attempt:true}}));
    try{const receipt=await getAlertLifecycleEvent(binding,selected,controller.signal);if(!active("detail",controller,captured,key,detail,selection))return;publish(old=>({...old,detail:{receipt,loading:false}}));}
    catch(failure){if(!active("detail",controller,captured,key,detail,selection)||refuse(failure))return;publish(old=>({...old,detail:{...old.detail,loading:false,error:failure instanceof Error?failure.message:"The exact alert event is unavailable. Retry this read."}}));}
    finally{if(owners.current.get("detail")===controller)owners.current.delete("detail");}
  }
  useEffect(()=>{if(denial.current===undefined)void readList(undefined,true);},[context]);
  useEffect(()=>{if(denial.current===undefined)void readDetail();},[context,selectionKey]);
  const visibleContext=previousContext.current===context,visibleSelection=previousSelection.current===selectionKey;
  const list=visibleContext?state.list:empty().list,detail=visibleContext&&visibleSelection?state.detail:empty().detail;
  function navigate(index:number){if(denial.current!==undefined||owners.current.has("list")||index<0)return;const list=received.current.list;if(list.pages[index])publish(old=>({...old,list:{...old.list,index}}));else if(index===list.pages.length&&list.pages.length<100&&list.pages.at(-1)?.nextCursor)void readList(list.pages.at(-1)!.nextCursor!,false,index);}
  function cancel(source:Source){if(!owners.current.has(source))return;stop(source);publish(old=>source==="list"?{...old,list:{...old.list,loading:false,error:"Waiting cancelled. Received pages remain; retry keeps this exact read and continuation."}}:{...old,detail:{...old.detail,loading:false,error:"Waiting cancelled. Any received exact event remains evidence; retry reads the same event and cycle."}});}
  function retryList(){const attempt=received.current.list.attempt;if(attempt)void readList(attempt.cursor,attempt.replace,attempt.index);}
  function retryAccess(){denial.current=undefined;setDenied(false);void readList(undefined,true);void readDetail();}
  return{list,detail,denied,navigate,cancel,retryList,refreshList:()=>void readList(undefined,true),readDetail,retryAccess};
}
export type AlertLifecycleHistoryRead = ReturnType<typeof useAlertLifecycleHistory>;
