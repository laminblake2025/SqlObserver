/* Deterministic M12 license evidence generator.
 *
 * The SBOM is the authority for component identity.  This program only emits
 * third-party NuGet and npm components present in that SBOM and requires one,
 * reviewed SPDX license for each.  It deliberately does not infer licenses
 * from package names or from arbitrary text.
 *
 * CLI: --sbom --nuget-root --npm-root [--license-map] --commit-sha --run-id
 *      --environment --output
 */
import { createHash } from "node:crypto";
import { execFile } from "node:child_process";
import { lstat, open, readFile, readdir, realpath } from "node:fs/promises";
import path from "node:path";
import { promisify } from "node:util";

export const LIMITS = Object.freeze({ components: 4096, jsonBytes: 4 * 1024 * 1024, stringLength: 512, depth: 32, fileBytes: 1024 * 1024 });
export const ALLOWED_SPDX = Object.freeze(["Apache-2.0", "MIT", "PostgreSQL"]);
export const SNI_OVERRIDE = Object.freeze({ purl: "pkg:nuget/microsoft.data.sqlclient.sni.runtime@6.0.2", path: "LICENSE.txt", sha256: "9335e8bad875dd7be4eebd55d2335eb6433d1cea61aadb3817af7807bef8932a", spdxId: "MIT" });
const HASH = /^[0-9a-f]{64}$/u;
const COMMIT = /^[0-9a-f]{40}$/u;
const RUN_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u;
const ENVIRONMENTS = new Set(["release-windows-server-2022", "release-windows-server-2025"]);
const URL = /^(?:https?|ftp):\/\//iu;
const HOST_OR_SECRET = /(?:password|secret|token|credential|private.?key|localhost|127\.0\.0\.1|[A-Za-z]:[\\/]|\\\\|\/Users\/|\/home\/)/iu;
const SAFE_LICENSE_FILE = /^(?:license|licence|copying|notice)(?:[._ -](?:txt|md|rst|html?))?$/iu;
const execFileAsync = promisify(execFile);

function fail(message) { throw new Error(`m12-licenses: ${message}`); }
function ordinal(a, b) { return a < b ? -1 : a > b ? 1 : 0; }
function sha256(value) { return createHash("sha256").update(value).digest("hex"); }
function stable(value) {
  if (Array.isArray(value)) return `[${value.map(stable).join(",")}]`;
  if (value && typeof value === "object") return `{${Object.keys(value).sort(ordinal).map((k) => `${JSON.stringify(k)}:${stable(value[k])}`).join(",")}}`;
  return JSON.stringify(value);
}
export function jsonBytes(value) { return Buffer.from(`${stable(value)}\n`, "utf8"); }
function string(value, label) { if (typeof value !== "string" || value.length < 1 || value.length > LIMITS.stringLength || /[\u0000-\u001f\u007f]/u.test(value) || HOST_OR_SECRET.test(value)) fail(`${label} is unsafe`); return value; }
function safeRelative(value, label) { string(value, label); if (value.includes("\\") || value.startsWith("/") || value.startsWith("~") || value.includes(":") || value.split("/").some((part) => !part || part === "." || part === "..") || path.posix.normalize(value) !== value) fail(`${label} is unsafe`); return value; }
function object(value, label) { if (!value || typeof value !== "object" || Array.isArray(value)) fail(`${label} must be an object`); return value; }

