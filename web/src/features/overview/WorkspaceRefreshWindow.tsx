import { createContext, useContext, useMemo, type ReactNode } from "react";
import { overviewWindow } from "./overviewModel";
import type { OverviewScope } from "./overviewTypes";

interface ResolvedWindow { readonly key: string; readonly refresh: number; readonly now: number; }
const WindowContext = createContext<ResolvedWindow | undefined>(undefined);
const windowKey = (scope: OverviewScope) => JSON.stringify([scope.target, scope.range, scope.from, scope.to]);

/** One clock instant per workspace refresh; protected responses remain caller-owned. */
export function WorkspaceRefreshWindow({ scope, refresh, children }: { readonly scope: OverviewScope; readonly refresh: number; readonly children: ReactNode }) {
  const key = windowKey(scope);
  const value = useMemo(() => ({ key, refresh, now: Date.now() }), [key, refresh]);
  return <WindowContext.Provider value={value}>{children}</WindowContext.Provider>;
}

export function useResolvedOverviewWindow(scope: OverviewScope, refresh: number) {
  const shared = useContext(WindowContext), key = windowKey(scope);
  return useMemo(() => {
    try { return overviewWindow(scope, shared?.key === key && shared.refresh === refresh ? shared.now : Date.now()); }
    catch { return undefined; }
  }, [key, refresh, shared]);
}
