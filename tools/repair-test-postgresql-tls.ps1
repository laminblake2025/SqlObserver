#Requires -Version 7.5
[CmdletBinding()]
param(
    [switch] $Apply,
    [string] $TlsDirectory = "$env:ProgramData\SqlObserverPostgresTls",
    [string] $PsqlPath = "$env:ProgramFiles\PostgreSQL\18\bin\psql.exe"
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function New-TestPostgresCertificatePair([string] $Directory) {
    foreach ($name in @('repository.crt', 'repository.key', 'repository-root.crt')) {
        if (Test-Path -LiteralPath (Join-Path $Directory $name)) { throw 'TLS files already exist; nothing was overwritten.' }
    }
    $issuerKey = [Security.Cryptography.RSA]::Create(3072)
    $serverKey = [Security.Cryptography.RSA]::Create(3072)
    $issuer = $null; $server = $null
    try {
        $hash = [Security.Cryptography.HashAlgorithmName]::SHA256
        $padding = [Security.Cryptography.RSASignaturePadding]::Pkcs1
        $issuerRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=SQL Observer Test PostgreSQL Root', $issuerKey, $hash, $padding)
        $issuerRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
        $issuerRequest.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyCertSign, $true))
        $issuer = $issuerRequest.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(366))
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $serverKey, $hash, $padding)
        $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        $san.AddDnsName('localhost')
        $san.AddDnsName([Environment]::MachineName)
        $san.AddIpAddress([Net.IPAddress]::Loopback)
        $san.AddIpAddress([Net.IPAddress]::IPv6Loopback)
        $request.CertificateExtensions.Add($san.Build())
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
        $usage = [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature -bor [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new($usage, $true))
        $oids = [Security.Cryptography.OidCollection]::new()
        [void]$oids.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.1'))
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $true))
        $server = $request.Create($issuer, [DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(365), [Security.Cryptography.RandomNumberGenerator]::GetBytes(16))
        [IO.File]::WriteAllText((Join-Path $Directory 'repository.crt'), $server.ExportCertificatePem())
        [IO.File]::WriteAllText((Join-Path $Directory 'repository.key'), $serverKey.ExportPkcs8PrivateKeyPem())
        [IO.File]::WriteAllText((Join-Path $Directory 'repository-root.crt'), $issuer.ExportCertificatePem())
        # The issuer private key is deliberately never persisted.
    } finally {
        if ($null -ne $server) { $server.Dispose() }
        if ($null -ne $issuer) { $issuer.Dispose() }
        $serverKey.Dispose(); $issuerKey.Dispose()
    }
}

