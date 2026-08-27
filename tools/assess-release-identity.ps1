[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $RepositoryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$AssessmentSchemaName = 'release-identity-assessment.v1.schema.json'
$PolicyId = 'sqlobserver-m12-policy-v1'
$ZeroCommit = '0000000000000000000000000000000000000000'
$ApprovedHashes = [ordered]@{
    'm12-certification-matrix.v1.json' = 'accdbd6d90f3012a7841daebf51b039b2ef574865476fcb75d682ff1c50a9330'
    'm12-certification-matrix.v1.schema.json' = '32c6a01aee7cb5884572410e85c5efa5b1dc2b2fef11049aab069512723bd5fe'
    'm12-certification-manifest.v1.schema.json' = '3613c4ac3fa4d39b6e96bb8a6aa2546f625f594ef287483b8c8e33749774dda6'
}

function Assert-TrustedPath {
    param([string] $Path)
    $fullPath = [IO.Path]::GetFullPath($Path)
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)
    $cursor = $pathRoot
    foreach ($component in ($fullPath.Substring($pathRoot.Length) -split '[\\/]')) {
        if ([string]::IsNullOrEmpty($component)) { continue }
        $cursor = Join-Path $cursor $component
        $componentItem = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (($componentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ASSESSMENT_PATH_UNTRUSTED' }
    }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ASSESSMENT_PATH_UNTRUSTED' }
    $current = if ($item.PSIsContainer) { $item } else { Get-Item -LiteralPath $item.DirectoryName -Force -ErrorAction Stop }
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ASSESSMENT_PATH_UNTRUSTED' }
        $parent = $current.Parent
        if ($null -eq $parent -or $parent.FullName -eq $current.FullName) { break }
        $current = $parent
    }
}

function Get-TrustedRoot {
    param([string] $RequestedRoot)
    $authoritative = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..') -ErrorAction Stop).Path
    $requested = (Resolve-Path -LiteralPath $RequestedRoot -ErrorAction Stop).Path
    Assert-TrustedPath $authoritative
    Assert-TrustedPath $requested
    if (-not [String]::Equals($authoritative, $requested, [StringComparison]::Ordinal)) { throw 'ASSESSMENT_ROOT_MISMATCH' }
    foreach ($relative in @(
        'tools/assess-release-identity.ps1', 'tools/verify-test-results.ps1',
        'release/certification/m12-certification-matrix.v1.json',
        'release/certification/m12-certification-matrix.v1.schema.json',
        'release/certification/m12-certification-manifest.v1.schema.json',
        'release/certification/m12-certification-assets.sha256',
        'release/contracts/release-identity-assessment.v1.schema.json',
        'release/contracts/checksums.sha256')) {
        Assert-TrustedPath (Join-Path $requested $relative)
    }
    return $requested
}

function New-Check {
    param([int] $Order, [string] $CheckId, [string] $Status, [string] $Code)
    [ordered]@{ checkId = $CheckId; order = $Order; status = $Status; code = $Code }
}

function Get-CanonicalUtc {
    return [DateTimeOffset]::UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", [Globalization.CultureInfo]::InvariantCulture)
}

function Resolve-RepositoryHeadSha {
    param([string] $Root)
    try {
        $gitEntry = Get-Item -LiteralPath (Join-Path $Root '.git') -Force -ErrorAction Stop
        if (($gitEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $null }
        $rootCanonical = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        $gitRoot = $gitEntry.FullName
        $refRoot = $gitRoot
        if (-not $gitEntry.PSIsContainer) {
            $gitText = [IO.File]::ReadAllText($gitRoot)
            if ($gitText.Length -gt 4096 -or $gitText -notmatch '^gitdir: ([^\r\n]+)\r?\n?$') { return $null }
            $gitDirValue = $Matches[1]
            if ($gitDirValue -match '[\x00-\x1f\x7f"]' -or ($gitDirValue.Contains(':', [StringComparison]::Ordinal) -and $gitDirValue -notmatch '^[A-Za-z]:[\\/]') -or ($gitDirValue -match '^[A-Za-z]:[\\/].*:')) { return $null }
            if ([IO.Path]::IsPathRooted($gitDirValue)) { $gitRoot = [IO.Path]::GetFullPath($gitDirValue) }
            else { $gitRoot = [IO.Path]::GetFullPath((Join-Path $rootCanonical $gitDirValue)) }
            $worktreesDir = ([System.IO.DirectoryInfo]$gitRoot).Parent
            if ($null -eq $worktreesDir -or $worktreesDir.Name -cne 'worktrees' -or $null -eq $worktreesDir.Parent -or $worktreesDir.Parent.Name -cne '.git') { return $null }
            $commonBoundary = [IO.Path]::GetFullPath($worktreesDir.Parent.FullName).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
            $worktreeName = ([System.IO.DirectoryInfo]$gitRoot).Name
            if ($worktreeName -notmatch '^[A-Za-z0-9._-]+$' -or $worktreeName -ceq '.' -or $worktreeName -ceq '..' -or $worktreeName.EndsWith('.', [StringComparison]::Ordinal)) { return $null }
            if (-not (Test-Path -LiteralPath $gitRoot -PathType Container)) { return $null }
            $commondirPath = Join-Path $gitRoot 'commondir'
            if (-not (Test-Path -LiteralPath $commondirPath -PathType Leaf)) { return $null }
            Assert-TrustedPath $commondirPath
            $commonLines = [IO.File]::ReadAllLines($commondirPath)
            if ($commonLines.Count -ne 1 -or -not [String]::Equals($commonLines[0], '../..', [StringComparison]::Ordinal)) { return $null }
            $commonValue = $commonLines[0]
            $refRoot = [IO.Path]::GetFullPath((Join-Path $gitRoot $commonValue)).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
            if (-not [String]::Equals($refRoot, $commonBoundary, [StringComparison]::Ordinal)) { return $null }
            if (-not (Test-Path -LiteralPath $refRoot -PathType Container)) { return $null }
            Assert-TrustedPath $refRoot
            $reciprocalPath = Join-Path $gitRoot 'gitdir'
            if (-not (Test-Path -LiteralPath $reciprocalPath -PathType Leaf)) { return $null }
            Assert-TrustedPath $reciprocalPath
            $reciprocalText = [IO.File]::ReadAllText($reciprocalPath)
            if ($reciprocalText.Length -gt 4096 -or $reciprocalText -notmatch '^([^\r\n]+)\r?\n?$') { return $null }
            $reciprocalValue = $Matches[1]
            if ($reciprocalValue -match '[\x00-\x1f\x7f"]' -or ($reciprocalValue.Contains(':', [StringComparison]::Ordinal) -and $reciprocalValue -notmatch '^[A-Za-z]:[\\/]') -or ($reciprocalValue -match '^[A-Za-z]:[\\/].*:')) { return $null }
            $reciprocal = if ([IO.Path]::IsPathRooted($reciprocalValue)) { [IO.Path]::GetFullPath($reciprocalValue) } else { [IO.Path]::GetFullPath((Join-Path $gitRoot $reciprocalValue)) }
            if (-not [String]::Equals($reciprocal, [IO.Path]::GetFullPath((Join-Path $rootCanonical '.git')), [StringComparison]::Ordinal)) { return $null }
        }
        Assert-TrustedPath $gitRoot
        $headPath = Join-Path $gitRoot 'HEAD'
        Assert-TrustedPath $headPath
        $head = [IO.File]::ReadAllText($headPath)
        if ($head.Length -gt 4096) { return $null }
        $head = $head.TrimEnd([char[]]"`r`n")
        $shaMatch = [regex]::Match($head, '^([0-9a-f]{40})$', [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if ($shaMatch.Success) { return $shaMatch.Groups[1].Value }
        $refMatch = [regex]::Match($head, '^ref: (refs/[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)*)$', [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if (-not $refMatch.Success) { return $null }
        $refName = $refMatch.Groups[1].Value
        foreach ($component in ($refName.Substring(5) -split '/')) { if ($component -ceq '.' -or $component -ceq '..' -or $component.EndsWith('.', [StringComparison]::Ordinal) -or $component.EndsWith('.lock', [StringComparison]::Ordinal)) { return $null } }
        $loose = Join-Path $refRoot $refName.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $gitRootCanonical = [IO.Path]::GetFullPath($refRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        $looseCanonical = [IO.Path]::GetFullPath($loose)
        if (-not $looseCanonical.StartsWith($gitRootCanonical + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { return $null }
        $found = [System.Collections.Generic.List[string]]::new()
        if (Test-Path -LiteralPath $loose -PathType Leaf) {
            Assert-TrustedPath $loose
            $looseText = [IO.File]::ReadAllText($loose)
            if ($looseText.Length -gt 4096) { return $null }
            $looseSha = $looseText.TrimEnd([char[]]"`r`n")
            if (-not [regex]::IsMatch($looseSha, '^[0-9a-f]{40}$', [Text.RegularExpressions.RegexOptions]::CultureInvariant)) { return $null }
            $found.Add($looseSha)
        }
        $packed = Join-Path $refRoot 'packed-refs'
        if (Test-Path -LiteralPath $packed -PathType Leaf) {
            Assert-TrustedPath $packed
            $packedText = [IO.File]::ReadAllText($packed)
            if ($packedText.Length -gt 1048576) { return $null }
            $packedRefs = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            $havePackedRef = $false
            $havePeel = $false
            foreach ($line in ($packedText -split "`r?`n")) {
                if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#', [StringComparison]::Ordinal)) { continue }
                if ([regex]::IsMatch($line, '^\^[0-9a-f]{40}$', [Text.RegularExpressions.RegexOptions]::CultureInvariant)) { if (-not $havePackedRef -or $havePeel) { return $null }; $havePeel = $true; continue }
                $packedMatch = [regex]::Match($line, '^([0-9a-f]{40}) (refs/[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)*)$', [Text.RegularExpressions.RegexOptions]::CultureInvariant)
                if (-not $packedMatch.Success) { return $null }
                $packedRefName = $packedMatch.Groups[2].Value
                foreach ($component in ($packedRefName.Substring(5) -split '/')) { if ($component -ceq '.' -or $component -ceq '..' -or $component.EndsWith('.', [StringComparison]::Ordinal) -or $component.EndsWith('.lock', [StringComparison]::Ordinal)) { return $null } }
                if (-not $packedRefs.Add($packedRefName)) { return $null }
                $havePackedRef = $true
                $havePeel = $false
                if ([String]::Equals($packedRefName, $refName, [StringComparison]::Ordinal)) { $found.Add($packedMatch.Groups[1].Value) }
            }
        }
        if ($found.Count -ne 1 -or -not [regex]::IsMatch($found[0], '^[0-9a-f]{40}$', [Text.RegularExpressions.RegexOptions]::CultureInvariant)) { return $null }
        return $found[0]
    }
    catch { return $null }
}

function Read-CappedProcessStreams {
    param([Diagnostics.Process] $Process, [int] $MaxOutput, [int] $MaxError, [int] $TimeoutMs)
    $state = [pscustomobject]@{ Output = [Text.StringBuilder]::new(); Error = [Text.StringBuilder]::new(); OutputTooLarge = $false; ErrorTooLarge = $false; TimedOut = $false }
    $outBuffer = New-Object char[] 4096; $errBuffer = New-Object char[] 4096
    try {
        $outDone = $false; $errDone = $false
        $outTask = $Process.StandardOutput.ReadAsync($outBuffer, 0, $outBuffer.Length)
        $errTask = $Process.StandardError.ReadAsync($errBuffer, 0, $errBuffer.Length)
        $deadline = [Environment]::TickCount64 + $TimeoutMs
        do {
            [Threading.Tasks.Task]::Delay(25).GetAwaiter().GetResult()
            if (-not $outDone -and $outTask.IsCompleted) { $count = $outTask.GetAwaiter().GetResult(); if ($count -eq 0) { $outDone = $true } elseif ($state.Output.Length + $count -gt $MaxOutput) { $state.OutputTooLarge = $true } else { [void]$state.Output.Append($outBuffer, 0, $count); $outTask = $Process.StandardOutput.ReadAsync($outBuffer, 0, $outBuffer.Length) } }
            if (-not $errDone -and $errTask.IsCompleted) { $count = $errTask.GetAwaiter().GetResult(); if ($count -eq 0) { $errDone = $true } elseif ($state.Error.Length + $count -gt $MaxError) { $state.ErrorTooLarge = $true } else { [void]$state.Error.Append($errBuffer, 0, $count); $errTask = $Process.StandardError.ReadAsync($errBuffer, 0, $errBuffer.Length) } }
            if ($state.OutputTooLarge -or $state.ErrorTooLarge) { try { $Process.Kill($true) } catch { }; break }
            if ([Environment]::TickCount64 -ge $deadline) { $state.TimedOut = $true; try { $Process.Kill($true) } catch { }; break }
            if ($outDone -and $errDone -and $Process.HasExited) { break }
        } while ($true)
        try { if (-not $Process.HasExited) { $state.TimedOut = $true; try { $Process.Kill($true) } catch { }; try { [void]$Process.WaitForExit(100) } catch { } } } catch { $state.TimedOut = $true }
        try { if (-not $Process.HasExited) { try { $Process.Kill($true) } catch { }; try { [void]$Process.WaitForExit(100) } catch { } } } catch { $state.TimedOut = $true }
        foreach ($pendingTask in @($outTask, $errTask)) { try { if ($pendingTask.IsCompleted) { [void]$pendingTask.GetAwaiter().GetResult() } } catch { } }
        return $state
    }
    finally { }
}

function Invoke-MatrixOnly {
    param([string] $Root)
    try {
        $pwshPath = Join-Path $PSHOME ($(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' }))
        if (-not (Test-Path -LiteralPath $pwshPath -PathType Leaf)) { return $false }
        Assert-TrustedPath $pwshPath
        $verifier = Join-Path $Root 'tools/verify-test-results.ps1'
        if (-not (Test-Path -LiteralPath $verifier -PathType Leaf)) { return $false }
        Assert-TrustedPath $verifier
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = $pwshPath
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        [void]$start.ArgumentList.Add('-NoProfile')
        [void]$start.ArgumentList.Add('-NonInteractive')
        [void]$start.ArgumentList.Add('-File')
        [void]$start.ArgumentList.Add($verifier)
        [void]$start.ArgumentList.Add('-MatrixOnly')
        [void]$start.ArgumentList.Add('-RepositoryRoot')
        [void]$start.ArgumentList.Add($Root)
        $process = [Diagnostics.Process]::Start($start)
        if ($null -eq $process) { return $false }
        try {
            $streamState = Read-CappedProcessStreams $process 65536 4096 15000
            if ($streamState.TimedOut -or $process.ExitCode -ne 0 -or $streamState.ErrorTooLarge -or $streamState.OutputTooLarge -or $streamState.Error.Length -ne 0) { return $false }
            $text = $streamState.Output.ToString()
            $text = $text.Trim()
            if ([string]::IsNullOrWhiteSpace($text) -or $text -match '\r?\n') { return $false }
            $json = $text | ConvertFrom-Json
            $properties = @($json.PSObject.Properties.Name)
            if (($properties -join '|') -cne 'valid|policy|schemaVersion|matrix|profiles|lanes|cases' -or
                $json.valid -ne $true -or $json.policy -cne $PolicyId -or $json.schemaVersion -ne 1 -or
                $json.matrix -cne 'sqlobserver-m12' -or @($json.profiles).Count -ne 2 -or
                (@($json.profiles) -join '|') -cne 'Local|Release' -or $json.lanes -ne 20 -or $json.cases -ne 35) { return $false }
            return $true
        }
        finally { $process.Dispose() }
    }
    catch { return $false }
}

function Get-HeadCommit {
    param([string] $Root)
    $sha = Resolve-RepositoryHeadSha $Root
    return $(if ($sha) { $sha } else { $ZeroCommit })
}

function Get-AssessorOutput {
    param([string] $Root)
    $commit = Get-HeadCommit $Root
    $matrixLanes = 20
    $matrixCases = 35
    $implementedLanes = 8
    $implementedCases = 8
    $pendingLanes = 12
    $pendingCases = 27
    $policyVersion = '1.2.0'
    $matrixObserved = $false
    $assetsApproved = $false
    $matrixOnlyObserved = Invoke-MatrixOnly $Root

    try {
        $assetRoot = Join-Path $Root 'release/certification'
        $assetPinLines = [IO.File]::ReadAllLines((Join-Path $assetRoot 'm12-certification-assets.sha256'))
        if ($assetPinLines.Count -ne $ApprovedHashes.Count) { throw 'ASSET_PIN_INVALID' }
        $assetHashes = [System.Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
        $assetNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($line in $assetPinLines) {
            if ($line -notmatch '^([0-9a-f]{64})  (m12-certification-[a-z0-9.-]+\.json)$') { throw 'ASSET_PIN_INVALID' }
            if (-not $assetNames.Add($Matches[2])) { throw 'ASSET_PIN_DUPLICATE' }
            $assetHashes.Add($Matches[2], $Matches[1])
        }
        $assetsApproved = $assetPinLines.Count -eq $ApprovedHashes.Count -and $assetHashes.Count -eq $ApprovedHashes.Count
        foreach ($name in $ApprovedHashes.Keys) {
            $path = Join-Path $assetRoot $name
            $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            if (-not $assetHashes.ContainsKey($name) -or $assetHashes[$name] -cne $ApprovedHashes[$name] -or $actual -cne $ApprovedHashes[$name]) { $assetsApproved = $false }
        }
        $matrixPath = Join-Path $Root 'release/certification/m12-certification-matrix.v1.json'
        $matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json
        $lanes = @($matrix.lanes)
        $cases = @($lanes | ForEach-Object { @($_.cases) })
        $implementedLaneItems = @($lanes | Where-Object { [string]$_.implementationStatus -eq 'implemented' })
        $pendingLaneItems = @($lanes | Where-Object { [string]$_.implementationStatus -eq 'pending' })
        $implementedCaseItems = @($cases | Where-Object { [string]$_.implementationStatus -eq 'implemented' })
        $pendingCaseItems = @($cases | Where-Object { [string]$_.implementationStatus -eq 'pending' })
        $matrixLanes = $lanes.Count
        $matrixCases = $cases.Count
        $implementedLanes = $implementedLaneItems.Count
        $implementedCases = $implementedCaseItems.Count
        $pendingLanes = $pendingLaneItems.Count
        $pendingCases = $pendingCaseItems.Count
        $matrixObserved = $assetsApproved -and [string]$matrix.'$schema' -eq 'm12-certification-matrix.v1.schema.json' -and
            [int]$matrix.schemaVersion -eq 1 -and [string]$matrix.matrixId -eq 'sqlobserver-m12' -and
            [string]$matrix.version -ceq '1.2.0' -and
            [string]$matrix.profiles.Local.releaseEvidence -eq 'False' -and
            [string]$matrix.profiles.Release.releaseEvidence -eq 'True' -and
            $matrixLanes -eq 20 -and $matrixCases -eq 35 -and $implementedLanes -eq 8 -and
            $implementedCases -eq 8 -and $pendingLanes -eq 12 -and $pendingCases -eq 27
    }
    catch { $matrixObserved = $false }

    if (-not $matrixObserved) {
        # Counts and policy version are approved contract constants, never
        # claims copied from an untrusted or mutated matrix.
        $matrixLanes = 20; $matrixCases = 35; $implementedLanes = 8; $implementedCases = 8
        $pendingLanes = 12; $pendingCases = 27; $policyVersion = '1.2.0'
    }

    $checks = [System.Collections.Generic.List[object]]::new()
    $checks.Add((New-Check 1 'head-commit' ($(if ($commit -ne $ZeroCommit) { 'observed' } else { 'blocked' })) 'HEAD_VERIFIED'))
    $checks.Add((New-Check 2 'policy-identity' ($(if ($matrixObserved) { 'observed' } else { 'blocked' })) 'POLICY_ID_VERIFIED'))
    $checks.Add((New-Check 3 'matrix-identity' ($(if ($matrixObserved) { 'observed' } else { 'blocked' })) 'MATRIX_ID_VERIFIED'))
    $checks.Add((New-Check 4 'matrix-inventory' ($(if ($matrixObserved) { 'observed' } else { 'blocked' })) 'MATRIX_INVENTORY_VERIFIED'))
    $checks.Add((New-Check 5 'profile-inventory' ($(if ($matrixObserved) { 'observed' } else { 'blocked' })) 'PROFILE_INVENTORY_VERIFIED'))
    $schemaPinned = $false
    try {
        $schemaPath = Join-Path $Root 'release/contracts/release-identity-assessment.v1.schema.json'
        $checksumPath = Join-Path $Root 'release/contracts/checksums.sha256'
        $schemaHash = (Get-FileHash -LiteralPath $schemaPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $checksumLines = [IO.File]::ReadAllLines($checksumPath)
        $schemaPinned = $checksumLines.Count -eq 1 -and
            $checksumLines[0] -cmatch '^[0-9a-f]{64}  release-identity-assessment\.v1\.schema\.json$' -and
            $checksumLines[0] -ceq "$schemaHash  $AssessmentSchemaName"
    }
    catch { $schemaPinned = $false }
    $checks.Add((New-Check 6 'schema-checksum' ($(if ($schemaPinned) { 'observed' } else { 'blocked' })) ($(if ($schemaPinned) { 'ASSESSMENT_SCHEMA_PINNED' } else { 'ASSESSMENT_UNAVAILABLE' }))))
    $checks.Add((New-Check 7 'matrix-only-verification' ($(if ($matrixOnlyObserved) { 'observed' } else { 'blocked' })) 'MATRIX_ONLY_VERIFIED'))
    $checks.Add((New-Check 8 'repository-contract' ($(if ($matrixObserved) { 'observed' } else { 'blocked' })) 'REPOSITORY_CONTRACT_OBSERVED'))

    $ownerChecks = @(
        'product-version', 'artifact-digests', 'publisher-identity', 'license', 'versioning-policy',
        'signing-identity', 'key-custody', 'timestamping', 'key-rotation', 'revocation-response',
        'release-approver', 'artifact-retention', 'evidence-access', 'canonical-build-environment'
    )
    # Checks 9-10 are blocked by missing release evidence; 11-22 require owner decisions.
    $checks.Add((New-Check 9 $ownerChecks[0] 'blocked' 'PRODUCT_IDENTITY_PENDING'))
    $checks.Add((New-Check 10 $ownerChecks[1] 'blocked' 'ARTIFACT_DIGESTS_PENDING'))
    for ($i = 2; $i -lt $ownerChecks.Count; $i++) {
        $checks.Add((New-Check ($i + 9) $ownerChecks[$i] 'blocked' 'OWNER_DECISION_REQUIRED'))
    }

    $externalChecks = @(
        'windows-certification', 'postgresql-certification', 'sqlserver-certification',
        'browser-certification', 'installer-certification', 'sustained-load-recovery', 'release-manifest'
    )
    for ($i = 0; $i -lt $externalChecks.Count; $i++) {
        $checks.Add((New-Check ($i + 23) $externalChecks[$i] 'blocked' 'MATRIX_PENDING'))
    }

    $counts = [ordered]@{
        matrixLanes = $matrixLanes; matrixCases = $matrixCases
        implementedLanes = $implementedLanes; implementedCases = $implementedCases
        pendingLanes = $pendingLanes; pendingCases = $pendingCases; checks = $checks.Count
    }
    [ordered]@{
        '$schema' = $AssessmentSchemaName; schemaVersion = 1; assessmentId = 'm12-release-identity'
        assessedAtUtc = Get-CanonicalUtc; status = 'not_ready'; releaseEvidence = $false; readyToRelease = $false
        commitSha = $commit; policyId = $PolicyId; policyVersion = $policyVersion; matrixId = 'sqlobserver-m12'
        counts = $counts; checks = @($checks)
    }
}

try {
    $root = Get-TrustedRoot $RepositoryRoot
    $result = Get-AssessorOutput $root
}
catch {
    # Keep failures bounded and machine-readable; never serialize exception text.
    $fallbackIds = @(
        'head-commit', 'policy-identity', 'matrix-identity', 'matrix-inventory', 'profile-inventory',
        'schema-checksum', 'matrix-only-verification', 'repository-contract', 'product-version', 'artifact-digests',
        'publisher-identity', 'license', 'versioning-policy', 'signing-identity', 'key-custody', 'timestamping',
        'key-rotation', 'revocation-response', 'release-approver', 'artifact-retention', 'evidence-access',
        'canonical-build-environment', 'windows-certification', 'postgresql-certification', 'sqlserver-certification',
        'browser-certification', 'installer-certification', 'sustained-load-recovery', 'release-manifest'
    )
    $result = [ordered]@{
        '$schema' = $AssessmentSchemaName; schemaVersion = 1; assessmentId = 'm12-release-identity'
        assessedAtUtc = Get-CanonicalUtc; status = 'not_ready'; releaseEvidence = $false; readyToRelease = $false
        commitSha = $ZeroCommit; policyId = $PolicyId; policyVersion = '1.2.0'; matrixId = 'sqlobserver-m12'
        counts = [ordered]@{ matrixLanes = 20; matrixCases = 35; implementedLanes = 8; implementedCases = 8; pendingLanes = 12; pendingCases = 27; checks = 29 }
        checks = @((1..29 | ForEach-Object { New-Check $_ $fallbackIds[$_ - 1] 'blocked' 'ASSESSMENT_UNAVAILABLE' }))
    }
}
$result | ConvertTo-Json -Compress -Depth 5
