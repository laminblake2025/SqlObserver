/* Deterministic, dependency-free CycloneDX 1.7 subset generator for M12.
   CLI: --root --input-manifest --deps (exactly three) --pnpm-list --web-catalog
   --timestamp --commit-sha --run-id --environment --output. */
import { createHash } from "node:crypto";
import { readFile, lstat } from "node:fs/promises";
import { TextDecoder } from "node:util";
import path from "node:path";

export const LIMITS = Object.freeze({ components: 4096, dependencyNodes: 8192, jsonBytes: 4 * 1024 * 1024, stringLength: 512, depth: 32 });
const HASH = /^[0-9a-f]{64}$/u;
const COMMIT = /^[0-9a-f]{40,64}$/u;
const ENVIRONMENTS = new Set(["release-windows-server-2022", "release-windows-server-2025"]);
const CATALOG_ROLES = new Set(["entry-document", "entry-script", "chunk", "stylesheet", "font", "media", "build-metadata"]);
const FORBIDDEN = /(?:password|secret|token|credential|private.?key|authorization|connection.?string|localhost|127\.0\.0\.1|[A-Za-z]:[\\/]|\\\\|\/Users\/|\/home\/|(?:^|[/:@])(?:[A-Za-z0-9-]+\.)+(?:com|net|org|local|internal|test)(?:$|[/?:]))/iu;
const FORBIDDEN_HOST = /(?:^|[/:@])(?:[A-Za-z0-9-]+\.)+(?:com|net|org|local|internal|test)(?:$|[/?:])/iu;
const SAFE_PACKAGE_IDENTITY = /^[A-Za-z0-9@_.+~/-]+$/u;
const IDENTITY_FIELDS = new Set(["dependencies", "libraries", "packages", "targets", "name", "version", "resolvedVersion", "runtimeTarget"]);
const SAFE_PACKAGE_PATH_FIELDS = new Set(["path", "hashPath"]);
const EMPTY_INPUT_FIELDS = new Set(["signature", "sha512"]);
const SENSITIVE_PACKAGE_TERM = /(?:password|credential|private[\s._-]*key|authorization|connection[\s._-]*string|api[\s._-]*key|access[\s._-]*token|refresh[\s._-]*token|client[\s._-]*secret|secret|bearer|cookie)/iu;
const BENIGN_SECURITY_PACKAGE = /^(?:microsoft\.extensions\.configuration\.usersecrets|microsoft\.identitymodel\.jsonwebtokens|microsoft\.identitymodel\.tokens|system\.identitymodel\.tokens\.jwt)(?:[/.][a-z0-9+_.~-]+)*$/iu;

