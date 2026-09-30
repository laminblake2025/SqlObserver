import { getAgentFailures } from "./operationsApi.ts";
import { agentHistoryPage, validateAgentWindow, type AgentHistoryWindow } from "./agentHistoryModel.ts";

export async function readAgentHistory(targetId: string, revision: number, window: AgentHistoryWindow, signal: AbortSignal, cursor?: string) {
  validateAgentWindow(window);
  const page = await getAgentFailures(targetId, { ...window, limit: 50, signal, cursor });
  return agentHistoryPage(page, targetId, revision, window);
}
