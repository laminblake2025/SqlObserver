[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string] $Principal,
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Offline preview only: no SQL client, connection, process invocation, installation or removal.
if ($Principal.Length -gt 128 -or $Principal -cne $Principal.Trim() -or
    @($Principal.ToCharArray() | Where-Object { [char]::IsControl($_) }).Count -gt 0 -or
    ($Principal -notmatch '^[^\\@]+\\[^\\]+$' -and $Principal -notmatch '^[^@\\\s]+@[^@\\\s]+$')) {
    throw 'Principal must be a trimmed Windows DOMAIN\name or user@domain identity without control characters, at most 128 characters.'
}
$modulePath = Join-Path $PSScriptRoot 'capture-v1.sql'
$manifestBytes = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'capture-v1.manifest.json'))
$expectedManifestHash = '0018e18e3d46aaa18a5665a4c88c0a8e576a348eacd132b2a4a5c56cb90d9c6a'
if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifestBytes)).ToLowerInvariant() -cne $expectedManifestHash) {
    throw 'Qualification manifest differs from the approved exact contract, including metadata and field inventory.'
}
$manifest = [Text.UTF8Encoding]::new($false, $true).GetString($manifestBytes) | ConvertFrom-Json
$verificationBytes = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot 'verify-v1.sql'))
if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($verificationBytes)).ToLowerInvariant() -cne $manifest.verificationUtf8Sha256) {
    throw 'DBA inspection asset differs from the approved exact contract.'
}
$moduleBytes = [IO.File]::ReadAllBytes($modulePath)
$expectedModuleHash = '0ac04136ac0f04b718df47eb7e7f3fe07bd66c00f94b976e175bad5fefdd8991'
$moduleHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($moduleBytes)).ToLowerInvariant()
$moduleText = [Text.UTF8Encoding]::new($false, $true).GetString($moduleBytes)
$definitionHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::Unicode.GetBytes($moduleText))).ToLowerInvariant()
if ($moduleHash -cne $expectedModuleHash -or $manifest.moduleUtf8Sha256 -cne $expectedModuleHash -or
    $definitionHash -cne $manifest.expectedSqlDefinitionUtf16LeSha256 -or $moduleBytes.Contains([byte]13) -or
    $manifest.schemaVersion -ne 1 -or $manifest.sqlMajorVersion -ne 17 -or $manifest.maximumRows -ne 513 -or
    $manifest.installerImplemented -ne $false -or $manifest.removalImplemented -ne $false) {
    throw 'Qualification assets differ from the approved fixed module contract.'
}
$quotedIdentifier = '[' + $Principal.Replace(']', ']]') + ']'
$quotedLiteral = "N'" + $Principal.Replace("'", "''") + "'"
$plan = [ordered]@{
    schemaVersion = 1
    operation = 'offline_preview_only'
    installerImplemented = $false
    removalImplemented = $false
    executableInstallOrRemovalSqlGenerated = $false
    principal = $Principal
    principalSqlIdentifier = $quotedIdentifier
    principalSqlLiteral = $quotedLiteral
    module = $manifest
    proposedObjects = @(
        'master user SqlObserverAgentActivitySchemaOwnerV1 WITHOUT LOGIN',
        'master schema sqlobserver_agent AUTHORIZATION SqlObserverAgentActivitySchemaOwnerV1',
        'master procedure sqlobserver_agent.capture_v1 EXECUTE AS CALLER',
        'master certificate SqlObserverAgentActivityCertificateV1',
        'server certificate login SqlObserverAgentActivityCertificateLoginV1',
        'msdb public-only certificate SqlObserverAgentActivityCertificateV1',
        'msdb certificate user SqlObserverAgentActivityCertificateUserV1'
    )
    proposedGrants = @(
        'server: VIEW SERVER SECURITY STATE -> certificate login only',
        'msdb: CONNECT -> certificate user only',
        'msdb: SELECT on dbo.syssessions -> certificate user only',
        'msdb: SELECT on dbo.sysjobactivity -> certificate user only',
        ('master: EXECUTE on sqlobserver_agent.capture_v1 -> ' + $quotedIdentifier)
    )
    preconditions = @(
        'DBA confirms SQL17 Windows Standard/Enterprise and the exact intended target.',
        'Existing non-sysadmin Windows login and existing master user with EXACTLY the supplied Principal name must map to the same intended SID; an alias user name is not accepted, and no login or monitor mapping is created.',
        'All seven package objects must be absent. Any same-name object, signature, schema ownership or metadata ambiguity blocks installation; no adoption or overwrite.',
        'Record original monitor/server/database grants and relevant DENYs. Do not remove DENYs or other grants automatically.',
        'Record instance identity, deployment UUID, asset hashes and preimages before any authorized transaction.',
        'Prepare reviewed cross-database transaction and exact recovery before executing DDL. This package does not implement that transaction.'
    )
    proposedInstallSequence = @(
        'Under separately approved DBA execution, create only the absent named principals/schema/certificate/module; execute capture-v1.sql as its own batch with ANSI_NULLS and QUOTED_IDENTIFIER ON.',
        'Create the certificate login in master and grant its one server permission.',
        'Read CERTENCODED(CERT_ID(...)) in master; import those public bytes with CREATE CERTIFICATE ... FROM BINARY in msdb, without WITH PRIVATE KEY; create its certificate user and grant the three listed database permissions.',
        'ADD SIGNATURE TO OBJECT::sqlobserver_agent.capture_v1 BY CERTIFICATE SqlObserverAgentActivityCertificateV1 using DBA-owned signing material. Do not retain signing secrets in files or output.',
        'Verify exact definition hash, caller context, public-certificate identity and cryptographic signature; grant the monitor only the listed EXECUTE.',
        'ALTER CERTIFICATE SqlObserverAgentActivityCertificateV1 REMOVE PRIVATE KEY in master after successful signing; require NO_PRIVATE_KEY in both databases.',
        'Commit only after all expected postconditions and durable provenance are recorded; otherwise roll back the reviewed transaction. No partial recovery by broad DROP or REVOKE.'
    )
    removal = [ordered]@{
        implemented = $false
        action = 'refuse_until_reviewed_provenance_bound_removal_exists'
        requiredProof = @(
            'Original deployment UUID/instance identity and all original preimages match.',
            'Current object IDs, types, schema owner, module definition hash, signature thumbprint/validity, public certificate hashes and exact permission inventories match deployment receipt.',
            'No additional modules/signatures/users/login mappings/grants/dependencies use package principals or certificate; no drift or unknown objects.',
            'Only the deployment-created monitor EXECUTE grant and deployment-created seven package objects may be removed; preserve original monitor user/login and every pre-existing grant.',
            'Recovery is transactional and must stop on any mismatch; this preview cannot authorize or execute removal.'
        )
    }
    nativeGates = @(
        'syssessions documentation states sysadmin-only; certificate SELECT remains unproven on SQL17. Failure never authorizes a sysadmin signer.',
        'Actual monitor integrated/TLS session with unchanged identity/SPID/physical connection; preserve its baseline direct permissions/read outcomes while module capture succeeds. HAS_PERMS_BY_NAME=false is not proof that a DMV read fails; test each actual read separately.',
        'VIEW SERVER SECURITY STATE on the signer follows documented SQL17 requirements but is not yet proven necessary on this build. Qualify the minimum working signer grants; do not label an already-readable DMV denied.',
        'Exactly before/activity/after result contracts, 512 retained plus row513 lookahead, strict session/GUID ordering, null/invalid bound refusal.',
        'Bounded known foreign-owner job comparison against a private DBA baseline; empty results cannot prove complete job visibility.',
        'Explicit DENY and missing/invalid/removed signature fail closed; no ownership-chain or EXECUTE AS OWNER bypass.',
        'Cancellation/deadline, Agent restart bracket, ambiguous service evidence and metadata visibility must be tested before runtime integration.'
    )
    runtimeAdmissionChanged = $false
    directPermissionEvidenceMayBeSynthesized = $false
}
$json = ($plan | ConvertTo-Json -Depth 12).Replace("`r`n", "`n") + "`n"
if ([string]::IsNullOrWhiteSpace($OutputPath)) { [Console]::Out.Write($json); return }
if ([IO.Path]::GetExtension($OutputPath) -cne '.json') { throw 'OutputPath must end in .json.' }
$parent = Split-Path -Parent $OutputPath
if ([string]::IsNullOrWhiteSpace($parent)) { $parent = (Get-Location).Path }
$destination = Join-Path (Resolve-Path -LiteralPath $parent).Path (Split-Path -Leaf $OutputPath)
$stream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json); $stream.Write($bytes, 0, $bytes.Length) }
finally { $stream.Dispose() }