function fail(message) { throw new Error(`m12-sbom: ${message}`); }
function isSafePackageIdentity(value) { return SAFE_PACKAGE_IDENTITY.test(value) && (value.includes(".") || value.includes("/") || value.startsWith("@")) && !value.split("/").some((part) => !part || part === "." || part === ".." || part.includes(":")) && (!SENSITIVE_PACKAGE_TERM.test(value) || BENIGN_SECURITY_PACKAGE.test(value)); }
function ordinal(a, b) { return a < b ? -1 : a > b ? 1 : 0; }
function sha256(value) { return createHash("sha256").update(value).digest("hex"); }
function stable(value) {
  if (Array.isArray(value)) return `[${value.map(stable).join(",")}]`;
  if (value && typeof value === "object") return `{${Object.keys(value).sort(ordinal).map((key) => `${JSON.stringify(key)}:${stable(value[key])}`).join(",")}}`;
  return JSON.stringify(value);
}
export function jsonBytes(value) { return Buffer.from(`${stable(value)}\n`, "utf8"); }
function object(value, label) { if (!value || typeof value !== "object" || Array.isArray(value)) fail(`${label} must be an object`); return value; }
function string(value, label) { if (typeof value !== "string" || value.length < 1 || value.length > LIMITS.stringLength || /[\u0000-\u001f\u007f]/u.test(value) || FORBIDDEN.test(value)) fail(`${label} is unsafe`); return value; }
function safeRelative(value, label) { string(value, label); if (value.includes("\\") || value.startsWith("/") || value.startsWith("~") || value.split("/").some((part) => !part || part === "." || part === ".." || part.includes(":")) || path.posix.normalize(value) !== value) fail(`${label} is not a safe relative path`); return value; }
function checkDepth(value, depth = 0, field = "", identityContext = false) {
  if (depth > LIMITS.depth) fail("input nesting exceeds depth bound");
  if (typeof value === "string") {
    if (value.length > LIMITS.stringLength || (value.length === 0 && !EMPTY_INPUT_FIELDS.has(field)) || /[\u0000-\u001f\u007f]/u.test(value)) fail("input string is unsafe");
    const identity = identityContext || IDENTITY_FIELDS.has(field);
    const safePackage = (identity || SAFE_PACKAGE_PATH_FIELDS.has(field)) && isSafePackageIdentity(value);
    if ((FORBIDDEN.test(value) || SENSITIVE_PACKAGE_TERM.test(value)) && (!safePackage || FORBIDDEN_HOST.test(value))) fail("input string is unsafe");
  }
  if (Array.isArray(value)) value.forEach((item) => checkDepth(item, depth + 1, field, identityContext));
  else if (value && typeof value === "object") Object.entries(value).forEach(([key, item]) => checkDepth(item, depth + 1, key, typeof item === "string" && (identityContext || IDENTITY_FIELDS.has(key))));
}
function assertNoDuplicateKeys(text, label) {
  let index = 0;
  const whitespace = () => { while (index < text.length && /\s/u.test(text[index])) index += 1; };
  const quoted = () => { const start = index; if (text[index] !== '"') fail(`${label} has malformed JSON`); index += 1; while (index < text.length) { const char = text[index++]; if (char === '"') { try { return JSON.parse(text.slice(start, index)); } catch { fail(`${label} has malformed JSON`); } } if (char === "\\") { if (index >= text.length) fail(`${label} has malformed JSON`); index += 1; } else if (char < " ") fail(`${label} has malformed JSON`); } fail(`${label} has malformed JSON`); };
  const value = (depth) => { if (depth > LIMITS.depth) fail(`${label} nesting exceeds depth bound`); whitespace(); const char = text[index]; if (char === "{") { index += 1; whitespace(); const keys = new Set(); if (text[index] === "}") { index += 1; return; } while (true) { whitespace(); const key = quoted(); if (keys.has(key)) fail(`${label} has duplicate property ${key}`); keys.add(key); whitespace(); if (text[index++] !== ":") fail(`${label} has malformed JSON`); value(depth + 1); whitespace(); if (text[index] === "}") { index += 1; break; } if (text[index++] !== ",") fail(`${label} has malformed JSON`); } return; } if (char === "[") { index += 1; whitespace(); if (text[index] === "]") { index += 1; return; } while (true) { value(depth + 1); whitespace(); if (text[index] === "]") { index += 1; break; } if (text[index++] !== ",") fail(`${label} has malformed JSON`); } return; } if (char === '"') { quoted(); return; } const start = index; while (index < text.length && !/[\s,\]}]/u.test(text[index])) index += 1; if (start === index) fail(`${label} has malformed JSON`); try { JSON.parse(text.slice(start, index)); } catch { fail(`${label} has malformed JSON`); } };
  value(0); whitespace(); if (index !== text.length) fail(`${label} has trailing JSON data`);
}
function parse(bytes, label) { if (!bytes || typeof bytes.byteLength !== "number" || bytes.byteLength > LIMITS.jsonBytes) fail(`${label} exceeds byte bound`); let text; try { text = new TextDecoder("utf-8", { fatal: true }).decode(bytes); } catch { fail(`${label} is not valid UTF-8`); } assertNoDuplicateKeys(text, label); let value; try { value = JSON.parse(text); } catch { fail(`${label} is not valid JSON`); } checkDepth(value); return value; }
function purl(type, name, version) {
  const normalizedType = type.toLowerCase();
  // NuGet and npm package identities are case-insensitive.  Canonicalize the
  // identity used in the purl/ref while retaining the source spelling in name.
  const canonicalName = normalizedType === "nuget" || normalizedType === "npm" || normalizedType === "generic" ? name.toLowerCase() : name;
  const encodedName = canonicalName.split("/").map((part) => encodeURIComponent(part)).join("/");
  const encodedVersion = encodeURIComponent(version);
  return `pkg:${normalizedType}/${encodedName}@${encodedVersion}`;
}
export function canonicalPurl(type, name, version) {
  string(type, "purl type");
  if (typeof name !== "string" || name.length < 1 || name.length > LIMITS.stringLength || /[\u0000-\u001f\u007f]/u.test(name)) fail("purl name is unsafe");
  if ((FORBIDDEN.test(name) || SENSITIVE_PACKAGE_TERM.test(name)) && (!isSafePackageIdentity(name) || FORBIDDEN_HOST.test(name))) fail("purl name is unsafe");
  string(version, "purl version");
  if (name.includes("..") || name.includes("\\") || name.includes(":" ) || version.includes("..") || version.includes("\\")) fail("purl value is unsafe");
  return purl(type, name, version);
}
function component(type, name, version, kind = "library") { const ref = canonicalPurl(type, name, version); return { type: kind, "bom-ref": ref, name, version, purl: ref }; }
function libraryParts(key, value) {
  const source = typeof key === "string" ? key : "";
  const slash = source.lastIndexOf("/");
  const name = slash > 0 ? source.slice(0, slash) : source;
  const version = slash > 0 ? source.slice(slash + 1) : (typeof value === "string" ? value : value?.version ?? "unknown");
  if (!name || !version || name.includes("\\") || name.includes(":")) return null;
  return { type: "nuget", name, version };
}
function addComponent(map, type, name, version, kind = "library") {
  const c = component(type, name, version, kind); const existing = map.get(c["bom-ref"]);
  if (existing) {
    if (existing.type !== kind || existing.version !== c.version) fail(`conflicting component identity for ${c["bom-ref"]} (${existing.type}/${kind}, ${existing.name}/${c.name})`);
    const insensitive = type.toLowerCase() === "nuget" || type.toLowerCase() === "npm";
    if (!(existing.name === c.name || (insensitive && existing.name.toLowerCase() === c.name.toLowerCase()))) fail(`conflicting component identity for ${c["bom-ref"]} (${existing.type}/${kind}, ${existing.name}/${c.name})`);
    // Shared package occurrences are merged only when their canonical
    // identity is identical; choose a stable display spelling.
    if (ordinal(c.name, existing.name) < 0) map.set(c["bom-ref"], c);
    return c["bom-ref"];
  }
  map.set(c["bom-ref"], c); return c["bom-ref"];
}
function addEdges(edges, ref, values) {
  if (!ref || !values?.length) return;
  const prior = edges.get(ref) ?? new Set(); for (const value of values) { if (value === ref) fail(`dependency graph contains a self-edge at ${ref}`); prior.add(value); } edges.set(ref, prior);
}
function firstParty(name) { return /^SqlObserver\./u.test(name); }
function libraryRef(components, name, version) { return addComponent(components, firstParty(name) ? "generic" : "nuget", name.toLowerCase() === name ? name : name, version, firstParty(name) && /^(?:SqlObserver\.(?:Server|Collector|McpStdio))$/u.test(name) ? "application" : "library"); }
function dependenciesFromDeps(value, components, edges) {
  const targets = value?.targets;
  if (!targets || typeof targets !== "object" || Array.isArray(targets)) fail("host dependency graph has no closed targets object");
  for (const target of Object.values(targets)) {
    if (!target || typeof target !== "object" || Array.isArray(target)) fail("host dependency graph target is malformed");
    for (const [key, info] of Object.entries(target)) {
      const own = libraryParts(key, info?.version); if (!own) fail("host dependency graph has an invalid library identity");
      const ownRef = libraryRef(components, own.name, own.version);
      if (info?.dependencies !== undefined && (!info.dependencies || typeof info.dependencies !== "object" || Array.isArray(info.dependencies))) fail("host dependency graph dependencies are malformed");
      const direct = info?.dependencies ? Object.keys(info.dependencies) : [];
      const refs = direct.map((dep) => { const p = libraryParts(dep, info.dependencies[dep]); if (!p) fail("host dependency graph has an invalid dependency identity"); return libraryRef(components, p.name, p.version); });
      addEdges(edges, ownRef, refs);
    }
  }
  const libraries = value?.libraries;
  if (!libraries || typeof libraries !== "object" || Array.isArray(libraries)) fail("host dependency graph has no closed libraries object");
  for (const [key, info] of Object.entries(libraries)) { const p = libraryParts(key, info?.version); if (!p) fail("host dependency graph has an invalid library identity"); libraryRef(components, p.name, p.version); }
}
function dependencyObject(value, components, edges, workspace) {
  if (!value || typeof value !== "object") return;
  const assertGraphShape = (item, label) => { const allowed = new Set(["name", "version", "dependencies", "packages"]); for (const key of Object.keys(item)) if (!allowed.has(key)) fail(`${label} has unsafe field ${key}`); if (item.dependencies !== undefined && (!item.dependencies || typeof item.dependencies !== "object" || Array.isArray(item.dependencies))) fail(`${label}.dependencies must be an object`); };
  if (!Array.isArray(value)) assertGraphShape(value, "pnpm graph");
  let workspaceCount = 0; const workspaceDependencies = new Set();
  const visit = (name, item, isTopLevel = false) => {
    if (typeof name !== "string" || !name || !item || typeof item !== "object" || Array.isArray(item)) fail(`pnpm package ${name || "<unnamed>"} is malformed`);
    const packageName = name.replace(/^\//u, "");
    const version = item.version;
    if (typeof version !== "string" || !version) fail(`pnpm package ${packageName} has no version`);
    assertGraphShape(item, `pnpm package ${packageName}`); const isWorkspace = packageName === workspace.name && version === workspace.version;
    if (isWorkspace && !isTopLevel) fail("pnpm workspace package cannot be a dependency");
    if (isWorkspace) workspaceCount++;
    const ref = isWorkspace ? null : addComponent(components, "npm", packageName, version);
    const children = item.dependencies && typeof item.dependencies === "object" ? item.dependencies : {};
    const childRefs = Object.entries(children).map(([child, childValue]) => {
      const childItem = typeof childValue === "string" ? { version: childValue } : childValue;
      if (!childItem || typeof childItem !== "object" || Array.isArray(childItem) || typeof childItem.version !== "string" || !childItem.version) fail(`pnpm dependency ${child} is malformed`);
      if (child.replace(/^\//u, "") === workspace.name && childItem.version === workspace.version) fail("pnpm workspace package cannot be a dependency");
      return addComponent(components, "npm", child.replace(/^\//u, ""), childItem.version);
    });
    if (isWorkspace) for (const childRef of childRefs) workspaceDependencies.add(childRef); else addEdges(edges, ref, childRefs);
    for (const [child, childValue] of Object.entries(children)) visit(child, typeof childValue === "object" ? childValue : { version: childValue });
  };
  if (Array.isArray(value)) {
    for (const item of value) if (item && typeof item === "object") visit(item.name ?? item.package ?? item.path, item, true);
  } else if (value.packages && typeof value.packages === "object") {
    for (const [name, item] of Object.entries(value.packages)) visit(name, item, true);
  } else if (value.dependencies && typeof value.dependencies === "object") {
    for (const [name, item] of Object.entries(value.dependencies)) { const child = typeof item === "object" ? item : { version: item }; visit(name, child, true); workspaceDependencies.add(canonicalPurl("npm", name.replace(/^\//u, ""), child.version)); }
  } else {
    fail("pnpm graph has no closed package collection");
  }
  if ((Array.isArray(value) || value.packages) && workspaceCount !== 1) fail("pnpm graph must contain the exact workspace package once");
  return [...workspaceDependencies];
}
async function verifyManifest(root, manifestPath) {
  const manifest = parse(await readFile(manifestPath), "input manifest");
  const keys = Object.keys(manifest); if (keys.length !== 5 || !["$schema", "schemaVersion", "manifestId", "roots", "files"].every((key) => keys.includes(key))) fail("input manifest shape is not closed");
  if (manifest.$schema !== "m12-sbom-inputs.v1.schema.json" || manifest.schemaVersion !== 1 || manifest.manifestId !== "m12-sbom-inputs") fail("input manifest identity is invalid");
  const roots = ["src/SqlObserver.Server/SqlObserver.Server.csproj", "src/SqlObserver.Collector/SqlObserver.Collector.csproj", "src/SqlObserver.McpStdio/SqlObserver.McpStdio.csproj"];
  if (stable(manifest.roots) !== stable(roots)) fail("input roots are not exact");
  const expected = new Set(["Directory.Packages.props", "Directory.Build.props", "global.json", "web/package.json", "web/pnpm-lock.yaml", "web/tools/web-asset-manifest.mjs", "web/contracts/web-asset-manifest.v1.schema.json"]);
  const queue = [...roots], seen = new Set();
  while (queue.length) { const current = queue.shift(); if (seen.has(current)) continue; seen.add(current); expected.add(current); const lock = `${path.posix.dirname(current)}/packages.lock.json`; try { await lstat(path.join(root, ...lock.split("/"))); expected.add(lock); } catch { /* lock is optional only where the repository has none */ }
    const text = (await readFile(path.join(root, ...current.split("/")))).toString("utf8"); for (const match of text.matchAll(/<ProjectReference\s+Include=["']([^"']+)["']/giu)) { const next = path.posix.normalize(path.posix.join(path.posix.dirname(current), match[1].replaceAll("\\", "/"))); safeRelative(next, "project reference"); queue.push(next); }
  }
  if (!Array.isArray(manifest.files) || manifest.files.length !== expected.size) fail("input manifest file set is not exact");
  const actual = new Set(), actualLower = new Set(); for (const item of manifest.files) { object(item, "input pin"); if (Object.keys(item).length !== 2 || !["path", "sha256"].every((key) => key in item)) fail("input pin shape is not closed"); const relative = safeRelative(item.path, "input pin path"); const lowered = relative.toLowerCase(); if (actual.has(relative) || actualLower.has(lowered) || !expected.has(relative) || !HASH.test(item.sha256)) fail("input pins are duplicate, extra, or malformed"); actual.add(relative); actualLower.add(lowered); const bytes = await readFile(path.join(root, ...relative.split("/"))); if (sha256(bytes) !== item.sha256) fail(`input pin does not match ${relative}`); }
  if (actual.size !== expected.size || [...expected].some((item) => !actual.has(item))) fail("input pins are not bijective");
  return manifest;
}
export async function buildSbom({ root, inputManifest, deps = [], pnpmList, webCatalog, timestamp, commitSha, runId, environmentId }) {
  if (!root || !inputManifest) fail("root and inputManifest are required"); if (!COMMIT.test(commitSha ?? "")) fail("commitSha is invalid"); if (!ENVIRONMENTS.has(environmentId)) fail("environmentId is invalid");
  if (typeof timestamp !== "string" || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?Z$/u.test(timestamp) || Number.isNaN(Date.parse(timestamp))) fail("timestamp must be a supplied UTC instant"); string(runId, "runId"); await verifyManifest(root, inputManifest);
  const components = new Map(), edges = new Map();
  if (!Array.isArray(deps) || deps.length !== 3 || new Set(deps.map((file) => path.resolve(file))).size !== 3) fail("exactly three distinct host .deps.json inputs are required");
  const hostNames = ["SqlObserver.Server", "SqlObserver.Collector", "SqlObserver.McpStdio"], hostRefs = [];
  for (const file of deps) { const info = await lstat(file); if (!info.isFile() || info.isSymbolicLink()) fail("host dependency input is not a regular file"); const base = path.basename(file).replace(/\.deps\.json$/u, ""); const host = hostNames.find((name) => base === name || base.endsWith(`.${name}`)); if (!host || hostRefs.some((ref) => ref.host === host)) fail("host dependency identities are not the exact Server/Collector/McpStdio set"); const dependencyDocument = parse(await readFile(file), path.basename(file)); dependenciesFromDeps(dependencyDocument, components, edges); const discovered = [...components.values()].find((item) => item.name === host && item.type === "application"); if (!discovered) fail(`host dependency graph is missing ${host}`); hostRefs.push({ host, ref: discovered["bom-ref"] }); }
  if (hostRefs.length !== hostNames.length || hostNames.some((host) => !hostRefs.some((item) => item.host === host))) fail("host dependency identities are incomplete");
  if (!pnpmList || !webCatalog) fail("sanitized pnpm list and web asset catalog are required");
  const pnpmInfo = await lstat(pnpmList); const catalogInfo = await lstat(webCatalog); if (!pnpmInfo.isFile() || pnpmInfo.isSymbolicLink() || !catalogInfo.isFile() || catalogInfo.isSymbolicLink()) fail("dependency or catalog input is not a regular file");
  const webPackage = parse(await readFile(path.join(root, "web/package.json")), "web package manifest"); if (typeof webPackage.name !== "string" || !webPackage.name || typeof webPackage.version !== "string" || !webPackage.version || webPackage.private !== true) fail("web package identity is invalid");
  const webDependencyRefs = dependencyObject(parse(await readFile(pnpmList), "sanitized pnpm list"), components, edges, { name: webPackage.name, version: webPackage.version });
  const catalog = parse(await readFile(webCatalog), "web asset catalog"); if (!Array.isArray(catalog.files) || catalog.$schema !== "web-asset-manifest.v1.schema.json" || catalog.schemaVersion !== 1 || typeof catalog.sha256 !== "string") fail("web asset catalog shape is invalid");
  const catalogWithoutDigest = Object.fromEntries(Object.entries(catalog).filter(([key]) => key !== "sha256")); const catalogDigest = sha256(Buffer.from(stable(catalogWithoutDigest), "utf8")); if (catalogDigest !== catalog.sha256) fail("web asset catalog digest is invalid");
  const catalogPaths = new Set(), webRefs = []; for (const file of catalog.files) { object(file, "web catalog file"); if (Object.keys(file).length !== 4 || typeof file.path !== "string" || !CATALOG_ROLES.has(file.role) || !HASH.test(file.sha256) || !Number.isSafeInteger(file.bytes) || file.bytes < 0 || file.bytes > 4294967296) fail("web catalog file is unsafe"); const relative = safeRelative(file.path, "web catalog path"); if (catalogPaths.has(relative.toLowerCase())) fail("web catalog has duplicate paths"); catalogPaths.add(relative.toLowerCase()); const name = `web/${relative}`; webRefs.push(addComponent(components, "generic", name, `sha256:${file.sha256}`)); }
  // CycloneDX's metadata.component is the BOM root.  It is represented once
  // there (and therefore is intentionally absent from components), while its
  // dependency node remains explicit so the complete edge closure is closed.
  const rootComponent = component("generic", "SqlObserver", commitSha, "application"); const rootRef = rootComponent["bom-ref"];
  const webRef = addComponent(components, "generic", "SqlObserver.Web", commitSha, "application"); addEdges(edges, rootRef, [...hostRefs.map((item) => item.ref), webRef]); addEdges(edges, webRef, [...webDependencyRefs, ...webRefs]);
  if (components.size === 0 || components.size > LIMITS.components) fail("component count exceeds bound");
  const list = [...components.values()].sort((a, b) => ordinal(a["bom-ref"], b["bom-ref"])); const refs = new Set([rootRef, ...list.map((item) => item["bom-ref"])]);
  for (const ref of refs) if (!edges.has(ref)) edges.set(ref, new Set());
  for (const [ref, values] of edges) { if (!refs.has(ref) || [...values].some((value) => !refs.has(value))) fail("dependency graph has dangling references"); }
  const visiting = new Set(), visited = new Set(); const visit = (ref) => { if (visiting.has(ref)) fail("dependency graph contains a cycle"); if (visited.has(ref)) return; visiting.add(ref); for (const next of edges.get(ref) ?? []) visit(next); visiting.delete(ref); visited.add(ref); }; for (const ref of refs) visit(ref);
  const dependencies = [...edges.entries()].map(([ref, values]) => ({ ref, dependsOn: [...values].sort(ordinal) })).sort((a, b) => ordinal(a.ref, b.ref));
  if (dependencies.length > LIMITS.dependencyNodes) fail("dependency node count exceeds bound");
  const properties = { commitSha, runId, environmentId }; const seed = `${commitSha}|${runId}|${environmentId}|${stable(list)}|${stable(dependencies)}`; const digest = sha256(seed); const serialNumber = `urn:uuid:${digest.slice(0, 8)}-${digest.slice(8, 12)}-4${digest.slice(13, 16)}-${(parseInt(digest.slice(16, 18), 16) & 0x3f | 0x80).toString(16).padStart(2, "0")}${digest.slice(18, 20)}-${digest.slice(20, 32)}`;
  const metadataProperties = [{ name: "commitSha", value: commitSha }, { name: "runId", value: runId }, { name: "environmentId", value: environmentId }, { name: "identity.kind", value: "git-commit" }];
  const bom = { bomFormat: "CycloneDX", specVersion: "1.7", serialNumber, version: 1, metadata: { timestamp, component: rootComponent, properties: metadataProperties }, components: list, dependencies };
  const bytes = jsonBytes(bom); if (bytes.length > LIMITS.jsonBytes) fail("SBOM exceeds byte bound"); return { bom, bytes };
}
export { dependencyObject };
function args(argv) { const result = { deps: [] }; for (let i = 0; i < argv.length; i += 1) { const key = argv[i]; if (!key.startsWith("--")) fail(`unexpected argument ${key}`); const name = key.slice(2); if (name === "deps") { result.deps.push(argv[++i]); continue; } result[name.replaceAll("-", "_")] = argv[++i]; } return result; }
if (import.meta.url === `file://${process.argv[1]?.replaceAll("\\", "/")}` || process.argv[1]?.endsWith("generate-m12-sbom.mjs")) {
  try { const a = args(process.argv.slice(2)); const result = await buildSbom({ root: path.resolve(a.root ?? "."), inputManifest: path.resolve(a.input_manifest ?? "release/certification/m12-sbom-inputs.v1.json"), deps: a.deps.map((item) => path.resolve(item)), pnpmList: path.resolve(a.pnpm_list), webCatalog: path.resolve(a.web_catalog), timestamp: a.timestamp, commitSha: a.commit_sha, runId: a.run_id, environmentId: a.environment }); await import("node:fs/promises").then(({ writeFile }) => writeFile(path.resolve(a.output), result.bytes, { flag: "wx" })); }
  catch (error) { process.stderr.write(`${error.message}\n`); process.exitCode = 1; }
}
