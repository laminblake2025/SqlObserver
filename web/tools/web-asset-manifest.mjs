import { createHash } from "node:crypto";
import { promises as fs } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const SCHEMA = "web-asset-manifest.v1.schema.json";
const SCHEMA_VERSION = 1;
const LIMITS = Object.freeze({ maxFiles: 4096, maxGraphEntries: 4096, maxPathLength: 512, minCatalogFiles: 2, minGraphEntries: 1, maxFileBytes: 4294967296 });
const DEFAULT_DIST = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../dist");
const DEFAULT_OUTPUT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../.artifacts/web-asset-manifest.v1.json");
const HASH = /^[0-9a-f]{64}$/;
const HASH_TOKEN = /(?:^|[-_.])([A-Za-z0-9]{8})(?=[-_.]|$)/;
const PAYLOAD_EXTENSIONS = new Set([".js", ".css", ".mjs", ".cjs", ".ts", ".woff", ".woff2", ".ttf", ".otf", ".eot", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".svg", ".ico", ".webm", ".mp4", ".ogg", ".mp3", ".wav"]);
const FONT_EXTENSIONS = new Set([".woff", ".woff2", ".ttf", ".otf", ".eot"]);
const MEDIA_EXTENSIONS = new Set([".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".svg", ".ico", ".webm", ".mp4", ".ogg", ".mp3", ".wav"]);
const MANIFEST_FIELDS = new Set(["file", "name", "src", "isEntry", "isDynamicEntry", "imports", "dynamicImports", "css", "assets"]);
const ROLES = new Set(["entry-document", "entry-script", "chunk", "stylesheet", "font", "media", "build-metadata"]);

function fail(message) { throw new Error(`web asset manifest: ${message}`); }

function assertObject(value, label) {
  if (value === null || typeof value !== "object" || Array.isArray(value)) fail(`${label} must be an object`);
  return value;
}

function assertString(value, label) {
  if (typeof value !== "string") fail(`${label} must be a string`);
  return value;
}

function ordinal(a, b) { return a < b ? -1 : a > b ? 1 : 0; }

function safePath(value, label) {
  const candidate = assertString(value, label);
  const characterCount = [...candidate].length;
  if (characterCount < 1 || characterCount > LIMITS.maxPathLength || candidate.includes("\\") || candidate.includes("?") || candidate.includes("#") || /%(?:2f|2F|5c|5C|2e|2E)/u.test(candidate) || candidate.startsWith("/") || candidate.startsWith("~") || /[\u0000-\u001f\u007f]/u.test(candidate)) fail(`${label} is not a safe POSIX relative path`);
  const parts = candidate.split("/");
  if (parts.some((part) => part.length === 0 || part === "." || part === ".." || part.includes(":"))) fail(`${label} is not a normalized POSIX relative path`);
  if (path.posix.normalize(candidate) !== candidate) fail(`${label} is not normalized`);
  return candidate;
}

function stable(value) {
  if (Array.isArray(value)) return `[${value.map(stable).join(",")}]`;
  if (value && typeof value === "object") return `{${Object.keys(value).sort().map((key) => `${JSON.stringify(key)}:${stable(value[key])}`).join(",")}}`;
  return JSON.stringify(value);
}

function sha256(bytes) { return createHash("sha256").update(bytes).digest("hex"); }
function catalogDigest(catalog) {
  const withoutDigest = { ...catalog };
  delete withoutDigest.sha256;
  return sha256(Buffer.from(stable(withoutDigest), "utf8"));
}

function jsonBytes(value) { return Buffer.from(`${stable(value)}\n`, "utf8"); }

async function walk(root, current = "", state = { count: 0 }) {
  const directory = path.join(root, ...current ? current.split("/") : []);
  const entries = await fs.readdir(directory, { withFileTypes: true });
  const result = [];
  for (const entry of entries.sort((a, b) => ordinal(a.name, b.name))) {
    const relative = current ? `${current}/${entry.name}` : entry.name;
    safePath(relative, "dist path");
    const absolute = path.join(directory, entry.name);
    const stat = await fs.lstat(absolute);
    if (stat.isSymbolicLink() || (stat.mode & 0xf000) === 0xa000 || (process.platform === "win32" && (stat.attributes ?? 0) & 0x400)) fail(`symlink or reparse point is not permitted: ${relative}`);
    if (stat.isDirectory()) result.push(...await walk(root, relative));
    else if (stat.isFile()) {
      if (stat.size > LIMITS.maxFileBytes) fail(`file exceeds ${LIMITS.maxFileBytes} bytes: ${relative}`);
      state.count += 1;
      if (state.count > LIMITS.maxFiles) fail(`dist contains more than ${LIMITS.maxFiles} files`);
      result.push({ path: relative, absolute, size: stat.size });
    }
    else fail(`unsupported dist entry: ${relative}`);
  }
  return result;
}

function readManifest(bytes) {
  let value;
  try { value = JSON.parse(bytes.toString("utf8")); } catch { fail("Vite manifest is not valid JSON"); }
  const root = assertObject(value, "Vite manifest");
  const keys = Object.keys(root);
  if (keys.length === 0) fail("Vite manifest has no entries");
  if (keys.length > LIMITS.maxGraphEntries) fail(`Vite manifest has more than ${LIMITS.maxGraphEntries} entries`);
  const entries = [];
  for (const key of keys.sort(ordinal)) {
    safePath(key, "Vite manifest key");
    const source = assertObject(root[key], `Vite manifest entry ${key}`);
    for (const field of Object.keys(source)) if (!MANIFEST_FIELDS.has(field)) fail(`unknown Vite manifest field ${field}`);
    if (typeof source.file !== "string") fail(`Vite manifest entry ${key} has no file`);
    const optionalArray = (field) => {
      if (source[field] === undefined) return [];
      if (!Array.isArray(source[field])) fail(`${key}.${field} must be an array of strings`);
      return source[field];
    };
    const entry = { key, file: safePath(source.file, `Vite manifest ${key}.file`), name: source.name, src: source.src, isEntry: source.isEntry === true, isDynamicEntry: source.isDynamicEntry === true, imports: optionalArray("imports"), dynamicImports: optionalArray("dynamicImports"), css: optionalArray("css"), assets: optionalArray("assets") };
    if (source.name !== undefined) assertString(source.name, `${key}.name`);
    if (source.src !== undefined) safePath(source.src, `${key}.src`);
    if (source.isEntry !== undefined && typeof source.isEntry !== "boolean") fail(`${key}.isEntry must be boolean`);
    if (source.isDynamicEntry !== undefined && typeof source.isDynamicEntry !== "boolean") fail(`${key}.isDynamicEntry must be boolean`);
    for (const field of ["imports", "dynamicImports", "css", "assets"]) {
      if (!Array.isArray(entry[field]) || entry[field].some((item) => typeof item !== "string")) fail(`${key}.${field} must be an array of strings`);
      entry[field] = entry[field].map((item) => safePath(item, `${key}.${field}`));
      const distinct = new Set(entry[field].map((item) => item.toLowerCase()));
      if (distinct.size !== entry[field].length) fail(`${key}.${field} contains duplicate references`);
    }
    entries.push(entry);
  }
  const entryPoints = entries.filter((entry) => entry.isEntry);
  if (entryPoints.length !== 1) fail("Vite manifest must contain exactly one entrypoint");
  const entryPoint = entryPoints[0];
  if (entryPoint.key !== "index.html" || entryPoint.src !== "index.html") fail("Vite manifest entrypoint must be index.html");
  return { entries, entryPoint };
}

function roleFor(filePath, graph) {
  if (filePath === "index.html") return "entry-document";
  if (filePath === ".vite/manifest.json" || filePath.endsWith(".map")) return "build-metadata";
  const extension = path.posix.extname(filePath).toLowerCase();
  if (extension === ".js" || extension === ".mjs" || extension === ".cjs") return graph.entryFiles.has(filePath) ? "entry-script" : "chunk";
  if (extension === ".css") return "stylesheet";
  if (FONT_EXTENSIONS.has(extension)) return "font";
  if (MEDIA_EXTENSIONS.has(extension)) return "media";
  fail(`unknown dist payload type: ${filePath}`);
}

function assertHashedPayload(filePath) {
  const base = path.posix.basename(filePath);
  if (!HASH_TOKEN.test(base)) fail(`payload is not content-hashed: ${filePath}`);
}

async function readDist(root) {
  const absoluteRoot = path.resolve(root);
  const rootStat = await fs.lstat(absoluteRoot).catch(() => null);
  if (!rootStat?.isDirectory() || rootStat.isSymbolicLink()) fail(`dist directory is missing or unsafe: ${root}`);
  const realRoot = await fs.realpath(absoluteRoot);
  const files = await walk(absoluteRoot);
  for (const file of files) {
    const realFile = await fs.realpath(file.absolute);
    if (realFile !== realRoot && !realFile.startsWith(`${realRoot}${path.sep}`)) fail(`dist file escapes root: ${file.path}`);
  }
  return files;
}

function parseHtmlTag(html, start) {
  const nameMatch = /^<([A-Za-z][A-Za-z0-9:-]*)/u.exec(html.slice(start));
  if (!nameMatch) fail("index.html contains a malformed tag");
  const tagName = nameMatch[1].toLowerCase();
  let cursor = start + nameMatch[0].length;
  const attributes = new Map();
  let end = -1;
  let quote = null;
  for (let index = cursor; index < html.length && index < start + 65536; index += 1) {
    const character = html[index];
    if (quote) { if (character === quote) quote = null; continue; }
    if (character === "'" || character === '"') { quote = character; continue; }
    if (character === ">") { end = index; break; }
  }
  if (quote || end < 0) fail("index.html contains an unterminated tag");
  const body = html.slice(cursor, end).replace(/\s*\/$/u, "");
  cursor = 0;
  while (cursor < body.length) {
    while (cursor < body.length && /\s/u.test(body[cursor])) cursor += 1;
    if (cursor >= body.length) break;
    const attributeMatch = /^[A-Za-z_:][A-Za-z0-9:._-]*/u.exec(body.slice(cursor));
    if (!attributeMatch) fail("index.html contains a malformed attribute");
    const name = attributeMatch[0].toLowerCase();
    cursor += attributeMatch[0].length;
    while (cursor < body.length && /\s/u.test(body[cursor])) cursor += 1;
    if (body[cursor] !== "=") {
      // HTML boolean attributes (for example Vite's `crossorigin`) have no
      // value. Relevant URL/rel attributes never have an implicit value.
      if (["src", "href", "rel"].includes(name)) fail(`index.html attribute ${name} must have a quoted value`);
      continue;
    }
    cursor += 1;
    while (cursor < body.length && /\s/u.test(body[cursor])) cursor += 1;
    const delimiter = body[cursor];
    if (delimiter !== '"' && delimiter !== "'") fail(`index.html attribute ${name} must have a quoted value`);
    cursor += 1;
    const valueStart = cursor;
    while (cursor < body.length && body[cursor] !== delimiter) cursor += 1;
    if (cursor >= body.length) fail("index.html contains an unterminated attribute value");
    const value = body.slice(valueStart, cursor);
    cursor += 1;
    if (["src", "href", "rel"].includes(name)) {
      if (attributes.has(name)) fail(`index.html contains duplicate ${name} attribute`);
      attributes.set(name, value);
    }
    if (cursor < body.length && !/\s/u.test(body[cursor])) fail("index.html attributes must be separated by whitespace");
  }
  return { tagName, end, attributes };
}

function indexReferences(htmlBytes) {
  const html = htmlBytes.toString("utf8");
  if (html.length > 4 * 1024 * 1024) fail("index.html exceeds parser bound");
  const scripts = [];
  const styles = [];
  const normalize = (value) => safePath(value.startsWith("/") ? value.slice(1) : value, "index.html reference");
  let cursor = 0;
  while (cursor < html.length) {
    const commentStart = html.indexOf("<!--", cursor);
    const tagStart = html.indexOf("<", cursor);
    if (tagStart < 0) break;
    if (commentStart >= 0 && commentStart === tagStart) {
      const commentEnd = html.indexOf("-->", commentStart + 4);
      if (commentEnd < 0) fail("index.html contains an unterminated comment");
      cursor = commentEnd + 3;
      continue;
    }
    const tagMatch = /^<(script|link)(?=[\s/>])/iu.exec(html.slice(tagStart));
    if (!tagMatch) { cursor = tagStart + 1; continue; }
    const tag = parseHtmlTag(html, tagStart);
    if (tag.tagName === "script" && tag.attributes.has("src")) scripts.push(normalize(tag.attributes.get("src")));
    if (tag.tagName === "link" && tag.attributes.has("href") && tag.attributes.get("rel")?.split(/\s+/u).map((item) => item.toLowerCase()).includes("stylesheet")) styles.push(normalize(tag.attributes.get("href")));
    cursor = tag.end + 1;
    if (tag.tagName === "script") {
      const closing = html.toLowerCase().indexOf("</script", cursor);
      if (closing < 0) fail("index.html contains an unterminated script element");
      cursor = closing;
    }
  }
  return { scripts, styles };
}

function graphFromManifest(manifest) {
  const reachable = new Set();
  const byKey = new Map(manifest.entries.map((entry) => [entry.key, entry]));
  const visit = (entry) => {
    if (reachable.has(entry.key)) return;
    reachable.add(entry.key);
    for (const imported of [...entry.imports, ...entry.dynamicImports]) {
      const target = byKey.get(imported);
      if (!target) fail(`Vite graph reference has no manifest entry: ${imported}`);
      visit(target);
    }
  };
  visit(manifest.entryPoint);
  if (reachable.size !== manifest.entries.length) fail("Vite manifest contains an entry outside the entrypoint graph");
  const files = new Set();
  for (const key of reachable) {
    const entry = byKey.get(key);
    files.add(entry.file);
    for (const ref of [...entry.css, ...entry.assets]) files.add(ref);
  }
  return { reachable, files, byKey };
}

function graphRecord(entry) {
  return { key: entry.key, file: entry.file, isEntry: entry.isEntry, isDynamicEntry: entry.isDynamicEntry, imports: [...entry.imports].sort(), dynamicImports: [...entry.dynamicImports].sort(), css: [...entry.css].sort(), assets: [...entry.assets].sort() };
}

async function makeCatalog(distDirectory) {
  const files = await readDist(distDirectory);
  const byPath = new Map(files.map((file) => [file.path, file]));
  const lowerPaths = new Set();
  for (const file of files) {
    const lower = file.path.toLowerCase();
    if (!lowerPaths.add(lower)) fail(`duplicate case-insensitive dist path: ${file.path}`);
  }
  const manifestFile = byPath.get(".vite/manifest.json");
  if (!manifestFile) fail("Vite manifest .vite/manifest.json is missing");
  const manifest = readManifest(await fs.readFile(manifestFile.absolute));
  const graph = graphFromManifest(manifest);
  const references = indexReferences(await fs.readFile(byPath.get("index.html")?.absolute ?? fail("entry document index.html is missing")));
  const expectedScripts = manifest.entries.filter((entry) => entry.isEntry).map((entry) => entry.file);
  const expectedStyles = [...new Set(manifest.entryPoint.css)].sort();
  if (stable(references.scripts.sort()) !== stable(expectedScripts.sort()) || stable(references.styles.sort()) !== stable(expectedStyles)) fail("index.html references do not match the Vite entrypoint graph");
  const payloadGraph = [...graph.files].sort();
  for (const graphFile of payloadGraph) if (!byPath.has(graphFile)) fail(`Vite graph references missing file: ${graphFile}`);
  for (const file of files) {
    if (!file.path.endsWith(".map") && file.path !== ".vite/manifest.json" && file.path !== "index.html") assertHashedPayload(file.path);
    if (!file.path.endsWith(".map") && file.path !== ".vite/manifest.json" && file.path !== "index.html" && !graph.files.has(file.path)) fail(`unreferenced payload is outside the Vite graph: ${file.path}`);
  }
  if (files.length < LIMITS.minCatalogFiles || files.length > LIMITS.maxFiles) fail(`dist file count must be between ${LIMITS.minCatalogFiles} and ${LIMITS.maxFiles}`);
  const catalogFiles = [];
  for (const file of files.sort((a, b) => ordinal(a.path, b.path))) {
    if (file.size > LIMITS.maxFileBytes) fail(`file exceeds ${LIMITS.maxFileBytes} bytes: ${file.path}`);
    const bytes = await fs.readFile(file.absolute);
    if (bytes.byteLength > LIMITS.maxFileBytes) fail(`file exceeds ${LIMITS.maxFileBytes} bytes: ${file.path}`);
    catalogFiles.push({ path: file.path, role: roleFor(file.path, { entryFiles: new Set(manifest.entries.filter((entry) => entry.isEntry).map((entry) => entry.file)) }), bytes: bytes.byteLength, sha256: sha256(bytes) });
  }
  for (const sourceMap of files.filter((file) => file.path.endsWith(".map"))) {
    const target = sourceMap.path.slice(0, -4);
    if (!byPath.has(target)) fail(`source map has no corresponding payload: ${sourceMap.path}`);
  }
  const catalog = { $schema: SCHEMA, schemaVersion: SCHEMA_VERSION, sha256: "", entrypoint: { document: "index.html", graph: manifest.entries.map(graphRecord).sort((a, b) => ordinal(a.key, b.key)) }, files: catalogFiles };
  catalog.sha256 = catalogDigest(catalog);
  return catalog;
}

function assertCatalogShape(catalog) {
  assertObject(catalog, "catalog");
  const catalogKeys = Object.keys(catalog);
  if (catalogKeys.length !== 5 || !["$schema", "schemaVersion", "sha256", "entrypoint", "files"].every((key) => catalogKeys.includes(key))) fail("catalog has unknown or missing fields");
  if (catalog.$schema !== SCHEMA || catalog.schemaVersion !== SCHEMA_VERSION) fail("catalog schema identity is invalid");
  if (!Array.isArray(catalog.files) || catalog.files.length < LIMITS.minCatalogFiles || catalog.files.length > LIMITS.maxFiles) fail("catalog files count is outside the schema bound");
  if (!Array.isArray(catalog.entrypoint?.graph) || catalog.entrypoint.graph.length < LIMITS.minGraphEntries || catalog.entrypoint.graph.length > LIMITS.maxGraphEntries) fail("catalog graph count is outside the schema bound");
  if (!HASH.test(catalog.sha256)) fail("catalog digest is invalid");
  if (catalogDigest(catalog) !== catalog.sha256) fail("catalog digest is invalid");
  if (Object.keys(catalog.entrypoint).length !== 2 || !Object.keys(catalog.entrypoint).every((key) => ["document", "graph"].includes(key))) fail("catalog entrypoint has unknown or missing fields");
  if (catalog.entrypoint.document !== "index.html") fail("catalog entry document must be index.html");
  const paths = new Set();
  for (const file of catalog.files) {
    assertObject(file, "catalog file");
    if (Object.keys(file).length !== 4 || !["path", "role", "bytes", "sha256"].every((key) => Object.keys(file).includes(key))) fail("catalog file has unknown or missing fields");
    for (const key of ["path", "role", "bytes", "sha256"]) if (!(key in file)) fail(`catalog file is missing ${key}`);
    const filePath = safePath(file.path, "catalog file path");
    const lower = filePath.toLowerCase();
    if (paths.has(lower)) fail(`duplicate case-insensitive catalog path: ${filePath}`);
    paths.add(lower);
    if (!ROLES.has(file.role) || !Number.isSafeInteger(file.bytes) || file.bytes < 0 || file.bytes > LIMITS.maxFileBytes || !HASH.test(file.sha256)) fail(`catalog file metadata is invalid: ${filePath}`);
  }
  for (const graph of catalog.entrypoint.graph) {
    assertObject(graph, "catalog graph entry");
    if (Object.keys(graph).length !== 8 || !["key", "file", "isEntry", "isDynamicEntry", "imports", "dynamicImports", "css", "assets"].every((key) => Object.keys(graph).includes(key))) fail("catalog graph entry has unknown or missing fields");
    for (const key of ["key", "file", "isEntry", "isDynamicEntry", "imports", "dynamicImports", "css", "assets"]) if (!(key in graph)) fail(`catalog graph entry is missing ${key}`);
    safePath(graph.key, "catalog graph key"); safePath(graph.file, "catalog graph file");
    for (const field of ["imports", "dynamicImports", "css", "assets"]) {
      if (!Array.isArray(graph[field]) || graph[field].some((value) => typeof value !== "string")) fail(`catalog graph ${field} is invalid`);
      if (new Set(graph[field].map((value) => value.toLowerCase())).size !== graph[field].length) fail(`catalog graph ${field} contains duplicate references`);
      graph[field].forEach((value) => safePath(value, `catalog graph ${field}`));
    }
    if (typeof graph.isEntry !== "boolean" || typeof graph.isDynamicEntry !== "boolean") fail("catalog graph flags are invalid");
  }
}

async function verifyCatalog(distDirectory, outputPath) {
  const bytes = await fs.readFile(outputPath).catch(() => fail(`catalog is missing: ${outputPath}`));
  if (!bytes.toString("utf8").endsWith("\n") || bytes.toString("utf8").includes("\r")) fail("catalog must be UTF-8 with one LF terminator");
  let catalog;
  try { catalog = JSON.parse(bytes.toString("utf8")); } catch { fail("catalog is not valid JSON"); }
  assertCatalogShape(catalog);
  const expected = await makeCatalog(distDirectory);
  if (stable(catalog) !== stable(expected)) fail("catalog does not exactly match the dist tree and Vite graph");
  if (Buffer.compare(bytes, jsonBytes(catalog)) !== 0) fail("catalog bytes are not deterministic sorted UTF-8 LF");
  return catalog;
}

async function main() {
  const [command = "verify", distArg, outputArg] = process.argv.slice(2);
  const distDirectory = path.resolve(distArg ?? DEFAULT_DIST);
  const outputPath = path.resolve(outputArg ?? DEFAULT_OUTPUT);
  if (command === "generate") {
    const temporaryPath = `${outputPath}.tmp-${process.pid}`;
    try {
      const existing = await fs.lstat(outputPath).catch(() => null);
      if (existing && !existing.isFile()) fail(`catalog output is not a regular file: ${outputPath}`);
      await fs.mkdir(path.dirname(outputPath), { recursive: true });
      const catalog = await makeCatalog(distDirectory);
      assertCatalogShape(catalog);
      await fs.writeFile(temporaryPath, jsonBytes(catalog), { encoding: "utf8", flag: "wx" });
      await verifyCatalog(distDirectory, temporaryPath);
      // Windows does not replace an existing destination with rename(). The
      // verified temporary file is ready before the old authority is removed;
      // any failure in this final handoff is handled by the catch cleanup.
      await fs.rm(outputPath, { force: true });
      await fs.rename(temporaryPath, outputPath);
      console.log(`Generated and verified ${path.relative(process.cwd(), outputPath)}`);
    } catch (error) {
      await fs.rm(temporaryPath, { force: true });
      const existing = await fs.lstat(outputPath).catch(() => null);
      if (existing?.isFile()) await fs.rm(outputPath, { force: true });
      throw error;
    }
  } else if (command === "verify") {
    await verifyCatalog(distDirectory, outputPath);
    console.log(`Verified ${path.relative(process.cwd(), outputPath)}`);
  } else fail(`unknown command ${command}; use generate or verify`);
}

export { assertCatalogShape, catalogDigest, makeCatalog, verifyCatalog, safePath, jsonBytes };

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url))) await main();
