import { overviewHref, readOverviewScope } from "../overview/overviewModel.ts";
import type { OverviewScope } from "../overview/overviewTypes";
import { canonicalUtcTimestamp, utcMicroseconds } from "../../utcTimestamp.ts";
import { resourceInspectionUtc } from "./resourceInspectionModel.ts";

export interface ResourceFileSelection { readonly databaseId: number; readonly fileId: number }
export interface ResourceVolumeSelection { readonly sampleTargetRevision: number; readonly volumeKey: string }
export interface ResourceFileSnapshotSelection { readonly runId: string; readonly sampleTargetRevision: number }
export interface ResourceCpuCaptureSelection { readonly runId: string; readonly sampleTargetRevision: number }
export interface ResourceVolumePlanningSelection { readonly runId: string; readonly sampleTargetRevision: number }
export interface ResourceCpuObservationSelection { readonly runId: string; readonly sampleTargetRevision: number }
export interface ResourceSelection {
  readonly cpuObservation?: ResourceCpuObservationSelection;
  readonly invalidCpuObservation?: boolean;
  readonly volumePlanning?: ResourceVolumePlanningSelection;
  readonly invalidVolumePlanning?: boolean;
  readonly cpuCapture?: ResourceCpuCaptureSelection;
  readonly invalidCpuCapture?: boolean;
  readonly file?: ResourceFileSelection;
  readonly fileSnapshot?: ResourceFileSnapshotSelection;
  readonly invalidFileSnapshot?: boolean;
  readonly volume?: ResourceVolumeSelection;
  readonly invalidVolume?: boolean;
  readonly numaNodeId?: number;
  readonly inspectionUtc?: string;
  readonly issues?: readonly string[];
  readonly invalidNode?: boolean;
  readonly invalidContext?: boolean;
}
const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/iu;
const validTarget = (value: string) => guid.test(value) && value.toLowerCase() !== "00000000-0000-0000-0000-000000000000";
const volumeKey = /^[0-9a-f]{64}$/u;
const revision = (value: string | null): number | undefined => {
  if (value === null || !/^[1-9]\d{0,15}$/u.test(value)) return undefined;
  const result = Number(value);
  return Number.isSafeInteger(result) && result >= 1 ? result : undefined;
};
const inspectionTime = (value: string): string => {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6}|\.\d{6}0)?Z$/u.test(value)) throw new Error("Use a valid UTC timestamp.");
  const normalized = value.replace(/(\.\d{6})0Z$/u, "$1Z");
  utcMicroseconds(normalized);
  return canonicalUtcTimestamp(normalized);
};
const integer = (value: string | null, min: number, max: number): number | undefined => {
  if (value === null || !/^(?:0|[1-9]\d{0,4})$/u.test(value)) return undefined;
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed >= min && parsed <= max ? parsed : undefined;
};
export function resourceWindowMatches(fromUtc: string, toUtc: string, expectedFromUtc: string, expectedToUtc: string): boolean {
  const exact = (value: string) => utcMicroseconds(value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/u, "$1"));
  try { return exact(fromUtc) === exact(expectedFromUtc) && exact(toUtc) === exact(expectedToUtc); }
  catch { return false; }
}
function validFile(file: ResourceFileSelection | undefined, scope: OverviewScope): boolean {
  return file !== undefined && Number.isSafeInteger(file.databaseId) && file.databaseId >= 1 && file.databaseId <= 32767 &&
    Number.isSafeInteger(file.fileId) && file.fileId >= 1 && file.fileId <= 65535 &&
    (scope.databaseId === undefined || scope.databaseId === file.databaseId);
}
function validBaseContext(params: URLSearchParams, scope: OverviewScope): boolean {
  if (["range", "from", "to", "databaseId", "compare"].some(key => params.getAll(key).length > 1)) return false;
  if (params.has("range") && !["1h", "6h", "24h", "7d", "custom"].includes(params.get("range")!)) return false;
  if (params.has("databaseId") && integer(params.get("databaseId"), 1, 32767) === undefined) return false;
  if (params.has("compare") && !["0", "1"].includes(params.get("compare")!)) return false;
  try {
    if (params.has("from")) inspectionTime(params.get("from")!);
    if (params.has("to")) inspectionTime(params.get("to")!);
    if (scope.range === "custom") {
      const from = inspectionTime(params.get("from") ?? ""), to = inspectionTime(params.get("to") ?? "");
      resourceInspectionUtc(from, from, to);
      if (utcMicroseconds(to) - utcMicroseconds(from) > 31n * 86400n * 1000000n) return false;
    }
    return true;
  } catch { return false; }
}

