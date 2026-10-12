import test from "node:test";
import assert from "node:assert/strict";
import { parseSavedIncident } from "../src/features/incidents/incidentModel.ts";
import { summarySavedEvidence } from "../src/features/incidents/savedEvidenceModel.ts";

const target = "11111111-1111-4111-8111-111111111111", packetId = "22222222-2222-4222-8222-222222222222";
const eventId = "abcdefab-1234-4567-89ab-123456789abc";
const at = "2026-09-29T12:30:00.123456Z", cutoff = "2026-09-29T13:00:00.654321Z";
function nativeIncident() {
  const packet = { packetId, occurredAtUtc: at, evidenceKind: "metric", sourceRunId: null,
    sourceSha256: "a".repeat(64), identitySha256: "b".repeat(64), sourceCutoffSha256: "c".repeat(64),
    sourceCutoffUtc: cutoff, visibilityState: "complete", confidence: .125, evidence: {
      references: [{ type: "health", identity: eventId, occurredAtUtc: at }], tombstones: [],
      packetId, targetId: target, targetRevision: 7, trigger: "metric", algorithm: "evidence-v1", generation: 1,
      state: "complete", truncated: false, evidenceKind: "metric", sourceDigest: "a".repeat(64),
      sourceCutoffSha256: "c".repeat(64), sourceCutoffUtc: cutoff, sourceRunId: null,
    } };
  return { targetId: target, targetRevision: 7, threadId: "33333333-3333-4333-8333-333333333333",
    operationId: "44444444-4444-4444-8444-444444444444", fromUtc: "2026-09-29T12:00:00Z",
    toUtc: "2026-09-29T13:00:00Z", savedAtUtc: "2026-09-29T14:00:00Z", title: "Investigation", notes: "",
    evidenceTruncated: false, evidence: [packet] };
}
const summarize = incident => summarySavedEvidence(incident, incident.evidence[0]);
const inner = incident => incident.evidence[0].evidence;

test("native metadata yields diagnostic-event references without inventing metric values or links", () => {
  const incident = parseSavedIncident(nativeIncident(), target), original = JSON.stringify(incident);
  const result = summarize(incident);
  assert.equal(result.state, "recognized");
  assert.deepEqual(result.references, [{ type: "health", label: "Diagnostic event", identity: eventId, occurredAtUtc: at }]);
  assert.equal(result.algorithm, "evidence-v1"); assert.equal(result.generation, 1);
  assert.equal(result.producerTruncated, false); assert.deepEqual(result.tombstones, []);
  assert.equal("metricValue" in result, false); assert.equal("href" in result.references[0], false);
  assert.equal(JSON.stringify(incident), original);
  const equivalent = nativeIncident(); inner(equivalent).targetId = target.toUpperCase(); inner(equivalent).packetId = packetId.toUpperCase();
  inner(equivalent).sourceDigest = "A".repeat(64); inner(equivalent).sourceCutoffUtc = cutoff.replace("Z", "+00:00");
  inner(equivalent).references[0].occurredAtUtc = at.replace("Z", "+00:00");
  assert.equal(summarize(equivalent).state, "recognized");
});

test("partial, unavailable and producer truncation remain independent of capture completeness", () => {
  const incident = nativeIncident(); incident.evidence[0].visibilityState = "partial"; inner(incident).state = "partial";
  inner(incident).truncated = true; inner(incident).tombstones = ["retained-source-identity"];
  const result = summarize(incident);
  assert.equal(result.state, "recognized"); assert.equal(result.producerTruncated, true);
  assert.deepEqual(result.tombstones, ["retained-source-identity"]); assert.equal(incident.evidenceTruncated, false);
  for (const visibility of ["unavailable", "unsupported"]) {
    incident.evidence[0].visibilityState = visibility; inner(incident).state = visibility;
    assert.equal(summarize(incident).state, "unavailable");
  }
  inner(incident).references = [];
  assert.deepEqual(summarize(incident).references, []); // No healthy/zero metric inference.
});

