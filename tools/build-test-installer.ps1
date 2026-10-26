[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ArchivePath,
    [Parameter(Mandatory)][ValidatePattern('^[a-fA-F0-9]{64}$')][string] $ExpectedArchiveSha256,
    [string] $IsccPath = "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
    [switch] $Offline
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (!$IsWindows -or ![Environment]::Is64BitProcess -or $PSVersionTable.PSVersion -lt [Version]'7.5') { throw 'Build on Windows x64 with PowerShell 7.5+.' }
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$previous = Get-Location
function Invoke-SetupBuild([string] $Executable, [string[]] $Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Test installer build step failed: $Executable" }
}
function Assert-PinnedPrerequisite($Pin, [string] $Directory) {
    if ($Pin.file -notmatch '^[A-Za-z0-9_.-]+\.(exe|msi)$' -or $Pin.algorithm -notin @('SHA256','SHA512')) { throw 'Invalid prerequisite pin.' }
    $uri = [Uri]$Pin.url
    if ($uri.Scheme -cne 'https' -or $uri.Host -notin @('builds.dotnet.microsoft.com','github.com','get.enterprisedb.com')) { throw 'Unexpected prerequisite origin.' }
    $path = Join-Path $Directory $Pin.file
    if (!(Test-Path -LiteralPath $path)) {
        if ($Offline) { throw "Prerequisite missing from offline cache: $($Pin.file)" }
        Invoke-WebRequest -Uri $uri -OutFile $path
    }
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Prerequisite link rejected.' }
    if ((Get-FileHash -LiteralPath $path -Algorithm $Pin.algorithm).Hash -ine $Pin.hash) { throw 'Prerequisite checksum mismatch.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike "*$($Pin.publisher)*") { throw 'Prerequisite publisher verification failed.' }
    return [ordered]@{ file=$Pin.file; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); publisher=$signature.SignerCertificate.Subject }
}
try {
    Set-Location -LiteralPath $repo
    $commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -cnotmatch '^[a-f0-9]{40}$' -or @(& git status --porcelain=v1 --untracked-files=all).Count -ne 0) { throw 'Commit installer source before building a transferable test installer.' }
    $sdk = (Get-Content global.json -Raw | ConvertFrom-Json).sdk.version
    if ((& dotnet --version).Trim() -cne $sdk) { throw 'Use the pinned .NET SDK.' }
    $compilerSignature = Get-AuthenticodeSignature -LiteralPath $IsccPath
    if ($compilerSignature.Status -ne 'Valid' -or $compilerSignature.SignerCertificate.Subject -notlike '*Pyrsys B.V.*') { throw 'Use an authentic Inno Setup 7 compiler.' }
    & ./tools/test-test-installer-prerequisites.ps1 -IsccPath $IsccPath
    $archive = [IO.Path]::GetFullPath($ArchivePath)
    & ./tools/test-lab-package.ps1 -ArchivePath $archive -ExpectedArchiveSha256 $ExpectedArchiveSha256
    $run = [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
    $output = Join-Path $repo "artifacts/test-installers/$run"
    $stage = Join-Path $output 'staging'
    $application = Join-Path $stage 'Application'
    $helper = Join-Path $stage 'Setup'
    [void][IO.Directory]::CreateDirectory($stage)
    # Hold a deny-write/delete handle from final digest verification through extraction.
    $locked = [IO.File]::Open($archive, 'Open', 'Read', 'Read')
    try {
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($locked)) -ine $ExpectedArchiveSha256) { throw 'Archive changed before extraction.' }
        [IO.Compression.ZipFile]::ExtractToDirectory($archive, $application, $false)
    } finally { $locked.Dispose() }
    $manifestPath = Join-Path $application 'package-manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $setupInput = [ordered]@{ schemaVersion=1; manifestSha256=(Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant(); applicationSourceCommit=$manifest.sourceCommit; installerSourceCommit=$commit }
    $inputPath = Join-Path $stage 'SetupBuild.json'
    [IO.File]::WriteAllText($inputPath, ($setupInput | ConvertTo-Json) + "`n")
    Invoke-SetupBuild dotnet @('restore','installer/test/SqlObserver.TestSetup.Tests/SqlObserver.TestSetup.Tests.csproj','--locked-mode','--configfile','NuGet.Config')
    Invoke-SetupBuild dotnet @('test','installer/test/SqlObserver.TestSetup.Tests/SqlObserver.TestSetup.Tests.csproj','--configuration','Release','--no-restore','--logger','trx','--results-directory',(Join-Path $output 'tests'),'-m:1','-nr:false')
    Invoke-SetupBuild dotnet @('publish','installer/test/SqlObserver.TestSetup/SqlObserver.TestSetup.csproj','--configuration','Release','--no-restore','--self-contained','false','-p:UseAppHost=true','-p:AppHostRuntimeIdentifier=win-x64',"-p:SetupBuildInput=$inputPath","-p:SourceRevisionId=$commit",'--output',$helper,'-m:1','-nr:false')
    Invoke-SetupBuild dotnet @((Join-Path $helper 'SqlObserver.TestSetup.dll'),'--verify')
    $cache = Join-Path $repo 'artifacts/test-installer/prerequisites'
    [void][IO.Directory]::CreateDirectory($cache)
    $pins = Get-Content installer/test/prerequisites.json -Raw | ConvertFrom-Json
    $prerequisites = [ordered]@{}
    foreach ($name in @('dotnetRuntime','aspnetRuntime','powerShell','postgreSql')) { $prerequisites[$name] = Assert-PinnedPrerequisite $pins.$name $cache }
    $definitions = [ordered]@{
        SourceCommitShort=$manifest.sourceCommit.Substring(0,8)
        InstallerCommitShort=$commit.Substring(0,8)
        InstallerOutput=$output
        DotnetFile=$pins.dotnetRuntime.file; DotnetSha256=$prerequisites.dotnetRuntime.sha256
        AspnetFile=$pins.aspnetRuntime.file; AspnetSha256=$prerequisites.aspnetRuntime.sha256
        PowerShellFile=$pins.powerShell.file; PowerShellSha256=$prerequisites.powerShell.sha256
        PostgresFile=$pins.postgreSql.file; PostgresSha256=$prerequisites.postgreSql.sha256
    }
    $defines = foreach ($name in $definitions.Keys) {
        $value = [string]$definitions[$name]
        if ($value.Contains('"') -or $value.Contains("`n") -or $value.Contains("`r")) { throw 'Unsafe compiler definition.' }
        "#define $name `"$value`""
    }
    $buildPins = Join-Path $stage 'BuildPins.iss'
    [IO.File]::WriteAllLines($buildPins, $defines)
    Invoke-SetupBuild $IsccPath @('/Qp',"/DPayloadRoot=$application", "/DHelperRoot=$helper", "/DPrerequisiteRoot=$cache", "/DBuildPins=$buildPins", "/DSetupGuide=$(Join-Path $repo 'installer/test/TEST-INSTALL.txt')", 'installer/test/test-setup.iss')
    if ($commit -cne (& git rev-parse HEAD).Trim() -or @(& git status --porcelain=v1 --untracked-files=all).Count -ne 0) { throw 'Source changed during installer build.' }
    $exe = @(Get-ChildItem -LiteralPath $output -Filter '*.exe' -File)
    if ($exe.Count -ne 1) { throw 'Expected exactly one unsigned installer.' }
    if ((Get-AuthenticodeSignature $exe[0].FullName).Status -ne 'NotSigned') { throw 'Unexpected signing state; this build is test-only.' }
    $sha = (Get-FileHash $exe[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($exe[0].FullName + '.sha256'), "$sha  $($exe[0].Name)`n")
    $receipt = [ordered]@{ kind='unsigned-test-installer'; schemaVersion=1; version='0.1.0-test'; installerSourceCommit=$commit;
        applicationSourceCommit=$manifest.sourceCommit; applicationArchiveSha256=$ExpectedArchiveSha256.ToLowerInvariant();
        applicationManifestSha256=$setupInput.manifestSha256; installer=$exe[0].Name; sha256=$sha; bytes=$exe[0].Length;
        compilerSha256=(Get-FileHash $IsccPath -Algorithm SHA256).Hash.ToLowerInvariant(); prerequisites=$prerequisites;
        automatedPolicyTests='passed'; nativeInstallerLifecycle='pending'; signed=$false; productionSupported=$false;
        completedAtUtc=[DateTimeOffset]::UtcNow.ToString('O') }
    [IO.File]::WriteAllText((Join-Path $output 'build-receipt.json'), ($receipt | ConvertTo-Json -Depth 8) + "`n")
    Write-Host "Unsigned test installer: $($exe[0].FullName)"
    Write-Host "SHA-256: $sha"
} finally { Set-Location -LiteralPath $previous.Path }
