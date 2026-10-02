import { useEffect, useRef, useState } from "react";
import { readAgentHistory } from "./agentHistoryApi.ts";
import { validateAgentWindow, type AgentHistoryPage, type AgentHistoryWindow } from "./agentHistoryModel.ts";

export function useAgentHistory(targetId: string, revision: number, window: AgentHistoryWindow | undefined, refresh: number) {
  const scopeIdentity = JSON.stringify([targetId.toLowerCase(), revision, window?.fromUtc, window?.toUtc]);
  const identity = JSON.stringify([targetId.toLowerCase(), revision, window?.fromUtc, window?.toUtc, refresh]);
  const [result, setResult] = useState<{ identity: string; scopeIdentity: string; page?: AgentHistoryPage; error?: string; loading: boolean; refreshing: boolean; pageNumber: number }>();
  const request = useRef<AbortController | undefined>(undefined);
  const identityRef = useRef(identity);
  identityRef.current = identity;
  let windowError: string | undefined;
  try { if (window) validateAgentWindow(window); } catch (error) { windowError = error instanceof Error ? error.message : "Choose a valid SQL Agent history window."; }
  const validWindow = window !== undefined && !windowError;
  const current = validWindow && result?.identity === identity ? result : undefined;
  // Only a validated first page may bridge a refresh of this exact scope.
  // A later pagination page must not become the new first-page snapshot.
  const retainedFirstPage = validWindow && result?.scopeIdentity === scopeIdentity && result.pageNumber === 1 && !result.error
    ? result.page : undefined;
  const page = current ? current.page : retainedFirstPage;
  const loading = validWindow && (current?.loading ?? true);
  const refreshing = loading && page !== undefined && (current?.refreshing ?? true);
  useEffect(() => {
    request.current?.abort();
    request.current = undefined;
    if (!window || windowError) { setResult(undefined); return; }
    const controller = new AbortController();
    request.current = controller;
    setResult(previous => {
      const page = previous?.scopeIdentity === scopeIdentity && previous.pageNumber === 1 && !previous.error ? previous.page : undefined;
      return { identity, scopeIdentity, page, loading: true, refreshing: page !== undefined, pageNumber: 1 };
    });
    void readAgentHistory(targetId, revision, window, controller.signal)
      .then(page => { if (!controller.signal.aborted && identityRef.current === identity) setResult({ identity, scopeIdentity, page, loading: false, refreshing: false, pageNumber: 1 }); })
      .catch((error: unknown) => { if (!controller.signal.aborted && identityRef.current === identity) setResult({ identity, scopeIdentity, loading: false, refreshing: false, pageNumber: 1,
        error: error instanceof Error ? error.message : "SQL Agent history is unavailable. Refresh to retry." }); })
      .finally(() => { if (request.current === controller) request.current = undefined; });
    return () => { request.current?.abort(); request.current = undefined; };
  }, [identity, windowError]);

  async function navigate(cursor?: string) {
    if (!window || windowError || request.current || identityRef.current !== identity) return;
    const controller = new AbortController(); request.current = controller;
    const pageNumber = cursor ? (current?.pageNumber ?? 1) + 1 : 1;
    // Replace each page. Never accumulate an unbounded history in memory.
    setResult({ identity, scopeIdentity, page: current?.page, loading: true,
      refreshing: !cursor && current?.pageNumber === 1 && current.page !== undefined, pageNumber: current?.pageNumber ?? 1 });
    try {
      const page = await readAgentHistory(targetId, revision, window, controller.signal, cursor);
      if (cursor && current?.page && page.runId !== current.page.runId) throw new Error("SQL Agent snapshot changed. Refresh the first page before continuing.");
      if (!controller.signal.aborted && identityRef.current === identity) setResult({ identity, scopeIdentity, page, loading: false, refreshing: false, pageNumber });
    } catch (error: unknown) {
      if (!controller.signal.aborted && identityRef.current === identity) setResult({ identity, scopeIdentity, loading: false, refreshing: false, pageNumber: current?.pageNumber ?? 1,
        error: error instanceof Error ? error.message : "SQL Agent history is unavailable. Refresh to retry." });
    } finally { if (request.current === controller) request.current = undefined; }
  }
  return { page, loading, refreshing,
    error: windowError ?? current?.error, pageNumber: current?.pageNumber ?? 1, navigate };
}
