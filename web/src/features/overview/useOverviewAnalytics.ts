import { useEffect, useRef, useState } from 'react';
import { getOverview, OverviewRequestError } from './overviewApi';
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

export function useOverviewAnalytics(scope: OverviewScope, refresh: number, targetRevision?: number, onAccessDenied?: (status: 401 | 403) => void) {
  const denialCallback = useRef(onAccessDenied); denialCallback.current = onAccessDenied;
  // This is the caller's known configuration boundary, not a revision supplied by the Overview DTO.
  const boundary = JSON.stringify([scope.target.toLowerCase(), targetRevision, scope.compare, scope.databaseId]);
  const generation = useRef({ boundary, number: 0 });
  if (generation.current.boundary !== boundary) generation.current = { boundary, number: generation.current.number + 1 };
  const readScopeKey = JSON.stringify([boundary, generation.current.number]);
  const scopeKey = JSON.stringify([readScopeKey, scope.range, scope.from, scope.to]);
  const [result, setResult] = useState<{key:string; scopeKey:string; readScopeKey:string; data?:OverviewSnapshot; previous?:OverviewSnapshot; error?:string; comparisonError?:string; loading:boolean}>({key:'',scopeKey:'',readScopeKey:'',loading:true});
  const key = JSON.stringify([scopeKey,refresh]);
  const latest = useRef(key); latest.current = key;
  const sameLoadedWindow = (previous: typeof result) => {
    if (previous.scopeKey === scopeKey) return true;
    if (previous.readScopeKey !== readScopeKey || !previous.data || scope.range !== 'custom' || !scope.from || !scope.to) return false;
    try {
      return utc(scope.from) === utc(previous.data.fromUtc) && utc(scope.to) === utc(previous.data.toUtc);
    } catch { return false; }
  };
  useEffect(() => {
    const controller = new AbortController();
    const current = () => !controller.signal.aborted && latest.current === key;
    setResult(previous => sameLoadedWindow(previous)
      ? {key,scopeKey,readScopeKey,data:previous.data,previous:previous.previous,loading:true}
      : {key,scopeKey,readScopeKey,loading:true});
    void (async () => {
      try {
        const window = overviewWindow(scope,Date.now());
        const data = await getOverview(scope.target,window,controller.signal);
        if (!current()) return;
        setResult({key,scopeKey,readScopeKey,data,loading:false});
        if (scope.compare) {
          const duration = utc(window.toUtc)-utc(window.fromUtc);
          try {
            const previous = await getOverview(scope.target,{fromUtc:formatUtc(utc(window.fromUtc)-duration),toUtc:window.fromUtc},controller.signal);
            if (current()) setResult({key,scopeKey,readScopeKey,data,previous,loading:false});
          } catch (failure) {
            if (!current()) return;
            if (failure instanceof OverviewRequestError && (failure.status === 401 || failure.status === 403)) {
              denialCallback.current?.(failure.status);
              if (current()) setResult({key,scopeKey,readScopeKey,loading:false,error:failure.message});
            } else setResult({key,scopeKey,readScopeKey,data,loading:false,comparisonError:'Previous-period evidence is unavailable.'});
          }
        }
      } catch (error) {
        if (!current()) return;
        if (error instanceof OverviewRequestError && (error.status === 401 || error.status === 403)) denialCallback.current?.(error.status);
        if (current()) setResult({key,scopeKey,readScopeKey,loading:false,error:error instanceof Error ? error.message : 'Overview is unavailable.'});
      }
    })();
    return () => controller.abort();
  },[key]);
  if (result.key === key) return result;
  return (sameLoadedWindow(result)
    ? {key,scopeKey,readScopeKey,data:result.data,previous:result.previous,loading:true}
    : {key,scopeKey,readScopeKey,loading:true}) as typeof result;
}
