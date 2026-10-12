import { utcMicroseconds } from "../../utcTimestamp.ts";
import type { CapabilityStatus, ObservationTargetPage, ObservationTargetSummary, TargetLifecycle } from "./targetTypes";

// Up to 100 native targets can carry 512-byte display names. ASP.NET's default
// HTML-safe JSON escaping expands each '<' to six bytes before decoding.
export const maximumTargetResponseBytes = 1024 * 1024;
export const invalidTargetResponseMessage = "SqlObserver target response is invalid. Refresh and retry.";
export function invalidTargetResponse(): Error { return new Error(invalidTargetResponseMessage); }

const lifecycles: readonly TargetLifecycle[] = ["pending_discovery", "active", "disabled", "retired"];
const capabilityStatuses: readonly CapabilityStatus[] = ["pending", "supported", "degraded", "unsupported", "unreachable",
  "authentication_failed", "tls_validation_failed", "timed_out", "security_policy_rejected", "disabled", "retired"];

function record(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw invalidTargetResponse();
  return value as Record<string, unknown>;
}
function identity(value: unknown, expected?: string): string {
  // Native MonitoredInstanceId accepts any nonempty Guid, including historical IDs.
  if (typeof value !== "string" || !/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/iu.test(value) ||
    value === "00000000-0000-0000-0000-000000000000" || expected !== undefined && value.toLowerCase() !== expected.toLowerCase()) throw invalidTargetResponse();
  return value;
}
function text(value: unknown, maximumBytes: number): string {
  if (typeof value !== "string" || value.trim().length === 0 || /[\u0000-\u001f\u007f-\u009f]/u.test(value) ||
    /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(value) ||
    new TextEncoder().encode(value).byteLength > maximumBytes) throw invalidTargetResponse();
  return value;
}
function token(value: unknown, maximumLength: number, pattern: RegExp): string {
  if (typeof value !== "string" || value.length > maximumLength || !pattern.test(value)) throw invalidTargetResponse();
  return value;
}
function positiveInteger(value: unknown): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < 1) throw invalidTargetResponse();
  return value;
}
function utc(value: unknown): string {
  if (typeof value !== "string") throw invalidTargetResponse();
  try { utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1")); }
  catch { throw invalidTargetResponse(); }
  return value;
}
function oneOf<T extends string>(value: unknown, allowed: readonly T[]): T {
  for (const candidate of allowed) if (value === candidate) return candidate;
  throw invalidTargetResponse();
}
const hostPattern = /^[a-z0-9][a-z0-9._:\-]*$/iu;
const keyPattern = /^[a-z0-9][a-z0-9._:\-]*$/u;
const labelPattern = /^[a-z0-9][a-z0-9._:\-]*$/u;

export function parseObservationTarget(value: unknown, expectedInstanceId?: string): ObservationTargetSummary {
  const row = record(value), instanceId = identity(row.instanceId, expectedInstanceId);
  const namedInstance = row.namedInstance == null ? undefined : token(row.namedInstance, 128, /^[a-z0-9][a-z0-9_$\-]*$/iu);
  const tcpPort = row.tcpPort == null ? undefined : positiveInteger(row.tcpPort);
  if ((namedInstance === undefined) === (tcpPort === undefined) || tcpPort !== undefined && tcpPort > 65535 ||
    row.authenticationMode !== "windows_integrated_service_identity" || row.encryptionMode !== "mandatory_validated") throw invalidTargetResponse();
  const lifecycle = oneOf(row.lifecycle, lifecycles), capabilityStatus = oneOf(row.capabilityStatus, capabilityStatuses);
  if (!Array.isArray(row.capabilityReasons) || row.capabilityReasons.length > 32) throw invalidTargetResponse();
  const certificateHostName = row.certificateHostName == null ? undefined : token(row.certificateHostName, 255, hostPattern);
  const lastDiscoveryAtUtc = row.lastDiscoveryAtUtc == null ? undefined : utc(row.lastDiscoveryAtUtc);
  return {
    instanceId, instanceKey: token(row.instanceKey, 256, keyPattern), displayName: text(row.displayName, 512),
    host: token(row.host, 255, hostPattern), ...(namedInstance === undefined ? {} : { namedInstance }),
    ...(tcpPort === undefined ? {} : { tcpPort }), ...(certificateHostName === undefined ? {} : { certificateHostName }),
    authenticationMode: "windows_integrated_service_identity", encryptionMode: "mandatory_validated", lifecycle, capabilityStatus,
    capabilityReasons: row.capabilityReasons.map(reason => text(reason, 512)), configurationRevision: positiveInteger(row.configurationRevision),
    discoveryRequestedAtUtc: utc(row.discoveryRequestedAtUtc), ...(lastDiscoveryAtUtc === undefined ? {} : { lastDiscoveryAtUtc }),
  };
}

export function parseObservationTargetPage(value: unknown, limit: number): ObservationTargetPage {
  const page = record(value);
  if (!Number.isInteger(limit) || limit < 1 || limit > 100 || !Array.isArray(page.items) || page.items.length > limit) throw invalidTargetResponse();
  const items = page.items.map(item => parseObservationTarget(item));
  if (new Set(items.map(item => item.instanceId.toLowerCase())).size !== items.length) throw invalidTargetResponse();
  const nextCursor = page.nextCursor == null ? undefined : token(page.nextCursor, 512, /^[a-z0-9_\-]+$/iu);
  return { items, ...(nextCursor === undefined ? {} : { nextCursor }) };
}

export interface ObservationTargetGroupingResult {
  readonly instanceId: string;
  readonly environment: string | null;
  readonly tags: readonly string[];
  readonly groupingRevision: number;
}
export function parseObservationTargetGrouping(value: unknown, expectedInstanceId: string): ObservationTargetGroupingResult {
  const row = record(value);
  const instanceId = identity(row.instanceId, expectedInstanceId);
  const environment = row.environment === null ? null : token(row.environment, 64, labelPattern);
  if (!Array.isArray(row.tags) || row.tags.length > 16) throw invalidTargetResponse();
  const tags = row.tags.map(tag => token(tag, 64, labelPattern));
  if (new Set(tags).size !== tags.length) throw invalidTargetResponse();
  return { instanceId, environment, tags, groupingRevision: positiveInteger(row.groupingRevision) };
}
