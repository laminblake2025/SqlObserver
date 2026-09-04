import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { link, mkdtemp, mkdir, readFile, rm, symlink, writeFile } from "node:fs/promises";
import { execFile } from "node:child_process";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { promisify } from "node:util";
import { buildLicenseEvidence, jsonBytes, LIMITS } from "../../tools/generate-m12-license-evidence.mjs";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

const commit = "a".repeat(40);
const runId = "11111111-1111-4111-8111-111111111111";
const environmentId = "release-windows-server-2022";
const execFileAsync = promisify(execFile);
function sbom() {
  const components = [
    { type: "application", "bom-ref": `pkg:generic/sqlobserver.collector@${commit}`, name: "SqlObserver.Collector", version: commit, purl: `pkg:generic/sqlobserver.collector@${commit}` },
    { type: "application", "bom-ref": `pkg:generic/sqlobserver.mcpstdio@${commit}`, name: "SqlObserver.McpStdio", version: commit, purl: `pkg:generic/sqlobserver.mcpstdio@${commit}` },
    { type: "application", "bom-ref": `pkg:generic/sqlobserver.server@${commit}`, name: "SqlObserver.Server", version: commit, purl: `pkg:generic/sqlobserver.server@${commit}` },
    { type: "application", "bom-ref": `pkg:generic/sqlobserver.web@${commit}`, name: "SqlObserver.Web", version: commit, purl: `pkg:generic/sqlobserver.web@${commit}` },
    { type: "library", "bom-ref": "pkg:npm/react@19.2.8", name: "react", version: "19.2.8", purl: "pkg:npm/react@19.2.8" },
    { type: "library", "bom-ref": "pkg:nuget/serilog@3.0.0", name: "Serilog", version: "3.0.0", purl: "pkg:nuget/serilog@3.0.0" },
  ];
  const root = `pkg:generic/sqlobserver@${commit}`;
  const dependencies = [{ ref: root, dependsOn: components.filter((x) => x.type === "application").map((x) => x["bom-ref"]).sort() }, ...components.map((x) => ({ ref: x["bom-ref"], dependsOn: [] }))].sort((a, b) => a.ref < b.ref ? -1 : a.ref > b.ref ? 1 : 0);
  return { bomFormat: "CycloneDX", specVersion: "1.7", serialNumber: "urn:uuid:00000000-0000-4000-8000-000000000000", version: 1, metadata: { timestamp: "2026-08-28T00:00:00Z", component: { type: "application", "bom-ref": root, name: "SqlObserver", version: commit, purl: root }, properties: [{ name: "commitSha", value: commit }, { name: "runId", value: runId }, { name: "environmentId", value: environmentId }, { name: "identity.kind", value: "git-commit" }] }, components, dependencies };
}
async function fixture() {
  const root = await mkdtemp(path.join(os.tmpdir(), "m12-license-generator-"));
  const nuget = path.join(root, "nuget"), npm = path.join(root, "node_modules"), packageDir = path.join(nuget, "serilog", "3.0.0"), npmDir = path.join(npm, "react");
  await mkdir(packageDir, { recursive: true }); await mkdir(npmDir, { recursive: true });
  await writeFile(path.join(packageDir, "LICENSE.txt"), "MIT\n"); await writeFile(path.join(packageDir, "serilog.nuspec"), "<package><metadata><id>serilog</id><version>3.0.0</version><license type=\"expression\">MIT</license></metadata></package>\n");
  await writeFile(path.join(npmDir, "package.json"), JSON.stringify({ name: "react", version: "19.2.8", license: "MIT" }) + "\n");
  return { root, nuget, npm };
}
function options(f, extra = {}) { return { sbom: sbom(), nugetRoot: f.nuget, npmRoot: f.npm, commitSha: commit, runId, environmentId, ...extra }; }
async function assertLicenseEvidenceSchema(value) {
  const schema = JSON.parse(await readFile(path.join(repositoryRoot, "release/certification/m12-license-evidence.v1.schema.json"), "utf8"));
  assert.deepEqual(Object.keys(value).sort(), [...schema.required].sort());
  const component = schema.$defs.component; const source = component.properties.source;
  for (const item of value.components) { assert.deepEqual(Object.keys(item).sort(), [...component.required].sort()); assert.deepEqual(Object.keys(item.source).sort(), [...source.required].sort()); assert.equal(item.source.bytes, undefined); }
}

