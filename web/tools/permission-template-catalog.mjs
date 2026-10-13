import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, rmdirSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const source = path.join(root, 'tools/generate-permissions.ps1');
const output = path.join(root, 'web/src/features/targets/permissionTemplates.json');
const normalize = value => value.replace(/^\uFEFF/, '').replace(/\r\n?/g, '\n');
const digest = value => createHash('sha256').update(value).digest('hex');
const sourceDigest = () => digest(normalize(readFileSync(source, 'utf8')));
const principal = "SQLOBSERVER_TEMPLATE\\principal'identity]";
const queryDatabase = "SQLOBSERVER_QUERY'database]";
const distribution = 'SQLOBSERVER_DISTRIBUTION_DATABASE';
const replacements = [
  [principal.replaceAll("'", "''"), '__SQLOBSERVER_PRINCIPAL_LITERAL__'],
  [principal.replaceAll(']', ']]'), '__SQLOBSERVER_PRINCIPAL_IDENTIFIER__'],
  [queryDatabase.replaceAll("'", "''"), '__SQLOBSERVER_DATABASE_LITERAL__'],
  [queryDatabase.replaceAll(']', ']]'), '__SQLOBSERVER_DATABASE_IDENTIFIER__'],
  [distribution, '__SQLOBSERVER_DISTRIBUTION_DATABASE__'],
];

export function verifyPermissionTemplates() {
  const catalog = JSON.parse(readFileSync(output, 'utf8'));
  if (catalog.schema !== 1 || catalog.sourceSha256 !== sourceDigest() ||
      catalog.templatesSha256 !== digest(JSON.stringify(catalog.templates)) ||
      Object.keys(catalog.templates ?? {}).length !== 30) {
    throw new Error('Permission templates are stale. Run node tools/permission-template-catalog.mjs --generate from web.');
  }
  for (const major of [12, 15, 16, 17]) {
    for (let mask = 0; mask < (major === 12 ? 2 : 8); mask++) {
      if (typeof catalog.templates[`${major}:grant:${mask}`] !== 'string') throw new Error('Permission template is missing.');
    }
    if (typeof catalog.templates[`${major}:remove`] !== 'string') throw new Error('Permission removal template is missing.');
  }
  return catalog;
}

function generate() {
  // Invoke the authoritative offline generator. None of these scripts execute SQL.
  const scratch = mkdtempSync(path.join(tmpdir(), 'sqlobserver-permission-templates-'));
  const templates = {};
  const files = [];
  try {
    for (const major of [12, 15, 16, 17]) {
      for (let mask = 0; mask <= 8; mask++) {
        if (major === 12 && ![0, 1, 8].includes(mask)) continue;
        const remove = mask === 8;
        const key = remove ? `${major}:remove` : `${major}:grant:${mask}`;
        const filename = path.join(scratch, `${major}-${mask}.sql`);
        files.push(filename);
        const args = ['-NoLogo', '-NoProfile', '-NonInteractive', '-File', source,
          '-SqlServerMajorVersion', String(major), '-Principal', principal,
          '-Operation', remove ? 'Remove' : 'Grant', '-OutputPath', filename];
        if (!remove && mask & 1) args.push('-LogicalFiles');
        if (!remove && mask & 2) args.push('-QueryStoreDatabase', queryDatabase);
        if (!remove && mask & 4) args.push('-Replication', '-DistributionDatabase', distribution);
        execFileSync(process.env.SQLOBSERVER_PWSH ?? 'pwsh', args, { encoding: 'utf8', windowsHide: true, stdio: 'pipe' });
        let sql = normalize(readFileSync(filename, 'utf8'));
        for (const [value, token] of replacements) sql = sql.replaceAll(value, token);
        if (sql.includes('SQLOBSERVER_TEMPLATE\\') || sql.includes("SQLOBSERVER_QUERY'") || !sql.endsWith('\n')) {
          throw new Error('Permission generator output could not be templated.');
        }
        templates[key] = sql;
      }
    }
    const catalog = { schema: 1, sourceSha256: sourceDigest(), templatesSha256: digest(JSON.stringify(templates)), templates };
    writeFileSync(output, JSON.stringify(catalog, null, 2) + '\n', 'utf8');
    verifyPermissionTemplates();
    console.log('Generated and verified 30 permission templates from tools/generate-permissions.ps1.');
  } finally {
    // Delete only the individual files we created, then the empty task directory.
    for (const file of files) rmSync(file, { force: true });
    rmdirSync(scratch);
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv[2] === '--generate') generate();
  else if (process.argv[2] === '--verify') { verifyPermissionTemplates(); console.log('Permission templates verified.'); }
  else throw new Error('Use --generate or --verify.');
}
