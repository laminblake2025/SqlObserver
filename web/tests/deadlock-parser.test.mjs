import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { normalizeDeadlockLockMode, parseDeadlockPage } from "../src/features/deadlocks/deadlockParser.mjs";

test("deadlock parser accepts bounded safe summaries", () => {
  const page = parseDeadlockPage({ targetId: "11111111-1111-4111-8111-111111111111", repositoryTimeUtc: "2026-08-24T01:00:00Z", items: [{ eventId: "f321555e-329f-421b-485c-cf796aa0f475", occurredAtUtc: "2026-08-24T00:00:00Z", fingerprint: "a".repeat(64), participantCount: 2, relationCount: 1, parseTruncated: false, collectedAtUtc: "2026-08-24T00:01:00Z" }] });
  assert.equal(page.items[0].participantCount, 2);
});

test("deadlock parser rejects oversized or malformed content", () => {
  assert.throws(() => parseDeadlockPage({ targetId: "11111111-1111-4111-8111-111111111111", items: [{ eventId: "bad", fingerprint: "short", occurredAtUtc: "", participantCount: 1, relationCount: 0, parseTruncated: false }] }));
  assert.throws(() => parseDeadlockPage("x".repeat(1024 * 1024 + 1)));
});

test("deadlock lock modes are canonicalized to the reviewed allowlist", () => {
  assert.equal(normalizeDeadlockLockMode("Sch-S"), "SCH_S");
  assert.equal(normalizeDeadlockLockMode("provider-secret"), "OTHER");
});

test("deadlock panel is reachable from the target workflow and supports detail", () => {
  const onboarding = readFileSync(new URL("../src/features/targets/TargetOnboarding.tsx", import.meta.url), "utf8");
  const panel = readFileSync(new URL("../src/features/deadlocks/TargetDeadlockPanel.tsx", import.meta.url), "utf8");
  const api = readFileSync(new URL("../src/features/deadlocks/deadlockApi.ts", import.meta.url), "utf8");
  assert.match(onboarding, /TargetDeadlockPanel/);
  assert.match(onboarding, /View deadlock evidence/);
  assert.match(panel, /getDeadlock\(/);
  assert.match(api, /readBoundedBody/);
});