test("unknown schemas and inconsistent native metadata fall back without rejecting a valid saved capture", () => {
  const changes = [row => row.algorithm = "future-v9", row => row.futureField = "new schema", row => delete row.references,
    row => row.targetId = packetId, row => row.packetId = target, row => row.targetRevision = 8,
    row => row.state = "partial", row => row.evidenceKind = "deadlock", row => row.sourceDigest = "d".repeat(64),
    row => row.sourceCutoffSha256 = "d".repeat(64), row => row.sourceRunId = eventId,
    row => row.sourceCutoffUtc = cutoff.replace("654321", "654322"), row => row.generation = 0,
    row => row.truncated = "false", row => row.trigger = "bad\u0001", row => row.references[0].identity = "not-a-guid"];
  for (const change of changes) {
    const incident = nativeIncident(); change(inner(incident));
    const parsed = parseSavedIncident(incident, target);
    assert.equal(summarize(parsed).state, "fallback"); assert.deepEqual(parsed.evidence[0].evidence, inner(incident));
  }
  const unknown = nativeIncident(); unknown.evidence[0].evidence = { description: "<script>opaque preserved</script>", count: 3 };
  assert.equal(summarize(parseSavedIncident(unknown, target)).state, "fallback");
  assert.doesNotMatch(summarize(unknown).reason, /<script>|opaque preserved/);
});

test("reference occurrence uses native twenty-minute bounds at microsecond precision", () => {
  for (const [time, expected] of [["2026-09-29T12:15:00.123456Z", "recognized"],
    ["2026-09-29T12:15:00.123455Z", "fallback"], ["2026-09-29T12:35:00.123455Z", "recognized"],
    ["2026-09-29T12:35:00.123456Z", "fallback"], ["2026-02-30T12:30:00Z", "fallback"],
    ["2026-09-29T12:30:00+01:00", "fallback"]]) {
    const incident = nativeIncident(); inner(incident).references[0].occurredAtUtc = time;
    assert.equal(summarize(incident).state, expected, time);
  }
  const incident = nativeIncident(); inner(incident).references[0].occurredAtUtc = at.replace("123456", "1234560");
  assert.equal(summarize(incident).state, "recognized");
  incident.fromUtc = "2026-09-29T12:29:00Z";
  inner(incident).references[0].occurredAtUtc = "2026-09-29T12:15:00.123456Z";
  assert.equal(summarize(incident).state, "recognized"); // Packet inclusion is separate from its reference window.
});

test("reference, tombstone and optional payload bounds retain maximum native lists", () => {
  const incident = nativeIncident();
  inner(incident).references = Array.from({ length: 256 }, (_, index) => ({ type: "host", identity: `host-${index}`, occurredAtUtc: at }));
  inner(incident).tombstones = Array.from({ length: 256 }, (_, index) => `expired-${index}`);
  inner(incident).dimensions = { label: "x".repeat(512), series: Array(64).fill(null) };
  assert.equal(summarize(incident).references.length, 256); assert.equal(summarize(incident).tombstones.length, 256);
  for (const change of [row => row.references.push(row.references[0]), row => row.tombstones.push("overflow"),
    row => row.references[0].identity = "x".repeat(257), row => row.tombstones[0] = "x".repeat(257),
    row => row.tombstones[1] = row.tombstones[0], row => row.tombstones[0] = row.references[0].identity,
    row => row.dimensions.series.push(null), row => row.dimensions.label = "x".repeat(513),
    row => row.dimensions.label = "bad\u0001", row => row.references[0].identity = "\ud800",
    row => row.references[0].extra = "schema drift", row => row.dimensions = { a: { b: { c: { d: { e: null } } } } }]) {
    const changed = structuredClone(incident); change(inner(changed)); assert.equal(summarize(changed).state, "fallback");
  }
  const tooLarge = nativeIncident(); inner(tooLarge).dimensions = { text: "x".repeat(262144) };
  assert.equal(summarize(tooLarge).state, "fallback");
});

test("non-health identities remain inert and nullable provenance matches its envelope", () => {
  const incident = nativeIncident(); incident.evidence[0].sourceCutoffUtc = null; inner(incident).sourceCutoffUtc = null;
  incident.evidence[0].sourceRunId = eventId; inner(incident).sourceRunId = eventId.toUpperCase();
  inner(incident).references = ["metric", "alert", "activity", "deadlock", "replication", "host"].map(type => ({
    type, identity: "https://untrusted.example/<script>opaque</script>", occurredAtUtc: at }));
  const result = summarize(incident); assert.equal(result.state, "recognized");
  assert.equal(result.references.length, 6);
  assert.ok(result.references.every(reference => !Object.hasOwn(reference, "href") && reference.label.endsWith("reference")));
  inner(incident).references[0].type = "query"; assert.equal(summarize(incident).state, "fallback");
  const invalidDigest = nativeIncident(); inner(invalidDigest).sourceDigest = "z".repeat(64);
  assert.equal(summarize(invalidDigest).state, "fallback");
});
