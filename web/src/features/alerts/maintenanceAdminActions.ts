import { apiFetch } from "../../apiClient.ts";
export interface NewMaintenanceWindow {
  readonly startsAtUtc: string;
  readonly endsAtUtc: string;
  readonly reason: string;
}

export class MaintenanceAttempts {
  private readonly creates = new Map<string, { windowId: string; operationId: string }>();
  private readonly cancellations = new Map<string, string>();

  createFor(targetId: string, window: NewMaintenanceWindow): { windowId: string; operationId: string } {
    const key = JSON.stringify([targetId.toLowerCase(), window.startsAtUtc, window.endsAtUtc, window.reason]);
    const existing = this.creates.get(key);
    if (existing) return existing;
    const attempt = { windowId: crypto.randomUUID(), operationId: crypto.randomUUID() };
    this.creates.set(key, attempt);
    return attempt;
  }

  completeCreate(targetId: string, window: NewMaintenanceWindow): void {
    this.creates.delete(JSON.stringify([targetId.toLowerCase(), window.startsAtUtc, window.endsAtUtc, window.reason]));
  }

  cancellationFor(targetId: string, windowId: string, revision: number): string {
    const key = JSON.stringify([targetId.toLowerCase(), windowId.toLowerCase(), revision]);
    const existing = this.cancellations.get(key);
    if (existing) return existing;
    const operationId = crypto.randomUUID();
    this.cancellations.set(key, operationId);
    return operationId;
  }

  completeCancellation(targetId: string, windowId: string, revision: number): void {
    this.cancellations.delete(JSON.stringify([targetId.toLowerCase(), windowId.toLowerCase(), revision]));
  }
}

const localDateTime = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/;

function toUtc(value: string): string {
  const match = localDateTime.exec(value);
  if (!match) throw new Error("Enter a valid local date and time.");
  const [, year, month, day, hour, minute] = match.map(Number);
  const date = new Date(value);
  if (!Number.isFinite(date.getTime()) || date.getFullYear() !== year || date.getMonth() + 1 !== month ||
      date.getDate() !== day || date.getHours() !== hour || date.getMinutes() !== minute)
    throw new Error("The local date and time does not exist in your time zone.");
  return date.toISOString();
}

export function prepareMaintenanceWindow(startsLocal: string, endsLocal: string, reason: string): NewMaintenanceWindow {
  const startsAtUtc = toUtc(startsLocal);
  const endsAtUtc = toUtc(endsLocal);
  const length = Date.parse(endsAtUtc) - Date.parse(startsAtUtc);
  if (length <= 0 || length > 7 * 86400000) throw new Error("Choose a window longer than zero and no longer than seven days.");
  if (!reason.trim() || [...reason].some(character => /[\x00-\x1f\x7f]/.test(character)) ||
      new TextEncoder().encode(reason).length > 512)
    throw new Error("Enter a reason of at most 512 UTF-8 bytes without control characters.");
  return { startsAtUtc, endsAtUtc, reason };
}

function mutationError(response: Response): Error {
  if (response.status === 403) return new Error("A Target Administrator role on this server is required.");
  if (response.status === 409) return new Error("This window changed. Refresh it before trying again.");
  if (response.status === 429) return new Error("Too many changes. Wait and retry the same action.");
  if (response.status === 503) return new Error("Maintenance changes are temporarily unavailable. Retry the same action.");
  return new Error("The maintenance change could not be saved.");
}

export async function createMaintenanceWindow(targetId: string, window: NewMaintenanceWindow,
    attempt: { windowId: string; operationId: string }, signal: AbortSignal): Promise<void> {
  const response = await apiFetch(`/api/v1/observation-targets/${encodeURIComponent(targetId)}/alerts/maintenance`, {
    method: "POST", credentials: "same-origin",
    headers: { Accept: "application/json", "Content-Type": "application/json" },
    body: JSON.stringify({ ...window, windowId: attempt.windowId, operationId: attempt.operationId }), signal,
  });
  if (!response.ok) throw mutationError(response);
}

export async function cancelMaintenanceWindow(targetId: string, windowId: string, revision: number,
    operationId: string, signal: AbortSignal): Promise<void> {
  const response = await apiFetch(`/api/v1/observation-targets/${encodeURIComponent(targetId)}/alerts/maintenance/${encodeURIComponent(windowId)}/cancel`, {
    method: "POST", credentials: "same-origin",
    headers: { Accept: "application/json", "Content-Type": "application/json" },
    body: JSON.stringify({ operationId, expectedRevision: revision }), signal,
  });
  if (!response.ok) throw mutationError(response);
}
