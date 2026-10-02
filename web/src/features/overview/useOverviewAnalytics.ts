import { useEffect, useState } from 'react';
import { getOverview } from './overviewApi';
import { overviewWindow } from './overviewModel';
import type { OverviewScope, OverviewSnapshot } from './overviewTypes';
import { utcMicroseconds } from '../../utcTimestamp.ts';

const utc = (value: string) => utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, '$1'));
function formatUtc(value: bigint): string {
  const milliseconds = value / 1000n - (value % 1000n < 0n ? 1n : 0n);
  const fraction = value - milliseconds * 1000n;
  const base = new Date(Number(milliseconds)).toISOString();
  return fraction === 0n ? base : base.replace('Z', `${fraction.toString().padStart(3,'0')}Z`);
}

export function useOverviewAnalytics(scope: OverviewScope, refresh: number) {
  const scopeKey = JSON.stringify([scope.target.toLowerCase(), scope.range, scope.from, scope.to, scope.compare, scope.databaseId]);
  const readScopeKey = JSON.stringify([scope.target.toLowerCase(), scope.compare, scope.databaseId]);
  const [result, setResult] = useState<{key:string; scopeKey:string; readScopeKey:string; data?:OverviewSnapshot; previous?:OverviewSnapshot; error?:string; comparisonError?:string; loading:boolean}>({key:'',scopeKey:'',readScopeKey:'',loading:true});
  const key = JSON.stringify([scopeKey,refresh]);
  const sameLoadedWindow = (previous: typeof result) => {
    if (previous.scopeKey === scopeKey) return true;
    if (previous.readScopeKey !== readScopeKey || !previous.data || scope.range !== 'custom' || !scope.from || !scope.to) return false;
    try {
      return utc(scope.from) === utc(previous.data.fromUtc) && utc(scope.to) === utc(previous.data.toUtc);
    } catch { return false; }
  };
  useEffect(() => {
    const controller = new AbortController();
    setResult(previous => sameLoadedWindow(previous)
      ? {key,scopeKey,readScopeKey,data:previous.data,previous:previous.previous,loading:true}
      : {key,scopeKey,readScopeKey,loading:true});
    void (async () => {
      try {
        const window = overviewWindow(scope,Date.now());
        const data = await getOverview(scope.target,window,controller.signal);
        if (controller.signal.aborted) return;
        setResult({key,scopeKey,readScopeKey,data,loading:false});
        if (scope.compare) {
          const duration = utc(window.toUtc)-utc(window.fromUtc);
          try {
            const previous = await getOverview(scope.target,{fromUtc:formatUtc(utc(window.fromUtc)-duration),toUtc:window.fromUtc},controller.signal);
            if (!controller.signal.aborted) setResult({key,scopeKey,readScopeKey,data,previous,loading:false});
          } catch { if (!controller.signal.aborted) setResult({key,scopeKey,readScopeKey,data,loading:false,comparisonError:'Previous-period evidence is unavailable.'}); }
        }
      } catch (error) { if (!controller.signal.aborted) setResult({key,scopeKey,readScopeKey,loading:false,error:error instanceof Error ? error.message : 'Overview is unavailable.'}); }
    })();
    return () => controller.abort();
  },[key]);
  if (result.key === key) return result;
  return (sameLoadedWindow(result)
    ? {key,scopeKey,readScopeKey,data:result.data,previous:result.previous,loading:true}
    : {key,scopeKey,readScopeKey,loading:true}) as typeof result;
}
