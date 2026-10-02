import { invalidTargetResponse, parseObservationTarget } from "./targetResponseParser.ts";
import type { ObservationTargetSummary, UpdateObservationTargetRequest } from "./targetTypes";

export type TargetManagementAction = "update" | "rediscovery" | "retire";

export function targetManagementIdentity(instanceId: string, expectedRevision: number): string {
  if (typeof instanceId !== "string" || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/iu.test(instanceId) ||
    instanceId === "00000000-0000-0000-0000-000000000000" ||
    !Number.isSafeInteger(expectedRevision) || expectedRevision < 1) throw new Error("Invalid target management request.");
  return instanceId.toLowerCase();
}

export function targetManagementUpdate(instanceId: string, request: UpdateObservationTargetRequest): UpdateObservationTargetRequest {
  const identity = targetManagementIdentity(instanceId, request.expectedRevision);
  const allowed = ["expectedRevision", "displayName", "host", "namedInstance", "tcpPort", "certificateHostName"];
  if (Object.keys(request).some(key => !allowed.includes(key))) throw new Error("Invalid target management request.");
  // Reuse native DTO field bounds; the temporary projection is never sent.
  const parsed = parseObservationTarget({ instanceId: identity, instanceKey: "target-management",
    displayName: request.displayName, host: request.host, namedInstance: request.namedInstance,
    tcpPort: request.tcpPort, certificateHostName: request.certificateHostName,
    authenticationMode: "windows_integrated_service_identity", encryptionMode: "mandatory_validated",
    lifecycle: "pending_discovery", capabilityStatus: "pending", capabilityReasons: [],
    configurationRevision: request.expectedRevision, discoveryRequestedAtUtc: "2026-01-01T00:00:00.000000Z" }, identity);
  if ([parsed.host, parsed.namedInstance, parsed.certificateHostName].some(value => value !== undefined && /[^\x00-\x7f]/u.test(value)))
    throw new Error("Invalid target management request.");
  return { expectedRevision: request.expectedRevision, displayName: parsed.displayName, host: parsed.host,
    ...(parsed.namedInstance === undefined ? { tcpPort: parsed.tcpPort! } : { namedInstance: parsed.namedInstance }),
    ...(parsed.certificateHostName === undefined ? {} : { certificateHostName: parsed.certificateHostName }) };
}

export function targetManagementReceipt(value: unknown, instanceId: string, expectedRevision: number,
  action: TargetManagementAction, update?: UpdateObservationTargetRequest): ObservationTargetSummary {
  const target = parseObservationTarget(value, instanceId);
  // Native AlreadyRetired precedes its revision check for all three actions.
  // This terminal snapshot is authoritative; it does not claim the draft applied.
  if (target.lifecycle === "retired") {
    if (target.capabilityStatus !== "retired" || target.capabilityReasons.length !== 0 || target.lastDiscoveryAtUtc !== undefined)
      throw invalidTargetResponse();
    return target;
  }
  if (action === "retire" || target.configurationRevision !== expectedRevision + 1 ||
    target.lifecycle !== "pending_discovery" || target.capabilityStatus !== "pending" ||
    target.capabilityReasons.length !== 1 || target.capabilityReasons[0] !== "discovery_pending" ||
    target.lastDiscoveryAtUtc !== undefined) throw invalidTargetResponse();
  if (action === "update" && (!update || target.displayName !== update.displayName || target.host !== update.host ||
    target.namedInstance !== update.namedInstance || target.tcpPort !== update.tcpPort ||
    target.certificateHostName !== update.certificateHostName)) throw invalidTargetResponse();
  return target;
}
