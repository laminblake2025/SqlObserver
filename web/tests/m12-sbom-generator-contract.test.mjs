import assert from "node:assert/strict";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { buildSbom, canonicalPurl, jsonBytes, LIMITS } from "../../tools/generate-m12-sbom.mjs";
import { catalogDigest } from "../tools/web-asset-manifest.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

test("SBOM purls and JSON bytes are canonical and deterministic", () => {
  const purl = canonicalPurl("npm", "react", "19.2.8");
  assert.equal(purl, "pkg:npm/react@19.2.8");
  assert.deepEqual(jsonBytes({ z: 1, a: [true, "x"] }), jsonBytes({ a: [true, "x"], z: 1 }));
  assert.equal(LIMITS.components, 4096); assert.equal(LIMITS.dependencyNodes, 8192); assert.equal(LIMITS.jsonBytes, 4194304);
});

test("unsafe purl data is rejected", () => {
  assert.throws(() => canonicalPurl("npm", "../secret", "1.0.0"), /unsafe/);
  assert.throws(() => canonicalPurl("npm", "host:password", "1.0.0"), /unsafe/);
  for (const name of ["Acme.Password", "Acme.Credential", "Acme.PrivateKey", "Acme.Authorization", "Acme.ConnectionString", "Acme.Secret", "Acme.ApiKey"]) assert.throws(() => canonicalPurl("nuget", name, "1.0.0"), /unsafe/);
  assert.throws(() => canonicalPurl("npm", "evil.example.com", "1.0.0"), /unsafe/);
  assert.throws(() => canonicalPurl("npm", "x", "x\u0000"), /unsafe/);
});

async function fixture() {
  const temp = await mkdtemp(path.join(os.tmpdir(), "m12-sbom-generator-"));
  const deps = { targets: { ".NETCoreApp,Version=v10.0": { "SqlObserver.Server/1.0.0": { dependencies: { "Serilog/3.0.0": "3.0.0", "Microsoft.Extensions.Configuration.UserSecrets/10.0.11": "10.0.11" } } } }, libraries: { "SqlObserver.Server/1.0.0": {}, "Serilog/3.0.0": {}, "Microsoft.Extensions.Configuration.UserSecrets/10.0.11": {} } };
  const depsPaths = [];
  for (const host of ["SqlObserver.Server", "SqlObserver.Collector", "SqlObserver.McpStdio"]) { const file = path.join(temp, `${host}.deps.json`); const hostDeps = JSON.parse(JSON.stringify(deps)); hostDeps.libraries[`${host}/1.0.0`] = {}; await writeFile(file, JSON.stringify(hostDeps)); depsPaths.push(file); }
  const catalog = { $schema: "web-asset-manifest.v1.schema.json", schemaVersion: 1, sha256: "", entrypoint: { document: "index.html", graph: [] }, files: [{ path: "assets/app-AbCd1234.js", role: "chunk", bytes: 7, sha256: "a".repeat(64) }] };
  catalog.sha256 = catalogDigest(catalog);
  const pnpm = path.join(temp, "pnpm.json"); const catalogPath = path.join(temp, "catalog.json");
  await writeFile(pnpm, JSON.stringify({ packages: { "sql-observer-web": { version: "0.1.0", dependencies: { react: "19.2.8" } }, react: { version: "19.2.8", dependencies: {} } } })); await writeFile(catalogPath, JSON.stringify(catalog));
  return { temp, depsPaths, pnpm, catalogPath };
}

test("generator emits an identical complete component and edge closure twice", async () => {
  const f = await fixture();
  try {
    const options = { root: repositoryRoot, inputManifest: path.join(repositoryRoot, "release/certification/m12-sbom-inputs.v1.json"), deps: f.depsPaths, pnpmList: f.pnpm, webCatalog: f.catalogPath, timestamp: "2026-08-27T00:00:00.0000000Z", commitSha: "a".repeat(40), runId: "11111111-1111-4111-8111-111111111111", environmentId: "release-windows-server-2022" };
    const first = await buildSbom(options); const second = await buildSbom(options);
    assert.deepEqual(first.bytes, second.bytes);
    const refs = first.bom.components.map((component) => component["bom-ref"]);
    assert.ok(refs.includes("pkg:generic/sqlobserver.server@1.0.0")); assert.ok(refs.includes("pkg:nuget/serilog@3.0.0")); assert.ok(refs.includes("pkg:nuget/microsoft.extensions.configuration.usersecrets@10.0.11")); assert.ok(refs.includes("pkg:npm/react@19.2.8")); assert.ok(!refs.includes("pkg:npm/sql-observer-web@0.1.0")); assert.ok(refs.some((ref) => ref.startsWith("pkg:generic/web/")));
    assert.ok(!refs.includes(first.bom.metadata.component["bom-ref"])); assert.equal(new Set([first.bom.metadata.component["bom-ref"], ...refs]).size, refs.length + 1);
    const server = first.bom.dependencies.find((item) => item.ref === "pkg:generic/sqlobserver.server@1.0.0"); assert.deepEqual(server.dependsOn, ["pkg:nuget/microsoft.extensions.configuration.usersecrets@10.0.11", "pkg:nuget/serilog@3.0.0"]);
    const web = first.bom.dependencies.find((item) => item.ref === `pkg:generic/sqlobserver.web@${options.commitSha}`); assert.ok(web.dependsOn.includes("pkg:npm/react@19.2.8")); assert.ok(web.dependsOn.some((ref) => ref.startsWith("pkg:generic/web/")));
    assert.equal(first.bom.metadata.properties.find((item) => item.name === "environmentId").value, "release-windows-server-2022"); assert.equal(first.bom.metadata.properties.find((item) => item.name === "identity.kind").value, "git-commit"); assert.equal(first.bom.metadata.component.type, "application"); assert.equal(first.bom.metadata.timestamp, options.timestamp);
    assert.ok(first.bom.components.some((item) => item.name === "Microsoft.Extensions.Configuration.UserSecrets")); assert.ok(first.bom.metadata.properties.every((item) => !/[/\\]|(?:password|token|secret)/iu.test(item.value)));
  } finally { await rm(f.temp, { recursive: true, force: true }); }
});

