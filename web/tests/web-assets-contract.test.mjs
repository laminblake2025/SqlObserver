import assert from "node:assert/strict";
import { access, mkdtemp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { execFile as execFileCallback } from "node:child_process";
import { promisify } from "node:util";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { assertCatalogShape, catalogDigest, jsonBytes, makeCatalog, safePath, verifyCatalog } from "../tools/web-asset-manifest.mjs";
import { fileURLToPath } from "node:url";

const execFile = promisify(execFileCallback);
const toolPath = fileURLToPath(new URL("../tools/web-asset-manifest.mjs", import.meta.url));

async function fixture() {
  const root = await mkdtemp(path.join(os.tmpdir(), "sqlobserver-web-assets-"));
  await mkdir(path.join(root, ".vite"), { recursive: true });
  await mkdir(path.join(root, "assets"), { recursive: true });
  await writeFile(path.join(root, "assets", "entry-main-AbCd1234.js"), "console.log('identity');\n");
  await writeFile(path.join(root, "assets", "main-DeF45678.css"), "body{color:black}\n");
  await writeFile(path.join(root, "assets", "entry-main-AbCd1234.js.map"), "{}\n");
  await writeFile(path.join(root, "index.html"), '<script type="module" src="/assets/entry-main-AbCd1234.js"></script><link rel="stylesheet" href="/assets/main-DeF45678.css">\n');
  await writeFile(path.join(root, ".vite", "manifest.json"), JSON.stringify({
    "index.html": { file: "assets/entry-main-AbCd1234.js", name: "main", src: "index.html", isEntry: true, css: ["assets/main-DeF45678.css"] },
  }));
  return root;
}

test("asset catalog bytes are deterministic and bind content", async () => {
  const root = await fixture();
  try {
    const first = await makeCatalog(root);
    const second = await makeCatalog(root);
    assert.deepEqual(first, second);
    assert.deepEqual(first.files.map((file) => file.path), [
      ".vite/manifest.json",
      "assets/entry-main-AbCd1234.js",
      "assets/entry-main-AbCd1234.js.map",
      "assets/main-DeF45678.css",
      "index.html",
    ]);
    assert.equal(first.files.find((file) => file.path.endsWith(".map")).role, "build-metadata");
    const output = `${root}.catalog.json`;
    await writeFile(output, jsonBytes(first));
    await verifyCatalog(root, output);
    await writeFile(path.join(root, "assets", "entry-main-AbCd1234.js"), "console.log('changed');\n");
    await assert.rejects(() => verifyCatalog(root, output), /does not exactly match/);
  } finally { await rm(root, { recursive: true, force: true }); await rm(`${root}.catalog.json`, { force: true }); }
});

test("unchanged names cannot hide payload tampering", async () => {
  const root = await fixture();
  try {
    const output = `${root}.catalog.json`;
    await writeFile(output, jsonBytes(await makeCatalog(root)));
    const asset = path.join(root, "assets", "main-DeF45678.css");
    await writeFile(asset, "body{color:red}\n");
    await assert.rejects(() => verifyCatalog(root, output), /does not exactly match/);
  } finally { await rm(root, { recursive: true, force: true }); await rm(`${root}.catalog.json`, { force: true }); }
});

test("unsafe paths, duplicate identities, and un-hashed payloads fail closed", async () => {
  assert.throws(() => safePath("../index.html", "test"), /relative path|normalized/);
  assert.throws(() => safePath("assets\\main.js", "test"), /relative path|normalized/);
  const root = await fixture();
  try {
    await writeFile(path.join(root, "assets", "runtime.js"), "bad");
    await assert.rejects(() => makeCatalog(root), /not content-hashed/);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("Vite manifest is a closed graph with exact HTML references", async () => {
  const root = await fixture();
  try {
    const manifestPath = path.join(root, ".vite", "manifest.json");
    const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
    manifest["index.html"].unexpected = true;
    await writeFile(manifestPath, JSON.stringify(manifest));
    await assert.rejects(() => makeCatalog(root), /unknown Vite manifest field/);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("optional Vite arrays reject explicit null and HTML ignores data-* lookalikes", async () => {
  const root = await fixture();
  try {
    const manifestPath = path.join(root, ".vite", "manifest.json");
    const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
    manifest["index.html"].css = null;
    await writeFile(manifestPath, JSON.stringify(manifest));
    await assert.rejects(() => makeCatalog(root), /must be an array of strings/);
    manifest["index.html"].css = ["assets/main-DeF45678.css"];
    await writeFile(manifestPath, JSON.stringify(manifest));
    await writeFile(path.join(root, "index.html"), '<script type="module" data-src="/wrong.js"></script><link data-rel="stylesheet" data-href="/wrong.css">\n');
    await assert.rejects(() => makeCatalog(root), /references do not match/);
    await writeFile(path.join(root, "index.html"), '<script type="module" src="/assets/entry-main-AbCd1234.js" data-src="/wrong.js"></script><link rel="stylesheet" href="/assets/main-DeF45678.css" data-href="/wrong.css">\n');
    await makeCatalog(root);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("failed CLI regeneration removes the prior authoritative catalog", async () => {
  const root = await fixture();
  const artifactRoot = `${root}.artifacts`;
  const output = path.join(artifactRoot, "web-asset-manifest.v1.json");
  try {
    await execFile(process.execPath, [toolPath, "generate", root, output], { windowsHide: true });
    await access(output);
    await writeFile(path.join(root, ".vite", "manifest.json"), "{\"index.html\":null}");
    await assert.rejects(() => execFile(process.execPath, [toolPath, "generate", root, output], { windowsHide: true }));
    await assert.rejects(() => access(output));
    await assert.rejects(() => access(`${output}.tmp-${process.pid}`));
  } finally { await rm(root, { recursive: true, force: true }); await rm(artifactRoot, { recursive: true, force: true }); }
});

test("schema boundaries are enforced without allocating a giant file", async () => {
  const root = await fixture();
  try {
    const base = await makeCatalog(root);
    const files = Array.from({ length: 4096 }, (_, index) => ({
      path: `assets/payload-${String(index).padStart(4, "0")}-AbCd1234.js`, role: "chunk", bytes: 0, sha256: "0".repeat(64),
    }));
    const atLimit = { ...base, files, sha256: "" };
    atLimit.sha256 = catalogDigest(atLimit);
    assert.doesNotThrow(() => assertCatalogShape(atLimit));
    const overFiles = { ...atLimit, files: [...files, { ...files[0], path: "assets/payload-over-AbCd1234.js" }], sha256: "" };
    overFiles.sha256 = catalogDigest(overFiles);
    assert.throws(() => assertCatalogShape(overFiles), /files count/);
    const overGraph = { ...atLimit, entrypoint: { document: "index.html", graph: Array.from({ length: 4097 }, (_, index) => ({ key: `chunks/chunk-${index}`, file: "assets/payload-0000-AbCd1234.js", isEntry: index === 0, isDynamicEntry: false, imports: [], dynamicImports: [], css: [], assets: [] })) }, sha256: "" };
    overGraph.sha256 = catalogDigest(overGraph);
    assert.throws(() => assertCatalogShape(overGraph), /graph count/);
    const atBytesLimit = { ...atLimit, files: [{ ...files[0], bytes: 4294967296 }, files[1]], sha256: "" };
    atBytesLimit.sha256 = catalogDigest(atBytesLimit);
    assert.doesNotThrow(() => assertCatalogShape(atBytesLimit));
    const overBytes = { ...atBytesLimit, files: [{ ...files[0], bytes: 4294967297 }, files[1]], sha256: "" };
    overBytes.sha256 = catalogDigest(overBytes);
    assert.throws(() => assertCatalogShape(overBytes), /metadata is invalid/);
    assert.doesNotThrow(() => safePath("a".repeat(512), "path"));
    assert.throws(() => safePath("a".repeat(513), "path"), /safe POSIX/);
  } finally { await rm(root, { recursive: true, force: true }); }
});

test("generator rejects file and graph counts above the schema boundary", async () => {
  const root = await fixture();
  try {
    await Promise.all(Array.from({ length: 4097 }, (_, index) => writeFile(path.join(root, "assets", `extra-${String(index).padStart(4, "0")}-AbCd1234.js`), "x")));
    await assert.rejects(() => makeCatalog(root), /more than 4096 files/);
  } finally { await rm(root, { recursive: true, force: true }); }
  const graphRoot = await fixture();
  try {
    const entries = { "index.html": { file: "assets/entry-main-AbCd1234.js", src: "index.html", isEntry: true } };
    for (let index = 1; index < 4097; index += 1) entries[`chunks/chunk-${index}`] = { file: "assets/entry-main-AbCd1234.js" };
    await writeFile(path.join(graphRoot, ".vite", "manifest.json"), JSON.stringify(entries));
    await assert.rejects(() => makeCatalog(graphRoot), /more than 4096 entries/);
  } finally { await rm(graphRoot, { recursive: true, force: true }); }
});
