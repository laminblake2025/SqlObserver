export type AvailabilityStream = "replicas" | "databases";
export type AvailabilityCoverage = "not_collected" | "collection_failed" | "evidence_not_retained" |
  "withheld_by_byte_limit" | "no_data" | "retained";
export type AvailabilityVisibility = "PrimaryAllKnown" | "SecondaryLocalOnly" | "ResolvingLocalOnly";

export interface AvailabilityIdentity {
  readonly groupFingerprint: string;
  readonly replicaFingerprint: string;
  readonly observerReplicaFingerprint: string | null;
  readonly replicaIsLocal: boolean | null;
  readonly visibilityScope: AvailabilityVisibility;
  readonly stateObserved: boolean;
}
export interface AvailabilityReplica extends AvailabilityIdentity {
  readonly role: string | null;
  readonly operationalState: string | null;
  readonly connectedState: string | null;
}
export interface AvailabilityDatabase extends AvailabilityIdentity {
  readonly databaseFingerprint: string;
  readonly synchronizationState: string | null;
  readonly synchronizationHealth: string | null;
  readonly databaseState: string | null;
  readonly isSuspended: boolean | null;
  readonly logSendQueueKb: number | null;
  readonly logSendRateKbPerSecond: number | null;
  readonly redoQueueKb: number | null;
  readonly redoRateKbPerSecond: number | null;
  readonly sourceLastCommitLocal: string | null;
}
export interface AvailabilityPage<T extends AvailabilityIdentity = AvailabilityReplica | AvailabilityDatabase> {
  readonly schemaVersion: 2;
  readonly targetId: string;
  readonly atUtc: string;
  readonly targetRevision: number;
  readonly runId: string | null;
  readonly sourceObservedAtUtc: string | null;
  readonly completedAtUtc: string | null;
  readonly state: "Complete" | "Partial" | "Degraded" | "Unsupported" | "PermissionDenied" | "NoData";
  readonly evidenceStatus: AvailabilityCoverage;
  readonly sourceRows: number;
  readonly outputItems: number;
  readonly retainedItems: number;
  readonly lossKind: string;
  readonly truncated: boolean;
  readonly collectorEnabled: boolean;
  readonly items: readonly T[];
  readonly hasMore: boolean;
  readonly nextCursor: string | null;
}

const fingerprint = /^[0-9a-f]{64}$/;
const guid = /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;
const statuses = ["not_collected", "collection_failed", "evidence_not_retained", "withheld_by_byte_limit", "no_data", "retained"];
const states = ["Complete", "Partial", "Degraded", "Unsupported", "PermissionDenied", "NoData"];
const losses = ["none", "source_row_limit", "response_byte_limit", "output_validation_failure", "ingestion_rejection", "visibility_incomplete"];
const replicaStates = {
  role: ["PRIMARY", "SECONDARY", "RESOLVING"],
  operationalState: ["PENDING_FAILOVER", "PENDING", "ONLINE", "OFFLINE", "FAILED", "FAILED_NO_QUORUM"],
  connectedState: ["CONNECTED", "DISCONNECTED"],
};
const databaseStates = {
  synchronizationState: ["NOT SYNCHRONIZING", "SYNCHRONIZING", "SYNCHRONIZED", "REVERTING", "INITIALIZING"],
  synchronizationHealth: ["NOT_HEALTHY", "PARTIALLY_HEALTHY", "HEALTHY"],
  databaseState: ["ONLINE", "RESTORING", "RECOVERING", "RECOVERY_PENDING", "SUSPECT", "EMERGENCY", "OFFLINE"],
};
const counters = ["logSendQueueKb", "logSendRateKbPerSecond", "redoQueueKb", "redoRateKbPerSecond"];
const identityFields = ["groupFingerprint", "replicaFingerprint", "observerReplicaFingerprint", "replicaIsLocal", "visibilityScope", "stateObserved"];
const rootFields = ["schemaVersion", "targetId", "atUtc", "targetRevision", "runId", "sourceObservedAtUtc", "completedAtUtc",
  "state", "evidenceStatus", "sourceRows", "outputItems", "retainedItems", "lossKind", "truncated", "collectorEnabled", "items", "hasMore", "nextCursor"];