test("generator rejects missing hosts, catalog tamper, unsafe paths, and component bounds", async () => {
  const f = await fixture();
  try {
    const options = { root: repositoryRoot, inputManifest: path.join(repositoryRoot, "release/certification/m12-sbom-inputs.v1.json"), deps: f.depsPaths.slice(0, 2), pnpmList: f.pnpm, webCatalog: f.catalogPath, timestamp: "2026-08-27T00:00:00Z", commitSha: "b".repeat(40), runId: "22222222-2222-4222-8222-222222222222", environmentId: "release-windows-server-2025" };
    await assert.rejects(() => buildSbom(options), /exactly three/);
    await assert.rejects(() => buildSbom({ ...options, deps: [f.depsPaths[0], f.depsPaths[0], f.depsPaths[1]] }), /distinct/);
    const catalog = JSON.parse(await (await import("node:fs/promises")).readFile(f.catalogPath, "utf8")); catalog.files[0].path = "../escape.js"; await writeFile(f.catalogPath, JSON.stringify(catalog));
    await assert.rejects(() => buildSbom({ ...options, deps: f.depsPaths }), /catalog digest/);
  } finally { await rm(f.temp, { recursive: true, force: true }); }
});

test("generator rejects duplicate JSON properties, unsafe pnpm fields, and dependency cycles", async () => {
  const f = await fixture();
  try {
    const base = { root: repositoryRoot, inputManifest: path.join(repositoryRoot, "release/certification/m12-sbom-inputs.v1.json"), deps: f.depsPaths, pnpmList: f.pnpm, webCatalog: f.catalogPath, timestamp: "2026-08-27T00:00:00Z", commitSha: "c".repeat(40), runId: "33333333-3333-4333-8333-333333333333", environmentId: "release-windows-server-2022" };
    await writeFile(f.pnpm, '{"dependencies":{"react":{"version":"1.0.0","version":"2.0.0"}}}\n');
    await assert.rejects(() => buildSbom(base), /duplicate property/);
    await writeFile(f.pnpm, JSON.stringify({ dependencies: { react: { version: "1.0.0", path: "C:/unsafe" } } }));
    await assert.rejects(() => buildSbom(base), /unsafe/);
    await writeFile(f.pnpm, JSON.stringify({ packages: { "sql-observer-web": { version: "0.1.0", dependencies: { a: "1.0.0" } }, a: { version: "1.0.0", dependencies: { b: "1.0.0" } }, b: { version: "1.0.0", dependencies: { a: "1.0.0" } } } }));
    await assert.rejects(() => buildSbom(base), /cycle/);
    await writeFile(f.pnpm, JSON.stringify({ packages: { "sql-observer-web": { version: "0.1.0", dependencies: { a: "1.0.0" } }, a: { version: "1.0.0", dependencies: { a: "1.0.0" } } } }));
    await assert.rejects(() => buildSbom(base), /self-edge|cycle/);
    await writeFile(f.pnpm, JSON.stringify({ packages: { react: { version: "19.2.8", dependencies: {} } } }));
    await assert.rejects(() => buildSbom(base), /workspace package/);
  } finally { await rm(f.temp, { recursive: true, force: true }); }
});

test("generator enforces byte bounds and fatal UTF-8 decoding", async () => {
  const f = await fixture();
  try {
    const base = { root: repositoryRoot, inputManifest: path.join(repositoryRoot, "release/certification/m12-sbom-inputs.v1.json"), deps: f.depsPaths, pnpmList: f.pnpm, webCatalog: f.catalogPath, timestamp: "2026-08-27T00:00:00Z", commitSha: "d".repeat(40), runId: "44444444-4444-4444-8444-444444444444", environmentId: "release-windows-server-2025" };
    const oversized = Buffer.concat([Buffer.from('{"dependencies":{"react":{"version":"1.0.0"}}}'), Buffer.alloc(LIMITS.jsonBytes, 0x20), Buffer.from("é")]);
    await writeFile(f.pnpm, oversized);
    await assert.rejects(() => buildSbom(base), /byte bound/);
    await writeFile(f.pnpm, Buffer.from([0x7b, 0x22, 0x64, 0x22, 0x3a, 0x22, 0xc3, 0x28, 0x22, 0x7d]));
    await assert.rejects(() => buildSbom(base), /UTF-8/);
  } finally { await rm(f.temp, { recursive: true, force: true }); }
});
