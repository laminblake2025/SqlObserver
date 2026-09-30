import { useEffect, useRef, useState } from "react";
import { readAgentHistory } from "./agentHistoryApi.ts";
import { validateAgentWindow, type AgentHistoryPage, type AgentHistoryWindow } from "./agentHistoryModel.ts";

export function useAgentHistory(targetId: string, revision: number, window: AgentHistoryWindow | undefined, refresh: number) {
  const identity = JSON.stringify([targetId.toLowerCase(), revision, window?.fromUtc, window?.toUtc, refresh]);
  const [result, setResult] = useState<{ identity: string; page?: AgentHistoryPage; error?: string; loading: boolean; pageNumber: number }>();
  const request = useRef<AbortController | undefined>(undefined);
  const identityRef = useRef(identity);
  identityRef.current = identity;
  let windowError: string | undefined;
  try { if (window) validateAgentWindow(window); } catch (error) { windowError = error instanceof Error ? error.message : "Choose a valid SQL Agent history window."; }
  const current = result?.identity === identity ? result : undefined;
  useEffect(() => {
    request.current?.abort();
    request.current = undefined;
    if (!window || windowError) return;
    const controller = new AbortController();
    request.current = controller;
    setResult({ identity, loading: true, pageNumber: 1 });
    void readAgentHistory(targetId, revision, window, controller.signal)
      .then(page => { if (!controller.signal.aborted) setResult({ identity, page, loading: false, pageNumber: 1 }); })
      .catch((error: unknown) => { if (!controller.signal.aborted) setResult({ identity, loading: false, pageNumber: 1,
        error: error instanceof Error ? error.message : "SQL Agent history is unavailable. Refresh to retry." }); })
      .finally(() => { if (request.current === controller) request.current = undefined; });
    return () => { request.current?.abort(); request.current = undefined; };
  }, [identity, windowError]);

  async function navigate(cursor?: string) {
    if (!window || windowError || request.current) return;
    const controller = new AbortController(); request.current = controller;
    const pageNumber = cursor ? (current?.pageNumber ?? 1) + 1 : 1;
    // Replace each page. Never accumulate an unbounded history in memory.
    setResult({ identity, page: current?.page, loading: true, pageNumber: current?.pageNumber ?? 1 });
    try {
      const page = await readAgentHistory(targetId, revision, window, controller.signal, cursor);
      if (cursor && current?.page && page.runId !== current.page.runId) throw new Error("SQL Agent snapshot changed. Refresh the first page before continuing.");
      if (!controller.signal.aborted && identityRef.current === identity) setResult({ identity, page, loading: false, pageNumber });
    } catch (error: unknown) {
      if (!controller.signal.aborted && identityRef.current === identity) setResult({ identity, page: current?.page, loading: false, pageNumber: current?.pageNumber ?? 1,
        error: error instanceof Error ? error.message : "SQL Agent history is unavailable. Refresh to retry." });
    } finally { if (request.current === controller) request.current = undefined; }
  }
  return { page: current?.page, loading: window !== undefined && !windowError && (current?.loading ?? true),
    error: windowError ?? current?.error, pageNumber: current?.pageNumber ?? 1, navigate };
}