function invalid(): never { throw new Error("Availability-group evidence could not be verified. Restart from the first page."); }
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== "object" || Array.isArray(value)) return invalid();
  return value as Record<string, unknown>;
}
function fields(value: Record<string, unknown>, expected: readonly string[]) {
  if (Object.keys(value).length !== expected.length || expected.some(key => !Object.hasOwn(value, key))) invalid();
}
function integer(value: unknown, minimum = 0, maximum = Number.MAX_SAFE_INTEGER): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= minimum && value <= maximum;
}
// Compare all six fractional digits; Date.parse alone loses sub-millisecond cutoff bindings.
export function availabilityUtcKey(value: unknown): string {
  if (typeof value !== "string") return invalid();
  const match = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,6}))?Z$/.exec(value);
  if (!match || !validClock(match[1]!)) return invalid();
  return `${match[1]}.${(match[2] ?? "").padEnd(6, "0")}Z`;
}
function validClock(value: string): boolean {
  const time = Date.parse(`${value}Z`);
  return Number.isFinite(time) && new Date(time).toISOString().slice(0, 19) === value;
}
function nullableUtc(value: unknown): string | null { return value === null ? null : availabilityUtcKey(value); }
function nullableBoolean(value: unknown) { if (value !== null && typeof value !== "boolean") invalid(); }

export function parseAvailabilityPage(value: unknown, stream: AvailabilityStream, targetId: string, atUtc: string): AvailabilityPage {
  const root = object(value);
  fields(root, rootFields);
  if (root.schemaVersion !== 2 || typeof root.targetId !== "string" || !guid.test(root.targetId) ||
      root.targetId.toLowerCase() !== targetId.toLowerCase() || availabilityUtcKey(root.atUtc) !== availabilityUtcKey(atUtc) ||
      !integer(root.targetRevision, 1) || root.runId !== null && (typeof root.runId !== "string" || !guid.test(root.runId))) invalid();
  const observed = nullableUtc(root.sourceObservedAtUtc), completed = nullableUtc(root.completedAtUtc);
  if ((root.runId === null) !== (completed === null) || completed !== null && completed > availabilityUtcKey(atUtc) ||
      !states.includes(String(root.state)) || !statuses.includes(String(root.evidenceStatus)) || !losses.includes(String(root.lossKind)) ||
      !integer(root.sourceRows, 0, 2049) || !integer(root.outputItems, 0, 2048) || !integer(root.retainedItems, 0, 2048) ||
      root.outputItems > root.sourceRows || root.retainedItems > root.outputItems || (root.retainedItems === 0) !== (observed === null) ||
      typeof root.collectorEnabled !== "boolean" || typeof root.truncated !== "boolean" || typeof root.hasMore !== "boolean" ||
      !Array.isArray(root.items) || root.items.length > 50 || root.items.length > root.retainedItems ||
      root.hasMore !== (root.nextCursor !== null) || root.nextCursor !== null &&
      (typeof root.nextCursor !== "string" || root.nextCursor.length === 0 || root.nextCursor.length > 512) ||
      root.hasMore && root.items.length !== 50) invalid();
  const readable = root.evidenceStatus === "retained" || root.evidenceStatus === "evidence_not_retained";
  if (!readable && (root.items.length !== 0 || root.hasMore)) invalid();
  if (root.evidenceStatus === "not_collected" && (root.runId !== null || root.state !== "NoData" || root.sourceRows !== 0 || root.outputItems !== 0 || root.lossKind !== "none" || root.truncated) ||
      root.evidenceStatus !== "not_collected" && root.runId === null ||
      root.evidenceStatus === "no_data" && (root.state !== "NoData" || root.sourceRows !== 0) ||
      root.evidenceStatus === "collection_failed" && (root.outputItems !== 0 || !["Degraded", "PermissionDenied", "Unsupported"].includes(String(root.state))) ||
      root.evidenceStatus === "withheld_by_byte_limit" && (root.state !== "Partial" || root.outputItems !== 0 || root.lossKind !== "response_byte_limit" || !root.truncated) ||
      root.evidenceStatus === "retained" && (root.sourceRows === 0 || root.retainedItems !== root.outputItems || !["Complete", "Partial", "Degraded"].includes(String(root.state))) ||
      root.evidenceStatus === "evidence_not_retained" && (root.state !== "Degraded" || root.retainedItems >= root.outputItems)) invalid();
  let previous = "";
  for (const value of root.items) {
    const item = object(value);
    const tokens = stream === "replicas" ? replicaStates : databaseStates;
    const nullable = stream === "replicas" ? Object.keys(tokens) : [...Object.keys(tokens), "isSuspended", ...counters, "sourceLastCommitLocal"];
    fields(item, [...identityFields, ...nullable, ...(stream === "databases" ? ["databaseFingerprint"] : [])]);
    if (typeof item.groupFingerprint !== "string" || !fingerprint.test(item.groupFingerprint) ||
        typeof item.replicaFingerprint !== "string" || !fingerprint.test(item.replicaFingerprint) ||
        item.observerReplicaFingerprint !== null && (typeof item.observerReplicaFingerprint !== "string" || !fingerprint.test(item.observerReplicaFingerprint)) ||
        !["PrimaryAllKnown", "SecondaryLocalOnly", "ResolvingLocalOnly"].includes(String(item.visibilityScope)) ||
        typeof item.stateObserved !== "boolean") invalid();
    nullableBoolean(item.replicaIsLocal);
    for (const [key, allowed] of Object.entries(tokens))
      if (item[key] !== null && (typeof item[key] !== "string" || !allowed.includes(item[key]))) invalid();
    let key = `${item.groupFingerprint}:${item.replicaFingerprint}`;
    if (stream === "databases") {
      if (typeof item.databaseFingerprint !== "string" || !fingerprint.test(item.databaseFingerprint)) invalid();
      key += `:${item.databaseFingerprint}`;
      nullableBoolean(item.isSuspended);
      for (const field of counters) if (item[field] !== null && !integer(item[field])) invalid();
      const local = item.sourceLastCommitLocal;
      if (local !== null && (typeof local !== "string" || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}$/.test(local) ||
          !validClock(local.slice(0, 19)) || Number(local.slice(0, 4)) < 1753)) invalid();
    }
    if (key <= previous || !item.stateObserved && nullable.some(field => item[field] !== null)) invalid();
    previous = key;
  }
  return root as unknown as AvailabilityPage;
}

