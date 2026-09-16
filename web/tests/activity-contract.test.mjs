import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const apiUrl = new URL("../src/features/activity/activityApi.ts", import.meta.url);
const parserUrl = new URL("../src/features/activity/activityParser.mjs", import.meta.url);
const panelUrl = new URL("../src/features/activity/TargetActivityPanel.tsx", import.meta.url);
const typesUrl = new URL("../src/features/activity/activityTypes.ts", import.meta.url);

test("activity client requests all bounded target routes and validates untrusted pages", async () => {
  const api = await readFile(apiUrl, "utf8");
  const parser = await readFile(parserUrl, "utf8");
  assert.match(api, /`\$\{base\}\/sessions\?limit=\$\{String\(pageLimit\)\}`/);
  assert.match(api, /`\$\{base\}\/requests\?limit=\$\{String\(pageLimit\)\}`/);
  assert.match(api, /`\$\{base\}\/waits\?limit=\$\{String\(pageLimit\)\}`/);
  assert.match(api, /`\$\{base\}\/blocking\/current\?limit=\$\{String\(pageLimit\)\}`/);
  assert.match(parser, /record\.items\.length > pageLimit/);
  assert.match(parser, /safeText/);
  assert.match(parser, /safeCursor/);
  assert.match(parser, /parseHistory/);
  assert.doesNotMatch(api, /record\.items as T\[\]/);
  assert.match(parser, /response\.body/);
  assert.doesNotMatch(api, /(?:query|login|host|program|resource|physical_path|provider_message)/i);
});

test("activity UI renders history edge/evidence and reset/loss evidence", async () => {
  const panel = await readFile(panelUrl, "utf8");
  const types = await readFile(typesUrl, "utf8");
  assert.match(types, /interface BlockingHistoryItem[\s\S]*readonly evidence: ActivityEvidence[\s\S]*readonly edge: BlockingEdge/);
  assert.match(panel, /item\.edge\.blockedSessionId/);
  assert.match(panel, /item\.evidence\.freshness/);
  assert.match(panel, /resetDetected/);
  assert.match(panel, /partial\/loss evidence/);
  assert.doesNotMatch(panel, /dangerouslySetInnerHTML/);
});