test("license evidence is deterministic and bijective over NuGet/npm SBOM components", async () => {
  const f = await fixture();
  try { const first = await buildLicenseEvidence(options(f)); const second = await buildLicenseEvidence(options(f)); await assertLicenseEvidenceSchema(first.evidence); assert.deepEqual(first.bytes, second.bytes); assert.equal(Object.keys(JSON.parse(first.bytes)).join("|"), "$schema|caseId|commitSha|components|environmentId|kind|producerId|result|runId|sbomSha256|sbomSize|schemaVersion"); assert.deepEqual(first.evidence.components.map((x) => x.bomRef), ["pkg:npm/react@19.2.8", "pkg:nuget/serilog@3.0.0"]); assert.equal(first.evidence.components[0].source.path, "package.json"); assert.equal(first.evidence.components[1].source.path, "serilog.nuspec"); }
  finally { await rm(f.root, { recursive: true, force: true }); }
});

test("license evidence can bind a published SBOM from a distinct run", async () => {
  const f = await fixture();
  try {
    const licenseRunId = "22222222-2222-4222-8222-222222222222";
    const generated = await buildLicenseEvidence(options(f, { runId: licenseRunId, sbomRunId: runId }));
    assert.equal(generated.evidence.runId, licenseRunId);
    assert.equal(generated.evidence.sbomSha256, createHash("sha256").update(jsonBytes(sbom())).digest("hex"));
    await assert.rejects(() => buildLicenseEvidence(options(f, { runId: licenseRunId })), /SBOM metadata properties are invalid/);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("NuGet SPDX expressions are bound to nuspec bytes without requiring a license file", async () => {
  const f = await fixture();
  try {
    await rm(path.join(f.nuget, "serilog", "3.0.0", "LICENSE.txt"));
    const nuspecPath = path.join(f.nuget, "serilog", "3.0.0", "serilog.nuspec");
    const nuspecBytes = await readFile(nuspecPath);
    const generated = await buildLicenseEvidence(options(f));
    const component = generated.evidence.components.find((item) => item.bomRef === "pkg:nuget/serilog@3.0.0");
    assert.equal(component.spdxId, "MIT");
    assert.deepEqual(component.source, { path: "serilog.nuspec", sha256: createHash("sha256").update(nuspecBytes).digest("hex") });
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("license generator permits reviewed security package identities and rejects sensitive lookalikes", async () => {
  const f = await fixture();
  try {
    const actual = sbom();
    const packages = [
      ["Microsoft.Extensions.Configuration.UserSecrets", "10.0.11"],
      ["Microsoft.IdentityModel.JsonWebTokens", "8.16.0"],
      ["Microsoft.IdentityModel.Tokens", "8.16.0"],
      ["System.IdentityModel.Tokens.Jwt", "8.16.0"],
    ];
    for (const [name, version] of packages) {
      const ref = `pkg:nuget/${name.toLowerCase()}@${version}`;
      actual.components.push({ type: "library", "bom-ref": ref, name, version, purl: ref });
      actual.dependencies.push({ ref, dependsOn: [] });
      const packageDir = path.join(f.nuget, name.toLowerCase(), version); await mkdir(packageDir, { recursive: true });
      await writeFile(path.join(packageDir, "LICENSE.txt"), "MIT\n");
      await writeFile(path.join(packageDir, `${name}.nuspec`), `<package><metadata><id>${name}</id><version>${version}</version><license type="expression">MIT</license></metadata></package>\n`);
    }
    actual.components.sort((a, b) => a["bom-ref"] < b["bom-ref"] ? -1 : a["bom-ref"] > b["bom-ref"] ? 1 : 0);
    actual.dependencies.sort((a, b) => a.ref < b.ref ? -1 : a.ref > b.ref ? 1 : 0);
    const generated = await buildLicenseEvidence(options(f, { sbom: actual }));
    for (const [name, version] of packages) assert.ok(generated.evidence.components.some((component) => component.bomRef === `pkg:nuget/${name.toLowerCase()}@${version}`));

    const unsafe = sbom(); const ref = "pkg:nuget/contoso.password@1.0.0";
    unsafe.components.push({ type: "library", "bom-ref": ref, name: "Contoso.Password", version: "1.0.0", purl: ref });
    unsafe.dependencies.push({ ref, dependsOn: [] });
    unsafe.components.sort((a, b) => a["bom-ref"] < b["bom-ref"] ? -1 : a["bom-ref"] > b["bom-ref"] ? 1 : 0);
    unsafe.dependencies.sort((a, b) => a.ref < b.ref ? -1 : a.ref > b.ref ? 1 : 0);
    await assert.rejects(() => buildLicenseEvidence(options(f, { sbom: unsafe })), /SBOM component name is unsafe/);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("license generator accepts unchanged real peer-suffixed react-dom package metadata", async () => {
  const f = await fixture();
  try {
    const source = path.join(repositoryRoot, "web/node_modules/.pnpm/react-dom@19.2.8_react@19.2.8/node_modules/react-dom/package.json");
    const bytes = await readFile(source);
    assert.equal(bytes.at(-1), 125, "the regression fixture must remain an unchanged npm package.json ending in } ");
    const target = path.join(f.npm, "react-dom"); await mkdir(target, { recursive: true }); await writeFile(path.join(target, "package.json"), bytes);
    const actual = structuredClone(sbom());
    const oldRef = actual.components[4]["bom-ref"]; const newRef = "pkg:npm/react-dom@19.2.8";
    actual.components[4] = { type: "library", "bom-ref": newRef, name: "react-dom", version: "19.2.8", purl: newRef };
    actual.dependencies = actual.dependencies.map((edge) => edge.ref === oldRef ? { ...edge, ref: newRef } : { ...edge, dependsOn: edge.dependsOn.map((ref) => ref === oldRef ? newRef : ref) });
    const generated = await buildLicenseEvidence(options(f, { sbom: actual }));
    assert.equal(generated.evidence.components.find((item) => item.bomRef === newRef).source.path, "package.json");
    assert.equal(generated.evidence.components.find((item) => item.bomRef === newRef).source.sha256, createHash("sha256").update(bytes).digest("hex"));
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("production PowerShell assertion accepts generated canonical evidence and rejects reordered evidence", async () => {
  const f = await fixture();
  try {
    const licenseRunId = "22222222-2222-4222-8222-222222222222"; const validSbom = sbom(); const sbomPath = path.join(f.root, "sbom.json"); const evidencePath = path.join(f.root, "evidence.json"); const reorderedPath = path.join(f.root, "reordered-evidence.json");
    await writeFile(sbomPath, jsonBytes(validSbom)); const generated = await buildLicenseEvidence(options(f, { sbom: validSbom, runId: licenseRunId, sbomRunId: runId })); await writeFile(evidencePath, generated.bytes);
    const reordered = {}; for (const key of ["caseId", "$schema", "commitSha", "components", "environmentId", "kind", "producerId", "result", "runId", "sbomSha256", "sbomSize", "schemaVersion"]) reordered[key] = JSON.parse(generated.bytes)[key]; await writeFile(reorderedPath, JSON.stringify(reordered) + "\n");
    const command = ". $env:M12_PRODUCER -RepositoryRoot $env:M12_ROOT -FunctionProbe\n$license=Read-M12LockedBytes $env:M12_EVIDENCE $env:M12_ROOT 4194304\n$sbom=Read-M12LockedBytes $env:M12_SBOM $env:M12_ROOT 4194304\n[void](Assert-M12LicenseEvidence ([pscustomobject]@{Bytes=$license.Bytes;Hash=$license.Hash}) $env:M12_ROOT $env:M12_SBOM $env:M12_COMMIT $env:M12_RUN $env:M12_ENV ([pscustomobject]@{Bytes=$sbom.Bytes;Hash=$sbom.Hash}) $env:M12_NUGET $env:M12_NPM $env:M12_SBOM_RUN)";
    const pwsh = process.env.SQLOBSERVER_M12_TRUSTED_PWSH_PATH ?? "pwsh"; const env = { ...process.env, M12_PRODUCER: path.join(repositoryRoot, "tools/run-m12-supply-chain-certification.ps1"), M12_ROOT: f.root, M12_EVIDENCE: evidencePath, M12_SBOM: sbomPath, M12_COMMIT: commit, M12_RUN: licenseRunId, M12_SBOM_RUN: runId, M12_ENV: environmentId, M12_NUGET: f.nuget, M12_NPM: f.npm };
    await execFileAsync(pwsh, ["-NoProfile", "-NonInteractive", "-Command", command], { windowsHide: true, env });
    await assert.rejects(() => execFileAsync(pwsh, ["-NoProfile", "-NonInteractive", "-Command", command], { windowsHide: true, env: { ...env, M12_EVIDENCE: reorderedPath } }));
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("license generator rejects missing, unknown, URL-only, and ambiguous licenses", async () => {
  const f = await fixture();
  try {
    await writeFile(path.join(f.npm, "react", "package.json"), JSON.stringify({ license: "https://example.invalid/license" }) + "\n"); await assert.rejects(() => buildLicenseEvidence(options(f)), /missing, unknown, URL-only, or ambiguous|identity does not match/);
    await writeFile(path.join(f.npm, "react", "package.json"), JSON.stringify({ licenses: ["MIT", "Apache-2.0"] }) + "\n"); await assert.rejects(() => buildLicenseEvidence(options(f)), /ambiguous|identity does not match/);
    await writeFile(path.join(f.npm, "react", "package.json"), JSON.stringify({ license: "GPL-3.0" }) + "\n"); await assert.rejects(() => buildLicenseEvidence(options(f)), /missing, unknown|identity does not match/);
    await writeFile(path.join(f.npm, "react", "package.json"), JSON.stringify({ name: "react", version: "19.2.8", license: { type: "MIT", expression: "GPL-3.0" } }) + "\n"); await assert.rejects(() => buildLicenseEvidence(options(f)), /missing, unknown|conflicting or ambiguous/);
    await assert.rejects(() => buildLicenseEvidence(options(f, { licenseMap: { "pkg:nuget/serilog@3.0.0": { spdxId: { type: "MIT", id: "GPL-3.0" }, path: "LICENSE.txt" } } })), /missing, unknown|conflicting or ambiguous/);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("license generator binds npm package metadata identity to the SBOM purl", async () => {
  const f = await fixture();
  try {
    const mismatched = sbom(); mismatched.components[4] = { type: "library", "bom-ref": "pkg:npm/react@999.9.9", name: "react", version: "999.9.9", purl: "pkg:npm/react@999.9.9" }; mismatched.dependencies = mismatched.dependencies.map((edge) => edge.ref === "pkg:npm/react@19.2.8" ? { ...edge, ref: "pkg:npm/react@999.9.9" } : { ...edge, dependsOn: edge.dependsOn.map((ref) => ref === "pkg:npm/react@19.2.8" ? "pkg:npm/react@999.9.9" : ref) });
    await assert.rejects(() => buildLicenseEvidence(options(f, { sbom: mismatched })), /identity does not match SBOM purl/);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("license generator rejects NuGet traversal and encoded path variants", async () => {
  const f = await fixture();
  try {
    for (const purl of ["pkg:nuget/serilog@3.0/..", "pkg:nuget/serilog%2f..@3.0.0", "pkg:nuget/serilog%5c..@3.0.0", "pkg:nuget/serilog@%2e%2e"]) {
      const unsafe = sbom(); unsafe.components[5] = { type: "library", "bom-ref": purl, name: "serilog", version: purl.slice(purl.lastIndexOf("@") + 1), purl };
      await assert.rejects(() => buildLicenseEvidence(options(f, { sbom: unsafe })), /unsafe|malformed/);
    }
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("license generator rejects Windows ADS on package files and directories", { skip: process.platform !== "win32" }, async () => {
  const f = await fixture(); const licenseAds = path.join(f.nuget, "serilog", "3.0.0", "serilog.nuspec:alternate"); const directoryAds = path.join(f.nuget, "serilog", "3.0.0:alternate");
  const makeAds = async (target) => execFileAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "Set-Content -LiteralPath ($env:M12_ADS_TARGET + ':alternate') -Value 'blocked'"], { windowsHide: true, env: { ...process.env, M12_ADS_TARGET: target } });
  try { await makeAds(path.join(f.nuget, "serilog", "3.0.0", "serilog.nuspec")); await assert.rejects(() => buildLicenseEvidence(options(f)), /alternate data stream|missing|regular file/); await rm(licenseAds, { force: true }); await makeAds(path.join(f.nuget, "serilog", "3.0.0")); await assert.rejects(() => buildLicenseEvidence(options(f)), /alternate data stream|missing|regular file/); }
  finally { await rm(licenseAds, { force: true }); await rm(directoryAds, { force: true }); await rm(f.root, { recursive: true, force: true }); }
});

test("license generator rejects a first-party-only SBOM", async () => {
  const f = await fixture();
  try { const onlyFirstParty = sbom(); onlyFirstParty.components = onlyFirstParty.components.filter((component) => component.type === "application"); onlyFirstParty.dependencies = onlyFirstParty.dependencies.filter((edge) => edge.ref === `pkg:generic/sqlobserver@${commit}` || onlyFirstParty.components.some((component) => component["bom-ref"] === edge.ref)); await assert.rejects(() => buildLicenseEvidence(options(f, { sbom: onlyFirstParty })), /no third-party components|bijective/); }
  finally { await rm(f.root, { recursive: true, force: true }); }
});

test("license generator rejects hardlinked authority, override, and package-manager roots", { skip: process.platform !== "win32" }, async () => {
  const f = await fixture(); const sbomPath = path.join(f.root, "sbom.json"); const sbomHardlink = path.join(f.root, "sbom-hardlink.json"); const mapPath = path.join(f.root, "map.json"); const mapHardlink = path.join(f.root, "map-hardlink.json"); const override = path.join(f.nuget, "serilog", "3.0.0", "COPYING.txt"); const nugetJunction = path.join(f.root, "nuget-junction"); const npmJunction = path.join(f.root, "npm-junction");
  try {
    await writeFile(sbomPath, JSON.stringify(sbom()) + "\n"); await link(sbomPath, sbomHardlink); await assert.rejects(() => buildLicenseEvidence(options(f, { sbom: undefined, sbomPath: sbomHardlink })), /multiple hard links/);
    await writeFile(mapPath, "{}\n"); await link(mapPath, mapHardlink); await assert.rejects(() => buildLicenseEvidence(options(f, { licenseMap: undefined, licenseMapPath: mapHardlink })), /multiple hard links/);
    await link(path.join(f.nuget, "serilog", "3.0.0", "LICENSE.txt"), override); await assert.rejects(() => buildLicenseEvidence(options(f, { licenseMap: { "pkg:nuget/serilog@3.0.0": { license: "MIT", path: "COPYING.txt" } } })), /multiple hard links/); await rm(override, { force: true });
    await symlink(f.nuget, nugetJunction, "junction"); await assert.rejects(() => buildLicenseEvidence(options(f, { nugetRoot: nugetJunction })), /reparse point/);
    await symlink(f.npm, npmJunction, "junction"); await assert.rejects(() => buildLicenseEvidence(options(f, { npmRoot: npmJunction })), /reparse point/);
  } finally { await rm(sbomHardlink, { force: true }); await rm(mapHardlink, { force: true }); await rm(override, { force: true }); await rm(nugetJunction, { recursive: true, force: true }); await rm(npmJunction, { recursive: true, force: true }); await rm(f.root, { recursive: true, force: true }); }
});

test("license generator rejects duplicate keys, invalid UTF-8, traversal, and oversized input", async () => {
  const f = await fixture();
  try {
    const sbomPath = path.join(f.root, "sbom.json"); await writeFile(sbomPath, Buffer.from('{"bomFormat":"CycloneDX","bomFormat":"CycloneDX"}\n')); await assert.rejects(() => buildLicenseEvidence(options(f, { sbomPath, sbom: undefined })), /duplicate property/);
    await writeFile(sbomPath, Buffer.from([0x7b, 0x22, 0x78, 0x22, 0x3a, 0xc3, 0x28, 0x7d, 0x0a])); await assert.rejects(() => buildLicenseEvidence(options(f, { sbomPath, sbom: undefined })), /UTF-8/);
    await writeFile(path.join(f.npm, "react", "package.json"), JSON.stringify({ license: "MIT" }) + "\n"); await assert.rejects(() => buildLicenseEvidence(options(f, { licenseMap: { "pkg:npm/react@19.2.8": { license: "MIT", path: "../escape", sha256: "a".repeat(64) } } })), /package-relative|identity does not match/);
    await writeFile(sbomPath, Buffer.concat([Buffer.from('{"x":"'), Buffer.alloc(LIMITS.jsonBytes, 0x20), Buffer.from('"}\n')])); await assert.rejects(() => buildLicenseEvidence(options(f, { sbomPath, sbom: undefined })), /byte bound/);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("license generator applies the closed SBOM contract equally to path and object inputs", async () => {
  const f = await fixture();
  try {
    const valid = sbom();
    const objectResult = await buildLicenseEvidence(options(f, { sbom: valid }));
    const canonicalPath = path.join(f.root, "canonical-sbom.json"); await writeFile(canonicalPath, jsonBytes(valid));
    const pathResult = await buildLicenseEvidence(options(f, { sbom: undefined, sbomPath: canonicalPath }));
    assert.deepEqual(pathResult.bytes, objectResult.bytes);
    const noncanonicalPath = path.join(f.root, "noncanonical-sbom.json"); await writeFile(noncanonicalPath, JSON.stringify(valid) + "\n");
    await assert.rejects(() => buildLicenseEvidence(options(f, { sbom: undefined, sbomPath: noncanonicalPath })), /canonical/);
    const malformed = sbom(); delete malformed.serialNumber;
    await assert.rejects(() => buildLicenseEvidence(options(f, { sbom: malformed })), /SBOM shape is invalid/);
    const sbomPath = path.join(f.root, "malformed-sbom.json"); await writeFile(sbomPath, JSON.stringify(malformed) + "\n");
    await assert.rejects(() => buildLicenseEvidence(options(f, { sbom: undefined, sbomPath })), /SBOM shape is invalid/);
    const extra = sbom(); extra.metadata.extra = true; await assert.rejects(() => buildLicenseEvidence(options(f, { sbom: extra })), /SBOM metadata shape is invalid/);
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("license generator accepts canonical scoped npm purls and rejects malformed NuGet XML", async () => {
  const f = await fixture();
  try {
    const scopedDir = path.join(f.npm, "@scope", "pkg"); await mkdir(scopedDir, { recursive: true }); await writeFile(path.join(scopedDir, "package.json"), JSON.stringify({ name: "@scope/pkg", version: "1.0.0", license: "MIT" }) + "\n");
    const scoped = sbom(); scoped.components.push({ type: "library", "bom-ref": "pkg:npm/%40scope/pkg@1.0.0", name: "@scope/pkg", version: "1.0.0", purl: "pkg:npm/%40scope/pkg@1.0.0" }); scoped.components.sort((a, b) => a["bom-ref"] < b["bom-ref"] ? -1 : a["bom-ref"] > b["bom-ref"] ? 1 : 0); scoped.dependencies.push({ ref: "pkg:npm/%40scope/pkg@1.0.0", dependsOn: [] }); scoped.dependencies.sort((a, b) => a.ref < b.ref ? -1 : a.ref > b.ref ? 1 : 0); const result = await buildLicenseEvidence(options(f, { sbom: scoped })); assert.ok(result.evidence.components.some((component) => component.bomRef === "pkg:npm/%40scope/pkg@1.0.0"));
    for (const xml of ["<package xmlns='urn:evil'><metadata><id>serilog</id><version>3.0.0</version><license type='expression'>MIT</license></metadata></package>\n", "<package><metadata><id><x>serilog</x></id><version>3.0.0</version><license type='expression'>MIT</license></metadata></package>\n", "<package><!--x--><metadata><id>serilog</id><version>3.0.0</version><license type='expression'>MIT</license></metadata></package>\n"]) { await writeFile(path.join(f.nuget, "serilog", "3.0.0", "serilog.nuspec"), xml); await assert.rejects(() => buildLicenseEvidence(options(f)), /NuGet metadata is malformed|NuGet metadata/); }
  } finally { await rm(f.root, { recursive: true, force: true }); }
});

test("JSON output uses canonical LF bytes", () => { assert.equal(jsonBytes({ z: 1, a: true }).toString("utf8"), '{"a":true,"z":1}\n'); });
