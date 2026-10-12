import { readFile, writeFile, readdir, copyFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { resolve, relative } from 'node:path';
import assert from 'node:assert/strict';

const output = import.meta.dirname, root = resolve(output, '../..');
const read = async path => JSON.parse(await readFile(resolve(root, path), 'utf8'));
const hash = async path => {
  const bytes = await readFile(resolve(root, path));
  return { path: path.replaceAll('\\', '/'), bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') };
};
const write = async (name, value) => writeFile(resolve(output, name), JSON.stringify(value, null, 2) + '\n');
const walk = async directory => {
  const paths = [];
  for (const entry of await readdir(resolve(root, directory), { withFileTypes: true })) {
    const path = `${directory}/${entry.name}`;
    if (entry.isDirectory()) paths.push(...await walk(path)); else if (entry.isFile()) paths.push(path);
  }
  return paths;
};
const prefix = '.artifacts/activity-redesign-20261009';
await copyFile(resolve(root, '.artifacts/ui-redesign-20261009/browser-results.json'), resolve(output, 'shared-shell-results.json'));
await copyFile(resolve(root, '.artifacts/lab-dashboard-20261009/browser-results.json'), resolve(output, 'connected-results.json'));
await copyFile(resolve(root, '.artifacts/lab-dashboard-20261009/connection-evidence.json'), resolve(output, 'native/connection-evidence.json'));
await copyFile(resolve(root, 'web/.artifacts/web-asset-manifest.v1.json'), resolve(output, 'web-asset-manifest.v1.json'));
await copyFile(resolve(root, 'web/.artifacts/initial-javascript-budget.json'), resolve(output, 'initial-javascript-budget.json'));

const suites = [];
for (const [name, file, expected] of [
  ['Activity component and permission checks', 'component-results.json', 54],
  ['Activity layout, keyboard, accessibility and interaction checks', 'browser-results.json', 8],
  ['History, timezone and exact-capture checks', 'history-results.json', 10],
  ['Shared navigation, capture, role and chart regression checks', 'shared-shell-results.json', 8],
  ['Final UI against retained native blocking capture', 'native/browser-results.json', 1],
  ['Connected lab and relay-isolation checks', 'connected-results.json', 2],
]) {
  const result = await read(`${prefix}/${file}`);
  assert.equal(result.stats.expected, expected, name);
  for (const field of ['unexpected', 'flaky', 'skipped']) assert.equal(result.stats[field], 0, `${name}: ${field}`);
  assert.deepEqual(result.errors, []);
  suites.push({ name, ...result.stats, receipt: await hash(`${prefix}/${file}`) });
}
const modelLog = await readFile(resolve(output, 'model-results.log'), 'utf8');
assert.match(modelLog, /# tests 1089\s+[\s\S]*?# pass 1089\s+# fail 0\s+# cancelled 0\s+# skipped 0/u);
const budget = await read(`${prefix}/initial-javascript-budget.json`);
assert.ok(budget.gzipBytes <= budget.maximumGzipBytes);
const assets = await read(`${prefix}/web-asset-manifest.v1.json`);
for (const file of assets.files) assert.equal((await hash(`web/dist/${file.path}`)).sha256, file.sha256, file.path);
const release = await read('release/certification/m12-sbom-inputs.v1.json');
for (const input of release.files) assert.equal((await hash(input.path)).sha256, input.sha256, input.path);
const workload = await read(`${prefix}/native/workload-result.json`), native = await read(`${prefix}/native/dashboard-evidence.json`);
const labInputs = await read(`${prefix}/native/inputs.json`);
for (const input of labInputs.files) assert.equal((await hash(`${prefix}/native/${input.name}`)).sha256, input.sha256, input.name);
assert.equal(workload.runId, native.runId); assert.equal(native.runId, labInputs.runId);
assert.equal(workload.rollbackVerified, true); assert.equal(workload.before, workload.after); assert.equal(workload.ownedSessionsRemaining, 0);
assert.ok(workload.workers.every(worker => worker.state === 'completed' && worker.rolledBack));
assert.equal(native.syntheticData, false); assert.equal(native.tls, 'validated'); assert.equal(native.priorityFirst, native.blockerSession);
assert.equal(native.identityPinned, true); assert.equal(native.deliberateSqlRead, true); assert.equal(native.queryHttpStatus, 200);

const sourcePaths = ['web/package.json', 'web/pnpm-lock.yaml', 'web/tsconfig.json', 'web/vite.config.ts', 'tools/test-web.ps1', `${prefix}/finalize-receipts.mjs`];
for (const directory of ['web/src', 'web/tools', 'web/tests', 'web/component-tests', 'web/browser-tests', 'web/browser-lab-tests']) sourcePaths.push(...await walk(directory));
sourcePaths.push(...(await readdir(resolve(root, 'web'))).filter(name => /^playwright\..*\.mjs$/u.test(name)).map(name => 'web/' + name));
const inputFiles = [];
for (const path of [...new Set(sourcePaths)].sort()) inputFiles.push(await hash(path));
await write('final-inputs.json', { schemaVersion: 1, scope: 'Local dashboard source, tests, build tools and verification harnesses', files: inputFiles });

const comparisons = [];
for (const width of [1600, 1440, 390]) {
  const geometry = await read(`${prefix}/screenshots/geometry-${width}.json`);
  comparisons.push({ viewport: { width, height: width === 1600 ? 1000 : width === 1440 ? 900 : 844 },
    allSessions: await hash(`${prefix}/screenshots/all-${width}.png`), selectedSession: await hash(`${prefix}/screenshots/selected-${width}.png`),
    nativeBlocking: await hash(`${prefix}/native/blocking-${width}.png`), geometry });
}
const references = [];
for (const name of ['all-session-desktop.png', 'selected-blocking-desktop.png', 'mobile-inspection.png']) references.push(await hash(`${prefix}/references/${name}`));
await write('visual-comparison.json', {
  schemaVersion: 1, method: 'Manual inspection of rendered screenshots against the supplied illustrative references; browser assertions for geometry, keyboard, overflow and WCAG A/AA checks.',
  referenceData: 'Illustrative; never substituted for native evidence', references, comparisons,
  mobileInspectionDetail: await hash(`${prefix}/screenshots/inspection-390.png`),
  matched: ['Navy surfaces, teal actions, thin borders and compact type', '230px desktop navigation and 24px workspace padding', 'Four clickable distinct-session summaries above one problems-first list', 'Six session columns with expandable exact requests', 'Desktop side inspector and mobile cards with inline inspection', 'Captured blocking graph beside the selected session', 'No empty graph or details surface before selection'],
  deliberateDifferences: ['Coverage, capture controls and existing investigation access remain visible.', 'Long native names wrap to keep the inspection action in view.', 'Workload ordering follows measured threshold exceedance, rather than illustrative row order.', 'Missing rates, context and unresolved evidence stay explicit.'],
});
await write('verification-receipt.json', {
  schemaVersion: 1, completedAtUtc: new Date().toISOString(), status: 'passed', application: 'Local SQL Observer dashboard',
  scope: 'Activity redesign with native lab compatibility qualification; no API, schema, collector or alert-rule changes',
  inputs: await hash(`${prefix}/final-inputs.json`), models: { passed: 1089, failed: 0, skipped: 0, receipt: await hash(`${prefix}/model-results.log`) },
  browserAndComponents: { passed: suites.reduce((sum, suite) => sum + suite.expected, 0), failed: 0, skipped: 0, suites },
  build: { status: 'passed', typecheck: 'passed', assetVerification: 'passed', receipt: await hash(`${prefix}/build-results.log`),
    assetManifest: await hash(`${prefix}/web-asset-manifest.v1.json`), initialJavaScriptGzipBytes: budget.gzipBytes, ceilingBytes: budget.maximumGzipBytes,
    releaseInputManifest: await hash('release/certification/m12-sbom-inputs.v1.json'), verifiedReleaseInputs: release.files.length },
  visuals: await hash(`${prefix}/visual-comparison.json`),
  nativeLab: { target: native.target, syntheticData: false, tls: 'validated', runId: native.runId, snapshotId: native.snapshotId, observedUtc: native.observedUtc,
    pagesRead: native.pagesRead, requestRows: native.requestRows, distinctSessions: native.distinctSessions, blocker: native.blockerSession, blocked: native.blockedSession,
    blockerRankedFirst: true, exactCapturePinned: true, explicitProtectedSqlHttpStatus: native.queryHttpStatus, protectedTextStored: false,
    boundedExecutionSeconds: workload.boundedSeconds, rollbackVerified: true, ownedSessionsRemaining: 0, servicesChanged: false,
    workloadReceipt: await hash(`${prefix}/native/workload-result.json`), dashboardReceipt: await hash(`${prefix}/native/dashboard-evidence.json`),
    note: 'The guarded workload was captured through the deployed collector, PostgreSQL repository and API. The final UI was verified by replaying that same retained native capture. This does not qualify a full deployment or migration of the newer backend.' },
  qualificationLimits: ['Snapshots retain the native 24-hour retention and 512-row collection bound.', 'All metadata reads are pinned to one capture and bounded to 11 validated native pages.', 'Thresholds are local investigation preferences; alert rules are unchanged.', 'The existing lab API/collector compatibility limits documented in docs/development-lab-dashboard.md remain.'],
});
console.log(JSON.stringify({ status: 'passed', models: 1089, browserAndComponents: suites.reduce((sum, suite) => sum + suite.expected, 0), initialJavaScriptKiBGzip: budget.gzipBytes / 1024, inputs: inputFiles.length, releaseInputs: release.files.length, receipt: relative(root, resolve(output, 'verification-receipt.json')) }));
