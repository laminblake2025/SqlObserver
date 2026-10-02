import { incidentUtc, validIncidentId } from "./incidentModel.ts";
import type { SavedIncident, SavedIncidentEvidence } from "./incidentModel.ts";

export interface SavedEvidenceReference {
  readonly type: string;
  readonly label: string;
  readonly identity: string;
  readonly occurredAtUtc: string;
}
interface StructuredSavedEvidence {
  readonly state: "recognized" | "unavailable";
  readonly references: readonly SavedEvidenceReference[];
  readonly tombstones: readonly string[];
  readonly producerTruncated: boolean;
  readonly algorithm: string;
  readonly generation: number;
}
export type SavedEvidenceSummary = StructuredSavedEvidence | { readonly state: "fallback"; readonly reason: string };

const fallback: SavedEvidenceSummary = Object.freeze({ state: "fallback", reason: "This packet's schema or metadata could not be recognized. Its saved raw evidence remains available." });
const metadata = ["references", "tombstones", "packetId", "targetId", "targetRevision", "trigger", "algorithm", "generation",
  "state", "truncated", "evidenceKind", "sourceDigest", "sourceCutoffSha256", "sourceCutoffUtc", "sourceRunId"];
const valueFields = new Set(["metricKey", "metricValue", "unit", "severity", "status", "reason", "source", "dimensions", "visibilityGap"]);
const referenceLabels: Readonly<Record<string, string>> = {
  health: "Diagnostic event", metric: "Metric reference", alert: "Alert reference", activity: "Activity reference",
  deadlock: "Deadlock reference", replication: "Replication reference", host: "Host reference",
};
function object(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw new Error();
  return value as Record<string, unknown>;
}
function text(value: unknown, maximum: number, empty = false): string {
  if (typeof value !== "string" || value.length > maximum || !empty && value.trim().length === 0 ||
    /[\u0000-\u001f\u007f-\u009f]/u.test(value) || /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/u.test(value)) throw new Error();
  return value;
}
function sameId(value: unknown, expected: string): boolean {
  return validIncidentId(value) && validIncidentId(expected) && value.toLowerCase() === expected.toLowerCase();
}
// Match the copied provenance envelope. This is not a recomputation of the
// native digest and must never be presented as a checksum of the JSON payload.
function sameDigest(value: unknown, expected: string): boolean {
  return typeof value === "string" && /^[0-9a-f]{64}$/iu.test(value) && /^[0-9a-f]{64}$/iu.test(expected)
    && value.toLowerCase() === expected.toLowerCase();
}
function utc(value: unknown): bigint {
  // Inner JSON uses the native serializer; PostgreSQL timestamps may retain
  // a seventh fractional zero, without adding precision beyond microseconds.
  return incidentUtc(typeof value === "string" ? value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1") : value);
}
function safeValue(value: unknown, depth = 0): void {
  if (depth > 4) throw new Error();
  if (value === null || typeof value === "boolean") return;
  if (typeof value === "number") { if (!Number.isFinite(value)) throw new Error(); return; }
  if (typeof value === "string") { text(value, 512, true); return; }
  if (Array.isArray(value)) {
    if (value.length > 64) throw new Error();
    for (const item of value) safeValue(item, depth + 1);
    return;
  }
  const row = object(value);
  if (Object.keys(row).length > 64) throw new Error();
  for (const [key, item] of Object.entries(row)) { text(key, 64); safeValue(item, depth + 1); }
}

/** Summarize a known native packet without treating its opaque JSON as facts or links. */
export function summarySavedEvidence(incident: SavedIncident, packet: SavedIncidentEvidence): SavedEvidenceSummary {
  try {
    const row = object(packet.evidence);
    if (new TextEncoder().encode(JSON.stringify(row)).length > 262144 || metadata.some(key => !Object.hasOwn(row, key)) ||
      Object.keys(row).some(key => !metadata.includes(key) && !valueFields.has(key)) ||
      !sameId(row.targetId, incident.targetId) || !sameId(row.packetId, packet.packetId) ||
      !Number.isSafeInteger(incident.targetRevision) || incident.targetRevision < 1 || row.targetRevision !== incident.targetRevision ||
      row.algorithm !== "evidence-v1" || !Number.isSafeInteger(row.generation) || (row.generation as number) < 1 ||
      row.state !== packet.visibilityState || !["complete", "partial", "unavailable", "unsupported"].includes(packet.visibilityState) ||
      row.evidenceKind !== text(packet.evidenceKind, 64) || typeof row.truncated !== "boolean" ||
      !sameDigest(row.sourceDigest, packet.sourceSha256) || !sameDigest(row.sourceCutoffSha256, packet.sourceCutoffSha256) ||
      typeof packet.identitySha256 !== "string" || !/^[0-9a-f]{64}$/iu.test(packet.identitySha256)) return fallback;
    text(row.trigger, 64);
    if (packet.sourceRunId === null ? row.sourceRunId !== null : !sameId(row.sourceRunId, packet.sourceRunId)) return fallback;
    if (packet.sourceCutoffUtc === null ? row.sourceCutoffUtc !== null : row.sourceCutoffUtc === null ||
      utc(row.sourceCutoffUtc) !== utc(packet.sourceCutoffUtc) || utc(packet.sourceCutoffUtc) > utc(incident.savedAtUtc)) return fallback;
    const at = utc(packet.occurredAtUtc);
    if (at < utc(incident.fromUtc) || at >= utc(incident.toUtc)) return fallback;
    if (!Array.isArray(row.references) || row.references.length > 256 || !Array.isArray(row.tombstones) || row.tombstones.length > 256) return fallback;
    const references = row.references.map(input => {
      const reference = object(input);
      if (Object.keys(reference).sort().join("|") !== "identity|occurredAtUtc|type") throw new Error();
      const type = text(reference.type, 64), identity = text(reference.identity, 256), occurredAtUtc = text(reference.occurredAtUtc, 40);
      if (!Object.hasOwn(referenceLabels, type) || type === "health" && !validIncidentId(identity)) throw new Error();
      const time = utc(occurredAtUtc);
      if (time < at - 15n * 60n * 1000000n || time >= at + 5n * 60n * 1000000n) throw new Error();
      return { type, label: referenceLabels[type]!, identity, occurredAtUtc };
    });
    const tombstones = row.tombstones.map(value => text(value, 256));
    if (new Set(tombstones).size !== tombstones.length || references.some(reference => tombstones.includes(reference.identity))) return fallback;
    for (const [key, value] of Object.entries(row)) if (valueFields.has(key)) safeValue(value);
    return { state: packet.visibilityState === "unavailable" || packet.visibilityState === "unsupported" ? "unavailable" : "recognized",
      references, tombstones, producerTruncated: row.truncated, algorithm: row.algorithm, generation: row.generation as number };
  } catch { return fallback; }
}
