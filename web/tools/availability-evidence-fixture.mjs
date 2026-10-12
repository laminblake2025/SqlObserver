// Synthetic loopback fixture only; never imported by the production application.
import { appendFile, mkdir, readFile } from "node:fs/promises";
const artifacts = new URL("../.artifacts/", import.meta.url);
const scenarioFile = new URL("availability-scenario.txt", artifacts);
const logFile = new URL("availability-requests.ndjson", artifacts);
const fingerprint = value => `${value.toString(16).padStart(8, "0")}${"a".repeat(56)}`;
const runId = "22222222-2222-4222-8222-222222222222";

export async function handleAvailabilityFixture(url, response) {
  const match = /^\/api\/v2\/observation-targets\/([^/]+)\/availability-groups\/(replicas|databases)$/.exec(url.pathname);
  if (!match) return false;
  await mkdir(artifacts, { recursive: true });
  let scenario = "primary";
  try { scenario = (await readFile(scenarioFile, "utf8")).trim(); } catch {}
  const [, targetId, stream] = match;
  const atUtc = url.searchParams.get("atUtc");
  const cursor = url.searchParams.get("cursor");
  await appendFile(logFile, `${JSON.stringify({ scenario, targetId, stream, atUtc, cursor })}\n`);
  const json = (value, status = 200) => {
    response.writeHead(status, { "content-type": "application/json", "cache-control": "no-store" });
    response.end(JSON.stringify(value));
  };
  if (!atUtc || !Number.isFinite(Date.parse(atUtc))) { json({}, 400); return true; }
  if (scenario === "stale" && cursor) { json({ code: "cursor_stale" }, 409); return true; }
  if (scenario === "database-error" && stream === "databases") { json({}, 503); return true; }
  if (scenario === "denied") { json({}, 403); return true; }
  if (scenario === "delayed") await new Promise(resolve => setTimeout(resolve, 1500));
  const sourceObservedAtUtc = new Date(Date.parse(atUtc) - 60000).toISOString();
  const completedAtUtc = new Date(Date.parse(atUtc) - 59000).toISOString();
  const secondary = scenario === "secondary";
  const replicas = Array.from({ length: 51 }, (_, index) => {
    const stateObserved = !secondary || index === 0;
    return {
      groupFingerprint: fingerprint(1), replicaFingerprint: fingerprint(index + 10),
      observerReplicaFingerprint: fingerprint(10), replicaIsLocal: index === 0,
      visibilityScope: secondary ? "SecondaryLocalOnly" : "PrimaryAllKnown", stateObserved,
      role: stateObserved ? index === 0 ? secondary ? "SECONDARY" : "PRIMARY" : "SECONDARY" : null,
      operationalState: stateObserved ? index === 1 ? "FAILED_NO_QUORUM" : "ONLINE" : null,
      connectedState: stateObserved ? index === 1 ? "DISCONNECTED" : "CONNECTED" : null,
    };
  });
  const databases = replicas.map((item, index) => ({
    groupFingerprint: item.groupFingerprint, replicaFingerprint: item.replicaFingerprint,
    databaseFingerprint: fingerprint(100), observerReplicaFingerprint: item.observerReplicaFingerprint,
    replicaIsLocal: item.replicaIsLocal, visibilityScope: item.visibilityScope, stateObserved: item.stateObserved,
    synchronizationState: item.stateObserved ? index === 1 ? "NOT SYNCHRONIZING" : "SYNCHRONIZED" : null,
    synchronizationHealth: item.stateObserved ? index === 1 ? "NOT_HEALTHY" : "HEALTHY" : null,
    databaseState: item.stateObserved ? index === 1 ? "SUSPECT" : "ONLINE" : null,
    isSuspended: item.stateObserved ? index === 1 : null,
    logSendQueueKb: item.stateObserved ? index === 1 ? null : 0 : null,
    logSendRateKbPerSecond: item.stateObserved ? 0 : null,
    redoQueueKb: item.stateObserved ? index === 1 ? 234 : 0 : null,
    redoRateKbPerSecond: null,
    sourceLastCommitLocal: item.stateObserved ? "2026-09-29T01:30:00.123456" : null,
  }));
  const start = cursor === `${stream}-page-2` ? 50 : 0;
  const all = stream === "replicas" ? replicas : databases;
  const items = all.slice(start, start + 50);
  const hasMore = start === 0;
  const page = {
    schemaVersion: 2, targetId, atUtc, targetRevision: scenario === "mismatch" && stream === "databases" ? 3 : 2,
    runId, sourceObservedAtUtc, completedAtUtc,
    state: secondary ? "Partial" : "Complete", evidenceStatus: "retained",
    sourceRows: 102, outputItems: 102, retainedItems: 102,
    lossKind: secondary ? "visibility_incomplete" : "none", truncated: false, collectorEnabled: true,
    items, hasMore, nextCursor: hasMore ? `${stream}-page-2` : null,
  };
  if (scenario === "disabled") Object.assign(page, { runId: null, sourceObservedAtUtc: null, completedAtUtc: null,
    state: "NoData", evidenceStatus: "not_collected", sourceRows: 0, outputItems: 0, retainedItems: 0,
    collectorEnabled: false, items: [], hasMore: false, nextCursor: null });
  if (scenario === "retired") Object.assign(page, { sourceObservedAtUtc: null, state: "Degraded",
    evidenceStatus: "evidence_not_retained", retainedItems: 0, items: [], hasMore: false, nextCursor: null });
  json(page);
  return true;
}
