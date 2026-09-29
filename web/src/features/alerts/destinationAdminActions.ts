import type { DestinationAdminItem } from "./alertAdminListsApi";

export interface DestinationValues {
  readonly kind: DestinationAdminItem["kind"];
  readonly configurationReference: string;
  readonly enabled: boolean;
}

export type DestinationAction = "update" | "approve" | "disable";

export function prepareDestination(values: DestinationValues): DestinationValues {
  const reference = values.configurationReference.trim();
  if (values.kind !== "https-webhook" && values.kind !== "windows-event-log")
    throw new Error("Choose an approved destination kind.");
  if (!reference || reference.length > 256 || reference.includes("://") ||
      [...reference].some(character => /[\x00-\x1f\x7f]/.test(character)))
    throw new Error("Enter a pre-provisioned configuration reference of at most 256 characters, without a URL or control characters.");
  if (values.kind === "windows-event-log" && reference !== "SqlObserver Alerts")
    throw new Error("The Windows Event Log reference must be SqlObserver Alerts.");
  return { kind: values.kind, configurationReference: reference, enabled: values.enabled };
}

export class DestinationAttempts {
  private readonly creates = new Map<string, { destinationId: string; operationId: string }>();
  private readonly changes = new Map<string, string>();
  private createKey(targetId: string, values: DestinationValues) {
    return JSON.stringify([targetId.toLowerCase(), values]);
  }
  createFor(targetId: string, values: DestinationValues) {
    const key = this.createKey(targetId, values);
    let attempt = this.creates.get(key);
    if (!attempt) { attempt = { destinationId: crypto.randomUUID(), operationId: crypto.randomUUID() }; this.creates.set(key, attempt); }
    return attempt;
  }
  completeCreate(targetId: string, values: DestinationValues) {
    this.creates.delete(this.createKey(targetId, values));
  }
  private changeKey(targetId: string, destinationId: string, revision: number,
      action: DestinationAction, values: DestinationValues) {
    return JSON.stringify([targetId.toLowerCase(), destinationId.toLowerCase(), revision, action, values]);
  }
  changeFor(targetId: string, destinationId: string, revision: number,
      action: DestinationAction, values: DestinationValues) {
    const key = this.changeKey(targetId, destinationId, revision, action, values);
    let operationId = this.changes.get(key);
    if (!operationId) { operationId = crypto.randomUUID(); this.changes.set(key, operationId); }
    return operationId;
  }
  completeChange(targetId: string, destinationId: string, revision: number,
      action: DestinationAction, values: DestinationValues) {
    this.changes.delete(this.changeKey(targetId, destinationId, revision, action, values));
  }
}

function mutationError(response: Response): Error {
  if (response.status === 403) return new Error("A Security Administrator role on this server is required.");
  if (response.status === 409) return new Error("This destination changed. Refresh before trying again.");
  if (response.status === 429) return new Error("Too many changes. Wait and retry the same action.");
  if (response.status === 503) return new Error("Destination configuration or approval is temporarily unavailable. Retry the same action.");
  if (response.status === 400) return new Error("The configuration reference or destination settings were rejected. Check the pre-provisioned server configuration.");
  return new Error("The destination change could not be saved.");
}

export async function createDestination(targetId: string, values: DestinationValues,
    attempt: { destinationId: string; operationId: string }, signal: AbortSignal): Promise<void> {
  const response = await fetch(`/api/v1/observation-targets/${encodeURIComponent(targetId)}/alerts/destinations`, {
    method: "POST", credentials: "same-origin",
    headers: { Accept: "application/json", "Content-Type": "application/json" },
    body: JSON.stringify({ ...values, ...attempt }), signal,
  });
  if (!response.ok) throw mutationError(response);
}

export async function changeDestination(targetId: string, destination: DestinationAdminItem,
    action: DestinationAction, values: DestinationValues, operationId: string, signal: AbortSignal): Promise<void> {
  const path = `/api/v1/observation-targets/${encodeURIComponent(targetId)}/alerts/destinations/${encodeURIComponent(destination.destinationId)}`;
  const response = await fetch(action === "update" ? path : `${path}/${action}`, {
    method: action === "update" ? "PUT" : "POST", credentials: "same-origin",
    headers: { Accept: "application/json", "Content-Type": "application/json" },
    body: JSON.stringify({ ...values, enabled: action === "disable" ? false : values.enabled,
      destinationId: destination.destinationId, operationId, expectedRevision: destination.revision }), signal,
  });
  if (!response.ok) throw mutationError(response);
}