// Parsing is intentionally independent of JSON.parse so duplicate properties
// cannot be silently accepted by the host runtime.
function assertNoDuplicateKeys(text, label) {
  let i = 0; const whitespace = () => { while (i < text.length && /\s/u.test(text[i])) i += 1; };
  const quoted = () => { const start = i; if (text[i++] !== '"') fail(`${label} is malformed JSON`); while (i < text.length) { const c = text[i++]; if (c === '"') { try { return JSON.parse(text.slice(start, i)); } catch { fail(`${label} is malformed JSON`); } } if (c === "\\") { if (i >= text.length) fail(`${label} is malformed JSON`); i += 1; } else if (c < " ") fail(`${label} is malformed JSON`); } fail(`${label} is malformed JSON`); };
  const value = (depth) => { if (depth > LIMITS.depth) fail(`${label} exceeds depth bound`); whitespace(); const c = text[i]; if (c === "{") { i += 1; whitespace(); const keys = new Set(); if (text[i] === "}") { i += 1; return; } while (true) { whitespace(); const key = quoted(); if (keys.has(key)) fail(`${label} has duplicate property ${key}`); keys.add(key); whitespace(); if (text[i++] !== ":") fail(`${label} is malformed JSON`); value(depth + 1); whitespace(); if (text[i] === "}") { i += 1; return; } if (text[i++] !== ",") fail(`${label} is malformed JSON`); } } if (c === "[") { i += 1; whitespace(); if (text[i] === "]") { i += 1; return; } while (true) { value(depth + 1); whitespace(); if (text[i] === "]") { i += 1; return; } if (text[i++] !== ",") fail(`${label} is malformed JSON`); } } if (c === '"') { quoted(); return; } const start = i; while (i < text.length && !/[\s,\]}]/u.test(text[i])) i += 1; if (start === i) fail(`${label} is malformed JSON`); try { JSON.parse(text.slice(start, i)); } catch { fail(`${label} is malformed JSON`); } };
  value(0); whitespace(); if (i !== text.length) fail(`${label} has trailing data`);
}
export function parseClosed(bytes, label = "JSON") {
  if (!bytes || bytes.length < 2 || bytes.length > LIMITS.jsonBytes) fail(`${label} exceeds byte bound`);
  if (bytes.at(-1) !== 10 || bytes.includes(13)) fail(`${label} must use LF newline`);
  let text; try { text = new TextDecoder("utf-8", { fatal: true }).decode(bytes); } catch { fail(`${label} is not valid UTF-8`); }
  assertNoDuplicateKeys(text, label); try { return JSON.parse(text); } catch { fail(`${label} is not valid JSON`); }
}
// npm package.json files are registry metadata, not authority/contract JSON.
// npm commonly writes these files without a terminal LF, so accept either a
// terminal LF or an exact final `}` while retaining strict UTF-8,
// duplicate-key, and trailing-byte checks.
export function parseNpmMetadata(bytes, label = "npm package metadata") {
  if (!bytes || bytes.length < 2 || bytes.length > LIMITS.fileBytes) fail(`${label} exceeds byte bound`);
  if (bytes.includes(13) || (bytes.at(-1) !== 10 && bytes.at(-1) !== 125)) fail(`${label} must end with LF or }`);
  let text; try { text = new TextDecoder("utf-8", { fatal: true }).decode(bytes); } catch { fail(`${label} is not valid UTF-8`); }
  if (text.endsWith("\n")) text = text.slice(0, -1);
  if (!text.endsWith("}")) fail(`${label} has trailing data`);
  assertNoDuplicateKeys(text, label); try { return JSON.parse(text); } catch { fail(`${label} is not valid JSON`); }
}
async function trustedPowerShell() {
  if (process.platform !== "win32") return null;
  const candidates = process.env.SQLOBSERVER_M12_TRUSTED_PWSH_PATH ? [process.env.SQLOBSERVER_M12_TRUSTED_PWSH_PATH] : [];
  let pwsh = null;
  for (const candidate of [...new Set(candidates.map((value) => path.resolve(value)))]) { try { const info = await lstat(candidate); if (!info.isFile() || info.isSymbolicLink() || info.nlink !== 1) continue; let parent = path.dirname(candidate); while (true) { const parentInfo = await lstat(parent); if (parentInfo.isSymbolicLink()) throw new Error("reparse"); const next = path.dirname(parent); if (next === parent) break; parent = next; } const canonical = await realpath(candidate); if (canonical.toLowerCase() !== candidate.toLowerCase()) continue; const version = await execFileAsync(candidate, ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write($PSVersionTable.PSVersion.ToString())"], { windowsHide: true, timeout: 5000, maxBuffer: 1024 }); if (!/^7\.(?:[5-9]|[1-9][0-9])(?:\.|$)/u.test(version.stdout.trim())) continue; pwsh = candidate; break; } catch { /* try the next verified installation */ } }
  if (!pwsh) fail("trusted PowerShell is unavailable"); return pwsh;
}
async function assertNoAlternateDataStreams(files) {
  if (process.platform !== "win32") return;
  // `powershell -Command` treats tokens after the script as script text, so
  // passing paths as positional arguments is unsafe for spaces/metacharacters.
  // Use a child-only environment value and parse it as JSON inside PowerShell.
  const pwsh = await trustedPowerShell();
  const script = "$paths = @(ConvertFrom-Json -InputObject $env:M12_LICENSE_ADS_PATHS); foreach($p in $paths){ Get-Item -LiteralPath $p -Force -ErrorAction Stop | Out-Null; $streams=@(Get-Item -LiteralPath $p -Force -Stream * -ErrorAction Stop); foreach($stream in $streams){ if([string]$stream.Stream -cne ':$DATA'){ exit 7 } } }; exit 0";
  try { await execFileAsync(pwsh, ["-NoProfile", "-NonInteractive", "-Command", script], { windowsHide: true, timeout: 15000, maxBuffer: 65536, env: { ...process.env, M12_LICENSE_ADS_PATHS: JSON.stringify(files) } }); } catch (error) { if (error?.message?.includes("trusted PowerShell is unavailable")) throw error; fail("path has an alternate data stream"); }
}
async function assertTrustedPath(root, target) {
  // Inspect the requested root and all of its parents before realpath(). A
  // junction must not be normalized into an apparently trusted root.
  const requestedRoot = path.resolve(root); const parsed = path.parse(requestedRoot); let current = parsed.root; const rootAncestors = [current];
  for (const segment of path.relative(parsed.root, requestedRoot).split(path.sep).filter(Boolean)) { current = path.join(current, segment); const info = await lstat(current).catch(() => null); if (!info || info.isSymbolicLink()) fail("path ancestor is a reparse point"); rootAncestors.push(current); }
  const requestedTarget = path.resolve(target); const requestedRelative = path.relative(requestedRoot, requestedTarget); if (requestedRelative.startsWith("..") || path.isAbsolute(requestedRelative)) fail("path escapes trusted root");
  const rootReal = await realpath(requestedRoot); const fullRoot = path.resolve(rootReal); const fullTarget = path.resolve(fullRoot, requestedRelative); const relative = path.relative(fullRoot, fullTarget); if (relative.startsWith("..") || path.isAbsolute(relative)) fail("path escapes trusted root");
  const segments = relative ? relative.split(path.sep) : []; current = fullRoot; const ancestors = [...rootAncestors, current];
  for (const segment of segments) { current = path.join(current, segment); const info = await lstat(current).catch(() => null); if (!info || info.isSymbolicLink()) fail("path ancestor is a reparse point"); ancestors.push(current); }
  await assertNoAlternateDataStreams(ancestors);
  return { rootReal, fullTarget };
}
async function readStable(file, label, maxBytes = LIMITS.jsonBytes) {
  // Bind the read to one handle.  A path-based lstat/read/lstat sequence can
  // be raced by replacing the path after either stat; the handle identity is
  // the authority for every byte returned below.
  const pathInfo = await lstat(file, { bigint: true }); if (!pathInfo.isFile() || pathInfo.isSymbolicLink()) fail(`${label} is not a regular file`); if (pathInfo.nlink !== undefined && pathInfo.nlink !== 1n) fail(`${label} has multiple hard links`);
  const handle = await open(file, "r");
  try {
    const before = await handle.stat({ bigint: true });
    if (!before.isFile() || before.nlink !== 1n) fail(before.nlink !== 1n ? `${label} has multiple hard links` : `${label} is not a regular file`);
    if (before.size < 1n || before.size > BigInt(maxBytes)) fail(`${label} exceeds byte bound`);
    if (pathInfo.dev !== undefined && BigInt(pathInfo.dev) !== before.dev || pathInfo.ino !== undefined && BigInt(pathInfo.ino) !== before.ino) fail(`${label} changed before read`);
    const bytes = Buffer.alloc(Number(before.size)); let offset = 0;
    while (offset < bytes.length) { const result = await handle.read(bytes, offset, bytes.length - offset, offset); if (result.bytesRead < 1) fail(`${label} ended during read`); offset += result.bytesRead; }
    const after = await handle.stat({ bigint: true });
    if (!after.isFile() || after.nlink !== 1n || after.size !== before.size || after.mtimeNs !== before.mtimeNs || after.dev !== before.dev || after.ino !== before.ino) fail(`${label} changed during read`);
    return { bytes, before, after };
  } finally { await handle.close(); }
}
export async function readClosed(file, label, maxBytes = LIMITS.jsonBytes) { const stable = await readStable(file, label, maxBytes); return { bytes: stable.bytes, value: parseClosed(stable.bytes, label), hash: sha256(stable.bytes) }; }
export function licenseValue(value, label = "license") {
  let candidate = value;
  if (typeof candidate === "object" && candidate !== null && !Array.isArray(candidate)) { const fields = ["type", "expression", "id"].filter((key) => Object.prototype.hasOwnProperty.call(candidate, key)); if (fields.length < 1) fail(`${label} is missing, unknown, URL-only, or ambiguous`); const values = fields.map((key) => licenseValue(candidate[key], label)); if (values.some((id) => id !== values[0])) fail(`${label} is conflicting or ambiguous`); candidate = values[0]; }
  if (Array.isArray(candidate)) { if (candidate.length !== 1) fail(`${label} is ambiguous`); return licenseValue(candidate[0], label); }
  if (typeof candidate !== "string" || !ALLOWED_SPDX.includes(candidate) || URL.test(candidate) || /\s(?:OR|AND)\s/iu.test(candidate)) fail(`${label} is missing, unknown, URL-only, or ambiguous`);
  return candidate;
}
function sourcePath(root, file) { const relative = path.relative(root, file).replaceAll(path.sep, "/"); return safeRelative(relative, "license source path"); }
async function checkedLicenseFile(root, file, expectedPath = null, expectedHash = null, packageDir = null) {
  const { rootReal: fullRoot, fullTarget: full } = await assertTrustedPath(root, file); const relative = sourcePath(fullRoot, full);
  const packageRelative = packageDir ? path.relative(packageDir, full).replaceAll(path.sep, "/") : relative;
  if (expectedPath !== null && packageRelative !== expectedPath) fail("license override path mismatch");
  const stable = await readStable(full, "license source"); if (stable.before.size < 1n || stable.before.size > BigInt(LIMITS.fileBytes)) fail("license source is oversized");
  const bytes = stable.bytes; const hash = sha256(bytes); if (expectedHash !== null && hash !== expectedHash) fail("license source digest mismatch");
  // Raw license text is intentionally never put into evidence; the digest and
  // safe package-relative path are sufficient for later independent review.
  return { path: safeRelative(packageRelative, "license source package path"), sha256: hash, bytes };
}
async function findLicenseFile(root, packageDir) {
  const entries = await readdir(packageDir, { withFileTypes: true });
  const candidates = entries.filter((e) => e.isFile() && SAFE_LICENSE_FILE.test(e.name)).sort((a, b) => ordinal(a.name.toLowerCase(), b.name.toLowerCase()));
  if (candidates.length !== 1) fail(candidates.length === 0 ? "license file is missing" : "license files are ambiguous");
  return checkedLicenseFile(root, path.join(packageDir, candidates[0].name), candidates[0].name, null, packageDir);
}
function packageParts(purl) {
  const match = /^pkg:(nuget|npm)\/(.+)@([^@]+)$/u.exec(purl); if (!match) fail("component purl is not NuGet/npm");
  let name; let version;
  try { name = decodeURIComponent(match[2]); version = decodeURIComponent(match[3]); } catch { fail("component purl is malformed"); }
  const npmName = match[1] === "npm" && (name.startsWith("@") ? /^@[A-Za-z0-9._~-]+\/[A-Za-z0-9._~-]+$/u.test(name) : /^[A-Za-z0-9._~-]+$/u.test(name));
  if (!name || !version || /[\u0000-\u0020\u007f]/u.test(name) || /[\u0000-\u0020\u007f]/u.test(version) || name.includes("\\") || name.includes(":") || version.includes("\\") || version.includes(":") || name.split("/").some((part) => !part || part === "." || part === "..") || version.split("/").some((part) => !part || part === "." || part === "..") || path.posix.isAbsolute(name) || path.posix.isAbsolute(version) || (match[1] === "nuget" && (!/^[A-Za-z0-9][A-Za-z0-9._+-]*$/u.test(name) || !/^[A-Za-z0-9][A-Za-z0-9._+-]*$/u.test(version))) || (match[1] === "npm" && !npmName)) fail("component purl is unsafe");
  const canonicalName = name.toLowerCase(); const encodedVersion = encodeURIComponent(version); const encodedSegmentsName = canonicalName.split("/").map((part) => encodeURIComponent(part)).join("/"); const canonicalSegments = `pkg:${match[1]}/${encodedSegmentsName}@${encodedVersion}`;
  // The generator preserves the package slash and encodes exactly once.  Do
  // not accept the alternate whole-name encoding or double-encoded aliases.
  if (purl !== canonicalSegments) fail("component purl is unsafe");
  return { ecosystem: match[1], name, version };
}
async function locatePackageDirectory(root, parts) {
  const direct = path.join(root, ...(parts.ecosystem === "npm" ? parts.name.split("/") : [parts.name.toLowerCase(), parts.version]));
  const directInfo = await lstat(direct).catch(() => null);
  const candidates = [];
  // A package-manager link is only a hint. Never follow it as the authority;
  // pnpm's virtual-store package below is checked by handle-bound reads.
  if (directInfo?.isDirectory() && !directInfo.isSymbolicLink()) candidates.push(direct);
  if (parts.ecosystem !== "npm") return candidates.length === 1 ? candidates[0] : null;
  const store = path.join(root, ".pnpm"); const storeInfo = await lstat(store).catch(() => null);
  if (storeInfo?.isDirectory() && !storeInfo.isSymbolicLink()) {
    const key = `${parts.name.replaceAll("/", "+")}@${parts.version}`;
    const entries = await readdir(store, { withFileTypes: true });
    const matching = entries.filter((entry) => entry.isDirectory() && !entry.isSymbolicLink() && (entry.name === key || entry.name.startsWith(`${key}_`)));
    if (matching.length > LIMITS.components) fail("pnpm store exceeds candidate bound");
    for (const entry of matching) {
      const candidate = path.join(store, entry.name, "node_modules", ...parts.name.split("/"));
      const info = await lstat(candidate).catch(() => null);
      if (info?.isDirectory() && !info.isSymbolicLink()) candidates.push(candidate);
    }
  }
  if (candidates.length !== 1) fail(candidates.length === 0 ? "npm package is missing" : "npm package resolution is ambiguous");
  return candidates[0];
}
async function parseNugetMetadata(file, stableBytes) {
  if (process.platform !== "win32") fail("NuGet metadata parser is unavailable");
  const pwsh = await trustedPowerShell(); const script = String.raw`$b=[Convert]::FromBase64String($env:M12_NUSPEC_BYTES);$ms=[IO.MemoryStream]::new($b,$false);$s=[Xml.XmlReaderSettings]::new();$s.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$s.XmlResolver=$null;$s.IgnoreComments=$false;$s.IgnoreProcessingInstructions=$false;$s.IgnoreWhitespace=$false;$d=[Xml.XmlDocument]::new();$d.XmlResolver=$null;$r=[Xml.XmlReader]::Create($ms,$s);try{$d.Load($r)}finally{$r.Dispose();$ms.Dispose()};
  $standard=@('','http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd','http://schemas.microsoft.com/packaging/2011/08/nuspec.xsd','http://schemas.microsoft.com/packaging/2012/06/nuspec.xsd','http://schemas.microsoft.com/packaging/2013/01/nuspec.xsd','http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd','http://schemas.microsoft.com/packaging/2016/06/nuspec.xsd');$root=$d.DocumentElement;if($null -eq $root -or $root.LocalName -cne 'package' -or $standard -notcontains $root.NamespaceURI){exit 9};$ns=$root.NamespaceURI;
  $walk=$null;$walk={param([Xml.XmlNode]$n)foreach($c in $n.ChildNodes){if($c.NodeType -in @([Xml.XmlNodeType]::Comment,[Xml.XmlNodeType]::ProcessingInstruction,[Xml.XmlNodeType]::CDATA,[Xml.XmlNodeType]::EntityReference,[Xml.XmlNodeType]::DocumentType)){throw 'bad-node'};if($c.NodeType -eq [Xml.XmlNodeType]::Element){if($c.NamespaceURI -cne $ns){throw 'bad-namespace'};&$walk $c}}};try{&$walk $d}catch{exit 9};
   $rootAttrs=@($root.Attributes|Where-Object {$_.NamespaceURI -ne 'http://www.w3.org/2000/xmlns/' -or $_.Name -cne 'xmlns'});if($rootAttrs.Count -ne 0){exit 9};$rootElements=@($root.ChildNodes|Where-Object NodeType -eq ([Xml.XmlNodeType]::Element));if($rootElements.Count -ne 1 -or $rootElements[0].LocalName -cne 'metadata'){exit 9};$metadata=$rootElements[0];if($metadata.NamespaceURI -cne $ns){exit 9};$metadataAttrs=@($metadata.Attributes|Where-Object {$_.NamespaceURI -ne '' -or $_.Name -cne 'minClientVersion' -or [string]::IsNullOrWhiteSpace($_.Value)});if($metadataAttrs.Count -ne 0){exit 9};
  foreach($n in @($root.ChildNodes|Where-Object {$_.NodeType -in @([Xml.XmlNodeType]::Text,[Xml.XmlNodeType]::Whitespace,[Xml.XmlNodeType]::SignificantWhitespace)})){if(-not[string]::IsNullOrWhiteSpace($n.Value)){exit 9}};foreach($n in @($metadata.ChildNodes|Where-Object {$_.NodeType -in @([Xml.XmlNodeType]::Text,[Xml.XmlNodeType]::Whitespace,[Xml.XmlNodeType]::SignificantWhitespace)})){if(-not[string]::IsNullOrWhiteSpace($n.Value)){exit 9}};
  $id=@($metadata.ChildNodes|Where-Object {$_.NodeType -eq [Xml.XmlNodeType]::Element -and $_.LocalName -ceq 'id'});$v=@($metadata.ChildNodes|Where-Object {$_.NodeType -eq [Xml.XmlNodeType]::Element -and $_.LocalName -ceq 'version'});$l=@($metadata.ChildNodes|Where-Object {$_.NodeType -eq [Xml.XmlNodeType]::Element -and $_.LocalName -ceq 'license'});if($id.Count -ne 1 -or $v.Count -ne 1 -or $l.Count -ne 1){exit 9};
   $leaf=$null;foreach($leaf in @($id[0],$v[0],$l[0])){if($leaf.NamespaceURI -cne $ns -or $leaf.ChildNodes.Count -ne 1 -or $leaf.ChildNodes[0].NodeType -ne [Xml.XmlNodeType]::Text -or [string]::IsNullOrWhiteSpace($leaf.ChildNodes[0].Value)){exit 9}};if($id[0].Attributes.Count -ne 0 -or $v[0].Attributes.Count -ne 0 -or $l[0].Attributes.Count -ne 1 -or $null -eq $l[0].Attributes['type'] -or $l[0].GetAttribute('type') -notin @('expression','file')){exit 9};$o=[ordered]@{id=[string]$id[0].ChildNodes[0].Value;version=[string]$v[0].ChildNodes[0].Value;license=[string]$l[0].ChildNodes[0].Value};[Console]::Out.Write(($o|ConvertTo-Json -Compress))`;
  try { const result = await execFileAsync(pwsh, ["-NoProfile", "-NonInteractive", "-Command", script], { windowsHide: true, timeout: 10000, maxBuffer: 4096, env: { ...process.env, M12_NUSPEC_PATH: file, M12_NUSPEC_BYTES: Buffer.from(stableBytes).toString("base64") } }); const parsed = JSON.parse(result.stdout); if (typeof parsed.id !== "string" || typeof parsed.version !== "string" || typeof parsed.license !== "string") fail("NuGet metadata is malformed"); return parsed; } catch (error) { if (error?.message?.startsWith("m12-licenses:")) throw error; fail("NuGet metadata is malformed"); }
}
async function packageLicense(component, roots, map) {
  const purl = component["bom-ref"]; const parts = packageParts(purl); const supplied = map?.[purl] ?? map?.[purl.toLowerCase()];
  const root = roots[parts.ecosystem]; if (!root) fail("license root is missing");
  const trustedRoot = await assertTrustedPath(root, root); const rootReal = trustedRoot.rootReal;
  const packageDir = await locatePackageDirectory(rootReal, parts); if (!packageDir) fail(`package directory is missing for ${purl}`);
  await assertTrustedPath(rootReal, packageDir);
  const packageReal = await realpath(packageDir); if (packageReal !== packageDir) fail("package directory is a reparse point");
  let npmMetadata; let npmSnapshot;
  if (parts.ecosystem === "npm") {
    const metadataPath = path.join(packageDir, "package.json"); await assertTrustedPath(rootReal, metadataPath);
    const stable = await readStable(metadataPath, "npm package metadata", LIMITS.fileBytes); npmSnapshot = { bytes: stable.bytes, value: parseNpmMetadata(stable.bytes), hash: sha256(stable.bytes) }; npmMetadata = npmSnapshot.value;
    if (typeof npmMetadata.name !== "string" || typeof npmMetadata.version !== "string" || npmMetadata.name !== parts.name || npmMetadata.version !== parts.version) fail("npm package identity does not match SBOM purl");
  }
  if (purl.toLowerCase() === SNI_OVERRIDE.purl) { const file = await checkedLicenseFile(rootReal, path.join(packageDir, SNI_OVERRIDE.path), SNI_OVERRIDE.path, SNI_OVERRIDE.sha256, packageDir); return { spdxId: SNI_OVERRIDE.spdxId, source: file }; }
  if (supplied !== undefined) {
    object(supplied, "license map entry"); const declarations = ["spdxId", "license", "licenseId"].filter((key) => Object.prototype.hasOwnProperty.call(supplied, key)).map((key) => licenseValue(supplied[key], "license map")); if (declarations.length !== 1 || declarations.some((id) => id !== declarations[0])) fail("license map is conflicting or ambiguous"); const spdxId = declarations[0];
    if (!supplied.path || typeof supplied.path !== "string" || supplied.path.includes("/") || supplied.path.includes("\\") || supplied.path.includes(":")) fail("license map path must be package-relative");
    const file = await checkedLicenseFile(rootReal, path.join(packageDir, supplied.path), supplied.path, supplied.sha256 ?? null, packageDir); return { spdxId, source: file };
  }
  if (parts.ecosystem === "npm") {
    const declarations = ["license", "licenses"].filter((key) => Object.prototype.hasOwnProperty.call(npmMetadata, key)).map((key) => licenseValue(npmMetadata[key], "npm license")); if (declarations.length !== 1 || declarations.some((id) => id !== declarations[0])) fail("npm license is conflicting or ambiguous"); const spdxId = declarations[0];
    return { spdxId, source: { path: "package.json", sha256: npmSnapshot.hash } };
  }
  // NuGet packages may carry an SPDX expression in a nuspec.  We still bind
  // evidence to a license file, avoiding an unauditable metadata-only claim.
  const file = await findLicenseFile(rootReal, packageDir); const nuspecs = (await readdir(packageDir, { withFileTypes: true })).filter((e) => e.isFile() && e.name.toLowerCase().endsWith(".nuspec")); if (nuspecs.length !== 1) fail(nuspecs.length === 0 ? "NuGet package metadata is missing" : "NuGet package metadata is ambiguous"); const nuspec = nuspecs[0]; safeRelative(nuspec.name, "NuGet metadata path"); const nuspecPath = path.join(packageDir, nuspec.name); const nuspecEvidence = await checkedLicenseFile(rootReal, nuspecPath, null, null, packageDir);
  const metadata = await parseNugetMetadata(nuspecPath, nuspecEvidence.bytes); if (metadata.id.trim().toLowerCase() !== parts.name.toLowerCase() || metadata.version.trim() !== parts.version) fail("NuGet metadata identity does not match SBOM purl"); return { spdxId: licenseValue(metadata.license.trim(), "NuGet license"), source: file };
}
function exactKeys(value, keys, label) { object(value, label); const actual = Object.keys(value).sort(ordinal); const expected = [...keys].sort(ordinal); if (actual.length !== expected.length || actual.some((key, index) => key !== expected[index])) fail(`${label} shape is invalid`); return value; }
function canonicalPurlParts(purl) {
  const match = /^pkg:([a-z][a-z0-9+.-]*)\/(.+)@([^@]+)$/u.exec(purl); if (!match) fail("SBOM component purl is malformed");
  let name; let version; try { name = decodeURIComponent(match[2]); version = decodeURIComponent(match[3]); } catch { fail("SBOM component purl is malformed"); }
  if (!name || !version || name.includes("\\") || name.includes(":") || version.includes("\\") || /[\u0000-\u0020\u007f]/u.test(name) || /[\u0000-\u0020\u007f]/u.test(version)) fail("SBOM component purl is unsafe");
  const encodedName = name.toLowerCase().split("/").map((part) => encodeURIComponent(part)).join("/"); const canonical = `pkg:${match[1]}/${encodedName}@${encodeURIComponent(version)}`; if (purl !== canonical) fail("SBOM component purl is unsafe or non-canonical");
  return { ecosystem: match[1], name, version };
}
function validateSbom(sbom, context = {}) {
  exactKeys(sbom, ["bomFormat", "specVersion", "serialNumber", "version", "metadata", "components", "dependencies"], "SBOM");
  if (sbom.bomFormat !== "CycloneDX" || sbom.specVersion !== "1.7" || !/^urn:uuid:[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/u.test(sbom.serialNumber) || sbom.version !== 1) fail("SBOM shape is invalid");
  exactKeys(sbom.metadata, ["timestamp", "component", "properties"], "SBOM metadata"); if (typeof sbom.metadata.timestamp !== "string" || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?Z$/u.test(sbom.metadata.timestamp) || Number.isNaN(Date.parse(sbom.metadata.timestamp))) fail("SBOM metadata timestamp is invalid");
  const root = exactKeys(sbom.metadata.component, ["type", "bom-ref", "name", "version", "purl"], "SBOM root component"); if (root.type !== "application" || root.name !== "SqlObserver" || root.version !== context.commitSha || root["bom-ref"] !== root.purl || root["bom-ref"] !== `pkg:generic/sqlobserver@${root.version}`) fail("SBOM root component is invalid");
  if (!Array.isArray(sbom.metadata.properties) || sbom.metadata.properties.length !== 4) fail("SBOM metadata properties are invalid"); const propertyNames = ["commitSha", "runId", "environmentId", "identity.kind"]; sbom.metadata.properties.forEach((property, index) => { exactKeys(property, ["name", "value"], "SBOM metadata property"); if (property.name !== propertyNames[index] || typeof property.value !== "string") fail("SBOM metadata properties are invalid"); }); if (sbom.metadata.properties[0].value !== context.commitSha || sbom.metadata.properties[1].value !== context.runId || sbom.metadata.properties[2].value !== context.environmentId || sbom.metadata.properties[3].value !== "git-commit") fail("SBOM metadata properties are invalid");
  if (!Array.isArray(sbom.components) || sbom.components.length < 1 || sbom.components.length > LIMITS.components || !Array.isArray(sbom.dependencies) || sbom.dependencies.length < 1 || sbom.dependencies.length > 8192) fail("SBOM shape is invalid");
  const rootRef = root["bom-ref"]; const refs = new Set([rootRef]); let previous = ""; const applications = new Set();
  for (const component of sbom.components) { exactKeys(component, ["type", "bom-ref", "name", "version", "purl"], "SBOM component"); if (!["application", "library"].includes(component.type) || component["bom-ref"] !== component.purl || typeof component.name !== "string" || typeof component.version !== "string" || !component.name || !component.version || ordinal(component["bom-ref"], previous) < 0 || !refs.add(component["bom-ref"])) fail("SBOM component refs are invalid"); previous = component["bom-ref"]; string(component.name, "SBOM component name"); string(component.version, "SBOM component version"); const parts = canonicalPurlParts(component.purl); if (parts.name !== component.name && !((parts.ecosystem === "nuget" || parts.ecosystem === "generic") && parts.name.toLowerCase() === component.name.toLowerCase())) fail("SBOM component identity is invalid"); if (parts.version !== component.version) fail("SBOM component version is invalid"); if (parts.ecosystem === "nuget" || parts.ecosystem === "npm") { if (component.type !== "library") fail("SBOM third-party component is malformed"); packageParts(component.purl); } else if (parts.ecosystem === "generic") { if (component.type === "application") applications.add(component.name); } else fail("SBOM component ecosystem is invalid"); }
  const expectedApplications = new Set(["SqlObserver.Collector", "SqlObserver.McpStdio", "SqlObserver.Server", "SqlObserver.Web"]); if (applications.size !== expectedApplications.size || [...expectedApplications].some((name) => !applications.has(name))) fail("SBOM application set is invalid");
  const edgeMap = new Map(); previous = ""; for (const edge of sbom.dependencies) { exactKeys(edge, ["ref", "dependsOn"], "SBOM dependency"); if (typeof edge.ref !== "string" || !refs.has(edge.ref) || ordinal(edge.ref, previous) < 0 || edgeMap.has(edge.ref) || !Array.isArray(edge.dependsOn)) fail("SBOM dependency graph is invalid"); previous = edge.ref; let prior = ""; const deps = new Set(); for (const dep of edge.dependsOn) { if (typeof dep !== "string" || !refs.has(dep) || deps.has(dep) || ordinal(dep, prior) < 0 || dep === edge.ref) fail("SBOM dependency graph is invalid"); deps.add(dep); prior = dep; } edgeMap.set(edge.ref, [...deps]); }
  if (edgeMap.size !== refs.size || [...refs].some((ref) => !edgeMap.has(ref))) fail("SBOM dependency graph is incomplete"); const visiting = new Set(); const visited = new Set(); const visit = (ref) => { if (visiting.has(ref)) fail("SBOM dependency graph contains a cycle"); if (visited.has(ref)) return; visiting.add(ref); for (const dep of edgeMap.get(ref)) visit(dep); visiting.delete(ref); visited.add(ref); }; for (const ref of refs) visit(ref);
  return sbom.components.filter((component) => /^pkg:(?:nuget|npm)\//u.test(component["bom-ref"]));
}
export async function buildLicenseEvidence({ sbomPath, sbom, nugetRoot, npmRoot, licenseMap, licenseMapPath, commitSha, runId, environmentId }) {
  if (!COMMIT.test(commitSha ?? "")) fail("commitSha is invalid"); if (!ENVIRONMENTS.has(environmentId)) fail("environmentId is invalid"); if (typeof runId !== "string" || !RUN_ID.test(runId)) fail("runId is invalid");
  let sbomInput; if (sbom) sbomInput = { value: sbom, bytes: jsonBytes(sbom), hash: sha256(jsonBytes(sbom)) }; else { const sbomFile = path.resolve(sbomPath); await assertTrustedPath(path.dirname(sbomFile), sbomFile); sbomInput = await readClosed(sbomFile, "SBOM"); }
  const thirdParty = validateSbom(sbomInput.value, { commitSha, runId, environmentId }); if (!sbom && !Buffer.from(jsonBytes(sbomInput.value)).equals(sbomInput.bytes)) fail("SBOM bytes are not canonical"); let map; if (licenseMap !== undefined) map = licenseMap; else if (licenseMapPath) { const mapFile = path.resolve(licenseMapPath); await assertTrustedPath(path.dirname(mapFile), mapFile); map = (await readClosed(mapFile, "license map")).value; } if (map !== undefined) object(map, "license map");
  const roots = { nuget: nugetRoot, npm: npmRoot }; const components = [];
  for (const component of thirdParty) { const parts = packageParts(component.purl); const found = await packageLicense(component, roots, map); components.push({ bomRef: component["bom-ref"], name: component.name, version: component.version, purl: component.purl, ecosystem: parts.ecosystem, spdxId: found.spdxId, source: { path: found.source.path, sha256: found.source.sha256 } }); }
  components.sort((a, b) => ordinal(a.bomRef, b.bomRef)); if (components.length < 1 || components.length !== thirdParty.length) fail("license evidence is not bijective");
  const result = { $schema: "m12-license-evidence.v1.schema.json", schemaVersion: 1, caseId: "m12-licenses", producerId: "m12-supply-chain-harness", kind: "supply-chain-evidence", result: "passed", environmentId, commitSha, runId, sbomSha256: sbomInput.hash, sbomSize: sbomInput.bytes.length, components };
  const bytes = jsonBytes(result); if (bytes.length > LIMITS.jsonBytes) fail("license evidence exceeds byte bound"); return { evidence: result, bytes };
}

function args(argv) { const result = {}; for (let i = 0; i < argv.length; i += 1) { const key = argv[i]; if (!key.startsWith("--") || i + 1 >= argv.length) fail("malformed argument"); result[key.slice(2).replaceAll("-", "_")] = argv[++i]; } return result; }
if (import.meta.url === `file://${process.argv[1]?.replaceAll("\\", "/")}` || process.argv[1]?.endsWith("generate-m12-license-evidence.mjs")) {
  try { const a = args(process.argv.slice(2)); const result = await buildLicenseEvidence({ sbomPath: path.resolve(a.sbom), nugetRoot: a.nuget_root ? path.resolve(a.nuget_root) : undefined, npmRoot: a.npm_root ? path.resolve(a.npm_root) : undefined, licenseMapPath: a.license_map ? path.resolve(a.license_map) : undefined, commitSha: a.commit_sha, runId: a.run_id, environmentId: a.environment, }); const { writeFile } = await import("node:fs/promises"); await writeFile(path.resolve(a.output), result.bytes, { flag: "wx" }); }
  catch (error) { process.stderr.write(`${error.message}\n`); process.exitCode = 1; }
}