function New-ProtectedTlsDirectory([string] $Path, [Security.AccessControl.DirectorySecurity] $Security) {
    if (!('SqlObserverTlsDirectory' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class SqlObserverTlsDirectory {
    [StructLayout(LayoutKind.Sequential)] struct SecurityAttributes {
        public int Length; public IntPtr Descriptor; public int Inherit;
    }
    [DllImport("kernel32.dll", EntryPoint="CreateDirectoryW", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);
    public static void CreateNew(string path, byte[] descriptor) {
        var pin = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try {
            var attributes = new SecurityAttributes { Length=Marshal.SizeOf<SecurityAttributes>(), Descriptor=pin.AddrOfPinnedObject() };
            if (!CreateDirectory(path, ref attributes)) throw new Win32Exception(Marshal.GetLastWin32Error());
        } finally { pin.Free(); }
    }
}
'@
    }
    [SqlObserverTlsDirectory]::CreateNew($Path, $Security.GetSecurityDescriptorBinaryForm())
}

function Invoke-TestPostgresTlsRepair {
    if (!$IsWindows) { throw 'Run this recovery tool on the affected Windows test server.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if (![Security.Principal.WindowsPrincipal]::new($identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Run PowerShell 7 as Administrator.'
        }
    } finally { $identity.Dispose() }
    $service = Get-CimInstance Win32_Service -Filter "Name='postgresql-x64-18'"
    if ($null -eq $service -or $service.State -ne 'Running' -or $service.StartName -ine 'NT AUTHORITY\NetworkService') {
        throw 'This repair is scoped to the observed running PostgreSQL 18 service using NetworkService.'
    }
    if (!(Test-Path -LiteralPath $PsqlPath -PathType Leaf)) { throw 'psql.exe was not found.' }
    $localConnection = 'dbname=postgres sslmode=disable connect_timeout=10'
    Write-Host 'Checking the local cluster. Enter the PostgreSQL postgres password when prompted.'
    $query = "SELECT json_build_object('major', current_setting('server_version_num')::integer / 10000, 'ssl', current_setting('ssl'), 'data', current_setting('data_directory'), 'cert', current_setting('ssl_cert_file'), 'key', current_setting('ssl_key_file'))"
    $inventory = @(& $PsqlPath -X -q -A -t -U postgres -h localhost -p 5432 -d $localConnection -W -v ON_ERROR_STOP=1 -c $query)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect PostgreSQL; no configuration was changed.' }
    $state = ($inventory -join "`n") | ConvertFrom-Json
    $expectedData = Join-Path $env:ProgramFiles 'PostgreSQL/18/data'
    $expectedOldRoot = Join-Path $env:ProgramData 'SqlObserverTest/PostgresTls'
    if ($state.major -ne 18 -or $state.ssl -ne 'on' -or [IO.Path]::GetFullPath($state.data) -ine [IO.Path]::GetFullPath($expectedData) -or
        [IO.Path]::GetFullPath($state.cert) -ine [IO.Path]::GetFullPath((Join-Path $expectedOldRoot 'repository.crt')) -or
        [IO.Path]::GetFullPath($state.key) -ine [IO.Path]::GetFullPath((Join-Path $expectedOldRoot 'repository.key'))) {
        throw 'The cluster or configured paths differ from the diagnosed failed test installation. Nothing was changed.'
    }
    if ((Test-Path -LiteralPath $state.cert) -and (Test-Path -LiteralPath $state.key)) {
        throw 'Both original TLS files are present. Restore or diagnose their access before replacing them.'
    }
    $tlsRoot = [IO.Path]::GetFullPath($TlsDirectory)
    if ([IO.Path]::GetDirectoryName($tlsRoot) -ine [IO.Path]::GetFullPath($env:ProgramData)) {
        throw 'Choose a fresh direct child of ProgramData for the retained PostgreSQL TLS files.'
    }
    if (Test-Path -LiteralPath $tlsRoot) { throw 'The new TLS directory already exists. Nothing was overwritten.' }
    for ($ancestor = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($tlsRoot)); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'TLS directory ancestors cannot be reparse points.' }
    }
    Write-Host "Repair target: local PostgreSQL 18, missing test TLS files. New retained directory: $tlsRoot"
    if (!$Apply) { Write-Host 'Inspection only. Run with -Apply to generate TLS files, update the two server file paths, reload and verify TLS.'; return }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-20'), 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    New-ProtectedTlsDirectory $tlsRoot $acl
    New-TestPostgresCertificatePair $tlsRoot
    $cert = (Join-Path $tlsRoot 'repository.crt').Replace('\', '/').Replace("'", "''")
    $key = (Join-Path $tlsRoot 'repository.key').Replace('\', '/').Replace("'", "''")
    $sqlPath = Join-Path $tlsRoot 'apply-tls.sql'
    [IO.File]::WriteAllText($sqlPath, "ALTER SYSTEM SET ssl_cert_file = '$cert';`nALTER SYSTEM SET ssl_key_file = '$key';`nSELECT pg_reload_conf();`nSELECT pg_sleep(1);`n")
    Write-Host 'Applying the two TLS file paths and reloading. Enter the PostgreSQL postgres password again.'
    & $PsqlPath -X -U postgres -h localhost -p 5432 -d $localConnection -W -v ON_ERROR_STOP=1 -f $sqlPath
    if ($LASTEXITCODE -ne 0) { throw "Repair did not complete. Retain $tlsRoot; TLS file-path settings may have been partially applied. Databases were not changed." }
    $rootCertificate = (Join-Path $tlsRoot 'repository-root.crt').Replace('\', '/')
    $verifiedConnection = "dbname=postgres sslmode=verify-full sslrootcert='$($rootCertificate.Replace("'", "\'"))' connect_timeout=10"
    Write-Host 'Verifying a fresh certificate-validated TLS connection. Enter the PostgreSQL postgres password once more.'
    $verified = @(& $PsqlPath -X -q -A -t -U postgres -h localhost -p 5432 -d $verifiedConnection -W -v ON_ERROR_STOP=1 -c 'SELECT ssl FROM pg_stat_ssl WHERE pid = pg_backend_pid();')
    if ($LASTEXITCODE -ne 0 -or ($verified -join '').Trim() -ne 't') {
        throw "TLS verification did not pass. Retain $tlsRoot and inspect the PostgreSQL server log; no database or captured-data key was changed."
    }
    Write-Host "TLS verification passed. At the SQL Observer PostgreSQL TLS root prompt, enter: $rootCertificate"
    Write-Host 'Keep this TLS directory for as long as PostgreSQL uses these paths. No databases, runtime logins, or captured-data keys were altered.'
}

if ($MyInvocation.InvocationName -ne '.') { Invoke-TestPostgresTlsRepair }
