import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const componentUrl = new URL(
  "../src/features/targets/TargetOnboarding.tsx",
  import.meta.url,
);
const typesUrl = new URL("../src/features/targets/targetTypes.ts", import.meta.url);
const apiUrl = new URL("../src/features/targets/targetApi.ts", import.meta.url);

test("onboarding exposes integrated identity and explicit non-healthy states", async () => {
  const source = await readFile(componentUrl, "utf8") + await readFile(new URL("../src/App.tsx", import.meta.url), "utf8") + await readFile(new URL("../src/dashboardModel.ts", import.meta.url), "utf8");
  const types = await readFile(typesUrl, "utf8");

  assert.match(source, /Collector Windows service identity/);
  assert.match(source, /Degraded visibility/);
  assert.match(source, /Unsupported/);
  assert.match(source, /Unreachable/);
  const servers = await readFile(new URL("../src/features/targets/ServersPage.tsx", import.meta.url), "utf8");
  assert.match(servers, /Lifecycle/);
  assert.match(source, /crypto\.randomUUID\(\)/);
  assert.match(source, /candidate\.instanceId !== target\.instanceId/);
  assert.match(types, /windows_integrated_service_identity/);
  assert.match(types, /mandatory_validated/);
  assert.match(types, /readonly instanceId: string/);
  assert.match(types, /"pending_discovery" \| "active" \| "disabled" \| "retired"/);
  assert.doesNotMatch(source, /dangerouslySetInnerHTML/);
  assert.doesNotMatch(`${source}\n${types}`, /(?:password|connectionString|trustServerCertificate)\s*[?:]/i);
});

test("onboarding handles bounded administrative rejection without reflecting a response body", async () => {
  const api = await readFile(apiUrl, "utf8");

  assert.match(api, /response\.status === 429/);
  assert.match(api, /Too many administrative requests\. Wait briefly and retry\./);
  assert.doesNotMatch(api, /await response\.text\(\)/);
});
