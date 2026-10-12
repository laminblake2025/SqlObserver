import { createContext, useContext, useLayoutEffect, useRef, useState } from "react";
import { ApiRequestError } from "../../apiClient.ts";

/** Target-read denial is shared by independent query projections. Protected-content-only denial is not. */
export function useQueryReadBoundary(targetId: string, onDenied: () => void, context = targetId.toLowerCase()) {
  const target = targetId.toLowerCase(), latest = useRef(target); latest.current = target;
  const currentContext=useRef(context);currentContext.current=context;
  const mounted = useRef(false), owners = useRef(new Map<AbortController,{target:string;context:string;epoch:number}>()), epoch = useRef(0), denial = useRef<string | undefined>(undefined);
  const previous = useRef(context), clear = useRef(onDenied); clear.current = onDenied;
  const [revision,setRevision] = useState(0);
  const stop = () => { epoch.current++; for (const controller of owners.current.keys()) controller.abort(); owners.current.clear(); };
  useLayoutEffect(()=>{mounted.current=true;return()=>{mounted.current=false;stop();};},[]);
  useLayoutEffect(()=>{if(previous.current===context)return;previous.current=context;stop();if(denial.current!==target)denial.current=undefined;},[context]);
  const allowed=()=>mounted.current&&latest.current===target&&currentContext.current===context&&denial.current!==target;
  function begin() { if (!allowed()) return undefined;
    const controller=new AbortController();owners.current.set(controller,{target,context,epoch:epoch.current});controller.signal.addEventListener("abort",()=>owners.current.delete(controller),{once:true});return controller;
  }
  function failed(failure: unknown, controller: AbortController) {
    const owner=owners.current.get(controller);
    if (!mounted.current || controller.signal.aborted || !owner || owner.target!==latest.current || owner.context!==currentContext.current || owner.epoch!==epoch.current || denial.current===latest.current) return true;
    if (!(failure instanceof ApiRequestError) || ![401,403].includes(failure.status)) return false;
    denial.current=owner.target;stop();clear.current();setRevision(value=>value+1);return true;
  }
  function retry() { if (!mounted.current || latest.current!==target || denial.current!==target) return;denial.current=undefined;stop();setRevision(value=>value+1); }
  return {denied:denial.current===target,revision,allowed,begin,failed,retry};
}
export type QueryReadAccess = ReturnType<typeof useQueryReadBoundary>;
export const QueryReadAccessContext = createContext<QueryReadAccess | undefined>(undefined);
export function useSharedQueryReadAccess() { return useContext(QueryReadAccessContext); }
