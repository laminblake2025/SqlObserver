import { parseActiveAlerts, readJsonBounded } from "./alertRuntimeParser.ts";
import type { ActiveAlertPage } from "./alertTypes.ts";
// Runtime parser owns row.targetId !== targetId, row.items.length > 100, nextCursor === null,
// isState/isUuid/isUtc and candidate.value !== undefined null validation.

export async function getActiveAlerts(instanceId: string, signal: AbortSignal, limit = 100, cursor?: string): Promise<ActiveAlertPage> {
  if (!Number.isInteger(limit) || limit < 1 || limit > 100 || cursor !== undefined && (cursor.length === 0 || cursor.length > 1024)) throw new Error("Invalid alert page request.");
  const query = new URLSearchParams({ limit: String(limit) }); if (cursor) query.set("cursor", cursor);
  const response = await fetch(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/alerts/active?${query}`, { credentials: "same-origin", headers: { Accept: "application/json" }, signal });
  if (!response.ok) throw new Error(response.status === 403 ? "You are not authorized to view alerts for this target." : "Alerts are temporarily unavailable.");
  return parseActiveAlerts(await readJsonBounded(response), instanceId);
}

export async function acknowledgeAlert(instanceId: string, alertId: string, operationId: string, signal: AbortSignal): Promise<void> {
  const response = await fetch(`/api/v1/observation-targets/${encodeURIComponent(instanceId)}/alerts/${encodeURIComponent(alertId)}/acknowledge`, { method: "POST", credentials: "same-origin", headers: { "Accept": "application/json", "Content-Type": "application/json" }, body: JSON.stringify({ operationId }), signal });
  if (!response.ok) throw new Error(response.status === 403 ? "You are not authorized to acknowledge alerts." : "The alert could not be acknowledged.");
}
