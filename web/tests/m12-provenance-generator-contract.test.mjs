import assert from "node:assert/strict";
import { link, mkdir, mkdtemp, readFile, rm, unlink, writeFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { buildProvenanceEvidence, jsonBytes, LIMITS } from "../../tools/generate-m12-provenance-evidence.mjs";

const commit = "a".repeat(40); const runId = "11111111-1111-4111-8111-111111111111"; const environmentId = "release-windows-server-2022";
const products = [
  ["sqlobserver-collector", "SqlObserver.Collector", "SqlObserver.Collector"],
  ["sqlobserver-mcp-stdio", "SqlObserver.McpStdio", "SqlObserver.McpStdio"],
  ["sqlobserver-server", "SqlObserver.Server", "SqlObserver.Server"],
  ["sqlobserver-web", "SqlObserver.Web", "index.html"],
];
const sha = (bytes) => createHash("sha256").update(bytes).digest("hex");

async function fixture() {
  const root = await mkdtemp(path.join(os.tmpdir(), "m12-provenance-"));
  const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
  const manifestPath = path.join(repositoryRoot, "release/certification/m12-sbom-inputs.v1.json");
  const manifest = JSON.parse((await readFile(manifestPath)).toString());
  const inputManifestPath = path.join(root, "release", "certification", "m12-sbom-inputs.v1.json"); await mkdir(path.dirname(inputManifestPath), { recursive: true }); await writeFile(inputManifestPath, await readFile(manifestPath));
  for (const entry of manifest.files) { const target = path.join(root, ...entry.path.split("/")); await mkdir(path.dirname(target), { recursive: true }); await writeFile(target, await readFile(path.join(repositoryRoot, ...entry.path.split("/")))); }
  const subjectDir = path.join(root, "TestResults", "m12", `.pending-${runId}`, "raw"); await mkdir(subjectDir, { recursive: true });
  const subjectRows = [];
  for (const [id, name, entryPoint] of products) {
    const spec = { "sqlobserver-server": ["publish-0", "SqlObserver.Server.exe"], "sqlobserver-collector": ["publish-1", "SqlObserver.Collector.exe"], "sqlobserver-mcp-stdio": ["publish-2", "SqlObserver.McpStdio.exe"], "sqlobserver-web": ["web/dist", "index.html"] }[id];
    const rel = `${spec[0]}/${spec[1]}`; const bytes = Buffer.from(`${id}\n`, "utf8"); await mkdir(path.dirname(path.join(subjectDir, rel)), { recursive: true }); await writeFile(path.join(subjectDir, rel), bytes); subjectRows.push({ productId: id, name, path: rel, entryPoint, sha256: sha(bytes), size: bytes.length });
  }
  const subjectsPath = path.join(subjectDir, "subjects.json"); await writeFile(subjectsPath, jsonBytes({ $schema: "m12-provenance-subjects.v1.schema.json", schemaVersion: 1, products: subjectRows }));
  const components = products.map(([, name]) => ({ type: "application", name, version: commit, "bom-ref": `pkg:generic/${name.toLowerCase()}@${commit}`, purl: `pkg:generic/${name.toLowerCase()}@${commit}` }));
  components.push({ type: "application", name: "SqlObserver", version: commit, "bom-ref": `pkg:generic/sqlobserver@${commit}`, purl: `pkg:generic/sqlobserver@${commit}` });
  const rootRef = `pkg:generic/sqlobserver@${commit}`; const dependencies = components.filter((item) => item.name !== "SqlObserver").map((component) => ({ ref: component["bom-ref"], dependsOn: [] })); dependencies.push({ ref: rootRef, dependsOn: components.filter((item) => item.name !== "SqlObserver").map((item) => item["bom-ref"]).sort() });
  const sbomPath = path.join(subjectDir, "sbom-one.json"); await writeFile(sbomPath, jsonBytes({ bomFormat: "CycloneDX", specVersion: "1.7", serialNumber: `urn:uuid:${runId}`, version: 1, metadata: { timestamp: "2026-08-28T12:00:00.000Z", component: { type: "application", name: "SqlObserver", version: commit, "bom-ref": rootRef, purl: rootRef }, properties: [{ name: "commitSha", value: commit }, { name: "runId", value: runId }, { name: "environmentId", value: environmentId }, { name: "identity.kind", value: "git-commit" }] }, components: components.filter((item) => item.name !== "SqlObserver"), dependencies }));
  return { root, subjectsPath, manifestPath: inputManifestPath, sbomPath };
}

test("four-product provenance is deterministic and bound to SBOM plus 41-file manifest", async () => {
  const f = await fixture(); try {
    const options = { subjectsPath: f.subjectsPath, sbomPath: f.sbomPath, inputManifestPath: f.manifestPath, root: f.root, commitSha: commit, runId, environmentId, generatedAt: "2026-08-28T12:00:00.0000000Z" };
    const first = await buildProvenanceEvidence(options); const second = await buildProvenanceEvidence(options);
    assert.deepEqual(first.bytes, second.bytes); assert.equal(first.evidence.products.length, 4); assert.equal(first.evidence.inputManifestFileCount, 41); assert.equal(first.bytes.at(-1), 10);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("nonexistent and all-zero claimed subjects fail closed", async () => {
  const f = await fixture(); try {
    const base = { subjectsPath: f.subjectsPath, sbomPath: f.sbomPath, inputManifestPath: f.manifestPath, root: f.root, commitSha: commit, runId, environmentId, generatedAt: "2026-08-28T12:00:00.0000000Z" };
    const value = JSON.parse((await readFile(f.subjectsPath)).toString()); value.products[0].path = "products/does-not-exist"; await writeFile(f.subjectsPath, jsonBytes(value)); await assert.rejects(() => buildProvenanceEvidence(base), /product|invalid|digest/);
    const fresh = await fixture(); try { const tampered = JSON.parse((await readFile(fresh.subjectsPath)).toString()); tampered.products[0].sha256 = "0".repeat(64); await writeFile(fresh.subjectsPath, jsonBytes(tampered)); await assert.rejects(() => buildProvenanceEvidence({ ...base, subjectsPath: fresh.subjectsPath, root: fresh.root, sbomPath: fresh.sbomPath, inputManifestPath: fresh.manifestPath }), /digest/); } finally { await rm(fresh.root, { recursive: true, force: true }); }
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("canonical inputs, LF JSON, and duplicate-key rejection are enforced", async () => {
  assert.equal(LIMITS.products, 4); assert.equal(LIMITS.manifestFiles, 41);
  const f = await fixture(); try {
    await writeFile(f.subjectsPath, Buffer.from('{"$schema":"m12-provenance-subjects.v1.schema.json","$schema":"bad","schemaVersion":1,"products":[]}\n'));
    await assert.rejects(() => buildProvenanceEvidence({ subjectsPath: f.subjectsPath, sbomPath: f.sbomPath, inputManifestPath: f.manifestPath, root: f.root, commitSha: commit, runId, environmentId, generatedAt: "2026-08-28T12:00:00.0000000Z" }), /duplicate/);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("malformed CycloneDX root, missing dependencies, and extra root are rejected", async () => {
  const f = await fixture(); try {
    const base = { subjectsPath: f.subjectsPath, sbomPath: f.sbomPath, inputManifestPath: f.manifestPath, root: f.root, commitSha: commit, runId, environmentId, generatedAt: "2026-08-28T12:00:00.0000000Z" };
    const sbom = JSON.parse((await readFile(f.sbomPath)).toString());
    for (const mutate of [value => { delete value.serialNumber; }, value => { delete value.dependencies; }, value => { value.metadata.component = { ...value.metadata.component, "bom-ref": "pkg:generic/extra@x" }; }]) {
      const changed = structuredClone(sbom); mutate(changed); await writeFile(f.sbomPath, jsonBytes(changed)); await assert.rejects(() => buildProvenanceEvidence(base), /SBOM/);
    }
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("outside and hardlinked subjects fail closed", async () => {
  const f = await fixture(); try {
    const base = { subjectsPath: f.subjectsPath, sbomPath: f.sbomPath, inputManifestPath: f.manifestPath, root: f.root, commitSha: commit, runId, environmentId, generatedAt: "2026-08-28T12:00:00.0000000Z" };
    const changed = JSON.parse((await readFile(f.subjectsPath)).toString()); changed.products[0].path = "../outside.exe"; await writeFile(f.subjectsPath, jsonBytes(changed)); await assert.rejects(() => buildProvenanceEvidence(base), /path|identity|outside/i);
    const fresh = await fixture(); try {
      const outside = path.join(fresh.root, "outside.exe"); const expected = path.join(fresh.root, "TestResults", "m12", `.pending-${runId}`, "raw", "publish-0", "SqlObserver.Server.exe"); await writeFile(outside, Buffer.from("hardlink\n")); await unlink(expected); await link(outside, expected); await assert.rejects(() => buildProvenanceEvidence({ ...base, root: fresh.root, subjectsPath: fresh.subjectsPath, sbomPath: fresh.sbomPath, inputManifestPath: fresh.manifestPath }), /fresh|regular|subject|link/i);
    } finally { await rm(fresh.root, { recursive: true, force: true }); }
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("input and subject bytes are revalidated after mutation", async () => {
  const f = await fixture(); try {
    const base = { subjectsPath: f.subjectsPath, sbomPath: f.sbomPath, inputManifestPath: f.manifestPath, root: f.root, commitSha: commit, runId, environmentId, generatedAt: "2026-08-28T12:00:00.0000000Z" };
    const manifestValue = JSON.parse((await readFile(f.manifestPath)).toString());
    const target = path.join(f.root, ...manifestValue.files[0].path.split("/"));
    await writeFile(target, Buffer.from("mutated-after-manifest-snapshot\n"));
    await assert.rejects(() => buildProvenanceEvidence(base), /manifest|digest|hash/i);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});
