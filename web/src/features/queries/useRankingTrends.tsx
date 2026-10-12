import { createContext, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { getPlanCacheIntervalHistory, getQueryStoreGroupPlanTrend } from "./queryPerformanceApi.ts";
import type { PlanCacheIntervalHistory, QueryStoreGroupTrend } from "./queryPerformanceTypes.ts";
import { createRankingTrendQueue } from "./rankingTrendQueue.ts";
import { useSharedQueryReadAccess } from "./useQueryReadAccess.tsx";

export type RankingTrendEvidence = { readonly kind: "query_store"; readonly trend: QueryStoreGroupTrend } | { readonly kind: "plan_cache"; readonly history: PlanCacheIntervalHistory };
type Queue = ReturnType<typeof createRankingTrendQueue<RankingTrendEvidence>>;
const Context = createContext<{ readonly queue: Queue; readonly version: number } | undefined>(undefined);

export function RankingTrendProvider({ contextKey, children }: { readonly contextKey: string; readonly children: ReactNode }) {
  const latest = useRef(contextKey); latest.current = contextKey;
  const access=useSharedQueryReadAccess(), currentAccess=useRef(access);currentAccess.current=access;
  const [version, update] = useState(0);
  const queue = useMemo(() => createRankingTrendQueue<RankingTrendEvidence>(async (request, signal) => {
    const boundary=currentAccess.current,owner=boundary?.begin();
    if(boundary&&!owner)throw new Error("Query read access must be explicitly retried.");
    const cancel=()=>owner?.abort();signal.addEventListener("abort",cancel,{once:true});if(signal.aborted)cancel();
    const readSignal=owner?.signal??signal;
    try {
      if (request.kind === "query_store") return { kind: "query_store", trend: await getQueryStoreGroupPlanTrend(request.targetId, request.databaseId,
        request.queryFingerprint, request.planFingerprint, request.fromUtc, request.toUtc, readSignal) };
      return { kind: "plan_cache", history: await getPlanCacheIntervalHistory({ targetId: request.targetId, databaseId: request.databaseId,
        databaseIncarnation: request.databaseIncarnation!, queryFingerprint: request.queryFingerprint, planFingerprint: request.planFingerprint }, request.fromUtc, request.toUtc, readSignal) };
    } catch(failure) { if(owner)boundary?.failed(failure,owner);throw failure; }
    finally { signal.removeEventListener("abort",cancel);owner?.abort(); }
  }, () => { if (latest.current === contextKey) update(value => value + 1); }), [contextKey]);
  useEffect(() => () => queue.cancel(), [queue]);
  return <Context.Provider value={{ queue, version }}>{children}</Context.Provider>;
}
export const useRankingTrends = () => useContext(Context)?.queue;
