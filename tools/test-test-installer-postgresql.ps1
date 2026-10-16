[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'This harness exercises the Windows setup helper against an isolated Linux PostgreSQL TLS container.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$run = [Guid]::NewGuid().ToString('N')
$container = "sqlobserver-installer-test-$run"
$output = Join-Path $repo "artifacts/test-installer/postgresql-$run"
[void][IO.Directory]::CreateDirectory($output)
if (@(Get-NetTCPConnection -State Listen -LocalPort 5432 -ErrorAction SilentlyContinue).Count -ne 0) { throw 'Port 5432 is occupied. The test never stops an existing repository.' }
function Invoke-Fixture([string[]] $Arguments) {
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'Isolated PostgreSQL fixture command failed.' }
}
$oldFlag = $env:SQLOBSERVER_INSTALLER_POSTGRES_TEST
$oldRoot = $env:SQLOBSERVER_INSTALLER_TEST_ROOT
try {
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        $san.AddDnsName('localhost'); $san.AddIpAddress([Net.IPAddress]::Loopback)
        $request.CertificateExtensions.Add($san.Build())
        $cert = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddDays(1))
        try { [IO.File]::WriteAllText((Join-Path $output 'server.crt'), $cert.ExportCertificatePem()); [IO.File]::WriteAllText((Join-Path $output 'server.key'), $rsa.ExportPkcs8PrivateKeyPem()) } finally { $cert.Dispose() }
    } finally { $rsa.Dispose() }
    Invoke-Fixture @('run','--detach','--rm','--name',$container,'--publish','127.0.0.1:5432:5432','--env','POSTGRES_PASSWORD=sqlobserver-installer-ci-only','postgres:18.4-bookworm@sha256:7e6103cf85f88f7a0eddb3ec0b1ba8940eba098ed118ade25a729ca9daee5568')
    for ($i=0; $i -lt 60; $i++) {
        & docker exec $container pg_isready --username postgres --dbname postgres *> $null
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    Invoke-Fixture @('cp',(Join-Path $output 'server.crt'),"${container}:/var/lib/postgresql/server.crt")
    Invoke-Fixture @('cp',(Join-Path $output 'server.key'),"${container}:/var/lib/postgresql/server.key")
    Invoke-Fixture @('exec','--user','root',$container,'chown','postgres:postgres','/var/lib/postgresql/server.crt','/var/lib/postgresql/server.key')
    Invoke-Fixture @('exec','--user','root',$container,'chmod','600','/var/lib/postgresql/server.key')
    foreach ($sql in @("ALTER SYSTEM SET ssl_cert_file='/var/lib/postgresql/server.crt'", "ALTER SYSTEM SET ssl_key_file='/var/lib/postgresql/server.key'", "ALTER SYSTEM SET ssl='on'", 'SELECT pg_reload_conf()')) {
        Invoke-Fixture @('exec','--user','postgres',$container,'psql','--no-psqlrc','--set','ON_ERROR_STOP=1','--dbname','postgres','--command',$sql)
    }
    $env:SQLOBSERVER_INSTALLER_POSTGRES_TEST = '1'
    $env:SQLOBSERVER_INSTALLER_TEST_ROOT = Join-Path $output 'server.crt'
    & dotnet test (Join-Path $repo 'installer/test/SqlObserver.TestSetup.Tests/SqlObserver.TestSetup.Tests.csproj') --configuration Release --no-restore -m:1 -nr:false --logger trx --results-directory $output
    if ($LASTEXITCODE -ne 0) { throw 'Installer PostgreSQL qualification tests failed.' }
} finally {
    $env:SQLOBSERVER_INSTALLER_POSTGRES_TEST = $oldFlag
    $env:SQLOBSERVER_INSTALLER_TEST_ROOT = $oldRoot
    & docker stop --time 15 $container *> $null
    if (Test-Path -LiteralPath (Join-Path $output 'server.key')) { Remove-Item -LiteralPath (Join-Path $output 'server.key') -Force }
}
