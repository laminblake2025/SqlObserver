import test from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import catalog from '../src/features/targets/permissionTemplates.json' with { type: 'json' };
import { generatePermissionScript, permissionTemplateKey, validatePermissionScriptValues,
  permissionScriptProvenance } from '../src/features/targets/permissionScriptModel.ts';

const principal = "CONTOSO\\O'Brien]倉庫$";
const database = "O'Brien] 倉庫; --";
const values = { sqlServerMajorVersion: 16, principal, operation: 'Grant' };

test('all 27 authoritative variants select only explicit version/operation/optional grants', () => {
  for (const major of [15, 16, 17]) {
    for (let mask = 0; mask < 8; mask++) {
      const input = { ...values, sqlServerMajorVersion: major, logicalFiles: Boolean(mask & 1),
        ...(mask & 2 ? { queryStoreDatabase: database } : {}),
        replication: Boolean(mask & 4), ...(mask & 4 ? { distributionDatabase: 'distribution_01' } : {}) };
      assert.equal(permissionTemplateKey(input), `${major}:grant:${mask}`);
      const sql = generatePermissionScript(input);
      assert.ok(sql.includes(`@sqlobserver_expected_major int = ${major}`));
      assert.ok(sql.includes("N'CONTOSO\\O''Brien]倉庫$'"));
      assert.ok(sql.includes("[CONTOSO\\O'Brien]]倉庫$]"));
      assert.equal(sql.includes('GRANT VIEW ANY DEFINITION'), Boolean(mask & 1));
      assert.equal(sql.includes('Explicit Query Store observation access'), Boolean(mask & 2));
      assert.equal(sql.includes('M10 replication monitoring'), Boolean(mask & 4));
      if (mask & 2) {
        assert.ok(sql.includes("USE [O'Brien]] 倉庫; --];"));
        assert.ok(sql.includes("name = N'O''Brien] 倉庫; --'"));
        assert.ok(sql.includes(major === 15 ? 'GRANT VIEW DATABASE STATE' : 'GRANT VIEW DATABASE PERFORMANCE STATE'));
      }
      assert.ok(sql.includes(major === 15 ? 'GRANT VIEW SERVER STATE' : 'ALTER SERVER ROLE [##MS_ServerPerformanceStateReader##] ADD MEMBER'));
      assert.ok(!sql.includes('__SQLOBSERVER_'));
      assert.ok(!sql.includes('\r') && sql.endsWith('\n'));
      assert.ok(!/CREATE LOGIN|ALTER DATABASE|sp_configure|EVENT SESSION/iu.test(sql));
    }
    const sql = generatePermissionScript({ ...values, sqlServerMajorVersion: major, operation: 'Remove' });
    assert.ok(sql.includes("@sqlobserver_operation nvarchar(6) = N'Remove'"));
    assert.ok(sql.includes(major === 15 ? 'REVOKE VIEW SERVER STATE' : 'DROP MEMBER'));
    assert.ok(sql.includes('M9 grants are intentionally not revoked'));
    assert.ok(!sql.includes('Explicit logical-file') && !sql.includes('Explicit Query Store') && !sql.includes('M10 replication'));
  }
  assert.equal(permissionScriptProvenance().sourceSha256, catalog.sourceSha256);
});

test('SQL insertion escapes literal and identifier contexts without recursively replacing inserted text', () => {
  const input = { ...values, principal: 'DOMAIN\\__SQLOBSERVER_DATABASE_LITERAL__',
    queryStoreDatabase: '__SQLOBSERVER_PRINCIPAL_IDENTIFIER__' };
  const sql = generatePermissionScript(input);
  assert.ok(sql.includes("@sqlobserver_principal sysname = N'DOMAIN\\__SQLOBSERVER_DATABASE_LITERAL__'"));
  assert.ok(sql.includes('USE [__SQLOBSERVER_PRINCIPAL_IDENTIFIER__];'));
  assert.ok(!sql.includes('USE [DOMAIN\\'));
});

test('principal validation follows Windows syntax, UTF-16 limit and authoritative whitespace/control checks', () => {
  for (const principal of ['sa', 'sysadmin', ' DOMAIN\\svc', 'DOMAIN\\svc ', 'DOMAIN\\svc\nGO', 'DOMAIN\\svc\u0085', 'DOMAIN\\svc\u0000', 'DOMAIN\\a\\b', 'domain@a@b', 'name @domain', 'D\\' + 'a'.repeat(127), 'D\\\ud800'])
    assert.throws(() => validatePermissionScriptValues({ ...values, principal }), /Windows/u, principal);
  for (const principal of ['DOMAIN\\svc$', 'svc@domain.example', 'DOMAIN\\name@domain', 'D\\' + 'a'.repeat(126), 'D\\' + '💻'.repeat(63), 'D\\\ufeffname'])
    assert.equal(validatePermissionScriptValues({ ...values, principal }).principal, principal);
  for (const sqlServerMajorVersion of [0, 12, 18, 16.1, '16', NaN]) assert.throws(() => generatePermissionScript({ ...values, sqlServerMajorVersion }), /Choose SQL/u);
});

