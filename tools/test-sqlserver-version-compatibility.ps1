[CmdletBinding(DefaultParameterSetName = 'Tcp')]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string] $HostName,
    [Parameter(ParameterSetName = 'Tcp')][ValidateRange(1, 65535)][int] $Port = 1433,
    [Parameter(Mandatory = $true, ParameterSetName = 'Instance')][ValidateNotNullOrEmpty()][string] $InstanceName,
    [Parameter(Mandatory = $true)][ValidateSet(13, 14, 15, 16, 17)][int] $ExpectedMajor,
    [string] $CertificateHostName
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runName = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$output = Join-Path $repositoryRoot "artifacts/sqlserver-compatibility/native-$ExpectedMajor/$runName"
New-Item -ItemType Directory -Path $output -Force | Out-Null
$settings = @{
    SQLOBSERVER_VERSION_TEST_HOST = $HostName
    SQLOBSERVER_VERSION_TEST_PORT = [string]$Port
    SQLOBSERVER_VERSION_TEST_INSTANCE = $InstanceName
    SQLOBSERVER_VERSION_TEST_MAJOR = [string]$ExpectedMajor
    SQLOBSERVER_VERSION_TEST_CERTIFICATE_HOST = $(if ([string]::IsNullOrWhiteSpace($CertificateHostName)) { $HostName } else { $CertificateHostName })
}
$previous = @{}
try {
    foreach ($name in $settings.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
    }
    & dotnet test (Join-Path $repositoryRoot 'tests/SqlObserver.IntegrationTests.SqlServer/SqlObserver.IntegrationTests.SqlServer.csproj') --no-restore --nologo --filter 'FullyQualifiedName=SqlObserver.IntegrationTests.SqlServer.SqlServerVersionNativeSmokeTests.ExplicitVersionLabUsesStrictPolicyAndBoundedNativeAssets' --logger 'trx;LogFileName=native-source-smoke.trx' --results-directory $output
    if ($LASTEXITCODE -ne 0) { throw 'Native SQL Server source smoke test failed. No acceptance receipt was produced.' }
    [xml]$result = Get-Content -LiteralPath (Join-Path $output 'native-source-smoke.trx') -Raw
    $counts = $result.TestRun.ResultSummary.Counters
    if ($counts.total -ne '1' -or $counts.passed -ne '1' -or $counts.failed -ne '0') { throw 'Native smoke receipt must contain exactly one passed test.' }
    $inputs = @{}
    foreach ($relative in @('src/SqlObserver.Infrastructure.SqlServer/bin/Debug/net10.0/SqlObserver.Infrastructure.SqlServer.dll', 'collectors/manifests/sqlserver2016.compatibility.assets-v1.sha256', 'collectors/manifests/activity.live.v1.sha256', 'tests/SqlObserver.IntegrationTests.SqlServer/bin/Debug/net10.0/SqlObserver.IntegrationTests.SqlServer.dll')) {
        $inputs[$relative] = (Get-FileHash -LiteralPath (Join-Path $repositoryRoot $relative) -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $receipt = [ordered]@{ kind = 'native-source-smoke'; expectedMajor = $ExpectedMajor; hostName = $HostName; completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); status = 'passed'; fullStackQualification = 'pending'; inputs = $inputs }
    [IO.File]::WriteAllText((Join-Path $output 'receipt.json'), ($receipt | ConvertTo-Json -Depth 5) + "`n", [Text.UTF8Encoding]::new($false))
}
finally {
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
