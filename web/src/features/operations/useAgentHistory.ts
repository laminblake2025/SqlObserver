import { readAgentHistory } from "./agentHistoryApi.ts";
import { validateAgentWindow, type AgentHistoryPage, type AgentHistoryWindow } from "./agentHistoryModel.ts";
import { useRetainedOperationsPage } from "./useRetainedOperationsPage.ts";
import { OperationalApiError } from "./operationsApi.ts";

export function useAgentHistory(targetId: string, revision: number, window: AgentHistoryWindow | undefined, refresh: number,
  retainedContext?: string) {
  let windowError: string | undefined;
  try { if (window) validateAgentWindow(window); } catch (error) { windowError = error instanceof Error ? error.message : "Choose a valid SQL Agent history window."; }
  const inputKey = JSON.stringify([window?.fromUtc, window?.toUtc]);
  const context = JSON.stringify([targetId.toLowerCase(), revision, retainedContext ?? inputKey]);
  const result = useRetainedOperationsPage<AgentHistoryWindow, AgentHistoryPage>({ context,
    input: windowError ? undefined : window, inputKey, refresh,
    read: (bounds, signal, cursor) => readAgentHistory(targetId, revision, bounds, signal, cursor),
    validate: (first, next) => { if (first.runId !== next.runId) throw new Error("SQL Agent snapshot changed. Return to latest before continuing."); },
    // Timeline markers describe the current received window; investigated history pages retain transient failures.
    discardOnError: error => retainedContext === undefined ||
      error instanceof OperationalApiError && (error.status === 401 || error.status === 403),
  });
  const error = windowError ?? (result.error instanceof Error ? result.error.message : result.error ? "SQL Agent history is unavailable." : undefined);
  return { ...result, error, pageNumber: result.index + 1, refreshing: result.loading && result.page !== undefined,
    navigate: (cursor?: string) => { if (cursor) result.next(cursor); else result.returnLatest(); } };
}