export function assertAvailabilitySnapshot(first: AvailabilityPage, next: AvailabilityPage): void {
  if (first.targetId.toLowerCase() !== next.targetId.toLowerCase() || first.targetRevision !== next.targetRevision ||
      first.runId !== next.runId || availabilityUtcKey(first.atUtc) !== availabilityUtcKey(next.atUtc))
    throw new Error("The AG snapshot changed. Restart both streams from the first page.");
}

export function availabilityCoverageMessage(page: AvailabilityPage): string {
  switch (page.evidenceStatus) {
    case "not_collected": return page.collectorEnabled
      ? "No v2 run was published in the seven days before the cutoff. Check collection health and read permissions."
      : "Per-replica collection is not enabled. Ask a target administrator to qualify and enable AG v2 collection.";
    case "no_data": return "A completed v2 run reported no AG rows. This describes that observation, not ongoing health.";
    case "collection_failed": return page.state === "PermissionDenied"
      ? "The collector could not read AG state. Ask a target administrator to verify SQL Server read permissions."
      : page.state === "Unsupported" ? "This target does not support the configured AG collector. Check its capability profile."
        : "The AG collection failed. Check collection health and connectivity, then retry.";
    case "withheld_by_byte_limit": return "The AG response exceeded its byte limit. No rows were retained; review collection bounds.";
    case "evidence_not_retained": return "Some evidence from this run is no longer retained. Remaining rows are incomplete; choose a newer cutoff.";
    case "retained": return page.state === "Complete" && !page.truncated && page.lossKind === "none"
      ? "All captured rows are retained. Complete coverage does not mean every replica is healthy."
      : "Captured rows have incomplete coverage. Check visibility scope, loss and unknown fields before drawing conclusions.";
  }
}

export function availabilityVisibilityLabel(value: AvailabilityVisibility): string {
  return value === "PrimaryAllKnown" ? "Primary: all known replicas (remote state is last reported)"
    : value === "SecondaryLocalOnly" ? "Secondary: local state only" : "Resolving: local state only";
}