/** Decode selections only on their target's Resources route; malformed fields never choose substitutes. */
export function readResourceSelection(hash: string, expectedTarget?: string): ResourceSelection {
  const [path, query] = hash.replace(/^#\/?/u, "").split("?");
  if (path !== "resources") return {};
  const params = new URLSearchParams(query);
  const target = params.get("target");
  if (params.getAll("target").length !== 1 || !target || !validTarget(target) ||
    (expectedTarget !== undefined && target.toLowerCase() !== expectedTarget.toLowerCase())) return {};
  const scope = readOverviewScope(hash);
  const hasSelection = ["resourceDatabaseId", "resourceFileId", "fileSnapshotRun", "fileSnapshotRevision", "cpuCaptureRun", "cpuCaptureRevision", "cpuObservationRun", "cpuObservationRevision", "volumePlanningRun", "volumePlanningRevision", "numaNodeId", "resourceAt", "volumeRevision", "volumeKey"].some(key => params.has(key));
  if (hasSelection && !validBaseContext(params, scope)) return {
    invalidContext: true,
    issues: ["Requested resource selections have invalid or ambiguous database and time context. Clear resource selections before choosing new evidence."],
    ...(params.has("numaNodeId") ? { invalidNode: true } : {}),
    ...(params.has("volumeRevision") || params.has("volumeKey") ? { invalidVolume: true } : {}),
    ...(params.has("fileSnapshotRun") || params.has("fileSnapshotRevision") ? { invalidFileSnapshot: true } : {}),
    ...(params.has("cpuObservationRun") || params.has("cpuObservationRevision") ? { invalidCpuObservation: true } : {}),
    ...(params.has("cpuCaptureRun") || params.has("cpuCaptureRevision") ? { invalidCpuCapture: true } : {}),
    ...(params.has("volumePlanningRun") || params.has("volumePlanningRevision") ? { invalidVolumePlanning: true } : {}),
  };
  const issues: string[] = [];
  let file: ResourceFileSelection | undefined;
  let numaNodeId: number | undefined;
  let inspectionUtc: string | undefined;
  let invalidNode = false;
  let volume: ResourceVolumeSelection | undefined;
  let invalidVolume = false;
  let fileSnapshot: ResourceFileSnapshotSelection | undefined;
  let invalidFileSnapshot = false;
  let cpuObservation: ResourceCpuObservationSelection | undefined;
  let invalidCpuObservation = false;
  let cpuCapture: ResourceCpuCaptureSelection | undefined;
  let invalidCpuCapture = false;
  let volumePlanning: ResourceVolumePlanningSelection | undefined;
  let invalidVolumePlanning = false;
  if (params.has("volumePlanningRun") || params.has("volumePlanningRevision")) {
    const runId = params.get("volumePlanningRun"), sampleTargetRevision = revision(params.get("volumePlanningRevision"));
    if (params.getAll("volumePlanningRun").length !== 1 || params.getAll("volumePlanningRevision").length !== 1 ||
        runId === null || !validTarget(runId) || sampleTargetRevision === undefined) {
      invalidVolumePlanning = true;
      issues.push("The requested volume planning run or sample revision is invalid. Clear the planning selection or choose an exact received receipt; no other snapshot has been selected.");
    } else volumePlanning = { runId: runId.toLowerCase(), sampleTargetRevision };
  }
  if (params.has("cpuObservationRun") || params.has("cpuObservationRevision")) {
    const runId = params.get("cpuObservationRun"), sampleTargetRevision = revision(params.get("cpuObservationRevision"));
    if (params.getAll("cpuObservationRun").length !== 1 || params.getAll("cpuObservationRevision").length !== 1 ||
        runId === null || !validTarget(runId) || sampleTargetRevision === undefined) {
      invalidCpuObservation = true;
      issues.push("The requested CPU observation run or sample revision is invalid. Clear the observation selection or choose an exact received receipt; no other observation has been selected.");
    } else cpuObservation = { runId: runId.toLowerCase(), sampleTargetRevision };
  }
  if (params.has("cpuCaptureRun") || params.has("cpuCaptureRevision")) {
    const runId = params.get("cpuCaptureRun"), sampleTargetRevision = revision(params.get("cpuCaptureRevision"));
    if (params.getAll("cpuCaptureRun").length !== 1 || params.getAll("cpuCaptureRevision").length !== 1 ||
        runId === null || !validTarget(runId) || sampleTargetRevision === undefined) {
      invalidCpuCapture = true;
      issues.push("The requested CPU capture run or sample revision is invalid. Clear the capture selection or choose an exact received receipt; no other capture has been selected.");
    } else cpuCapture = { runId: runId.toLowerCase(), sampleTargetRevision };
  }
  if (params.has("fileSnapshotRun") || params.has("fileSnapshotRevision")) {
    const runId = params.get("fileSnapshotRun"), sampleTargetRevision = revision(params.get("fileSnapshotRevision"));
    if (params.getAll("fileSnapshotRun").length !== 1 || params.getAll("fileSnapshotRevision").length !== 1 ||
        runId === null || !validTarget(runId) || sampleTargetRevision === undefined) {
      invalidFileSnapshot = true;
      issues.push("The requested file snapshot run or sample revision is invalid. Clear the snapshot selection or choose an exact received receipt; no other snapshot has been selected.");
    } else fileSnapshot = { runId: runId.toLowerCase(), sampleTargetRevision };
  }
  if (params.has("volumeRevision") || params.has("volumeKey")) {
    const sampleTargetRevision = revision(params.get("volumeRevision"));
    const key = params.get("volumeKey");
    if (params.getAll("volumeRevision").length !== 1 || params.getAll("volumeKey").length !== 1 ||
        sampleTargetRevision === undefined || key === null || !volumeKey.test(key)) {
      invalidVolume = true;
      issues.push("The requested volume identity or sample revision is invalid. Choose an exact received identity or clear resource selections; no other volume has been selected.");
    } else volume = { sampleTargetRevision, volumeKey: key };
  }
  if (params.has("resourceDatabaseId") || params.has("resourceFileId")) {
    const databaseId = integer(params.get("resourceDatabaseId"), 1, 32767);
    const fileId = integer(params.get("resourceFileId"), 1, 65535);
    if (params.getAll("resourceDatabaseId").length !== 1 || params.getAll("resourceFileId").length !== 1 || databaseId === undefined || fileId === undefined) {
      issues.push("The requested resource file selection is invalid. Choose a file from the loaded snapshot or clear resource selections.");
    } else if (scope.databaseId !== undefined && scope.databaseId !== databaseId) {
      issues.push("The requested file belongs to a different database than the workspace context. No other file has been selected.");
    } else file = { databaseId, fileId };
  }
  if (params.has("numaNodeId")) {
    const node = integer(params.get("numaNodeId"), 0, 1023);
    if (params.getAll("numaNodeId").length !== 1 || node === undefined) {
      invalidNode = true;
      issues.push("The requested NUMA node selection is invalid. Choose a node from the loaded inventory or clear resource selections.");
    }
    else numaNodeId = node;
  }
  if (params.has("resourceAt")) {
    try {
      if (params.getAll("resourceAt").length !== 1) throw new Error("Repeated inspection time.");
      inspectionUtc = inspectionTime(params.get("resourceAt")!);
      if (scope.range === "custom") inspectionUtc = resourceInspectionUtc(inspectionUtc, scope.from ?? "", scope.to ?? "");
    } catch { inspectionUtc = undefined; issues.push("The requested resource inspection time is invalid or outside the shared window. Align another time or clear resource selections."); }
  }
  return { ...(cpuObservation ? { cpuObservation } : {}), ...(invalidCpuObservation ? { invalidCpuObservation: true } : {}), ...(volumePlanning ? { volumePlanning } : {}), ...(invalidVolumePlanning ? { invalidVolumePlanning: true } : {}), ...(cpuCapture ? { cpuCapture } : {}), ...(invalidCpuCapture ? { invalidCpuCapture: true } : {}), ...(file ? { file } : {}), ...(fileSnapshot ? { fileSnapshot } : {}), ...(invalidFileSnapshot ? { invalidFileSnapshot: true } : {}), ...(volume ? { volume } : {}), ...(invalidVolume ? { invalidVolume: true } : {}), ...(numaNodeId !== undefined ? { numaNodeId } : {}),
    ...(inspectionUtc ? { inspectionUtc } : {}), ...(issues.length ? { issues } : {}), ...(invalidNode ? { invalidNode: true } : {}) };
}

export function resourceSelectionHref(scope: OverviewScope, selection: ResourceSelection): string {
  const href = overviewHref(scope, "resources");
  if (!validTarget(scope.target)) return href;
  const params = new URLSearchParams(href.split("?")[1]);
  if (!validBaseContext(params, scope)) return href;
  if (selection.volumePlanning && validTarget(selection.volumePlanning.runId) && Number.isSafeInteger(selection.volumePlanning.sampleTargetRevision) && selection.volumePlanning.sampleTargetRevision >= 1) {
    params.set("volumePlanningRun", selection.volumePlanning.runId.toLowerCase());
    params.set("volumePlanningRevision", String(selection.volumePlanning.sampleTargetRevision));
  }
  if (validFile(selection.file, scope)) {
    params.set("resourceDatabaseId", String(selection.file!.databaseId));
    params.set("resourceFileId", String(selection.file!.fileId));
  }
  if (selection.cpuObservation && validTarget(selection.cpuObservation.runId) && Number.isSafeInteger(selection.cpuObservation.sampleTargetRevision) && selection.cpuObservation.sampleTargetRevision >= 1) {
    params.set("cpuObservationRun", selection.cpuObservation.runId.toLowerCase());
    params.set("cpuObservationRevision", String(selection.cpuObservation.sampleTargetRevision));
  }
  if (selection.cpuCapture && validTarget(selection.cpuCapture.runId) && Number.isSafeInteger(selection.cpuCapture.sampleTargetRevision) && selection.cpuCapture.sampleTargetRevision >= 1) {
    params.set("cpuCaptureRun", selection.cpuCapture.runId.toLowerCase());
    params.set("cpuCaptureRevision", String(selection.cpuCapture.sampleTargetRevision));
  }
  if (selection.fileSnapshot && validTarget(selection.fileSnapshot.runId) && Number.isSafeInteger(selection.fileSnapshot.sampleTargetRevision) && selection.fileSnapshot.sampleTargetRevision >= 1) {
    params.set("fileSnapshotRun", selection.fileSnapshot.runId.toLowerCase());
    params.set("fileSnapshotRevision", String(selection.fileSnapshot.sampleTargetRevision));
  }
  if (selection.volume && volumeKey.test(selection.volume.volumeKey) && Number.isSafeInteger(selection.volume.sampleTargetRevision) && selection.volume.sampleTargetRevision >= 1) {
    params.set("volumeRevision", String(selection.volume.sampleTargetRevision));
    params.set("volumeKey", selection.volume.volumeKey);
  }
  if (selection.numaNodeId !== undefined && Number.isSafeInteger(selection.numaNodeId) && selection.numaNodeId >= 0 && selection.numaNodeId <= 1023) params.set("numaNodeId", String(selection.numaNodeId));
  if (selection.inspectionUtc) {
    try {
      const utc = inspectionTime(selection.inspectionUtc);
      params.set("resourceAt", scope.range === "custom" ? resourceInspectionUtc(utc, scope.from ?? "", scope.to ?? "") : utc);
    } catch { /* Invalid or incompatible times are omitted; they never become alignment markers. */ }
  }
  return `#/resources?${params}`;
}

/** Preserve compatible selections while App changes this same server's time/database context. */
export function retainResourceSelectionHref(href: string, currentHash: string): string {
  const [path, query] = href.replace(/^#\/?/u, "").split("?");
  if (path !== "resources") return href;
  const destination = new URLSearchParams(query);
  const target = destination.get("target");
  if (!target || destination.getAll("target").length !== 1 || !validTarget(target)) return href;
  const currentScope = readOverviewScope(currentHash);
  if (currentScope.target.toLowerCase() !== target.toLowerCase()) return href;
  const nextScope = readOverviewScope(href);
  if (!validBaseContext(destination, nextScope)) return href;
  const selection = readResourceSelection(currentHash, target);
  const sameWindow = currentScope.range === nextScope.range && (nextScope.range !== "custom" ||
    resourceWindowMatches(currentScope.from ?? "", currentScope.to ?? "", nextScope.from ?? "", nextScope.to ?? ""));
  const compatible = sameWindow ? selection : { ...selection, volumePlanning: undefined };
  return preserveInvalidCpuObservationHref(preserveInvalidVolumePlanningHref(preserveInvalidCpuCaptureHref(preserveInvalidFileSnapshotHref(resourceSelectionHref(nextScope, compatible), currentHash), currentHash), currentHash), currentHash);
}

/** An invalid explicit snapshot pin remains explicit until the user clears or replaces it. */
export function preserveInvalidFileSnapshotHref(href: string, currentHash: string): string {
  const current=readOverviewScope(currentHash),next=readOverviewScope(href);
  if(current.target.toLowerCase()!==next.target.toLowerCase()||!readResourceSelection(currentHash,current.target).invalidFileSnapshot)return href;
  const [path,query]=href.split("?"),destination=new URLSearchParams(query),source=new URLSearchParams(currentHash.split("?")[1]);
  if(!/^#\/?resources$/u.test(path??"")||!validBaseContext(destination,next))return href;
  for(const key of ["fileSnapshotRun","fileSnapshotRevision"]){destination.delete(key);for(const value of source.getAll(key))destination.append(key,value);}
  return `${path}?${destination}`;
}

/** Malformed explicit CPU pins survive unrelated context changes until cleared or replaced. */
export function preserveInvalidCpuCaptureHref(href: string, currentHash: string): string {
  const current = readOverviewScope(currentHash), next = readOverviewScope(href);
  if (current.target.toLowerCase() !== next.target.toLowerCase() || !readResourceSelection(currentHash, current.target).invalidCpuCapture) return href;
  const [path, query] = href.split("?"), destination = new URLSearchParams(query), source = new URLSearchParams(currentHash.split("?")[1]);
  if (!/^#\/?resources$/u.test(path ?? "") || !validBaseContext(destination, next)) return href;
  for (const key of ["cpuCaptureRun", "cpuCaptureRevision"]) {
    destination.delete(key);
    for (const value of source.getAll(key)) destination.append(key, value);
  }
  return `${path}?${destination}`;
}

/** Malformed explicit planning pins survive unrelated changes until cleared or replaced. */
export function preserveInvalidVolumePlanningHref(href: string, currentHash: string): string {
  const current = readOverviewScope(currentHash), next = readOverviewScope(href);
  if (current.target.toLowerCase() !== next.target.toLowerCase() || !readResourceSelection(currentHash, current.target).invalidVolumePlanning) return href;
  const [path, query] = href.split("?"), destination = new URLSearchParams(query), source = new URLSearchParams(currentHash.split("?")[1]);
  if (!/^#\/?resources$/u.test(path ?? "") || !validBaseContext(destination, next)) return href;
  for (const key of ["volumePlanningRun", "volumePlanningRevision"]) {
    destination.delete(key);
    for (const value of source.getAll(key)) destination.append(key, value);
  }
  return `${path}?${destination}`;
}

export function preserveInvalidCpuObservationHref(href: string, currentHash: string): string {
  const current = readOverviewScope(currentHash), next = readOverviewScope(href);
  if (current.target.toLowerCase() !== next.target.toLowerCase() || !readResourceSelection(currentHash, current.target).invalidCpuObservation) return href;
  const [path, query] = href.split("?"), destination = new URLSearchParams(query), source = new URLSearchParams(currentHash.split("?")[1]);
  if (!/^#\/?resources$/u.test(path ?? "") || !validBaseContext(destination, next)) return href;
  for (const key of ["cpuObservationRun", "cpuObservationRevision"]) {
    destination.delete(key);
    for (const value of source.getAll(key)) destination.append(key, value);
  }
  return `${path}?${destination}`;
}
