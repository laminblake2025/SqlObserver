/* Deterministic, closed-input M12 provenance evidence generator. */
import { createHash } from "node:crypto";
import { lstat, open, readFile, realpath } from "node:fs/promises";
import path from "node:path";

export const LIMITS = Object.freeze({ jsonBytes: 4194304, manifestFiles: 40, products: 4, stringLength: 512, depth: 32 });
const COMMIT = /^[0-9a-f]{40}$/u;
const RUN_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u;
const UTC = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}Z$/u;
const ENVIRONMENTS = new Set(["release-windows-server-2022", "release-windows-server-2025"]);
const ROOTS = ["src/SqlObserver.Server/SqlObserver.Server.csproj", "src/SqlObserver.Collector/SqlObserver.Collector.csproj", "src/SqlObserver.McpStdio/SqlObserver.McpStdio.csproj"];
const PRODUCTS = new Map([
  ["sqlobserver-server", { name: "SqlObserver.Server", entryPoint: "SqlObserver.Server", root: "publish-0", file: "SqlObserver.Server.exe" }],
  ["sqlobserver-collector", { name: "SqlObserver.Collector", entryPoint: "SqlObserver.Collector", root: "publish-1", file: "SqlObserver.Collector.exe" }],
  ["sqlobserver-mcp-stdio", { name: "SqlObserver.McpStdio", entryPoint: "SqlObserver.McpStdio", root: "publish-2", file: "SqlObserver.McpStdio.exe" }],
  ["sqlobserver-web", { name: "SqlObserver.Web", entryPoint: "index.html", root: "web/dist", file: "index.html" }],
]);
const sha256 = (bytes) => createHash("sha256").update(bytes).digest("hex");
function fail(message) { throw new Error(`m12-provenance: ${message}`); }
function object(value, label) { if (value === null || typeof value !== "object" || Array.isArray(value)) fail(`${label} must be an object`); return value; }
function exactKeys(value, expected, label) { const actual = Object.keys(value); if (actual.length !== expected.length || actual.slice().sort().join("|") !== expected.slice().sort().join("|")) fail(`${label} shape is invalid`); }
function safeString(value, label) { if (typeof value !== "string" || value.length < 1 || value.length > LIMITS.stringLength || /[\u0000-\u001f\u007f]/u.test(value)) fail(`${label} is invalid`); return value; }
function safeRelative(value, label) {
  safeString(value, label);
  if (value.startsWith("/") || value.startsWith("\\") || value.includes("\\") || value.includes(":")) fail(`${label} is unsafe`);
  const parts = value.split("/"); if (parts.some((part) => part === "" || part === "." || part === ".." || part.startsWith("."))) fail(`${label} is unsafe`);
  if (path.posix.normalize(value) !== value) fail(`${label} is unsafe`); return value;
}
function stable(value, depth = 0) {
  if (depth > LIMITS.depth) fail("JSON depth exceeds bound");
  if (Array.isArray(value)) return `[${value.map((item) => stable(item, depth + 1)).join(",")}]`;
  if (value !== null && typeof value === "object") return `{${Object.keys(value).sort((a, b) => a < b ? -1 : a > b ? 1 : 0).map((key) => `${JSON.stringify(key)}:${stable(value[key], depth + 1)}`).join(",")}}`;
  return JSON.stringify(value);
}
export function jsonBytes(value) { return Buffer.from(`${stable(value)}\n`, "utf8"); }
function assertClosedText(text, label) {
  let i = 0;
  const quoted = () => { const start = i++; while (i < text.length) { const c = text[i++]; if (c === "\\") { if (i >= text.length) fail(`${label} is malformed JSON`); i++; } else if (c === '"') return JSON.parse(text.slice(start, i)); } fail(`${label} has an unterminated string`); };
  const ws = () => { while (i < text.length && /\s/u.test(text[i])) i++; };
  const parse = (depth) => {
    if (depth > LIMITS.depth) fail(`${label} exceeds depth bound`); ws(); const c = text[i];
    if (c === "{") { i++; ws(); const keys = new Set(); if (text[i] === "}") { i++; return; } while (true) { ws(); if (text[i] !== '"') fail(`${label} has an invalid key`); const key = quoted(); if (keys.has(key)) fail(`${label} has a duplicate property`); keys.add(key); ws(); if (text[i++] !== ":") fail(`${label} is malformed JSON`); parse(depth + 1); ws(); if (text[i] === "}") { i++; return; } if (text[i++] !== ",") fail(`${label} is malformed JSON`); } }
    if (c === "[") { i++; ws(); if (text[i] === "]") { i++; return; } while (true) { parse(depth + 1); ws(); if (text[i] === "]") { i++; return; } if (text[i++] !== ",") fail(`${label} is malformed JSON`); } }
    if (c === '"') { quoted(); return; }
    const start = i; while (i < text.length && !/[\s,\]}]/u.test(text[i])) i++; if (i === start) fail(`${label} is malformed JSON`); try { JSON.parse(text.slice(start, i)); } catch { fail(`${label} is malformed JSON`); }
  };
  parse(0); ws(); if (i !== text.length) fail(`${label} has trailing data`);
}
export function parseClosed(bytes, label = "JSON") {
  if (!Buffer.isBuffer(bytes) || bytes.length < 2 || bytes.length > LIMITS.jsonBytes || bytes.at(-1) !== 10 || bytes.includes(13)) fail(`${label} is not closed`);
  let text; try { text = new TextDecoder("utf-8", { fatal: true }).decode(bytes); } catch { fail(`${label} is not valid UTF-8`); }
  assertClosedText(text.slice(0, -1), label); try { return JSON.parse(text); } catch { fail(`${label} is malformed JSON`); }
}
function identity(info) { return { dev: info.dev, ino: info.ino, nlink: info.nlink, size: info.size, mtimeNs: info.mtimeNs?.toString() ?? `${info.mtimeMs}` }; }
function sameIdentity(a, b) { return a.dev === b.dev && a.ino === b.ino && a.nlink === b.nlink && a.size === b.size && a.mtimeNs === b.mtimeNs; }
function sameLstat(a, b) {
  return a.dev === b.dev && a.ino === b.ino && a.nlink === b.nlink &&
    a.size === b.size && (a.mtimeNs?.toString() ?? `${a.mtimeMs}`) === (b.mtimeNs?.toString() ?? `${b.mtimeMs}`);
}
async function assertCanonicalRegular(file, label, root) {
  const stat = await lstat(file, { bigint: true });
  if (!stat.isFile() || stat.isSymbolicLink() || stat.nlink !== 1n || stat.size < 1n || stat.size > BigInt(LIMITS.jsonBytes)) fail(`${label} is not a fresh regular file`);
  const canonicalRoot = await realpath(root);
  const canonicalFile = await realpath(file);
  const relative = path.relative(canonicalRoot, canonicalFile);
  if (!relative || relative.startsWith(".." + path.sep) || path.isAbsolute(relative)) fail(`${label} is outside root`);
  // A colon after the drive prefix is an NTFS alternate-data-stream path.
  if (process.platform === "win32" && /:[^/\\]+$/u.test(canonicalFile.slice(3))) fail(`${label} uses an alternate data stream`);
  return { stat, canonicalFile };
}
async function readRegular(file, label, maximum = LIMITS.jsonBytes, root = process.cwd()) {
  let handle;
  try {
    const initial = await assertCanonicalRegular(file, label, root);
    if (initial.stat.size > BigInt(maximum)) fail(`${label} exceeds size bound`);
    handle = await open(file, "r"); const before = await handle.stat({ bigint: true });
    if (!before.isFile() || before.nlink !== 1n || before.size < 1n || before.size > BigInt(maximum) || !sameLstat(initial.stat, before)) fail(`${label} changed during open`);
    const info = identity(before); const bytes = Buffer.alloc(Number(before.size)); let offset = 0;
    while (offset < bytes.length) { const result = await handle.read(bytes, offset, bytes.length - offset, offset); if (result.bytesRead < 1) fail(`${label} changed during read`); offset += result.bytesRead; }
    const after = await handle.stat({ bigint: true }); const final = await assertCanonicalRegular(file, label, root);
    if (!sameIdentity(info, identity(after)) || after.nlink !== 1n || !sameLstat(initial.stat, final.stat) || final.canonicalFile !== initial.canonicalFile) fail(`${label} changed during read`);
    return { bytes, hash: sha256(bytes), identity: info, path: path.resolve(file), canonicalPath: initial.canonicalFile };
  } catch (error) { if (error?.message?.startsWith("m12-provenance:")) throw error; fail(`${label} is unreadable`); } finally { if (handle) await handle.close(); }
}
function within(root, candidate, label) { const rootFull = path.resolve(root); const full = path.resolve(rootFull, candidate); const relative = path.relative(rootFull, full); if (!relative || relative.startsWith(".." + path.sep) || path.isAbsolute(relative)) fail(`${label} is outside root`); return full; }
async function readRootFile(root, relative, label, maximum = LIMITS.jsonBytes) { safeRelative(relative, label); return readRegular(within(root, path.join(root, ...relative.split("/")), label), label, maximum, root); }
function validateManifestShape(value) {
  object(value, "input manifest"); exactKeys(value, ["$schema", "schemaVersion", "manifestId", "roots", "files"], "input manifest");
  if (typeof value.$schema !== "string" || value.$schema !== "m12-sbom-inputs.v1.schema.json" || typeof value.schemaVersion !== "number" || !Number.isInteger(value.schemaVersion) || value.schemaVersion !== 1 || typeof value.manifestId !== "string" || value.manifestId !== "m12-sbom-inputs" || !Array.isArray(value.roots) || JSON.stringify(value.roots) !== JSON.stringify(ROOTS) || !Array.isArray(value.files) || value.files.length !== LIMITS.manifestFiles) fail("input manifest is invalid");
  const seen = new Set(), lowered = new Set(); for (const entry of value.files) { object(entry, "manifest entry"); exactKeys(entry, ["path", "sha256"], "manifest entry"); if (typeof entry.path !== "string" || typeof entry.sha256 !== "string") fail("manifest entry is invalid"); const relative = safeRelative(entry.path, "manifest path"); if (!/^[0-9a-f]{64}$/u.test(entry.sha256) || seen.has(relative) || lowered.has(relative.toLowerCase())) fail("manifest entry is invalid"); seen.add(relative); lowered.add(relative.toLowerCase()); }
}
async function expectedManifest(root) {
  const expected = new Set(["Directory.Packages.props", "Directory.Build.props", "global.json", "web/package.json", "web/pnpm-lock.yaml", "web/tools/web-asset-manifest.mjs", "web/contracts/web-asset-manifest.v1.schema.json"]);
  const queue = [...ROOTS], seen = new Set(); while (queue.length) {
    const current = queue.shift(); if (seen.has(current)) continue; seen.add(current); expected.add(current);
    const lock = `${path.posix.dirname(current)}/packages.lock.json`; try { await readRootFile(root, lock, "lock file", LIMITS.jsonBytes); expected.add(lock); } catch (error) { if (!error?.message?.includes("lock file is unreadable")) throw error; }
    const text = (await readRootFile(root, current, "project file")).bytes.toString("utf8"); for (const match of text.matchAll(/<ProjectReference\s+Include=["']([^"']+)["']/giu)) { const next = path.posix.normalize(path.posix.join(path.posix.dirname(current), match[1].replaceAll("\\", "/"))); safeRelative(next, "project reference"); queue.push(next); }
  } return expected;
}
async function validateManifest(value, root) {
  validateManifestShape(value); const expected = await expectedManifest(root); if (expected.size !== LIMITS.manifestFiles) fail("input manifest closure is not exactly 40 files"); const actual = new Set();
  for (const entry of value.files) { if (!expected.has(entry.path)) fail("input manifest contains an extra file"); const file = await readRootFile(root, entry.path, `manifest file ${entry.path}`); if (file.hash !== entry.sha256) fail("input manifest hash mismatch"); actual.add(entry.path); }
  if (actual.size !== expected.size || [...expected].some((item) => !actual.has(item))) fail("input manifest closure is incomplete"); return value;
}
function canonicalPurl(type, name, version) { const kind = safeString(type, "purl type").toLowerCase(); safeString(name, "purl name"); safeString(version, "purl version"); if (name.includes("..") || name.includes("\\") || name.includes(":") || version.includes("..") || version.includes("\\")) fail("SBOM purl is unsafe"); const normalizedName = ["nuget", "npm", "generic"].includes(kind) ? name.toLowerCase() : name; return `pkg:${kind}/${normalizedName.split("/").map(encodeURIComponent).join("/")}@${encodeURIComponent(version)}`; }
function validateSbom(value, commit, runId, environmentId) {
  object(value, "SBOM"); exactKeys(value, ["bomFormat", "components", "dependencies", "metadata", "serialNumber", "specVersion", "version"], "SBOM"); if (value.bomFormat !== "CycloneDX" || value.specVersion !== "1.7" || value.version !== 1 || typeof value.serialNumber !== "string" || !/^urn:uuid:[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u.test(value.serialNumber)) fail("SBOM root is invalid");
  object(value.metadata, "SBOM metadata"); exactKeys(value.metadata, ["component", "properties", "timestamp"], "SBOM metadata"); if (typeof value.metadata.timestamp !== "string" || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?Z$/u.test(value.metadata.timestamp) || !Array.isArray(value.metadata.properties)) fail("SBOM metadata is invalid");
  const root = value.metadata.component; object(root, "SBOM root component"); exactKeys(root, ["bom-ref", "name", "purl", "type", "version"], "SBOM root component"); const rootRef = canonicalPurl("generic", "SqlObserver", commit); if (root.type !== "application" || root.name !== "SqlObserver" || root.version !== commit || root["bom-ref"] !== rootRef || root.purl !== rootRef) fail("SBOM root component is invalid");
  const properties = value.metadata.properties; const propertyNames = ["commitSha", "runId", "environmentId", "identity.kind"]; if (properties.length !== 4 || properties.some((item, i) => !item || Object.keys(item).length !== 2 || item.name !== propertyNames[i] || typeof item.value !== "string")) fail("SBOM metadata properties are invalid"); if (properties[0].value !== commit || properties[1].value !== runId || properties[2].value !== environmentId || properties[3].value !== "git-commit") fail("SBOM metadata binding is invalid");
  if (!Array.isArray(value.components) || !Array.isArray(value.dependencies) || value.components.length < 1 || value.components.length > 4096 || value.dependencies.length > 8192) fail("SBOM arrays are invalid");
  const refs = new Set([rootRef]), components = []; let previous = ""; for (const component of value.components) { object(component, "SBOM component"); exactKeys(component, ["bom-ref", "name", "purl", "type", "version"], "SBOM component"); const match = /^pkg:([^/]+)\//u.exec(component["bom-ref"]); if (!match || (component.type !== "application" && component.type !== "library") || component["bom-ref"] !== component.purl || component["bom-ref"] !== canonicalPurl(match[1], component.name, component.version) || refs.has(component["bom-ref"]) || component["bom-ref"] < previous) fail("SBOM component set is invalid"); if (/(?:https?:\/\/|localhost|127\.0\.0\.1|registry\.npmjs|[A-Za-z]:[\\/])/iu.test(component.purl) || /(?:password|secret|token|credential|private.?key)/iu.test(component.version)) fail("SBOM component is unsafe"); refs.add(component["bom-ref"]); components.push(component); previous = component["bom-ref"]; }
  const apps = components.filter((item) => item.type === "application"); const expectedApps = ["SqlObserver.Collector", "SqlObserver.McpStdio", "SqlObserver.Server", "SqlObserver.Web"]; if (apps.length !== 4 || apps.map((item) => item.name).sort().join("|") !== expectedApps.join("|") || apps.some((item) => item.version !== commit || !item["bom-ref"].startsWith("pkg:generic/"))) fail("SBOM application set is invalid"); if (components.some((item) => item.type !== "application" && item.name.startsWith("SqlObserver.") && item.type !== "library")) fail("SBOM first-party component is invalid");
  const edgeMap = new Map(); previous = ""; for (const edge of value.dependencies) { object(edge, "SBOM dependency"); exactKeys(edge, ["dependsOn", "ref"], "SBOM dependency"); if (!refs.has(edge.ref) || edge.ref < previous || edgeMap.has(edge.ref) || !Array.isArray(edge.dependsOn)) fail("SBOM dependency set is invalid"); let prior = ""; const deps = []; for (const dep of edge.dependsOn) { if (!refs.has(dep) || dep <= prior || deps.includes(dep)) fail("SBOM dependency edge is invalid"); deps.push(dep); prior = dep; } edgeMap.set(edge.ref, deps); previous = edge.ref; }
  if (edgeMap.size !== refs.size || [...refs].some((ref) => !edgeMap.has(ref))) fail("SBOM dependency closure is incomplete"); const appRefs = apps.map((item) => item["bom-ref"]).sort(); const rootDeps = [...edgeMap.get(rootRef)].sort(); if (rootDeps.length !== 4 || rootDeps.join("|") !== appRefs.join("|")) fail("SBOM root dependencies are invalid"); const visiting = new Set(), done = new Set(); const visit = (ref) => { if (visiting.has(ref)) fail("SBOM dependency graph contains a cycle"); if (done.has(ref)) return; visiting.add(ref); for (const dep of edgeMap.get(ref)) visit(dep); visiting.delete(ref); done.add(ref); }; for (const ref of refs) visit(ref); return value;
}
function subjectBase(root, subjectsPath, runId) { const full = within(root, subjectsPath, "subjects manifest"); const raw = path.dirname(full); const pending = path.basename(path.dirname(raw)); if (path.basename(raw) !== "raw" || pending !== `.pending-${runId}` || path.basename(path.dirname(path.dirname(raw))) !== "m12") fail("subjects manifest is not runner-owned"); return { full, raw }; }
async function validateSubjects(value, root, subjectsPath, runId) {
  object(value, "subjects"); exactKeys(value, ["$schema", "schemaVersion", "products"], "subjects"); if (value.$schema !== "m12-provenance-subjects.v1.schema.json" || value.schemaVersion !== 1 || !Array.isArray(value.products) || value.products.length !== LIMITS.products) fail("subjects shape is invalid"); const { raw } = subjectBase(root, subjectsPath, runId); const seenIds = new Set(), seenPaths = new Set(), rows = [];
  for (const product of value.products) { object(product, "product subject"); exactKeys(product, ["productId", "name", "path", "entryPoint", "sha256", "size"], "product subject"); const spec = PRODUCTS.get(product.productId); if (!spec || seenIds.has(product.productId) || product.name !== spec.name || product.entryPoint !== spec.entryPoint) fail("product subject identity is invalid"); const expected = `${spec.root}/${spec.file}`; if (product.path !== expected) fail("product subject path is not the fixed runner path"); safeRelative(product.path, "product path"); if (seenPaths.has(product.path.toLowerCase()) || typeof product.sha256 !== "string" || !/^[0-9a-f]{64}$/u.test(product.sha256) || !Number.isSafeInteger(product.size) || typeof product.size !== "number" || product.size < 1 || product.size > LIMITS.jsonBytes) fail("product subject binding is invalid"); const file = await readRegular(within(raw, path.join(raw, ...product.path.split("/")), `product ${product.productId}`), `product ${product.productId}`, LIMITS.jsonBytes, root); if (file.hash !== product.sha256 || file.bytes.length !== product.size) fail("product subject digest does not match bytes"); seenIds.add(product.productId); seenPaths.add(product.path.toLowerCase()); rows.push({ ...product }); }
  if (seenIds.size !== LIMITS.products) fail("product subject set is incomplete"); return rows.sort((a, b) => a.productId < b.productId ? -1 : a.productId > b.productId ? 1 : 0);
}
export async function buildProvenanceEvidence({ subjects, subjectsPath, sbomPath, inputManifestPath, root = process.cwd(), commitSha, runId, environmentId, generatedAt }) {
  if (subjects !== undefined) fail("subject objects are not accepted; runner manifest is required"); if (!COMMIT.test(commitSha ?? "") || !RUN_ID.test(runId ?? "") || !ENVIRONMENTS.has(environmentId) || !UTC.test(generatedAt ?? "") || Number.isNaN(Date.parse(generatedAt))) fail("build identity is invalid"); const rootFull = path.resolve(root); const { raw } = subjectBase(rootFull, subjectsPath, runId); const resolvedSbom = within(rootFull, sbomPath, "SBOM"); if (path.dirname(resolvedSbom) !== raw) fail("SBOM must be the runner-owned raw snapshot"); const expectedManifest = path.resolve(rootFull, "release/certification/m12-sbom-inputs.v1.json"); const resolvedManifest = within(rootFull, inputManifestPath, "input manifest"); if (resolvedManifest !== expectedManifest) fail("input manifest must be the canonical root file");
  const subjectFile = await readRegular(within(rootFull, subjectsPath, "subjects manifest"), "subjects manifest", LIMITS.jsonBytes, rootFull); const source = parseClosed(subjectFile.bytes, "subjects manifest"); const products = await validateSubjects(source, rootFull, subjectsPath, runId); const sbomFile = await readRegular(resolvedSbom, "SBOM", LIMITS.jsonBytes, rootFull); const sbom = parseClosed(sbomFile.bytes, "SBOM"); validateSbom(sbom, commitSha, runId, environmentId); const manifestFile = await readRegular(resolvedManifest, "input manifest", LIMITS.jsonBytes, rootFull); const manifest = parseClosed(manifestFile.bytes, "input manifest"); await validateManifest(manifest, rootFull);
  const rows = manifest.files.map((entry) => `${entry.path}\0${entry.sha256}\n`); const evidence = { $schema: "m12-provenance-evidence.v1.schema.json", schemaVersion: 1, caseId: "m12-provenance", producerId: "m12-supply-chain-harness", kind: "supply-chain-evidence", result: "passed", environmentId, commitSha, runId, generatedAtUtc: generatedAt, sbomSha256: sbomFile.hash, sbomSize: sbomFile.bytes.length, inputManifestSha256: manifestFile.hash, inputManifestFileCount: manifest.files.length, inputManifestTreeSha256: sha256(Buffer.from(rows.sort().join(""), "utf8")), products }; return { evidence, bytes: jsonBytes(evidence), snapshots: { subjects: subjectFile, sbom: sbomFile, inputManifest: manifestFile, products } };
}
function args(argv) { const out = {}; for (let i = 0; i < argv.length; i += 2) { const key = argv[i]?.replace(/^--/u, "").replaceAll("-", "_"); if (!key || !argv[i + 1] || argv[i + 1].startsWith("--")) fail("invalid arguments"); out[key] = argv[i + 1]; } return out; }
if (import.meta.url === `file://${process.argv[1]?.replaceAll("\\", "/")}` || process.argv[1]?.endsWith("generate-m12-provenance-evidence.mjs")) { try { const a = args(process.argv.slice(2)); for (const required of ["subjects", "sbom", "input_manifest", "commit_sha", "run_id", "environment", "generated_at", "output"]) if (!a[required]) fail(`--${required.replaceAll("_", "-")} is required`); const result = await buildProvenanceEvidence({ subjectsPath: a.subjects, sbomPath: a.sbom, inputManifestPath: a.input_manifest, root: a.root ?? process.cwd(), commitSha: a.commit_sha, runId: a.run_id, environmentId: a.environment, generatedAt: a.generated_at }); const { writeFile } = await import("node:fs/promises"); await writeFile(a.output, result.bytes, { flag: "wx" }); } catch (error) { process.stderr.write(`${error instanceof Error ? error.message : "m12-provenance: failed"}\n`); process.exitCode = 1; } }
