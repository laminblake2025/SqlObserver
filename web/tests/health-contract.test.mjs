import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const panelUrl = new URL(
  "../src/features/health/TargetHealthPanel.tsx",
  import.meta.url,
);
const typesUrl = new URL("../src/features/health/healthTypes.ts", import.meta.url);
const apiUrl = new URL("../src/features/health/healthApi.ts", import.meta.url);
const onboardingUrl = new URL(
  "../src/features/targets/TargetOnboarding.tsx",
  import.meta.url,
);

test("target detail exposes bounded repository health evidence", async () => {
  const panel = await readFile(panelUrl, "utf8");
  const types = await readFile(typesUrl, "utf8");
  const onboarding = await readFile(onboardingUrl, "utf8");

  assert.match(onboarding, /View health evidence/);
  assert.match(onboarding, /<TargetHealthPanel/);
  assert.match(panel, /No data yet/);
  assert.match(panel, /Degraded visibility/);
  assert.match(panel, /Stale evidence/);
  assert.match(panel, /evidence_stale: "The latest successful evidence is overdue"/);
  assert.match(panel, /Visibility gap:/);
  assert.match(panel, /refreshIntervalMilliseconds = 30_000/);
  assert.match(panel, /request\?\.abort\(\)/);
  assert.match(types, /readonly sourceRows: number/);
  assert.match(types, /readonly responseBytes: number/);
  assert.match(types, /readonly sampleLossKind: CollectorLossKind \| null/);
  assert.match(types, /readonly minimumLostItems: number \| null/);
  assert.match(types, /readonly lossCountIsExact: boolean \| null/);
  assert.match(types, /readonly retryCount: number/);
  assert.match(types, /readonly circuitState: CollectorCircuitState/);
  assert.match(types, /readonly lastSuccessAtUtc: string \| null/);
  assert.match(types, /readonly coreMetrics: readonly CoreMetricSummary\[\]/);
  assert.match(panel, /No core metric samples are available\./);
  assert.match(types, /interface DatabaseHealthPage \{[^}]*readonly collector: CollectorHealthSummary/);
  assert.match(types, /interface DatabaseFileHealthPage \{[^}]*readonly collector: CollectorHealthSummary/);
  assert.match(panel, /<PageCollectorHealth collector=\{page\.collector\} \/>/);
  assert.match(panel, /Collector reason:/);
  assert.match(panel, /Sample loss:/);
  assert.match(panel, /isCurrentLossFree\(page\.collector\)/);
  assert.match(panel, /collector\.lastExecutionOutcome === "succeeded"/);
  assert.match(panel, /collector\.minimumLostItems === null/);
  assert.match(panel, /No database rows were reported by a current, loss-free collection\./);
  assert.match(panel, /Database rows cannot be treated as complete for this snapshot\./);
  assert.match(panel, /No logical-file rows were reported by a current, loss-free collection\./);
  assert.match(panel, /Logical-file rows cannot be treated as complete for this snapshot\./);
  assert.match(panel, /Additional databases exist; this view shows the bounded first page\./);
  assert.match(panel, /Additional logical files exist; this view shows the bounded first page\./);
  assert.match(panel, /BigInt\(value\)\.toLocaleString\(\)/);
  assert.match(types, /readonly sizeBytes: string/);
  assert.match(types, /readonly bytesRead: string/);
  assert.match(types, /readonly ioStallMilliseconds: string/);
  assert.doesNotMatch(`${panel}\n${types}`, /physicalPath/i);
  assert.doesNotMatch(`${panel}\n${types}\n${onboarding}`, /dangerouslySetInnerHTML/);
});

test("health fetch uses a target-scoped route and never reflects provider errors", async () => {
  const api = await readFile(apiUrl, "utf8");

  assert.match(
    api,
    /\/api\/v1\/observation-targets\/\$\{encodeURIComponent\(instanceId\)\}\/health/,
  );
  assert.match(api, /\/health\/databases\?limit=\$\{String\(firstPageLimit\)\}/);
  assert.match(api, /\/health\/files\?limit=\$\{String\(firstPageLimit\)\}/);
  assert.match(api, /Promise\.allSettled\(\[/);
  assert.match(api, /credentials: "same-origin"/);
  assert.match(api, /signal,/);
  assert.match(api, /You are not authorized to view health for this target\./);
  assert.match(api, /The health request exceeded its execution limit\./);
  assert.match(api, /The health service is temporarily unavailable\./);
  assert.match(api, /class HealthRequestError extends Error/);
  assert.doesNotMatch(api, /response\.(?:text|body)/);
  assert.doesNotMatch(api, /status \$\{status\}/);
  assert.doesNotMatch(api, /(?:password|connectionString|trustServerCertificate)\s*[?:]/i);
});
