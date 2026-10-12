import { readFile, writeFile } from 'node:fs/promises';
import { gzipSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { resolve } from 'node:path';

const root = resolve(import.meta.dirname, '../dist');
const manifestBytes = await readFile(resolve(root, '.vite/manifest.json'));
const manifest = JSON.parse(manifestBytes);
const overviewKey = Object.keys(manifest).find(key => key.endsWith('/OverviewPage.tsx'));
if (!overviewKey || !manifest['index.html']?.isEntry) throw new Error('Initial budget requires the verified entrypoint and Fleet route.');
const visited = new Set(), files = new Set();
function visit(key) {
  if (visited.has(key)) return;
  const entry = manifest[key];
  if (!entry) throw new Error(`Missing initial dependency ${key}`);
  visited.add(key);
  if (entry.file.endsWith('.js')) files.add(entry.file);
  for (const dependency of entry.imports ?? []) visit(dependency);
}
visit('index.html'); visit(overviewKey);
const assets = [];
for (const file of [...files].sort()) {
  const bytes = await readFile(resolve(root, file));
  assets.push({ file, bytes: bytes.length, gzipBytes: gzipSync(bytes).length, sha256: createHash('sha256').update(bytes).digest('hex') });
}
const gzipBytes = assets.reduce((sum, asset) => sum + asset.gzipBytes, 0);
if (gzipBytes > 250 * 1024) throw new Error(`Initial Fleet JavaScript ${gzipBytes} bytes gzip exceeds 250 KiB.`);
const receipt = { schemaVersion: 1, maximumGzipBytes: 250 * 1024, gzipBytes, manifestSha256: createHash('sha256').update(manifestBytes).digest('hex'), assets };
await writeFile(resolve(import.meta.dirname, '../.artifacts/initial-javascript-budget.json'), JSON.stringify(receipt, null, 2) + '\n');
console.log(`Initial Fleet JavaScript: ${(gzipBytes / 1024).toFixed(2)} KiB gzip (limit 250 KiB).`);
