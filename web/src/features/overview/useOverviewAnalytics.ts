import { useEffect, useState } from 'react';
import { getOverview } from './overviewApi';
import { overviewWindow } from './overviewModel';
import type { OverviewScope, OverviewSnapshot } from './overviewTypes';

export function useOverviewAnalytics(scope: OverviewScope, refresh: number) {
  const [result, setResult] = useState<{key:string; data?:OverviewSnapshot; previous?:OverviewSnapshot; error?:string; comparisonError?:string; loading:boolean}>({key:'',loading:true});
  const key = JSON.stringify([scope,refresh]);
  useEffect(() => {
    const controller = new AbortController();
    setResult({key,loading:true});
    void (async () => {
      try {
        const window = overviewWindow(scope,Date.now());
        const data = await getOverview(scope.target,window,controller.signal);
        if (controller.signal.aborted) return;
        setResult({key,data,loading:false});
        if (scope.compare) {
          const duration = Date.parse(window.toUtc)-Date.parse(window.fromUtc);
          try {
            const previous = await getOverview(scope.target,{fromUtc:new Date(Date.parse(window.fromUtc)-duration).toISOString(),toUtc:window.fromUtc},controller.signal);
            if (!controller.signal.aborted) setResult({key,data,previous,loading:false});
          } catch { if (!controller.signal.aborted) setResult({key,data,loading:false,comparisonError:'Previous-period evidence is unavailable.'}); }
        }
      } catch (error) { if (!controller.signal.aborted) setResult({key,loading:false,error:error instanceof Error ? error.message : 'Overview is unavailable.'}); }
    })();
    return () => controller.abort();
  },[key]);
  return result.key === key ? result : {key,loading:true} as typeof result;
}