test('optional database access rejects malformed/system names and provenance-free removal', () => {
  for (const queryStoreDatabase of ['', 'master', 'TeMpDb', 'model', 'msdb', ' Accounting', 'Accounting ', 'Accounting\nUSE master', 'a'.repeat(129)])
    assert.throws(() => generatePermissionScript({ ...values, queryStoreDatabase }), /user database/u);
  for (const distributionDatabase of ['', 'distribution]', 'distribution db', 'db;GO', '.', '..', 'a'.repeat(129)])
    assert.throws(() => generatePermissionScript({ ...values, replication: true, distributionDatabase }), /distribution database/u);
  assert.throws(() => generatePermissionScript({ ...values, distributionDatabase: 'distribution' }), /explicit replication/u);
  assert.throws(() => generatePermissionScript({ ...values, replication: true }), /distribution database/u);
  for (const options of [{ logicalFiles: true }, { queryStoreDatabase: database }, { replication: true, distributionDatabase: 'distribution' }])
    assert.throws(() => generatePermissionScript({ ...values, ...options, operation: 'Remove' }), /base capability/u);
});

test('malformed, missing and unexpected catalog templates fail closed before producing output', () => {
  for (const change of [data => { data.schema = 2; }, data => { data.sourceSha256 = 'wrong'; },
    data => { delete data.templates['15:grant:0']; }, data => { data.templates.unknown = 'SQL\n'; },
    data => { data.templates['16:grant:0'] += '__SQLOBSERVER_UNKNOWN42__\n'; },
    data => { data.templates['16:grant:0'] = data.templates['16:grant:0'].replaceAll('__SQLOBSERVER_PRINCIPAL_LITERAL__', 'missing'); },
    data => { data.templates['16:grant:0'] += '__SQLOBSERVER_DATABASE_LITERAL__\n'; },
    data => { data.templates['16:grant:0'] = 'x'.repeat(65537) + '\n'; }]) {
    const damaged = structuredClone(catalog); change(damaged);
    assert.throws(() => generatePermissionScript(values, damaged), /catalog/u);
  }
});

// Explicit opt-in keeps the ordinary frontend suite portable. This only runs the offline generator.
test('browser output is byte-for-byte canonical PowerShell output for escaped optional grants and removal',
  { skip: process.env.SQLOBSERVER_PERMISSION_PARITY !== '1' }, () => {
    const scriptPath = fileURLToPath(new URL('../../tools/generate-permissions.ps1', import.meta.url));
    for (const input of [...[15, 16, 17].map(sqlServerMajorVersion => ({ ...values, sqlServerMajorVersion,
      logicalFiles: true, queryStoreDatabase: database, replication: true, distributionDatabase: 'distribution_01' })),
      { ...values, operation: 'Remove' },
      ...[13, 14].flatMap(sqlServerMajorVersion => [
        ...[false, true].map(logicalFiles => ({ ...values, sqlServerMajorVersion, logicalFiles })),
        { ...values, sqlServerMajorVersion, operation: 'Remove' }])]) {
      const args = ['-NoProfile', '-File', scriptPath, '-SqlServerMajorVersion', String(input.sqlServerMajorVersion),
        '-Principal', input.principal, '-Operation', input.operation];
      if (input.logicalFiles) args.push('-LogicalFiles');
      if (input.queryStoreDatabase) args.push('-QueryStoreDatabase', input.queryStoreDatabase);
      if (input.replication) args.push('-Replication', '-DistributionDatabase', input.distributionDatabase);
      const trustedPowerShell = process.env.SQLOBSERVER_M12_TRUSTED_PWSH_PATH;
      assert.ok(trustedPowerShell, 'Permission parity requires an explicitly trusted PowerShell runtime.');
      const expected = execFileSync(trustedPowerShell, args, { encoding: 'utf8', timeout: 15000, windowsHide: true });
      assert.equal(generatePermissionScript(input), expected);
    }
  });

for (const sqlServerMajorVersion of [13, 14]) test(`SQL Server ${sqlServerMajorVersion} grants core state without unqualified advanced access`, () => {
  const input = { ...values, sqlServerMajorVersion };
  const sql = generatePermissionScript(input);
  assert.ok(sql.includes('GRANT VIEW SERVER STATE'));
  assert.ok(sql.includes('REVOKE VIEW SERVER STATE'));
  assert.ok(sql.includes('PARSENAME'));
  assert.ok(!sql.includes('USE [msdb]'));
  assert.ok(!sql.includes('ADD MEMBER'));
  assert.ok(generatePermissionScript({ ...input, logicalFiles: true }).includes('GRANT VIEW ANY DEFINITION'));
  assert.throws(() => generatePermissionScript({ ...input, queryStoreDatabase: database }), /not yet qualified/u);
  assert.throws(() => generatePermissionScript({ ...input, replication: true, distributionDatabase: 'distribution' }), /not yet qualified/u);
});

test('SQL Server 2014 is below the active minimum', () => {
  assert.throws(() => generatePermissionScript({ ...values, sqlServerMajorVersion: 12 }), /Choose SQL Server 2016/u);
});
