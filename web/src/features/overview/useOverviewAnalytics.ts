import { useEffect, useState } from 'react';
import { getOverview } from './overviewApi';
import { overviewWindow } from './overviewModel';
import type { OverviewScope, OverviewSnapshot } from './overviewTypes';

export function useOverviewAnalytics(scope: OverviewScope, refresh: number, automatic: boolean) {
  const [tick, setTick] = useState(0);
  const [result, setResult] = useState<{key:string; data?:OverviewSnapshot; previous?:OverviewSnapshot; error?:string; comparisonError?:string; loading:boolean}>({key:'',loading:true});
  const key = JSON.stringify([scope,refresh,tick]);
  useEffect(() => {
    const controller = new AbortController(); let timer: ReturnType<typeof setTimeout> | undefined;
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
      finally {
        if (!controller.signal.aborted && automatic && scope.range !== 'custom') timer=setTimeout(() => { if (!document.hidden) setTick(v => v+1); },60000);
      }
    })();
    const visible = () => { if (!document.hidden && automatic && scope.range !== 'custom') setTick(v => v+1); };
    document.addEventListener('visibilitychange',visible);
    return () => { controller.abort(); clearTimeout(timer); document.removeEventListener('visibilitychange',visible); };
  },[key,automatic]);
  return result.key === key ? result : {key,loading:true} as typeof result;
}
