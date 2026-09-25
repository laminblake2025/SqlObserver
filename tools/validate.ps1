[CmdletBinding()]
param(
    # Retained for the canonical desktop validation invocation. The repository
    # validation gate does not require Godot; accepting the path keeps the command
    # stable across milestone slices.
    [string] $GodotPath,

    [ValidateSet('Local', 'Release')]
    [string] $Profile = 'Local',

    # Optional CI output; a distinct TRX filename is emitted for every test project.
    [string] $TestResultsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -ne 'Core' -or $PSVersionTable.PSVersion -lt [Version]'7.5') {
    throw 'SqlObserver validation requires PowerShell 7.5 or newer (pwsh); Windows PowerShell 5.1 and pwsh 7.4 are unsupported.'
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$solutionPath = Join-Path $repositoryRoot 'SqlObserver.slnx'
$nugetConfigPath = Join-Path $repositoryRoot 'NuGet.Config'
$webPath = Join-Path $repositoryRoot 'web'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Executable,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [Parameter(Mandatory = $true)]
        [string] $WorkingDirectory
    )

    Push-Location $WorkingDirectory
    try {
        Write-Host ("> {0} {1}" -f $Executable, ($Arguments -join ' '))
        & $Executable @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Command failed with exit code ${LASTEXITCODE}: $Executable $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

function Assert-TrustedExecutablePath {
    param([Parameter(Mandatory = $true)][string] $Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $pathRoot = [IO.Path]::GetPathRoot($fullPath)
    $cursor = $pathRoot
    foreach ($component in ($fullPath.Substring($pathRoot.Length) -split '[\\/]')) {
        if ([string]::IsNullOrEmpty($component)) { continue }
        $cursor = Join-Path $cursor $component
        $componentItem = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (($componentItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'M12 trusted executable path is untrusted.' }
    }
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (-not $item.PSIsContainer -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'M12 trusted executable path is untrusted.'
    }
    $current = if ($item.PSIsContainer) { $item } else { Get-Item -LiteralPath $item.DirectoryName -Force -ErrorAction Stop }
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'M12 trusted executable path is untrusted.' }
        $parent = $current.Parent
        if ($null -eq $parent -or $parent.FullName -eq $current.FullName) { break }
        $current = $parent
    }
}

function Get-TrustedPowerShellPath {
    $name = if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' }
    $path = [IO.Path]::GetFullPath((Join-Path $PSHOME $name))
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'M12 trusted PowerShell executable is unavailable.' }
    Assert-TrustedExecutablePath $path
    return $path
}

function Resolve-RepositoryHeadSha {
    param([Parameter(Mandatory = $true)][string] $Root)

    try {
        $rootCanonical = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Root -ErrorAction Stop).Path).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        $gitEntry = Get-Item -LiteralPath (Join-Path $rootCanonical '.git') -Force -ErrorAction Stop
        if (($gitEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { return $null }
        $gitRoot = $gitEntry.FullName
        $refRoot = $gitRoot
        if (-not $gitEntry.PSIsContainer) {
            $gitText = [IO.File]::ReadAllText($gitRoot)
            if ($gitText.Length -gt 4096 -or $gitText -notmatch '^gitdir: ([^\r\n]+)\r?\n?$') { return $null }
            $gitDirValue = $Matches[1]
            if ($gitDirValue -match '[\x00-\x1f\x7f"]' -or ($gitDirValue.Contains(':', [StringComparison]::Ordinal) -and $gitDirValue -notmatch '^[A-Za-z]:[\\/]') -or ($gitDirValue -match '^[A-Za-z]:[\\/].*:')) { return $null }
            if ([IO.Path]::IsPathRooted($gitDirValue)) { $gitRoot = [IO.Path]::GetFullPath($gitDirValue) }
            else { $gitRoot = [IO.Path]::GetFullPath((Join-Path $rootCanonical $gitDirValue)) }
            $rootGit = [IO.Path]::GetFullPath((Join-Path $rootCanonical '.git')).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
            $parentInfo = [System.IO.DirectoryInfo]$rootCanonical
            $commonGit = [IO.Path]::GetFullPath((Join-Path $parentInfo.Parent.FullName '.git')).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
            $worktreesDir = ([System.IO.DirectoryInfo]$gitRoot).Parent
            if ($null -eq $worktreesDir -or $worktreesDir.Name -cne 'worktrees' -or $null -eq $worktreesDir.Parent -or $worktreesDir.Parent.Name -cne '.git') { return $null }
            $commonGit = [IO.Path]::GetFullPath($worktreesDir.Parent.FullName).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
            $worktreeName = ([System.IO.DirectoryInfo]$gitRoot).Name
            if ($worktreeName -notmatch '^[A-Za-z0-9._-]+$' -or $worktreeName -ceq '.' -or $worktreeName -ceq '..' -or $worktreeName.EndsWith('.', [StringComparison]::Ordinal)) { return $null }
            if (-not (Test-Path -LiteralPath $gitRoot -PathType Container)) { return $null }
            $commondirPath = Join-Path $gitRoot 'commondir'
            if (-not (Test-Path -LiteralPath $commondirPath -PathType Leaf)) { return $null }
            Assert-TrustedExecutablePath $commondirPath
            $commonLines = [IO.File]::ReadAllLines($commondirPath)
            if ($commonLines.Count -ne 1 -or -not [String]::Equals($commonLines[0], '../..', [StringComparison]::Ordinal)) { return $null }
            $commonValue = $commonLines[0]
            $refRoot = [IO.Path]::GetFullPath((Join-Path $gitRoot $commonValue)).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
            if (-not [String]::Equals($refRoot, $commonGit, [StringComparison]::Ordinal)) { return $null }
            if (-not (Test-Path -LiteralPath $refRoot -PathType Container)) { return $null }
            Assert-TrustedExecutablePath $refRoot
            $reciprocalPath = Join-Path $gitRoot 'gitdir'
            if (-not (Test-Path -LiteralPath $reciprocalPath -PathType Leaf)) { return $null }
            Assert-TrustedExecutablePath $reciprocalPath
            $reciprocalText = [IO.File]::ReadAllText($reciprocalPath)
            if ($reciprocalText.Length -gt 4096 -or $reciprocalText -notmatch '^([^\r\n]+)\r?\n?$') { return $null }
            $reciprocalValue = $Matches[1]
            if ($reciprocalValue -match '[\x00-\x1f\x7f"]' -or ($reciprocalValue.Contains(':', [StringComparison]::Ordinal) -and $reciprocalValue -notmatch '^[A-Za-z]:[\\/]') -or ($reciprocalValue -match '^[A-Za-z]:[\\/].*:')) { return $null }
            $reciprocal = if ([IO.Path]::IsPathRooted($reciprocalValue)) { [IO.Path]::GetFullPath($reciprocalValue) } else { [IO.Path]::GetFullPath((Join-Path $gitRoot $reciprocalValue)) }
            if (-not [String]::Equals($reciprocal, [IO.Path]::GetFullPath((Join-Path $rootCanonical '.git')), [StringComparison]::Ordinal)) { return $null }
        }
        Assert-TrustedExecutablePath $gitRoot
        $headPath = Join-Path $gitRoot 'HEAD'
        Assert-TrustedExecutablePath $headPath
        $headText = [IO.File]::ReadAllText($headPath)
        if ($headText.Length -gt 4096) { return $null }
        $head = $headText.TrimEnd([char[]]"`r`n")
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
        $haveLooseRef = $false
        if (Test-Path -LiteralPath $loose -PathType Leaf) {
            Assert-TrustedExecutablePath $loose
            $looseText = [IO.File]::ReadAllText($loose)
            if ($looseText.Length -gt 4096) { return $null }
            $looseSha = $looseText.TrimEnd([char[]]"`r`n")
            if (-not [regex]::IsMatch($looseSha, '^[0-9a-f]{40}$', [Text.RegularExpressions.RegexOptions]::CultureInvariant)) { return $null }
            $found.Add($looseSha)
            $haveLooseRef = $true
        }
        $packed = Join-Path $refRoot 'packed-refs'
        if (Test-Path -LiteralPath $packed -PathType Leaf) {
            Assert-TrustedExecutablePath $packed
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
                if (-not $haveLooseRef -and [String]::Equals($packedRefName, $refName, [StringComparison]::Ordinal)) { $found.Add($packedMatch.Groups[1].Value) }
            }
        }
        if ($found.Count -ne 1 -or -not [regex]::IsMatch($found[0], '^[0-9a-f]{40}$', [Text.RegularExpressions.RegexOptions]::CultureInvariant)) { return $null }
        return $found[0]
    }
    catch { return $null }
}

function Test-SqlIdentifierStart {
    param([char] $Character)

    return [char]::IsLetter($Character) -or $Character -eq '_'
}

function Test-SqlIdentifierPart {
    param([char] $Character)

    return [char]::IsLetterOrDigit($Character) -or $Character -in @('_', '$')
}

function Test-SqlDollarQuoteTagPart {
    param([char] $Character)

    return [char]::IsLetterOrDigit($Character) -or $Character -eq '_'
}

function Test-SqlContainsTopLevelTransactionControl {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string] $Sql
    )

    $index = 0
    $firstKeyword = $null
    $isTransactionControlCandidate = $true

    while ($index -lt $Sql.Length) {
        $current = $Sql[$index]
        if ([char]::IsWhiteSpace($current)) {
            $index++
            continue
        }

        if ($current -eq '-' -and $index + 1 -lt $Sql.Length -and $Sql[$index + 1] -eq '-') {
            $index += 2
            while ($index -lt $Sql.Length -and $Sql[$index] -ne "`n") {
                $index++
            }
            continue
        }

        if ($current -eq '/' -and $index + 1 -lt $Sql.Length -and $Sql[$index + 1] -eq '*') {
            $commentDepth = 1
            $index += 2
            while ($index -lt $Sql.Length -and $commentDepth -gt 0) {
                if ($index + 1 -lt $Sql.Length -and $Sql[$index] -eq '/' -and $Sql[$index + 1] -eq '*') {
                    $commentDepth++
                    $index += 2
                }
                elseif ($index + 1 -lt $Sql.Length -and $Sql[$index] -eq '*' -and $Sql[$index + 1] -eq '/') {
                    $commentDepth--
                    $index += 2
                }
                else {
                    $index++
                }
            }
            continue
        }

        if ($current -eq ';') {
            $index++
            $firstKeyword = $null
            $isTransactionControlCandidate = $true
            continue
        }

        if ($current -eq [char] 39) {
            $prefixIndex = $index - 1
            $usesBackslashEscapes = $prefixIndex -ge 0 -and
                $Sql[$prefixIndex] -in @('e', 'E') -and
                ($prefixIndex -eq 0 -or -not (Test-SqlIdentifierPart $Sql[$prefixIndex - 1]))
            $index++
            while ($index -lt $Sql.Length) {
                if ($usesBackslashEscapes -and $Sql[$index] -eq [char] 92) {
                    $index = [Math]::Min($index + 2, $Sql.Length)
                }
                elseif ($Sql[$index] -ne [char] 39) {
                    $index++
                }
                elseif ($index + 1 -lt $Sql.Length -and $Sql[$index + 1] -eq [char] 39) {
                    $index += 2
                }
                else {
                    $index++
                    break
                }
            }
            $firstKeyword = $null
            $isTransactionControlCandidate = $false
            continue
        }

        if ($current -eq [char] 34) {
            $index++
            while ($index -lt $Sql.Length) {
                if ($Sql[$index] -ne [char] 34) {
                    $index++
                }
                elseif ($index + 1 -lt $Sql.Length -and $Sql[$index + 1] -eq [char] 34) {
                    $index += 2
                }
                else {
                    $index++
                    break
                }
            }
            $firstKeyword = $null
            $isTransactionControlCandidate = $false
            continue
        }

        if ($current -eq '$') {
            $tagEnd = $index + 1
            $delimiter = $null
            if ($tagEnd -lt $Sql.Length -and $Sql[$tagEnd] -eq '$') {
                $delimiter = '$$'
            }
            elseif ($tagEnd -lt $Sql.Length -and (Test-SqlIdentifierStart $Sql[$tagEnd])) {
                $tagEnd++
                while ($tagEnd -lt $Sql.Length -and (Test-SqlDollarQuoteTagPart $Sql[$tagEnd])) {
                    $tagEnd++
                }
                if ($tagEnd -lt $Sql.Length -and $Sql[$tagEnd] -eq '$') {
                    $delimiter = $Sql.Substring($index, $tagEnd - $index + 1)
                }
            }

            if ($null -ne $delimiter) {
                $contentStart = $tagEnd + 1
                $closingDelimiter = $Sql.IndexOf(
                    $delimiter,
                    $contentStart,
                    [StringComparison]::Ordinal)
                if ($closingDelimiter -lt 0) {
                    $index = $Sql.Length
                }
                else {
                    $index = $closingDelimiter + $delimiter.Length
                }
                $firstKeyword = $null
                $isTransactionControlCandidate = $false
                continue
            }
        }

        if ($isTransactionControlCandidate -and (Test-SqlIdentifierStart $current)) {
            $tokenStart = $index
            $index++
            while ($index -lt $Sql.Length -and (Test-SqlIdentifierPart $Sql[$index])) {
                $index++
            }

            $keyword = $Sql.Substring($tokenStart, $index - $tokenStart).ToUpperInvariant()
            if ($null -eq $firstKeyword) {
                if ($keyword -in @('BEGIN', 'COMMIT', 'END', 'ROLLBACK', 'ABORT', 'SAVEPOINT', 'RELEASE')) {
                    return $true
                }

                if ($keyword -in @('START', 'PREPARE', 'SET')) {
                    $firstKeyword = $keyword
                }
                else {
                    $isTransactionControlCandidate = $false
                }
            }
            else {
                if (($firstKeyword -eq 'START' -and $keyword -eq 'TRANSACTION') -or
                    ($firstKeyword -eq 'PREPARE' -and $keyword -eq 'TRANSACTION') -or
                    ($firstKeyword -eq 'SET' -and $keyword -eq 'TRANSACTION')) {
                    return $true
                }

                $firstKeyword = $null
                $isTransactionControlCandidate = $false
            }
            continue
        }

        $firstKeyword = $null
        $isTransactionControlCandidate = $false
        $index++
    }

    return $false
}

function ConvertTo-JsonContractValue {
    param([AllowNull()] $Value)

    if ($null -eq $Value) { return 'null' }
    return ($Value | ConvertTo-Json -Compress -Depth 100)
}

function Assert-JsonSchemaValue {
    param(
        [AllowNull()] $Value,
        [Parameter(Mandatory = $true)] $Schema,
        [Parameter(Mandatory = $true)] [string] $Path
    )

    if ($Schema.PSObject.Properties.Name -contains 'const' -and
        (ConvertTo-JsonContractValue $Value) -cne (ConvertTo-JsonContractValue $Schema.const)) {
        throw "JSON schema const mismatch at $Path"
    }
    if ($Schema.PSObject.Properties.Name -contains 'enum') {
        $valueJson = ConvertTo-JsonContractValue $Value
        if (-not (@($Schema.enum) | Where-Object { (ConvertTo-JsonContractValue $_) -ceq $valueJson })) {
            throw "JSON schema enum mismatch at $Path"
        }
    }

    if ($Schema.PSObject.Properties.Name -contains 'type') {
        $type = [string]$Schema.type
        $actual = if ($null -eq $Value) { 'null' }
            elseif ($Value -is [System.Management.Automation.PSCustomObject]) { 'object' }
            elseif ($Value -is [System.Array]) { 'array' }
            elseif ($Value -is [bool]) { 'boolean' }
            elseif ($Value -is [string]) { 'string' }
            elseif ($Value -is [int] -or $Value -is [long] -or $Value -is [decimal] -or $Value -is [double]) { 'number' }
            else { 'unknown' }
        if ($type -eq 'integer' -and $actual -eq 'number' -and ([double]$Value % 1 -eq 0)) { $actual = 'integer' }
        if ($actual -cne $type) { throw "JSON schema type mismatch at $Path (expected $type, got $actual)" }
    }

    if ($null -eq $Value) { return }
    if ($Value -is [System.Management.Automation.PSCustomObject]) {
        if ($Schema.PSObject.Properties.Name -contains 'required') {
            foreach ($requiredName in @($Schema.required)) {
                if ($Value.PSObject.Properties.Name -notcontains [string]$requiredName) {
                    throw "JSON schema required field is missing at $Path.$requiredName"
                }
            }
        }
        $declared = @()
        if ($Schema.PSObject.Properties.Name -contains 'properties' -and $null -ne $Schema.properties) {
            $declared = @($Schema.properties.PSObject.Properties.Name)
        }
        $closed = $Schema.PSObject.Properties.Name -contains 'additionalProperties' -and $Schema.additionalProperties -eq $false
        foreach ($property in @($Value.PSObject.Properties)) {
            if ($declared -notcontains $property.Name) {
                if ($closed) { throw "JSON schema rejects unknown field at $Path.$($property.Name)" }
                continue
            }
            $propertySchema = $Schema.properties.PSObject.Properties[$property.Name].Value
            Assert-JsonSchemaValue $property.Value $propertySchema "$Path.$($property.Name)"
        }
    }
    elseif ($Value -is [System.Array]) {
        if ($Schema.PSObject.Properties.Name -contains 'uniqueItems' -and $Schema.uniqueItems -eq $true) {
            $seen = @{}
            foreach ($item in @($Value)) {
                $itemJson = ConvertTo-JsonContractValue $item
                if ($seen.ContainsKey($itemJson)) { throw "JSON schema rejects duplicate array item at $Path" }
                $seen[$itemJson] = $true
            }
        }
        if ($Schema.PSObject.Properties.Name -contains 'items') {
            for ($index = 0; $index -lt $Value.Count; $index++) {
                Assert-JsonSchemaValue $Value[$index] $Schema.items "$Path[$index]"
            }
        }
    }
}

function Assert-RepositoryShape {
    $requiredFiles = @(
        'README.md',
        'SECURITY.md',
        'CONTRIBUTING.md',
        'BACKLOG.md',
        'SqlObserver.slnx',
        'global.json',
        'Directory.Build.props',
        'Directory.Packages.props',
        'NuGet.Config',
        '.gitattributes',
        '.github/workflows/validate.yml',
        'tools/dev-up.ps1',
        'tools/seed-lab.ps1',
        'tools/development-common.ps1',
        'docker-compose.yml',
        'database/seeds/development.sql',
        'docs/development.md',
        'tools/generate-permissions.ps1',
        'web/package.json',
        'web/pnpm-lock.yaml',
        'docs/architecture/overview.md',
        'docs/architecture/support-matrix.md',
        'docs/architecture/threat-model.md',
        'docs/product/clean-room-boundary.md',
        'docs/product/terminology.md',
        'docs/runbooks/README.md',
        'docs/milestones/M2-postgresql-repository.md',
        'docs/milestones/M3-onboarding-and-capabilities.md',
        'docs/milestones/M4-collector-framework-and-core-health.md',
        'docs/milestones/M10-rollups-host-replication-retention.md',
        'docs/milestones/M10-analytics-contracts.md',
        'docs/milestones/M11-mcp.md',
        'docs/adr/ADR-0013-host-observation-boundary.md',
        'docs/adr/ADR-0014-analytics-retention-boundary.md',
        'docs/architecture/host-observation-threat-notes.md',
        'collectors/manifests/collector-manifest.schema.json',
        'collectors/manifests/capability.connection.v1.json',
        'collectors/manifests/capability.connection.assets.sha256',
        'collectors/manifests/collector-manifest.v2.schema.json',
        'collectors/manifests/engine.core.v1.json',
        'collectors/manifests/database.inventory.v1.json',
        'collectors/manifests/database.files.v1.json',
        'collectors/manifests/m4-core-health.assets.sha256',
        'collectors/manifests/host.metrics.v1.json',
        'collectors/manifests/host.metrics.v1.schema.json',
        'collectors/manifests/m10-host.assets.sha256',
        'collectors/manifests/replication.health.v1.json',
        'collectors/manifests/replication.health.v1.schema.json',
        'collectors/manifests/m10-replication.assets.sha256',
        'collectors/manifests/capability.connection.v3.json',
        'collectors/manifests/capability.connection.v3.schema.json',
        'collectors/manifests/capability.connection.assets-v3.sha256'
    )

    foreach ($relativePath in $requiredFiles) {
        $absolutePath = Join-Path $repositoryRoot $relativePath
        if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
            throw "Required repository artifact is missing: $relativePath"
        }
    }

    $requiredDirectories = @(
        'docs/architecture',
        'docs/adr',
        'docs/product',
        'docs/runbooks',
        'database/migrations',
        'database/functions',
        'database/views',
        'database/seeds',
        'database/testdata',
        'collectors/sql',
        'collectors/manifests',
        'installer/wix',
        'installer/postgres'
    )

    foreach ($relativePath in $requiredDirectories) {
        $absolutePath = Join-Path $repositoryRoot $relativePath
        if (-not (Test-Path -LiteralPath $absolutePath -PathType Container)) {
            throw "Required repository directory is missing: $relativePath"
        }
    }

    $requiredSourceProjects = @(
        'SqlObserver.Domain',
        'SqlObserver.Application',
        'SqlObserver.Infrastructure.PostgreSql',
        'SqlObserver.Infrastructure.SqlServer',
        'SqlObserver.Infrastructure.Windows',
        'SqlObserver.Collector.Abstractions',
        'SqlObserver.Collectors',
        'SqlObserver.Reporting',
        'SqlObserver.Alerting',
        'SqlObserver.Analytics',
        'SqlObserver.Security',
        'SqlObserver.Audit',
        'SqlObserver.Server',
        'SqlObserver.Collector',
        'SqlObserver.Mcp',
        'SqlObserver.McpStdio',
        'SqlObserver.Cli',
        'SqlObserver.Observability'
    )

    foreach ($projectName in $requiredSourceProjects) {
        $projectPath = Join-Path $repositoryRoot "src/$projectName/$projectName.csproj"
        if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
            throw "Required source project is missing: $projectName"
        }
    }

    $requiredTestProjects = @(
        'SqlObserver.UnitTests',
        'SqlObserver.IntegrationTests.PostgreSql',
        'SqlObserver.IntegrationTests.SqlServer',
        'SqlObserver.ApiContractTests',
        'SqlObserver.McpContractTests',
        'SqlObserver.SecurityTests',
        'SqlObserver.PerformanceTests',
        'SqlObserver.EndToEndTests',
        'SqlObserver.ReleaseTests'
    )

    foreach ($projectName in $requiredTestProjects) {
        $projectPath = Join-Path $repositoryRoot "tests/$projectName/$projectName.csproj"
        if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
            throw "Required test project is missing: $projectName"
        }
    }

    $sourceProjects = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -Filter '*.csproj' -File)
    $testProjects = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests') -Recurse -Filter '*.csproj' -File)
    if ($sourceProjects.Count -ne 18 -or $testProjects.Count -ne 9) {
        throw "Expected 18 source and 9 test projects; found $($sourceProjects.Count) source and $($testProjects.Count) test projects."
    }

    [xml] $solution = Get-Content -LiteralPath $solutionPath -Raw
    $solutionProjects = @($solution.SelectNodes('//Project'))
    if ($solutionProjects.Count -ne 27) {
        throw "Expected 27 projects in SqlObserver.slnx, found $($solutionProjects.Count)."
    }

    $expectedSolutionProjects = @(
        $requiredSourceProjects | ForEach-Object { "src/$($_)/$($_).csproj" }
        $requiredTestProjects | ForEach-Object { "tests/$($_)/$($_).csproj" }
    ) | Sort-Object -Unique
    $actualSolutionProjects = @(
        $solutionProjects | ForEach-Object { $_.GetAttribute('Path').Replace('\', '/') }
    ) | Sort-Object -Unique
    $solutionDifferences = @(Compare-Object $expectedSolutionProjects $actualSolutionProjects)
    if ($actualSolutionProjects.Count -ne 27 -or $solutionDifferences.Count -ne 0) {
        throw 'SqlObserver.slnx membership differs from the exact required 18 source and 9 test projects.'
    }

    $buildProperties = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
    $requiredBuildSettings = @(
        '<TargetFramework>net10.0</TargetFramework>',
        '<Nullable>enable</Nullable>',
        '<EnableNETAnalyzers>true</EnableNETAnalyzers>',
        '<AnalysisLevel>latest-recommended</AnalysisLevel>',
        '<TreatWarningsAsErrors>true</TreatWarningsAsErrors>',
        '<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>'
    )
    foreach ($setting in $requiredBuildSettings) {
        if (-not $buildProperties.Contains($setting)) {
            throw "Required central .NET build setting is missing: $setting"
        }
    }

    $centralPackages = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Packages.props') -Raw
    if (-not $centralPackages.Contains('<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>')) {
        throw 'Central NuGet package management must remain enabled.'
    }

    $packageLocks = @(
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -Filter 'packages.lock.json' -File |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests') -Recurse -Filter 'packages.lock.json' -File |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    )
    if ($packageLocks.Count -ne 27) {
        throw "Expected one NuGet lock file per project (27), found $($packageLocks.Count)."
    }

    $allowedPackagesByProject = @{
        'SqlObserver.Observability' = @(
            'OpenTelemetry.Exporter.OpenTelemetryProtocol',
            'OpenTelemetry.Extensions.Hosting'
        )
        'SqlObserver.Collector' = @(
            'Microsoft.Extensions.Hosting',
            'Microsoft.Extensions.Hosting.WindowsServices'
        )
        'SqlObserver.Infrastructure.PostgreSql' = @('Npgsql')
        'SqlObserver.Infrastructure.SqlServer' = @('Microsoft.Data.SqlClient')
        'SqlObserver.Infrastructure.Windows' = @('System.Diagnostics.EventLog', 'System.Security.Cryptography.ProtectedData')
        'SqlObserver.Server' = @(
            'Microsoft.AspNetCore.Authentication.Negotiate',
            'Microsoft.Extensions.Hosting.WindowsServices'
        )
        'SqlObserver.Mcp' = @(
            'ModelContextProtocol',
            'ModelContextProtocol.AspNetCore'
        )
        'SqlObserver.McpStdio' = @(
            'Microsoft.Extensions.Hosting',
            'Microsoft.Extensions.Logging.Console'
        )
    }
    foreach ($sourceProject in $sourceProjects) {
        [xml] $projectXml = Get-Content -LiteralPath $sourceProject.FullName -Raw
        $actualPackages = @(
            $projectXml.SelectNodes('//PackageReference') |
                ForEach-Object { $_.GetAttribute('Include') }
        ) | Sort-Object -Unique
        $expectedPackages = @()
        if ($allowedPackagesByProject.ContainsKey($sourceProject.BaseName)) {
            $expectedPackages = @($allowedPackagesByProject[$sourceProject.BaseName]) | Sort-Object -Unique
        }

        if (($expectedPackages -join '|') -ne ($actualPackages -join '|')) {
            throw "Unexpected runtime package set in $($sourceProject.Name)."
        }
    }

    [xml] $collectorProject = Get-Content -LiteralPath (
        Join-Path $repositoryRoot 'src/SqlObserver.Collector/SqlObserver.Collector.csproj') -Raw
    if ($collectorProject.Project.Sdk -ne 'Microsoft.NET.Sdk.Worker') {
        throw 'SqlObserver.Collector must remain a .NET Worker project.'
    }

    [xml] $serverProject = Get-Content -LiteralPath (
        Join-Path $repositoryRoot 'src/SqlObserver.Server/SqlObserver.Server.csproj') -Raw
    if ($serverProject.Project.Sdk -ne 'Microsoft.NET.Sdk.Web') {
        throw 'SqlObserver.Server must remain an ASP.NET Core web project.'
    }

    $serverProjectText = $serverProject.OuterXml
    if ($serverProjectText.Contains('SqlObserver.Infrastructure.SqlServer')) {
        throw 'SqlObserver.Server must not reference the monitored-target adapter.'
    }

    $adrPath = Join-Path $repositoryRoot 'docs/adr'
    # ADR inventory is a canonical ordered contract.  A loose count lets a
    # stale/renamed decision silently pass, which in turn makes the validator
    # disagree with the checked-in architecture index.  Keep this list in
    # numeric order and compare both names and count.
    $expectedAdrNames = @(
        'ADR-0001-modular-monolith.md', 'ADR-0002-postgresql-repository.md',
        'ADR-0003-windows-services.md', 'ADR-0004-sql-first-migrations.md',
        'ADR-0005-collector-contract.md', 'ADR-0006-read-only-mcp.md',
        'ADR-0007-authentication.md', 'ADR-0008-partitioning-and-retention.md',
        'ADR-0009-clean-room-boundary.md', 'ADR-0010-passive-versus-enhanced-monitoring.md',
        'ADR-0011-query-text-and-plans-are-sensitive.md', 'ADR-0012-postgresql-worker-leases.md',
        'ADR-0013-host-observation-boundary.md', 'ADR-0014-analytics-retention-boundary.md',
        'ADR-0015-reports-and-exports.md', 'ADR-0016-windows-installer-and-postgresql-lifecycle.md',
        'ADR-0017-deployment-identities-secrets-and-transport.md', 'ADR-0018-web-assets-and-signalr.md',
        'ADR-0019-release-identity-and-evidence.md', 'ADR-0020-development-authentication.md'
    )
    $adrFiles = @(Get-ChildItem -LiteralPath $adrPath -Filter 'ADR-*.md' -File | Sort-Object Name)
    $actualAdrNames = @($adrFiles | ForEach-Object { $_.Name })
    if (($actualAdrNames -join '|') -cne ($expectedAdrNames -join '|')) {
        throw "ADR inventory is not the canonical ordered set. Expected $($expectedAdrNames.Count) files, found $($adrFiles.Count): $($actualAdrNames -join ', ')"
    }

    foreach ($adrFile in $adrFiles) {
        $adrContent = Get-Content -LiteralPath $adrFile.FullName -Raw
        if ($adrContent -notmatch '(?m)^- Status: (Accepted(?: \(local implementation\))?|Proposed)\r?$') {
            throw "ADR must have Accepted or Proposed status: $($adrFile.Name)"
        }
    }

    $sourceSql = @(
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -Filter '*.cs' -File |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    )
    if ($sourceSql.Count -gt 0) {
        $forbiddenTool = $sourceSql | Select-String -SimpleMatch 'execute_sql'
        if ($null -ne $forbiddenTool) {
            throw 'The forbidden execute_sql MCP tool name appears in production source.'
        }
    }

    foreach ($mcpPackage in @('ModelContextProtocol', 'ModelContextProtocol.AspNetCore', 'ModelContextProtocol.Core')) {
        if ($centralPackages -notmatch ('<PackageVersion Include="' + [regex]::Escape($mcpPackage) + '" Version="2\.2\.0"\s*/>')) {
            throw "M11 must pin the official stable $mcpPackage package to 2.2.0."
        }
    }
    $mcpRuntimeSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/SqlObserver.Mcp/McpRuntime.cs') -Raw
    foreach ($requiredMcpBoundary in @('McpCursorSigner', 'McpOutputProjection', 'StructuredContent', 'OutputSchema', 'McpApplicationResponseOversizeException', 'FixedTimeEquals', 'RequestDigest')) {
        if (-not $mcpRuntimeSource.Contains($requiredMcpBoundary)) {
            throw "M11 MCP boundary asset is missing: $requiredMcpBoundary"
        }
    }
    $mcpForecastMigration = Join-Path $repositoryRoot 'database/migrations/0017_mcp_forecast_limit.sql'
    if (-not (Test-Path -LiteralPath $mcpForecastMigration -PathType Leaf)) {
        throw 'M11 forecast limit migration 0017 is required and immutable migrations must not be edited in place.'
    }
    $mcpForecastSql = Get-Content -LiteralPath $mcpForecastMigration -Raw
    if ($mcpRuntimeSource -match 'Convert\.ToBase64String\(bytes\)') {
        throw 'M11 cursors must use signed envelopes rather than bare base64 payloads.'
    }
    if ($mcpForecastSql -notmatch 'get_m10_forecast_scoped[\s\S]*p_limit integer' -or
        $mcpForecastSql -notmatch 'LIMIT p_limit' -or
        $mcpForecastSql -notmatch 'GRANT EXECUTE') {
        throw 'M11 forecast migration must expose a bounded server-only limit overload.'
    }
    $mcpMigrationDirectory = Join-Path $repositoryRoot 'database/migrations'
    foreach ($mcpMigrationNumber in 15..20) {
        $mcpMigration = @(Get-ChildItem -LiteralPath $mcpMigrationDirectory -Filter ("{0:D4}_*.sql" -f $mcpMigrationNumber) -File)
        if ($mcpMigration.Count -ne 1) { throw "M11 migration $mcpMigrationNumber is missing or ambiguous." }
        $mcpMigrationText = Get-Content -LiteralPath $mcpMigration[0].FullName -Raw
        foreach ($mcpBoundary in @('SECURITY DEFINER', 'search_path', 'TimeZone', 'REVOKE', 'PUBLIC', 'sqlobserver_collector', 'sqlobserver_auditor', 'GRANT EXECUTE', 'sqlobserver_server')) {
            if ($mcpMigrationText -notmatch [regex]::Escape($mcpBoundary)) { throw "M11 migration $($mcpMigration[0].Name) lacks required boundary: $mcpBoundary" }
        }
    }
    $mcpForecastCursorMigration = Get-Content -LiteralPath (Join-Path $repositoryRoot 'database/migrations/0019_mcp_forecast_cursor.sql') -Raw
    $mcpForecastSignature = 'get_m10_forecast_scoped\s*\(\s*p_instance_id\s+uuid\s*,\s*p_target_revision\s+bigint\s*,\s*p_metric_key\s+text\s*,\s*p_dimensions\s+jsonb\s*,\s*p_horizon\s+interval\s*,\s*p_snapshot_utc\s+timestamptz\s*,\s*p_limit\s+integer\s*,\s*p_cursor_horizon_start\s+timestamptz\s*,\s*p_cursor_forecast_id\s+uuid\s*\)'
    if ($mcpForecastCursorMigration -notmatch $mcpForecastSignature -or
        $mcpForecastCursorMigration -notmatch 'p_limit\s+BETWEEN\s+1\s+AND\s+201' -or
        $mcpForecastCursorMigration -notmatch 'p_cursor_horizon_start\s+IS\s+NULL\s+AND\s+p_cursor_forecast_id\s+IS\s+NULL' -or
        $mcpForecastCursorMigration -notmatch 'p_cursor_horizon_start\s+IS\s+NOT\s+NULL\s+AND\s+p_cursor_forecast_id\s+IS\s+NOT\s+NULL' -or
        $mcpForecastCursorMigration -notmatch '\(f\.horizon_start\s*,\s*f\.forecast_id\)\s*>\s*\(p_cursor_horizon_start\s*,\s*p_cursor_forecast_id\)' -or
        $mcpForecastCursorMigration -notmatch 'ORDER\s+BY\s+f\.horizon_start\s*,\s*f\.forecast_id' -or
        $mcpForecastCursorMigration -notmatch 'LANGUAGE\s+sql\s+STABLE\s+SECURITY\s+DEFINER' -or
        $mcpForecastCursorMigration -notmatch 'SET\s+search_path\s*=\s*pg_catalog,analytics,control' -or
        $mcpForecastCursorMigration -notmatch "SET\s+TimeZone\s*=\s*'UTC'" -or
        $mcpForecastCursorMigration -notmatch 'REVOKE\s+ALL\s+ON\s+FUNCTION[\s\S]*FROM\s+PUBLIC,sqlobserver_collector,sqlobserver_auditor' -or
        $mcpForecastCursorMigration -notmatch 'GRANT\s+EXECUTE\s+ON\s+FUNCTION[\s\S]*TO\s+sqlobserver_server') {
        throw 'M11 migration 0019 must expose the exact bounded 9-argument, secure, complete-tuple forecast contract.'
    }
    $mcpSnapshotMigration = Get-Content -LiteralPath (Join-Path $repositoryRoot 'database/migrations/0020_mcp_snapshot_and_incident_cursor.sql') -Raw
    foreach ($mcpSnapshotClause in @('h\.observed_at<=p_snapshot_utc', 'h\.collected_at<=p_snapshot_utc', 'f\.computed_at<=p_snapshot_utc', 'd\.collected_at<=p_snapshot_utc', 'p_cursor_occurred_at', 'p_cursor_packet_id', 'p_cursor_generation', 'ORDER BY e\.occurred_at,e\.packet_id', 'ORDER BY g\.generation')) {
        if ($mcpSnapshotMigration -notmatch $mcpSnapshotClause) { throw "M11 migration 0020 is missing snapshot/cursor clause: $mcpSnapshotClause" }
    }
    $mcpProjects = @(
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -Filter '*.csproj' -File |
            Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match 'ModelContextProtocol' }
    )
    if ($mcpProjects.Count -ne 1 -or $mcpProjects[0].BaseName -cne 'SqlObserver.Mcp') {
        throw 'Official MCP SDK references must remain confined to SqlObserver.Mcp.'
    }

    $placeholderOnlyDirectories = @(
        'database/testdata',
        'installer/wix',
        'installer/postgres'
    )
    foreach ($relativePath in $placeholderOnlyDirectories) {
        $unexpectedAssets = @(
            Get-ChildItem -LiteralPath (Join-Path $repositoryRoot $relativePath) -Recurse -File -Force |
                Where-Object { $_.Name -ne '.gitkeep' }
        )
        if ($unexpectedAssets.Count -ne 0) {
            throw "Runtime assets are outside the currently implemented milestone scope: $relativePath"
        }
    }

    $migrationPath = Join-Path $repositoryRoot 'database/migrations'
    $migrationFiles = @(Get-ChildItem -LiteralPath $migrationPath -Filter '*.sql' -File | Sort-Object Name)
    if ($migrationFiles.Count -lt 3) {
        throw "M2 requires at least three ordered SQL migrations; found $($migrationFiles.Count)."
    }

    $expectedMigrationVersion = 1
    foreach ($migrationFile in $migrationFiles) {
        if ($migrationFile.Name -notmatch '^(\d{4})_[a-z0-9_]+\.sql$') {
            throw "Migration filename is not immutable-numbered form: $($migrationFile.Name)"
        }

        if ([int] $Matches[1] -ne $expectedMigrationVersion) {
            throw "Migration sequence has a gap at $($migrationFile.Name)."
        }

        if ([IO.File]::ReadAllBytes($migrationFile.FullName) -contains 13) {
            throw "Migration must use deterministic LF line endings: $($migrationFile.Name)"
        }

        $migrationText = Get-Content -LiteralPath $migrationFile.FullName -Raw
        if (Test-SqlContainsTopLevelTransactionControl $migrationText) {
            throw "Migration contains transaction control owned by the runner: $($migrationFile.Name)"
        }

        $expectedMigrationVersion++
    }

    $checksumPath = Join-Path $migrationPath 'checksums.sha256'
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
        throw 'M2 migration checksum manifest is missing.'
    }

    $manifestEntries = @{}
    $manifestOrder = @()
    foreach ($line in Get-Content -LiteralPath $checksumPath) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#', [StringComparison]::Ordinal)) {
            continue
        }

        if ($line -notmatch '^([0-9a-f]{64})  (\d{4}_[a-z0-9_]+\.sql)$') {
            throw "Invalid migration checksum manifest entry: $line"
        }

        if ($manifestEntries.ContainsKey($Matches[2])) {
            throw "Duplicate migration checksum entry: $($Matches[2])"
        }

        $manifestEntries[$Matches[2]] = $Matches[1]
        $manifestOrder += $Matches[2]
    }

    if ($manifestEntries.Count -ne $migrationFiles.Count) {
        throw 'Migration checksum manifest must contain exactly one entry per SQL migration.'
    }

    $expectedManifestOrder = @($migrationFiles | ForEach-Object { $_.Name })
    if (($manifestOrder -join '|') -cne ($expectedManifestOrder -join '|')) {
        throw 'Migration checksum manifest entries must remain in exact migration order.'
    }

    foreach ($migrationFile in $migrationFiles) {
        $actualChecksum = (Get-FileHash -LiteralPath $migrationFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not $manifestEntries.ContainsKey($migrationFile.Name) -or
            $manifestEntries[$migrationFile.Name] -cne $actualChecksum) {
            throw "Migration checksum mismatch: $($migrationFile.Name)"
        }
    }

    $migrationSql = ($migrationFiles | ForEach-Object {
        Get-Content -LiteralPath $_.FullName -Raw
    }) -join "`n"
    foreach ($schemaName in @(
        'control', 'security', 'telemetry', 'events', 'analytics',
        'alerting', 'reporting', 'audit', 'system')) {
        if ($migrationSql -notmatch "(?i)CREATE\s+SCHEMA\s+(?:IF\s+NOT\s+EXISTS\s+)?$schemaName\b") {
            throw "M2 repository schema is missing from migrations: $schemaName"
        }
    }

    foreach ($requiredSqlInvariant in @(
        'PARTITION BY RANGE',
        'USING brin',
        'sqlobserver_server',
        'sqlobserver_collector',
        'sqlobserver_migrator',
        'sqlobserver_auditor',
        'NOLOGIN')) {
        if ($migrationSql.IndexOf($requiredSqlInvariant, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "M2 SQL invariant is missing: $requiredSqlInvariant"
        }
    }

    if ($migrationSql -match '(?i)\bPASSWORD\s+' -or
        $migrationSql -match '(?i)(?<!NO)\bSUPERUSER\b' -or
        $migrationSql -match '(?i)timestamp\s+without\s+time\s+zone') {
        throw 'M2 migrations must not embed passwords, create superusers, or persist non-UTC timestamp types.'
    }

    $postgresIntegrationSource = @(
        Get-ChildItem -LiteralPath (
            Join-Path $repositoryRoot 'tests/SqlObserver.IntegrationTests.PostgreSql') -Filter '*.cs' -File
    )
    $skippedPostgresTests = @(
        $postgresIntegrationSource |
            Select-String -Pattern '\[(Fact|Theory)\s*\(\s*Skip\s*='
    )
    if ($postgresIntegrationSource.Count -eq 0 -or $skippedPostgresTests.Count -ne 0) {
        throw 'M2 PostgreSQL integration tests must be active and must not use Skip.'
    }

    $collectorManifestPath = Join-Path $repositoryRoot 'collectors/manifests/capability.connection.v1.json'
    $collectorManifest = Get-Content -LiteralPath $collectorManifestPath -Raw | ConvertFrom-Json
    if ($collectorManifest.collectorId -cne 'capability.connection' -or
        $collectorManifest.schemaVersion -ne 1 -or
        $collectorManifest.collectorVersion -ne 1 -or
        $collectorManifest.outputSchemaVersion -ne 1 -or
        $collectorManifest.operationalMode -cne 'passive' -or
        $collectorManifest.supportedTargets.minimumMajorVersion -ne 15 -or
        $collectorManifest.supportedTargets.maximumMajorVersion -ne 17 -or
        $collectorManifest.executionBounds.maximumRows -ne 1 -or
        $collectorManifest.executionBounds.maximumResponseBytes -gt 16384 -or
        $collectorManifest.executionBounds.connectTimeoutSeconds -gt 30 -or
        $collectorManifest.executionBounds.commandTimeoutSeconds -gt 120 -or
        -not $collectorManifest.cadence.nonOverlappingPerTarget) {
        throw 'M3 capability.connection manifest identity, support, passive mode, version, or bounds are invalid.'
    }

    $collectorSqlPath = Join-Path $repositoryRoot 'collectors/sql'
    $collectorSqlFiles = @(Get-ChildItem -LiteralPath $collectorSqlPath -Filter '*.sql' -File | Sort-Object Name)
    $expectedM3CollectorSqlNames = @(
        'capability.connection.bootstrap.v1.sql',
        'capability.connection.fallback.v1.sql',
        'capability.connection.sqlserver15-windows.v1.sql',
        'capability.connection.sqlserver16-windows.v1.sql',
        'capability.connection.sqlserver17-windows.v1.sql'
    ) | Sort-Object
    $expectedM4CollectorSqlNames = @(
        'engine.core.sqlserver15-windows.v1.sql',
        'engine.core.sqlserver16-windows.v1.sql',
        'engine.core.sqlserver17-windows.v1.sql',
        'database.inventory.sqlserver15-windows.v1.sql',
        'database.inventory.sqlserver16-windows.v1.sql',
        'database.inventory.sqlserver17-windows.v1.sql',
        'database.files.sqlserver15-windows.v1.sql',
        'database.files.sqlserver16-windows.v1.sql',
        'database.files.sqlserver17-windows.v1.sql'
    ) | Sort-Object
    $activeM5CollectorSqlNames = @(
        'activity.sessions.sqlserver15-windows.v1.sql',
        'activity.sessions.sqlserver16-windows.v1.sql',
        'activity.sessions.sqlserver17-windows.v1.sql',
        'activity.requests.sqlserver15-windows.v1.sql',
        'activity.requests.sqlserver16-windows.v1.sql',
        'activity.requests.sqlserver17-windows.v1.sql',
        'waits.server.sqlserver15-windows.v1.sql',
        'waits.server.sqlserver16-windows.v1.sql',
        'waits.server.sqlserver17-windows.v1.sql',
        'blocking.current.sqlserver15-windows.v1.sql',
        'blocking.current.sqlserver16-windows.v1.sql',
        'blocking.current.sqlserver17-windows.v1.sql'
    ) | Sort-Object
    $activeM6CollectorSqlNames = @(
        'deadlocks.system-health.sqlserver15-windows.v1.sql',
        'deadlocks.system-health.sqlserver16-windows.v1.sql',
        'deadlocks.system-health.sqlserver17-windows.v1.sql'
    ) | Sort-Object
    $activeM7CollectorSqlNames = @(
        'queries.performance.sqlserver15-windows.v1.sql',
        'queries.performance.sqlserver16-windows.v1.sql',
        'queries.performance.sqlserver17-windows.v1.sql'
    ) | Sort-Object
    $expectedM9CollectorSqlNames = @(
        'capability.connection.sqlserver15-windows.v2.sql',
        'capability.connection.sqlserver16-windows.v2.sql',
        'capability.connection.sqlserver17-windows.v2.sql',
        'backups.status.sqlserver15-windows.v1.sql',
        'backups.status.sqlserver16-windows.v1.sql',
        'backups.status.sqlserver17-windows.v1.sql',
        'sql-agent.failures.sqlserver15-windows.v1.sql',
        'sql-agent.failures.sqlserver16-windows.v1.sql',
        'sql-agent.failures.sqlserver17-windows.v1.sql',
        'tempdb.health.sqlserver15-windows.v1.sql',
        'tempdb.health.sqlserver16-windows.v1.sql',
        'tempdb.health.sqlserver17-windows.v1.sql',
        'availability-groups.health.sqlserver15-windows.v1.sql',
        'availability-groups.health.sqlserver16-windows.v1.sql',
        'availability-groups.health.sqlserver17-windows.v1.sql'
    ) | Sort-Object
    $expectedM10CollectorSqlNames = @(
        'capability.connection.sqlserver15-windows.v3.sql',
        'capability.connection.sqlserver16-windows.v3.sql',
        'capability.connection.sqlserver17-windows.v3.sql',
        'replication.health.sqlserver15-windows.v1.sql',
        'replication.health.sqlserver16-windows.v1.sql',
        'replication.health.sqlserver17-windows.v1.sql'
    ) | Sort-Object
    $expectedM10ReplicationSqlNames = @(
        'replication.health.sqlserver15-windows.v1.sql',
        'replication.health.sqlserver16-windows.v1.sql',
        'replication.health.sqlserver17-windows.v1.sql'
    ) | Sort-Object
    $expectedCollectorSqlNames = @(
        $expectedM3CollectorSqlNames + $expectedM4CollectorSqlNames + $activeM5CollectorSqlNames + $activeM6CollectorSqlNames + $activeM7CollectorSqlNames + $expectedM9CollectorSqlNames + $expectedM10CollectorSqlNames
        'activity.live.v1.sql'
    ) | Sort-Object
    if (($collectorSqlFiles.Name -join '|') -cne ($expectedCollectorSqlNames -join '|')) {
        throw 'Collector SQL must contain exactly the reviewed M3/M4/M5/M6/M7/M9/M10 assets.'
    }

    foreach ($collectorSqlFile in $collectorSqlFiles) {
        if ($collectorSqlFile.Name -ceq 'activity.live.v1.sql') {
            $liveSqlPin = [IO.File]::ReadAllText((Join-Path $repositoryRoot 'collectors/manifests/activity.live.v1.sha256')).Trim()
            if ($liveSqlPin -cne (Get-FileHash -LiteralPath $collectorSqlFile.FullName -Algorithm SHA256).Hash) { throw 'Live activity SQL checksum mismatch.' }
        }
        if ([IO.File]::ReadAllBytes($collectorSqlFile.FullName) -contains 13) {
            throw "Collector SQL must use deterministic LF line endings: $($collectorSqlFile.Name)"
        }

        $collectorSql = Get-Content -LiteralPath $collectorSqlFile.FullName -Raw
        if ($collectorSql -notmatch '(?i)\bSET\s+NOCOUNT\s+ON\s*;' -or
            $collectorSql -match '(?i)\b(INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|EXEC(?:UTE)?|DBCC|BACKUP|RESTORE|RECONFIGURE|KILL)\b' -or
            $collectorSql -match '(?i)\b(xp_|sp_OA|sp_executesql|OPENROWSET|OPENDATASOURCE)') {
            throw "Collector SQL must remain fixed, bounded, read-only, and supported: $($collectorSqlFile.Name)"
        }

        if ($collectorSqlFile.Name -in $expectedM3CollectorSqlNames -and
            $collectorSql -notmatch '(?i)\bSELECT\s+TOP\s*\(\s*1\s*\)') {
            throw "M3 capability SQL must remain one-row bounded: $($collectorSqlFile.Name)"
        }

        if ($collectorSqlFile.Name -in $expectedM4CollectorSqlNames -and
            ($collectorSql -notmatch '(?i)\bTOP\s*\(\s*@maximum_rows\s*\)' -or
             $collectorSql -notmatch '(?i)\bORDER\s+BY\b' -or
             $collectorSql -match '(?i)\bphysical_name\b')) {
            throw "M4 collector SQL must use the typed row bound, deterministic order, and exclude physical paths: $($collectorSqlFile.Name)"
        }

        if ($collectorSqlFile.Name -in $activeM5CollectorSqlNames -and
            ($collectorSql -notmatch '(?i)\bTOP\s*\(\s*@maximum_rows\s*\)' -or
             $collectorSql -notmatch '(?i)\bORDER\s+BY\b' -or
             $collectorSql -match '(?i)\b(sql_handle|plan_handle|query_hash|query_plan_hash|wait_resource|resource_description|login_name|host_name|program_name|client_interface_name|local_net_address|client_net_address)\b')) {
            throw "M5 collector SQL must remain bounded, ordered, and exclude sensitive identity/query/resource fields: $($collectorSqlFile.Name)"
        }

        if ($collectorSqlFile.Name -in $activeM6CollectorSqlNames -and
            ($collectorSql -notmatch '(?i)\bTOP\s*\(\s*@maximum_rows\s*\)' -or
             $collectorSql -notmatch '(?i)\bORDER\s+BY\b' -or
             $collectorSql -notmatch '(?i)sys\.fn_xe_file_target_read_file' -or
             $collectorSql -notmatch '(?i)system_health' -or
             $collectorSql -notmatch '(?i)system_health\*\.xel' -or
             $collectorSql -notmatch '(?i)CHARINDEX\s*\(' -or
             $collectorSql -notmatch '(?i)REVERSE\s*\(' -or
             $collectorSql -notmatch '(?i)DATEADD\s*\(\s*day\s*,\s*-33' -or
             $collectorSql -notmatch '(?i)source_window\.start_utc' -or
             $collectorSql -notmatch '(?i)occurred_at_utc\s+IS\s+NULL' -or
             $collectorSql -notmatch '(?i)DATALENGTH\s*\(' -or
             $collectorSql -notmatch '(?i)server_event_session_fields' -or
             $collectorSql -notmatch '(?i)max_rollover_files\s+BETWEEN\s+1\s+AND\s+10' -or
             $collectorSql -notmatch '(?i)max_file_size_mb\s+BETWEEN\s+1\s+AND\s+100' -or
             $collectorSql -notmatch '(?i)config_invalid' -or
             $collectorSql -notmatch '(?i)fn_xe_file_target_read_file\([^\r\n]*,\s*NULL\)' -or
             $collectorSql -match '(?i)\b(CREATE|ALTER|DROP|START|STOP)\s+(EVENT\s+SESSION|SESSION)')) {
            throw "M6 collector SQL must be passive system_health reads with fixed bounds and deterministic order: $($collectorSqlFile.Name)"
        }
        if ($collectorSqlFile.Name -in $activeM7CollectorSqlNames -and
            ($collectorSql -notmatch '(?i)\bTOP\s*\(\s*@probe_rows\s*\)' -or
             $collectorSql -notmatch '(?i)\bORDER\s+BY\b' -or
             $collectorSql -notmatch '(?i)query_store' -or
             $collectorSql -match '(?i)\b(sql_handle|plan_handle|sys\.dm_exec_sql_text|query_plan|EXEC(?:UTE)?|ALTER|UPDATE|DELETE|INSERT)\b')) {
            throw "M7 collector SQL must be bounded, metadata-only, and passive: $($collectorSqlFile.Name)"
        }
        if ($collectorSqlFile.Name -in $expectedM9CollectorSqlNames -and
            $collectorSqlFile.Name -notlike 'capability.connection.*.v2.sql' -and
            ($collectorSql -notmatch '(?i)\bSELECT\s+TOP\s*(?:\(\s*@(?:maximum_rows|scan_rows)\s*\)|\(\s*1\s*\))' -or
             $collectorSql -notmatch '(?i)\bORDER\s+BY\b' -or
             $collectorSql -match '(?i)\b(INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|EXEC(?:UTE)?|DBCC|BACKUP|RESTORE|RECONFIGURE|KILL)\b')) {
            throw "M9 collector SQL must remain bounded, ordered, and passive: $($collectorSqlFile.Name)"
        }
        if ($collectorSqlFile.Name -in $expectedM10ReplicationSqlNames -and
            ($collectorSql -notmatch '(?i)\bTOP\s*\(\s*@maximum_rows\s*\)' -or
             $collectorSql -notmatch '(?i)\bORDER\s+BY\b' -or
             $collectorSql -notmatch '(?i)@distribution_database' -or
             $collectorSql -match '(?i)\b(LSN|EXEC(?:UTE)?|INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE)\b')) {
            throw "M10 replication SQL must remain bounded, passive, and visibility-safe: $($collectorSqlFile.Name)"
        }
        if ($collectorSqlFile.Name -in @('capability.connection.sqlserver15-windows.v2.sql','capability.connection.sqlserver16-windows.v2.sql','capability.connection.sqlserver17-windows.v2.sql') -and
            ($collectorSql -notmatch '(?i)\bSELECT\s+TOP\s*\(\s*1\s*\)' -or
             $collectorSql -match '(?i)\b(INSERT|UPDATE|DELETE|MERGE|CREATE|ALTER|DROP|TRUNCATE|EXEC(?:UTE)?|DBCC|BACKUP|RESTORE|RECONFIGURE|KILL)\b')) {
            throw "Capability v2 SQL must remain bounded and passive: $($collectorSqlFile.Name)"
        }
        if ($collectorSqlFile.Name -like 'availability-groups.health.*' -and
            ($collectorSql -notmatch '(?i)sys\.dm_hadr_availability_replica_states\s+AS\s+ars' -or
             $collectorSql -notmatch '(?i)ars\.replica_id\s*=\s*ar\.replica_id' -or
             $collectorSql -match '(?i)\bar\.(?:role_desc|operational_state_desc|connected_state_desc)')) {
            throw "Availability-group assets must source state descriptions from the DMF for local/remote semantics: $($collectorSqlFile.Name)"
        }
        if ($collectorSqlFile.Name -like 'sql-agent.failures.*' -and
            $collectorSql -match '(?i)SELECT[\s\S]*\b(run_date|run_time)\b') {
            throw "SQL Agent source-local timestamps must not be emitted: $($collectorSqlFile.Name)"
        }
    }

    $collectorChecksumPath = Join-Path $repositoryRoot 'collectors/manifests/capability.connection.assets.sha256'
    $collectorAssetPaths = @{
        'collector-manifest.schema.json' = Join-Path $repositoryRoot 'collectors/manifests/collector-manifest.schema.json'
        'capability.connection.v1.json' = Join-Path $repositoryRoot 'collectors/manifests/capability.connection.v1.json'
    }
    foreach ($collectorSqlFile in $collectorSqlFiles | Where-Object { $_.Name -in $expectedM3CollectorSqlNames }) {
        $collectorAssetPaths[$collectorSqlFile.Name] = $collectorSqlFile.FullName
    }
    $collectorChecksums = @{}
    foreach ($line in Get-Content -LiteralPath $collectorChecksumPath) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#', [StringComparison]::Ordinal)) {
            continue
        }
        if ($line -notmatch '^([0-9a-f]{64})  ([A-Za-z0-9.-]+)$') {
            throw "Invalid M3 collector checksum entry: $line"
        }
        $assetName = $Matches[2]
        if (-not $collectorAssetPaths.ContainsKey($assetName) -or
            $collectorChecksums.ContainsKey($assetName)) {
            throw "Unexpected or duplicate M3 collector checksum entry: $line"
        }
        $collectorChecksums[$assetName] = $Matches[1]
    }
    if ($collectorChecksums.Count -ne $collectorAssetPaths.Count) {
        throw 'M3 collector checksum manifest must contain exactly one entry per pinned asset.'
    }
    foreach ($assetName in $collectorAssetPaths.Keys) {
        $actualChecksum = (Get-FileHash -LiteralPath $collectorAssetPaths[$assetName] -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not $collectorChecksums.ContainsKey($assetName) -or
            $collectorChecksums[$assetName] -cne $actualChecksum) {
            throw "M3 collector checksum mismatch: $assetName"
        }
    }

    $m4CollectorAssetPaths = @{
        'collector-manifest.v2.schema.json' = Join-Path $repositoryRoot 'collectors/manifests/collector-manifest.v2.schema.json'
        'engine.core.v1.json' = Join-Path $repositoryRoot 'collectors/manifests/engine.core.v1.json'
        'database.inventory.v1.json' = Join-Path $repositoryRoot 'collectors/manifests/database.inventory.v1.json'
        'database.files.v1.json' = Join-Path $repositoryRoot 'collectors/manifests/database.files.v1.json'
    }
    foreach ($collectorSqlFile in $collectorSqlFiles | Where-Object { $_.Name -in $expectedM4CollectorSqlNames }) {
        $m4CollectorAssetPaths[$collectorSqlFile.Name] = $collectorSqlFile.FullName
    }
    $m4CollectorChecksums = @{}
    $m4CollectorChecksumPath = Join-Path $repositoryRoot 'collectors/manifests/m4-core-health.assets.sha256'
    foreach ($line in Get-Content -LiteralPath $m4CollectorChecksumPath) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#', [StringComparison]::Ordinal)) {
            continue
        }
        if ($line -notmatch '^([0-9a-f]{64})  ([A-Za-z0-9.-]+)$') {
            throw "Invalid M4 collector checksum entry: $line"
        }
        $assetName = $Matches[2]
        if (-not $m4CollectorAssetPaths.ContainsKey($assetName) -or
            $m4CollectorChecksums.ContainsKey($assetName)) {
            throw "Unexpected or duplicate M4 collector checksum entry: $line"
        }
        $m4CollectorChecksums[$assetName] = $Matches[1]
    }
    if ($m4CollectorChecksums.Count -ne $m4CollectorAssetPaths.Count) {
        throw 'M4 collector checksum manifest must contain exactly one entry per pinned asset.'
    }
    foreach ($assetName in $m4CollectorAssetPaths.Keys) {
        $actualChecksum = (Get-FileHash -LiteralPath $m4CollectorAssetPaths[$assetName] -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not $m4CollectorChecksums.ContainsKey($assetName) -or
            $m4CollectorChecksums[$assetName] -cne $actualChecksum) {
            throw "M4 collector checksum mismatch: $assetName"
        }
    }

    $m5CollectorAssetPaths = @{
        'collector-manifest.v3.schema.json' = Join-Path $repositoryRoot 'collectors/manifests/collector-manifest.v3.schema.json'
        'activity.sessions.v1.json' = Join-Path $repositoryRoot 'collectors/manifests/activity.sessions.v1.json'
        'activity.requests.v1.json' = Join-Path $repositoryRoot 'collectors/manifests/activity.requests.v1.json'
        'waits.server.v1.json' = Join-Path $repositoryRoot 'collectors/manifests/waits.server.v1.json'
        'blocking.current.v1.json' = Join-Path $repositoryRoot 'collectors/manifests/blocking.current.v1.json'
    }
    foreach ($collectorSqlFile in $collectorSqlFiles | Where-Object { $_.Name -in $activeM5CollectorSqlNames }) {
        $m5CollectorAssetPaths[$collectorSqlFile.Name] = $collectorSqlFile.FullName
    }
    $m5CollectorChecksums = @{}
    $m5CollectorChecksumPath = Join-Path $repositoryRoot 'collectors/manifests/m5-activity.assets.sha256'
    foreach ($line in Get-Content -LiteralPath $m5CollectorChecksumPath) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#', [StringComparison]::Ordinal)) {
            continue
        }
        if ($line -notmatch '^([0-9a-f]{64})  ([A-Za-z0-9.-]+)$') {
            throw "Invalid M5 collector checksum entry: $line"
        }
        $assetName = $Matches[2]
        if (-not $m5CollectorAssetPaths.ContainsKey($assetName) -or
            $m5CollectorChecksums.ContainsKey($assetName)) {
            throw "Unexpected or duplicate M5 collector checksum entry: $line"
        }
        $m5CollectorChecksums[$assetName] = $Matches[1]
    }
    if ($m5CollectorChecksums.Count -ne $m5CollectorAssetPaths.Count) {
        throw 'M5 collector checksum manifest must contain exactly one entry per pinned asset.'
    }
    foreach ($assetName in $m5CollectorAssetPaths.Keys) {
        $actualChecksum = (Get-FileHash -LiteralPath $m5CollectorAssetPaths[$assetName] -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not $m5CollectorChecksums.ContainsKey($assetName) -or
            $m5CollectorChecksums[$assetName] -cne $actualChecksum) {
            throw "M5 collector checksum mismatch: $assetName"
        }
    }

    $m6CollectorAssetPaths = @{
        'collector-manifest.v4.schema.json' = Join-Path $repositoryRoot 'collectors/manifests/collector-manifest.v4.schema.json'
        'deadlocks.system-health.v1.json' = Join-Path $repositoryRoot 'collectors/manifests/deadlocks.system-health.v1.json'
    }
    foreach ($collectorSqlFile in $collectorSqlFiles | Where-Object { $_.Name -in $activeM6CollectorSqlNames }) {
        $m6CollectorAssetPaths[$collectorSqlFile.Name] = $collectorSqlFile.FullName
    }
    $m6CollectorChecksums = @{}
    $m6CollectorChecksumPath = Join-Path $repositoryRoot 'collectors/manifests/m6-deadlocks.assets.sha256'
    foreach ($line in Get-Content -LiteralPath $m6CollectorChecksumPath) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#', [StringComparison]::Ordinal)) { continue }
        if ($line -notmatch '^([0-9a-f]{64})  ([A-Za-z0-9.-]+)$') { throw "Invalid M6 collector checksum entry: $line" }
        $assetName = $Matches[2]
        if (-not $m6CollectorAssetPaths.ContainsKey($assetName) -or $m6CollectorChecksums.ContainsKey($assetName)) { throw "Unexpected or duplicate M6 collector checksum entry: $line" }
        $m6CollectorChecksums[$assetName] = $Matches[1]
    }
    if ($m6CollectorChecksums.Count -ne $m6CollectorAssetPaths.Count) { throw 'M6 collector checksum manifest must contain exactly one entry per pinned asset.' }
    foreach ($assetName in $m6CollectorAssetPaths.Keys) {
        $actualChecksum = (Get-FileHash -LiteralPath $m6CollectorAssetPaths[$assetName] -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($m6CollectorChecksums[$assetName] -cne $actualChecksum) { throw "M6 collector checksum mismatch: $assetName" }
    }

    $m7CollectorAssetPaths = @{
        'collector-manifest.v4.schema.json' = Join-Path $repositoryRoot 'collectors/manifests/collector-manifest.v4.schema.json'
        'queries.performance.v1.json' = Join-Path $repositoryRoot 'collectors/manifests/queries.performance.v1.json'
    }
    foreach ($collectorSqlFile in $collectorSqlFiles | Where-Object { $_.Name -in $activeM7CollectorSqlNames }) { $m7CollectorAssetPaths[$collectorSqlFile.Name] = $collectorSqlFile.FullName }
    $m7CollectorChecksumPath = Join-Path $repositoryRoot 'collectors/manifests/m7-query-performance.assets.sha256'; $m7CollectorChecksums = @{}
    foreach ($line in Get-Content -LiteralPath $m7CollectorChecksumPath) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#', [StringComparison]::Ordinal)) { continue }
        if ($line -notmatch '^([0-9a-f]{64})  ([A-Za-z0-9.-]+)$') { throw "Invalid M7 collector checksum entry: $line" }
        $assetName = $Matches[2]; if (-not $m7CollectorAssetPaths.ContainsKey($assetName) -or $m7CollectorChecksums.ContainsKey($assetName)) { throw "Unexpected or duplicate M7 collector checksum entry: $line" }; $m7CollectorChecksums[$assetName] = $Matches[1]
    }
    if ($m7CollectorChecksums.Count -ne $m7CollectorAssetPaths.Count) { throw 'M7 collector checksum manifest must contain exactly one entry per pinned asset.' }
    foreach ($assetName in $m7CollectorAssetPaths.Keys) { $actualChecksum = (Get-FileHash -LiteralPath $m7CollectorAssetPaths[$assetName] -Algorithm SHA256).Hash.ToLowerInvariant(); if ($m7CollectorChecksums[$assetName] -cne $actualChecksum) { throw "M7 collector checksum mismatch: $assetName" } }
    $m7MilestoneDoc = Join-Path $repositoryRoot 'docs/milestones/M7-query-store-query-performance.md'; if (-not (Test-Path -LiteralPath $m7MilestoneDoc -PathType Leaf)) { throw 'M7 requires its Query Store milestone document.' }

    $m9CollectorAssetPaths = @{
        'collector-manifest.v5.schema.json' = Join-Path $repositoryRoot 'collectors/manifests/collector-manifest.v5.schema.json'
        'backups.status.v1.json' = Join-Path $repositoryRoot 'collectors/manifests/backups.status.v1.json'
        'sql-agent.failures.v1.json' = Join-Path $repositoryRoot 'collectors/manifests/sql-agent.failures.v1.json'
        'tempdb.health.v1.json' = Join-Path $repositoryRoot 'collectors/manifests/tempdb.health.v1.json'
        'availability-groups.health.v1.json' = Join-Path $repositoryRoot 'collectors/manifests/availability-groups.health.v1.json'
    }
    foreach ($collectorSqlFile in $collectorSqlFiles | Where-Object { $_.Name -in $expectedM9CollectorSqlNames -and $_.Name -notlike 'capability.connection.*.v2.sql' }) { $m9CollectorAssetPaths[$collectorSqlFile.Name] = $collectorSqlFile.FullName }
    $m9CollectorChecksums = @{}
    $m9CollectorChecksumPath = Join-Path $repositoryRoot 'collectors/manifests/m9-operational-health.assets.sha256'
    foreach ($line in Get-Content -LiteralPath $m9CollectorChecksumPath) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#', [StringComparison]::Ordinal)) { continue }
        if ($line -notmatch '^([0-9a-f]{64})  ([A-Za-z0-9.-]+)$') { throw "Invalid M9 collector checksum entry: $line" }
        $assetName = $Matches[2]
        if (-not $m9CollectorAssetPaths.ContainsKey($assetName) -or $m9CollectorChecksums.ContainsKey($assetName)) { throw "Unexpected or duplicate M9 collector checksum entry: $line" }
        $m9CollectorChecksums[$assetName] = $Matches[1]
    }
    if ($m9CollectorChecksums.Count -ne $m9CollectorAssetPaths.Count) { throw 'M9 collector checksum manifest must contain exactly one entry per pinned asset.' }
    foreach ($assetName in $m9CollectorAssetPaths.Keys) {
        $actualChecksum = (Get-FileHash -LiteralPath $m9CollectorAssetPaths[$assetName] -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($m9CollectorChecksums[$assetName] -cne $actualChecksum) { throw "M9 collector checksum mismatch: $assetName" }
    }
    $m9CapabilityAssetPaths = @{
        'capability.connection.v2.schema.json' = Join-Path $repositoryRoot 'collectors/manifests/capability.connection.v2.schema.json'
        'capability.connection.v2.json' = Join-Path $repositoryRoot 'collectors/manifests/capability.connection.v2.json'
        'capability.connection.sqlserver15-windows.v2.sql' = Join-Path $repositoryRoot 'collectors/sql/capability.connection.sqlserver15-windows.v2.sql'
        'capability.connection.sqlserver16-windows.v2.sql' = Join-Path $repositoryRoot 'collectors/sql/capability.connection.sqlserver16-windows.v2.sql'
        'capability.connection.sqlserver17-windows.v2.sql' = Join-Path $repositoryRoot 'collectors/sql/capability.connection.sqlserver17-windows.v2.sql'
    }
    $m9CapabilityChecksums = @{}
    foreach ($line in Get-Content -LiteralPath (Join-Path $repositoryRoot 'collectors/manifests/capability.connection.assets-v2.sha256')) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#', [StringComparison]::Ordinal)) { continue }
        if ($line -notmatch '^([0-9a-f]{64})  ([A-Za-z0-9.-]+)$') { throw "Invalid capability v2 checksum entry: $line" }
        $assetName = $Matches[2]
        if (-not $m9CapabilityAssetPaths.ContainsKey($assetName) -or $m9CapabilityChecksums.ContainsKey($assetName)) { throw "Unexpected or duplicate capability v2 checksum entry: $line" }
        $m9CapabilityChecksums[$assetName] = $Matches[1]
    }
    if ($m9CapabilityChecksums.Count -ne $m9CapabilityAssetPaths.Count) { throw 'Capability v2 checksum manifest must contain exactly one entry per pinned asset.' }
    foreach ($assetName in $m9CapabilityAssetPaths.Keys) {
        $actualChecksum = (Get-FileHash -LiteralPath $m9CapabilityAssetPaths[$assetName] -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($m9CapabilityChecksums[$assetName] -cne $actualChecksum) { throw "Capability v2 checksum mismatch: $assetName" }
    }

    $capabilityV2Schema = Get-Content -LiteralPath $m9CapabilityAssetPaths['capability.connection.v2.schema.json'] -Raw | ConvertFrom-Json
    $capabilityV2Manifest = Get-Content -LiteralPath $m9CapabilityAssetPaths['capability.connection.v2.json'] -Raw | ConvertFrom-Json
    Assert-JsonSchemaValue $capabilityV2Manifest $capabilityV2Schema '$manifest'
    $capabilityRuntimeSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/SqlObserver.Infrastructure.SqlServer/SqlServerCapabilityDiscoveryPort.cs') -Raw
    $capabilityPermissionIds = @('15','16','17') | ForEach-Object { @($capabilityV2Manifest.requiredPermissionsByMajor.$_) } | Select-Object -Unique
    foreach ($permissionId in $capabilityPermissionIds) {
        if ($capabilityRuntimeSource -notmatch [regex]::Escape([string]$permissionId)) {
            throw "Capability v2 permission is not represented by runtime discovery: $permissionId"
        }
    }
    foreach ($capabilitySqlPath in @($m9CapabilityAssetPaths['capability.connection.sqlserver15-windows.v2.sql'], $m9CapabilityAssetPaths['capability.connection.sqlserver16-windows.v2.sql'], $m9CapabilityAssetPaths['capability.connection.sqlserver17-windows.v2.sql'])) {
        $capabilitySql = Get-Content -LiteralPath $capabilitySqlPath -Raw
        if ($capabilitySql -notmatch 'IS_SRVROLEMEMBER\(N''##MS_ServerPerformanceStateReader##''\)' -or
            $capabilitySql -notmatch 'SERVERPROPERTY\(N''EngineEdition''\)' -or
            $capabilitySql -match 'OBJECT_ID\(N''msdb\.dbo\.sysjobhistory''\)') {
            throw "Capability v2 SQL does not use stable feature/role evidence: $capabilitySqlPath"
        }
    }

    $m9SchemaPath = Join-Path $repositoryRoot 'collectors/manifests/collector-manifest.v5.schema.json'
    $m9Schema = Get-Content -LiteralPath $m9SchemaPath -Raw | ConvertFrom-Json
    $m9SchemaRootNames = @($m9Schema.properties.psobject.Properties.Name) | Sort-Object
    foreach ($manifestId in @('backups.status','sql-agent.failures','tempdb.health','availability-groups.health')) {
        $manifestPath = Join-Path $repositoryRoot "collectors/manifests/$manifestId.v1.json"
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ((@($manifest.psobject.Properties.Name) | Sort-Object) -join '|' -cne ($m9SchemaRootNames -join '|') -or
            $manifest.'$schema' -cne 'collector-manifest.v5.schema.json' -or $manifest.schemaVersion -ne 5 -or
            $manifest.collectorId -cne $manifestId -or $manifest.collectorVersion -ne 1 -or $manifest.operationalMode -cne 'passive' -or
            (@($manifest.dependsOn) -join '|') -cne 'capability.connection|engine.core' -or
            $manifest.supportedTargets.product -cne 'Microsoft SQL Server' -or $manifest.supportedTargets.minimumMajorVersion -ne 15 -or
            $manifest.supportedTargets.maximumMajorVersion -ne 17 -or (@($manifest.supportedTargets.platforms) -join '|') -cne 'Windows' -or
            (@($manifest.requiredPermissionsByMajor.psobject.Properties.Name) | Sort-Object) -join '|' -cne '15|16|17' -or
            (@($manifest.queryResources.supportedByMajor.psobject.Properties.Name) | Sort-Object) -join '|' -cne '15|16|17' -or
            $manifest.fallback.mode -cne 'unsupported' -or $manifest.outputSchemaVersion -ne 1) {
            throw "M9 manifest does not conform to the strict v5 schema: $manifestId"
        }
        foreach ($major in @('15','16','17')) {
            $maximumPermissions = if ($manifestId -ceq 'backups.status') { 3 } else { 2 }
            if (@($manifest.requiredPermissionsByMajor.$major).Count -lt 1 -or @($manifest.requiredPermissionsByMajor.$major).Count -gt $maximumPermissions) { throw "M9 manifest permission bounds are invalid: $manifestId/$major" }
            if ($manifest.queryResources.supportedByMajor.$major -notmatch "^$([regex]::Escape($manifestId)).sqlserver$major-windows.v1.sql$") { throw "M9 manifest query resource is invalid: $manifestId/$major" }
        }
        if ($manifest.cadence.defaultIntervalSeconds -lt 30 -or $manifest.cadence.defaultIntervalSeconds -gt 300 -or
            $manifest.cadence.minimumIntervalSeconds -lt 10 -or $manifest.cadence.minimumIntervalSeconds -gt 60 -or
            -not $manifest.cadence.nonOverlappingPerTarget -or $manifest.executionBounds.connectTimeoutSeconds -ne 5 -or
            $manifest.executionBounds.commandTimeoutSeconds -notin @(5,10) -or $manifest.executionBounds.maximumRows -lt 1 -or
            $manifest.executionBounds.maximumRows -gt 2048 -or $manifest.executionBounds.maximumResponseBytes -lt 262144 -or
            $manifest.executionBounds.maximumResponseBytes -gt 2097152 -or $manifest.estimatedCostClass -notin @('low','moderate') -or
            $manifest.outputKind -notin @('backups_status','sql_agent_failures','tempdb_health','availability_groups_health')) {
            throw "M9 manifest bounds or enum is invalid: $manifestId"
        }
    }
    $m9BundleDigest = (Get-FileHash -LiteralPath $m9CollectorChecksumPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $runtimeRepositorySource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlCollectorRuntimeRepositoryPort.cs') -Raw
    $m9MigrationSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'database/migrations/0114_backup_permission_probe_binding.sql') -Raw
    if (([regex]::Matches($runtimeRepositorySource, [regex]::Escape('"' + $m9BundleDigest + '"'))).Count -ne 4 -or
        ([regex]::Matches($m9MigrationSource, [regex]::Escape("'$m9BundleDigest'"))).Count -ne 1) {
        throw "M9 bundle digest does not match the checksum manifest: $m9BundleDigest"
    }

    # M10 source-neutral host and passive replication bundles are independently
    # pinned.  Keep this gate separate from the immutable M3/M4/M9 manifests.
    foreach ($bundle in @(
        @{ Manifest = 'host.metrics.v1.json'; Schema = 'host.metrics.v1.schema.json'; Checksums = 'm10-host.assets.sha256' },
        @{ Manifest = 'replication.health.v1.json'; Schema = 'replication.health.v1.schema.json'; Checksums = 'm10-replication.assets.sha256' },
        @{ Manifest = 'capability.connection.v3.json'; Schema = 'capability.connection.v3.schema.json'; Checksums = 'capability.connection.assets-v3.sha256' }
    )) {
        $checksumFile = Join-Path $repositoryRoot "collectors/manifests/$($bundle.Checksums)"
        $checksumLines = @(Get-Content -LiteralPath $checksumFile | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        foreach ($line in $checksumLines) {
            if ($line -notmatch '^([0-9a-f]{64})  ([A-Za-z0-9.-]+)$') { throw "Invalid M10 asset checksum entry: $line" }
            $asset = Join-Path $repositoryRoot "collectors/manifests/$($Matches[2])"
            if (-not (Test-Path -LiteralPath $asset -PathType Leaf)) { $asset = Join-Path $repositoryRoot "collectors/sql/$($Matches[2])" }
            if (-not (Test-Path -LiteralPath $asset -PathType Leaf) -or (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Matches[1]) { throw "M10 asset checksum mismatch: $($Matches[2])" }
        }
        $manifest = Get-Content -LiteralPath (Join-Path $repositoryRoot "collectors/manifests/$($bundle.Manifest)") -Raw | ConvertFrom-Json
        if ($manifest.collectorId -notin @('host.metrics','replication.health','capability.connection') -or $manifest.outputSchemaVersion -ne 1 -and $manifest.outputSchemaVersion -ne 3) { throw "M10 manifest identity/version is invalid: $($bundle.Manifest)" }
        if ($manifest.collectorId -eq 'replication.health' -and
            (@($manifest.requiredCapabilities) -contains 'feature.host-binding' -or
             @($manifest.requiredCapabilities) -notcontains 'feature.replication')) {
            throw 'Replication manifest must require feature.replication only; host binding is a separate capability.'
        }
    }
    $m10MigrationStatic = Join-Path $repositoryRoot 'database/tests/m10_migration_static.ps1'
    if (-not (Test-Path -LiteralPath $m10MigrationStatic -PathType Leaf)) { throw 'M10 migration static gate is missing.' }
    & $m10MigrationStatic -RepositoryRoot $repositoryRoot
    if (-not $?) { throw 'M10 migration static gate failed.' }
    $m10MigrationSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'database/migrations/0014_analytics_host_replication_retention.sql') -Raw
    if ($m10MigrationSource -match '(?is)CREATE OR REPLACE FUNCTION telemetry\.commit_m10_host_metrics.*?gen_random_uuid') {
        throw 'M10 host commit must persist the supplied stable host identity; random host IDs are forbidden.'
    }
    # Normalize SQL whitespace before checking the exact seed rows.  Regexes
    # over the raw source are formatting-sensitive (and `.` does not cross
    # newlines in PowerShell), which previously let a wrapped/misaligned seed
    # evade this contract gate.
    $m10Normalized = [regex]::Replace($m10MigrationSource.ToLowerInvariant(), '\s+', ' ')
    $hostSeed = [regex]::Match($m10Normalized, "(?is)\('host\.metrics'\s*,\s*1\s*,\s*14\s*,\s*5\s*,\s*1\s*,.*?interval\s*'1 minute'\s*,\s*interval\s*'30 seconds'\s*,\s*interval\s*'5 seconds'\s*,\s*256\s*,\s*262144\s*,")
    $replicationSeed = [regex]::Match($m10Normalized, "(?is)\('replication\.health'\s*,\s*1\s*,\s*15\s*,\s*5\s*,\s*1\s*,.*?interval\s*'1 minute'\s*,\s*interval\s*'30 seconds'\s*,\s*interval\s*'10 seconds'\s*,\s*2048\s*,\s*2097152\s*,")
    if (-not $hostSeed.Success -or -not $replicationSeed.Success) {
        throw 'M10 host/replication database bounds must be exactly 256/256KiB/5s and 2048/2MiB/10s.'
    }
    if ($m10MigrationSource -match 'USING \(true\) WITH CHECK \(true\)' -and
        $m10MigrationSource -match 'm10_host_profile_scope ON control\.host_profile USING \(true\)') {
        throw 'M10 host profile RLS must be target-scoped; USING(true) is forbidden.'
    }
    foreach ($requiredM10Asset in @(
        'web/src/features/analytics/analyticsApi.ts',
        'web/src/features/analytics/analyticsParser.ts',
        'web/src/features/analytics/analyticsState.ts',
        'web/src/features/analytics/AnalyticsPanel.tsx',
        'tests/SqlObserver.UnitTests/M10AnalyticsContractTests.cs',
        'tests/SqlObserver.UnitTests/M10HostObservationTests.cs',
        'tests/SqlObserver.UnitTests/M10ReplicationContractTests.cs',
        'tests/SqlObserver.ApiContractTests/M10AnalyticsApiContractTests.cs',
        'tests/SqlObserver.SecurityTests/M10AnalyticsSecurityTests.cs',
        'tests/SqlObserver.PerformanceTests/M10AnalyticsPerformanceTests.cs')) {
        if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $requiredM10Asset) -PathType Leaf)) { throw "M10 contract asset is missing: $requiredM10Asset" }
    }

    $m6MilestoneDoc = Join-Path $repositoryRoot 'docs/milestones/M6-deadlocks-and-extended-events.md'
    if (-not (Test-Path -LiteralPath $m6MilestoneDoc -PathType Leaf)) { throw 'M6 requires its passive system_health milestone document.' }
    foreach ($forbiddenXeMutation in @('CREATE EVENT SESSION', 'ALTER EVENT SESSION', 'START EVENT SESSION', 'STOP EVENT SESSION', 'blocked process threshold')) {
        if ((Get-Content -LiteralPath $m6MilestoneDoc -Raw).IndexOf($forbiddenXeMutation, [StringComparison]::OrdinalIgnoreCase) -ge 0 -and $forbiddenXeMutation -ne 'blocked process threshold') { throw "M6 documentation must not ship an XE mutation command: $forbiddenXeMutation" }
    }

    $m5MilestoneDoc = Join-Path $repositoryRoot 'docs/milestones/M5-sessions-requests-waits-blocking.md'
    if (-not (Test-Path -LiteralPath $m5MilestoneDoc -PathType Leaf) -or
        (Test-Path -LiteralPath (Join-Path $repositoryRoot 'docs/milestones/M5-activity-migration.sql.wip') -PathType Leaf)) {
        throw 'M5 requires the immutable milestone document and must retire the WIP migration file.'
    }
    foreach ($requiredM5WebAsset in @(
        'web/src/features/activity/activityApi.ts',
        'web/src/features/activity/activityParser.mjs',
        'web/src/features/activity/activityTypes.ts',
        'web/src/features/activity/TargetActivityPanel.tsx',
        'web/tests/activity-contract.test.mjs',
        'web/tests/activity-parser-runtime.test.mjs')) {
        if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $requiredM5WebAsset) -PathType Leaf)) {
            throw "M5 web contract asset is missing: $requiredM5WebAsset"
        }
    }
    $parserRuntime = Get-Content -LiteralPath (Join-Path $repositoryRoot 'web/tests/activity-parser-runtime.test.mjs') -Raw
    if ($parserRuntime -notmatch 'activityParser\.mjs' -or $parserRuntime -match 'const\s+(?:text|counter|timestamp|object)\s*=') {
        throw 'M5 parser runtime tests must import the production parser without duplicated validation helpers.'
    }
    foreach ($requiredM5TestAsset in @(
        'tests/SqlObserver.ApiContractTests/M5ActivityApiContractTests.cs',
        'tests/SqlObserver.ApiContractTests/M5ActivityHttpContractTests.cs',
        'tests/SqlObserver.SecurityTests/M5ActivitySecurityPolicyTests.cs',
        'tests/SqlObserver.IntegrationTests.SqlServer/SqlServerActivityIntegrationTests.cs',
        'tests/SqlObserver.IntegrationTests.PostgreSql/M5ActivityPostgreSqlIntegrationTests.cs',
        'tests/SqlObserver.PerformanceTests/M5ActivityBoundaryPerformanceTests.cs')) {
        if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $requiredM5TestAsset) -PathType Leaf)) {
            throw "M5 focused test asset is missing: $requiredM5TestAsset"
        }
    }
    foreach ($requiredM6TestAsset in @(
        'tests/SqlObserver.UnitTests/M6DeadlockContractTests.cs',
        'tests/SqlObserver.ApiContractTests/M6DeadlockApiContractTests.cs',
        'tests/SqlObserver.SecurityTests/M6DeadlockSecurityPolicyTests.cs',
        'tests/SqlObserver.PerformanceTests/M6DeadlockBoundaryPerformanceTests.cs',
        'tests/SqlObserver.EndToEndTests/CollectorCompositionEndToEndTests.cs',
        'tests/SqlObserver.IntegrationTests.SqlServer/M6DeadlockIntegrationTests.cs',
        'tests/SqlObserver.IntegrationTests.PostgreSql/M6DeadlockPostgreSqlIntegrationTests.cs',
        'web/src/features/deadlocks/deadlockApi.ts',
        'web/src/features/deadlocks/TargetDeadlockPanel.tsx',
        'web/src/features/deadlocks/deadlockParser.mjs',
        'web/tests/deadlock-parser.test.mjs')) {
        if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $requiredM6TestAsset) -PathType Leaf)) {
            throw "M6 focused test asset is missing: $requiredM6TestAsset"
        }
    }
    foreach ($requiredM7TestAsset in @(
        'tests/SqlObserver.UnitTests/M7QueryPerformanceContractTests.cs',
        'tests/SqlObserver.ApiContractTests/M7QueryPerformanceApiContractTests.cs',
        'tests/SqlObserver.SecurityTests/M7QueryPerformanceSecurityPolicyTests.cs',
        'tests/SqlObserver.PerformanceTests/M7QueryPerformanceBoundaryPerformanceTests.cs',
        'tests/SqlObserver.IntegrationTests.SqlServer/M7QueryPerformanceIntegrationTests.cs',
        'tests/SqlObserver.IntegrationTests.PostgreSql/M7QueryPerformancePostgreSqlIntegrationTests.cs',
        'tests/SqlObserver.EndToEndTests/M7QueryPerformanceCompositionEndToEndTests.cs',
        'web/tests/query-performance-contract.test.mjs',
        'web/src/features/queries/TargetQueryPerformancePanel.tsx')) {
        if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $requiredM7TestAsset) -PathType Leaf)) { throw "M7 focused test asset is missing: $requiredM7TestAsset" }
    }
    $readmeStatus = Get-Content -LiteralPath (Join-Path $repositoryRoot 'README.md') -Raw
    $databaseTestReadme = Get-Content -LiteralPath (Join-Path $repositoryRoot 'database/tests/README.md') -Raw
    if ($readmeStatus -notmatch 'Milestones 0 through 11 are implemented' -or
        $readmeStatus -notmatch '(?i)M5 activity' -or
        $readmeStatus -notmatch '(?i)M6.*(?:system_health|deadlock)' -or
         $readmeStatus -notmatch '(?i)M7.*(?:Query Store|query-performance)' -or
         $readmeStatus -notmatch '(?i)M9.*operational-health' -or
         $readmeStatus -notmatch '(?i)M11.*(?:MCP|read-only)' -or
         $readmeStatus -match '(?i)quick start[^\r\n]*(?:through|only).*M4' -or
         $readmeStatus -notmatch 'PowerShell Core 7\.5\+' -or
         $databaseTestReadme -notmatch 'PowerShell Core 7\.5\+') {
        throw 'README repository status must explicitly identify M0-M11 and the bounded diagnostic, analytics, and MCP scope.'
    }
    $registeredSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/SqlObserver.Collector/CollectorServiceRegistration.cs') -Raw
    $registeredOrderPatterns = @(
        'new CollectorRegistration\(\s*executionOrder:\s*1,.*?SqlServerCoreEngineCollector',
        'new CollectorRegistration\(\s*executionOrder:\s*2,.*?SqlServerDatabaseInventoryCollector',
        'new CollectorRegistration\(\s*executionOrder:\s*3,.*?SqlServerDatabaseFilesCollector',
        'new CollectorRegistration\(\s*executionOrder:\s*4,.*?SqlServerActivitySessionsCollector',
        'new CollectorRegistration\(\s*executionOrder:\s*5,.*?SqlServerActivityRequestsCollector',
        'new CollectorRegistration\(\s*executionOrder:\s*6,.*?SqlServerServerWaitsCollector',
        'new CollectorRegistration\(\s*executionOrder:\s*7,.*?SqlServerCurrentBlockingCollector',
        'new CollectorRegistration\(\s*executionOrder:\s*8,.*?SqlServerDeadlockCollector',
        'new CollectorRegistration\(\s*executionOrder:\s*9,.*?SqlServerQueryPerformanceCollector',
        'new CollectorRegistration\(\s*10,.*?SqlServerBackupsStatusCollector',
        'new CollectorRegistration\(\s*11,.*?SqlServerSqlAgentFailuresCollector',
        'new CollectorRegistration\(\s*12,.*?SqlServerTempDbHealthCollector',
        'new CollectorRegistration\(\s*13,.*?SqlServerAvailabilityGroupsHealthCollector',
        'new CollectorRegistration\(\s*14,.*?HostMetricsCollectorAdapter',
        'new CollectorRegistration\(\s*15,.*?SqlServerReplicationCollector'
    )
    $registrationOffset = 0
    foreach ($pattern in $registeredOrderPatterns) {
        $match = [regex]::Match($registeredSource.Substring($registrationOffset), $pattern, [Text.RegularExpressions.RegexOptions]::Singleline)
        if (-not $match.Success) { throw "CollectorServiceRegistration.cs does not preserve the exact ordered 15-collector runtime catalog: $pattern" }
        $registrationOffset += $match.Index + $match.Length
    }
    if ($registeredSource -match 'new CollectorRegistration\([^\r\n]*capability\.connection') { throw 'capability.connection is control-plane discovery and must not be registered as a scheduled collector.' }
    $exactReadmeCollectors = '`engine.core`, `database.inventory`, `database.files`, `activity.sessions`, `activity.requests`, `waits.server`, `blocking.current`, `deadlocks.system-health`, `queries.performance`, `backups.status`, `sql-agent.failures`, `tempdb.health`, `availability-groups.health`, `host.metrics`, and `replication.health`'
    if ($readmeStatus.IndexOf($exactReadmeCollectors, [StringComparison]::Ordinal) -lt 0 -or $readmeStatus.IndexOf('`capability.connection` is control-plane discovery', [StringComparison]::Ordinal) -lt 0) {
        throw 'README must list the exact ordered 1-15 registered collectors and identify capability.connection as control-plane discovery.'
    }
    foreach ($staleText in @('dormant M5', 'M5-activity-migration.sql.wip', 'no M5 migration', 'no M5 activity', 'M6 remains incomplete', 'M6 deadlocks/Extended Events remain outside')) {
        $staleMatches = @(Get-ChildItem -LiteralPath $repositoryRoot -File -Force |
            Where-Object { $_.Name -in @('README.md', 'BACKLOG.md') } |
            Select-String -SimpleMatch $staleText)
        if ($staleMatches.Count -ne 0) {
            throw "M5 documentation contains stale dormant wording: $staleText"
        }
    }

    $sqlServerIntegrationSource = @(
        Get-ChildItem -LiteralPath (
            Join-Path $repositoryRoot 'tests/SqlObserver.IntegrationTests.SqlServer') -Filter '*.cs' -File
    )
    $skippedSqlServerTests = @(
        $sqlServerIntegrationSource |
            Select-String -Pattern '\[(Fact|Theory)\s*\(\s*Skip\s*='
    )
    if ($sqlServerIntegrationSource.Count -eq 0 -or $skippedSqlServerTests.Count -ne 0) {
        throw 'M3 SQL Server integration tests must be active and must not use Skip.'
    }

    foreach ($completedSuite in @(
        'SqlObserver.ApiContractTests',
        'SqlObserver.EndToEndTests',
        'SqlObserver.PerformanceTests',
        'SqlObserver.SecurityTests',
        'SqlObserver.McpContractTests')) {
        $completedSuiteSource = @(
            Get-ChildItem -LiteralPath (Join-Path $repositoryRoot "tests/$completedSuite") -Filter '*.cs' -File
        )
        $completedSuiteSkips = @(
            $completedSuiteSource | Select-String -Pattern '\[(Fact|Theory)\s*\(\s*Skip\s*='
        )
        if ($completedSuiteSource.Count -eq 0 -or $completedSuiteSkips.Count -ne 0) {
            throw "Completed runtime suite must contain active tests and no Skip attributes: $completedSuite"
        }
    }

    $productionCSharp = @(
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -Filter '*.cs' -File |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    )
    # ADR-0020 permits forwarding the supplied PostgreSQL password only in the
    # guarded local bootstrap. This exact assignment is not a SQL Server target
    # credential; all other matches, including elsewhere in that file, still fail.
    $developmentBootstrapPath = Join-Path $repositoryRoot 'src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlDevelopmentBootstrap.cs'
    $forbiddenTargetSecurity = @(
        $productionCSharp | Select-String -Pattern '(?i)TrustServerCertificate\s*=\s*true|User\s*ID\s*=|Password\s*=|SqlException\.Message' |
            Where-Object { $_.Path -ne $developmentBootstrapPath -or $_.Line -cnotmatch '^\s*Password = supplied\.Password,\s*$' }
    )
    if ($forbiddenTargetSecurity.Count -ne 0) {
        throw 'Production source contains a target credential, certificate bypass, or raw SQL provider error surface.'
    }

    $webPackage = Get-Content -LiteralPath (Join-Path $repositoryRoot 'web/package.json') -Raw | ConvertFrom-Json
    $frontendDependencies = @(
        $webPackage.dependencies.PSObject.Properties
        $webPackage.devDependencies.PSObject.Properties
    )
    if ($frontendDependencies.Count -ne 7) {
        throw "Expected seven declared frontend dependencies, found $($frontendDependencies.Count)."
    }

    foreach ($dependency in $frontendDependencies) {
        if ([string] $dependency.Value -notmatch '^\d+\.\d+\.\d+([+-][0-9A-Za-z.-]+)?$') {
            throw "Frontend dependency must use an exact version: $($dependency.Name)"
        }
    }

    $workflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github/workflows/validate.yml') -Raw
    if (-not $workflow.Contains('contents: read') -or
        -not $workflow.Contains('runs-on: windows-2025') -or
        -not $workflow.Contains('persist-credentials: false') -or
        -not $workflow.Contains('DOTNET_INSTALL_DIR: ${{ runner.temp }}/dotnet') -or
        -not $workflow.Contains('global-json-file: global.json') -or
        $workflow -notmatch '(?m)^\s+(?:run:\s+)?\./tools/validate\.ps1(?:\s|$)' -or
        -not $workflow.Contains('Require PowerShell 7.5+')) {
        throw 'CI must use a GitHub-hosted Windows runner, least permissions, non-persistent checkout credentials, the pinned SDK, and the canonical validator.'
    }

    $actionReferences = [regex]::Matches($workflow, '(?m)^\s*(?:-\s*)?uses:\s*([^\r\n]+)')
    if ($actionReferences.Count -eq 0) { throw 'CI must declare its reviewed GitHub Actions.' }
    foreach ($reference in $actionReferences) {
        $action = ($reference.Groups[1].Value -split '\s+#', 2)[0].Trim()
        if ($action -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*@[0-9a-f]{40}$') {
            throw 'Every third-party GitHub Action must be pinned to an immutable commit SHA.'
        }
    }

    $scriptFiles = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tools') -Filter '*.ps1' -File)
    foreach ($scriptFile in $scriptFiles) {
        $tokens = $null
        $parseErrors = $null
        [void] [System.Management.Automation.Language.Parser]::ParseFile(
            $scriptFile.FullName,
            [ref] $tokens,
            [ref] $parseErrors)
        if (@($parseErrors).Count -ne 0) {
            throw "PowerShell parser error in $($scriptFile.Name): $($parseErrors[0].Message)"
        }
    }

    $markdownFiles = @(
        Get-ChildItem -LiteralPath $repositoryRoot -Filter '*.md' -File
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'docs') -Recurse -Filter '*.md' -File
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'collectors') -Filter '*.md' -File
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'database') -Filter '*.md' -File
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'installer') -Filter '*.md' -File
    )
    foreach ($markdownFile in $markdownFiles) {
        $markdown = Get-Content -LiteralPath $markdownFile.FullName -Raw
        foreach ($match in [regex]::Matches($markdown, '\[[^\]]+\]\(([^)]+)\)')) {
            $target = $match.Groups[1].Value.Trim().Trim('<', '>')
            if ($target -match '^(https?://|mailto:|#)') {
                continue
            }

            $target = ($target -split '#', 2)[0]
            if ([string]::IsNullOrWhiteSpace($target)) {
                continue
            }

            $resolvedTarget = [IO.Path]::GetFullPath(
                (Join-Path $markdownFile.DirectoryName ([Uri]::UnescapeDataString($target))))
            if (-not (Test-Path -LiteralPath $resolvedTarget)) {
                throw "Broken local Markdown link in $($markdownFile.Name): $target"
            }
        }
    }

    # M12 evidence is a versioned contract.  Keep this static gate cheap so
    # malformed or silently replaced certification definitions fail before
    # any build or test work starts.
    foreach ($certificationAsset in @(
        'release/certification/m12-certification-matrix.v1.json',
        'release/certification/m12-certification-matrix.v1.schema.json',
        'release/certification/m12-certification-manifest.v1.schema.json',
        'tools/verify-test-results.ps1')) {
        if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $certificationAsset) -PathType Leaf)) {
            throw "M12 certification asset is missing: $certificationAsset"
        }
    }
    $certificationMatrix = Get-Content -LiteralPath (Join-Path $repositoryRoot 'release/certification/m12-certification-matrix.v1.json') -Raw | ConvertFrom-Json -DateKind String
    if ($certificationMatrix.'$schema' -cne 'm12-certification-matrix.v1.schema.json' -or
        $certificationMatrix.schemaVersion -ne 1 -or $certificationMatrix.matrixId -cne 'sqlobserver-m12' -or
        (@($certificationMatrix.profiles.PSObject.Properties.Name | Sort-Object) -join '|') -cne 'Local|Release') {
        throw 'M12 certification matrix is not the expected versioned Local/Release contract.'
    }

    # M12 lifecycle foundation contracts are closed, checksum-pinned, and
    # explicitly assessment-only. Do not let a missing or replaced contract
    # turn into an accidental installer or migration mutation surface.
    $lifecycleContractRoot = Join-Path $repositoryRoot 'installer/contracts'
    foreach ($contractName in @('lifecycle-assessment.v1.schema.json', 'migration-assessment.v1.schema.json', 'deployment-security-assessment.v1.schema.json', 'checksums.sha256')) {
        if (-not (Test-Path -LiteralPath (Join-Path $lifecycleContractRoot $contractName) -PathType Leaf)) {
            throw "M12 lifecycle contract is missing: $contractName"
        }
    }
    foreach ($schemaName in @('lifecycle-assessment.v1.schema.json', 'migration-assessment.v1.schema.json', 'deployment-security-assessment.v1.schema.json')) {
        $schemaPath = Join-Path $lifecycleContractRoot $schemaName
        $schema = Get-Content -LiteralPath $schemaPath -Raw | ConvertFrom-Json
        if ($schema.additionalProperties -ne $false -or $schema.type -cne 'object') {
            throw "M12 lifecycle schema must be a closed object: $schemaName"
        }
        $hash = (Get-FileHash -LiteralPath $schemaPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $checksumLines = Get-Content -LiteralPath (Join-Path $lifecycleContractRoot 'checksums.sha256')
        if (-not ($checksumLines -contains "$hash  $schemaName")) {
            throw "M12 lifecycle schema checksum mismatch: $schemaName"
        }
    }

    # The web identity foundation is deliberately local and decision-neutral.
    # Keep its schema pinned and its generator/verifier present without turning
    # this gate into a browser, hosting, cache, or SignalR qualification claim.
    $webContractRoot = Join-Path $repositoryRoot 'web/contracts'
    foreach ($webContractName in @('web-asset-manifest.v1.schema.json', 'checksums.sha256', 'README.md')) {
        if (-not (Test-Path -LiteralPath (Join-Path $webContractRoot $webContractName) -PathType Leaf)) {
            throw "M12 web identity contract is missing: $webContractName"
        }
    }
    $webSchemaPath = Join-Path $webContractRoot 'web-asset-manifest.v1.schema.json'
    $webSchema = Get-Content -LiteralPath $webSchemaPath -Raw | ConvertFrom-Json
    if ($webSchema.additionalProperties -ne $false -or $webSchema.type -cne 'object') {
        throw 'M12 web identity schema must be a closed object contract.'
    }
    $webSchemaHash = (Get-FileHash -LiteralPath $webSchemaPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $webChecksumLines = Get-Content -LiteralPath (Join-Path $webContractRoot 'checksums.sha256')
    if (-not ($webChecksumLines -contains "$webSchemaHash  web-asset-manifest.v1.schema.json")) {
        throw 'M12 web identity schema checksum mismatch.'
    }
    $webPackageScripts = $webPackage.scripts
    foreach ($webScriptName in @('assets:generate', 'assets:verify', 'build')) {
        if ($webPackageScripts.PSObject.Properties.Name -notcontains $webScriptName) {
            throw "Frontend package is missing required web identity script: $webScriptName"
        }
    }
    $webGeneratorPath = Join-Path $repositoryRoot 'web/tools/web-asset-manifest.mjs'
    if (-not (Test-Path -LiteralPath $webGeneratorPath -PathType Leaf)) {
        throw 'M12 web identity generator/verifier is missing.'
    }
    $webGeneratorText = Get-Content -LiteralPath $webGeneratorPath -Raw
    foreach ($webIdentityMarker in @('manifest.json', 'sha256', 'index.html', 'symlink', 'case-insensitive', 'unknown Vite manifest field')) {
        if (-not $webGeneratorText.Contains($webIdentityMarker)) {
            throw "Web identity verifier is missing required fail-closed check: $webIdentityMarker"
        }
    }
    $testSource = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests') -Recurse -Filter '*.cs' -File)
    # Every test project has an explicit lane.  Environment-backed classes
    # carry a stable trait and are selected by an explicit local filter; all
    # untagged tests remain part of the local validation profile.
    $testLaneMap = [ordered]@{
        'SqlObserver.UnitTests' = 'local'
        'SqlObserver.IntegrationTests.PostgreSql' = 'external-postgresql'
        'SqlObserver.IntegrationTests.SqlServer' = 'local-sqlserver-lab'
        'SqlObserver.ApiContractTests' = 'local'
        'SqlObserver.McpContractTests' = 'local'
        'SqlObserver.SecurityTests' = 'local'
        'SqlObserver.PerformanceTests' = 'local'
        'SqlObserver.EndToEndTests' = 'local-with-external-trait'
        'SqlObserver.ReleaseTests' = 'local-contract'
    }
    $discoveredTestProjectNames = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests') -Recurse -Filter '*.csproj' -File | ForEach-Object { $_.BaseName })
    if ((@($testLaneMap.Keys | Sort-Object) -join '|') -cne (@($discoveredTestProjectNames | Sort-Object) -join '|')) {
        throw 'Every test project must be assigned to exactly one Local or external validation lane.'
    }
    foreach ($traitFile in @(
        'M3RepositoryIntegrationTests.cs', 'M4CollectorPersistenceIntegrationTests.cs',
        'M5ActivityPostgreSqlIntegrationTests.cs', 'M6DeadlockPostgreSqlIntegrationTests.cs',
        'M7QueryPerformancePostgreSqlIntegrationTests.cs',
        'M8AlertingPostgreSqlIntegrationTests.cs', 'M9OperationalHealthPostgreSqlIntegrationTests.cs',
        'M10AnalyticsPostgreSqlIntegrationTests.cs',
        'M11McpAuditPostgreSqlIntegrationTests.cs', 'M11McpQueryProjectionIntegrationTests.cs',
        'LeaseResilienceIntegrationTests.cs', 'MigrationIntegrationTests.cs',
        'RepositoryReplaySafetyIntegrationTests.cs', 'RepositoryRuntimeIntegrationTests.cs',
        'RepositorySecurityAndFailureIntegrationTests.cs')) {
        $traitPath = Join-Path $repositoryRoot "tests/SqlObserver.IntegrationTests.PostgreSql/$traitFile"
        $traitText = Get-Content -LiteralPath $traitPath -Raw
        if ($traitText -notmatch '\[Trait\("Category",\s*"RequiresPostgreSql"\)\]') { throw "PostgreSQL environment class is missing its explicit RequiresPostgreSql trait: $traitFile" }
    }
    $m9ExternalText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tests/SqlObserver.EndToEndTests/M9OperationalHealthEndToEndTests.cs') -Raw
    if ($m9ExternalText -notmatch '\[Fact\]\s*\r?\n\s*\[Trait\("Category",\s*"RequiresPostgreSql"\)\]' -or $m9ExternalText -match 'PostgreSqlFact|\bSkip\s*=') {
        throw 'M9 PostgreSQL E2E must be an ordinary active Fact with the exact RequiresPostgreSql trait and no runtime Skip.'
    }
    $m9ResolverStart = $m9ExternalText.IndexOf("`n    private static string? ResolvePostgreSqlContract()", [StringComparison]::Ordinal)
    $m9ResolverEnd = if ($m9ResolverStart -ge 0) { $m9ExternalText.IndexOf('[Fact]', $m9ResolverStart, [StringComparison]::Ordinal) } else { -1 }
    $m9ResolverText = if ($m9ResolverStart -ge 0 -and $m9ResolverEnd -gt $m9ResolverStart) { $m9ExternalText.Substring($m9ResolverStart, $m9ResolverEnd - $m9ResolverStart) } else { '' }
    if ($m9ResolverText -match 'SQLOBSERVER_E2E_POSTGRES' -or
        $m9ResolverText -match 'releaseProfile\s*\?\s*release\s*:\s*local\s*\?\?\s*release' -or
        $m9ResolverText -notmatch 'NpgsqlConnectionStringBuilder' -or
        $m9ResolverText -notmatch 'must specify Host and Database') {
        throw 'M9 PostgreSQL resolver must never let the Release connection bleed into Local.'
    }
    $dynamicSkipMatches = @($testSource | Select-String -Pattern '(?i)\bSkip\s*=')
    if ($dynamicSkipMatches.Count -ne 0) {
        throw 'Dynamic test Skip mechanisms are forbidden; use an explicit trait and fail-fast precondition.'
    }
    $m10DockerScript = Get-Content -LiteralPath (Join-Path $repositoryRoot 'database/tests/m10_postgres_docker.ps1') -Raw
    if ($m10DockerScript -match 'UNAVAILABLE[^\r\n]*exit\s+0' -or $m10DockerScript -notmatch '(?i)ON_ERROR_STOP\s*=\s*1') {
        throw 'Docker/PostgreSQL probe cannot report unavailable as a successful certification result.'
    }
    foreach ($sqlServerSource in @(
        'tests/SqlObserver.IntegrationTests.SqlServer/SqlServerCapabilityIntegrationTests.cs',
        'tests/SqlObserver.IntegrationTests.SqlServer/SqlServerCoreHealthIntegrationTests.cs',
        'tests/SqlObserver.IntegrationTests.SqlServer/SqlServerActivityIntegrationTests.cs')) {
        $sqlServerText = Get-Content -LiteralPath (Join-Path $repositoryRoot $sqlServerSource) -Raw
        if ($sqlServerText -match 'DESKTOP-IORRV3E|DataSource\s*=\s*"[^$]') { throw "SQL Server lab endpoint must come from the local/release environment contract: $sqlServerSource" }
        if ($sqlServerText -notmatch '\[Trait\("Category",\s*"RequiresSqlServer"\)\]') { throw "SQL Server live test is missing its explicit RequiresSqlServer trait: $sqlServerSource" }
    }
    $m12SqlServerText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tests/SqlObserver.IntegrationTests.SqlServer/M12SqlServerPassiveCertificationTests.cs') -Raw
    if ($m12SqlServerText -notmatch '\[Trait\("Category",\s*"RequiresM12SqlServerRelease"\)\]' -or
        $m12SqlServerText -notmatch 'LiveReleaseSqlServerPassiveCertificationIsNonMutating') {
        throw 'M12 SQL Server passive certification must be an explicit release-only trait with fail-fast live test.'
    }
    foreach ($m12Marker in @('SqlServerCapabilityDiscoveryPort', 'DiscoverAsync', 'SqlServerCollectorAssetCatalog.LoadEmbedded', 'SqlServerActivityCollectorAssetCatalog.LoadEmbedded', 'SqlServerDeadlockCollectorAssetCatalog.LoadEmbedded', 'SqlServerQueryPerformanceCollectorAssetCatalog.LoadEmbedded', 'SqlServerOperationalHealthAssetCatalog.LoadEmbedded', 'SqlServerReplicationAssetCatalog.LoadEmbedded', 'BundleChecksum', 'CollectorOperationalMode.Passive', 'CollectorOutputValidator', 'collectorEvidence', 'ChangeDatabase', 'maxTotalRows', 'memory_partition_mode', 'msdb.dbo.sysschedules', 'unsupportedTargetCount')) { if (-not $m12SqlServerText.Contains($m12Marker, [StringComparison]::Ordinal)) { throw "M12 SQL Server producer/test is missing critical invariant: $m12Marker" } }
    foreach ($m12ProducerMarker in @('Read-M12LockedBytes', 'Assert-M12JsonTypesBytes', 'Open-M12ExecutableHandle', 'executableHandle.Stream.Dispose()', 'ProcessIds', 'Assert-M12NoDescendants', 'Assert-M12ObservedPidsExited', '$discoveredPids', '$stableEmptySweeps', '[Collections.Generic.Queue[int]]::new()', 'WaitForZero', 'ErrorAction Stop', 'function Remove-M12SafeDescendants', 'expectedMachineNames', 'Directory]::Move($verify,$final)')) { $m12ProducerText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tools/run-m12-sqlserver-certification.ps1') -Raw; if (-not $m12ProducerText.Contains($m12ProducerMarker, [StringComparison]::Ordinal)) { throw "M12 SQL Server producer is missing publication/process hardening: $m12ProducerMarker" } }
    foreach ($m12AssetMarker in @('SET NOCOUNT ON;\n', 'TOP (@maximum_rows)', 'TOP (@probe_rows)', 'TOP (@scan_rows)', 'RECONFIGURE', 'PHYSICAL_NAME', 'OPENROWSET', 'OPENDATASOURCE', 'OPENQUERY', 'KILL', 'Four-part')) { $m12LiveText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tests/SqlObserver.IntegrationTests.SqlServer/M12SqlServerPassiveCertificationTests.cs') -Raw; if (-not $m12LiveText.Contains($m12AssetMarker, [StringComparison]::Ordinal)) { throw "M12 SQL Server asset validation marker is missing: $m12AssetMarker" } }
    foreach ($m12Forbidden in @('target_name FROM sys.server_event_session_targets', 'event_name FROM sys.server_event_session_events', 'column_type FROM sys.server_event_session_fields', 'next_run_date,next_run_time FROM msdb.dbo.sysjobschedules', 'notification_message_id FROM msdb.dbo.sysalerts', 'SELECT name,enabled FROM sys.server_event_sessions')) { if ($m12SqlServerText.Contains($m12Forbidden, [StringComparison]::Ordinal)) { throw "M12 SQL Server snapshot contains an invalid catalog projection: $m12Forbidden" } }
    $m12SqlContractPath = Join-Path $repositoryRoot 'release/certification/m12-sqlserver-passive-contract.v1.json'
    $m12SqlSchemaPath = Join-Path $repositoryRoot 'release/certification/m12-sqlserver-passive-contract.v1.schema.json'
    $m12SqlPinPath = Join-Path $repositoryRoot 'release/certification/m12-sqlserver-passive-contract.v1.sha256'
    foreach ($m12SqlPath in @($m12SqlContractPath,$m12SqlSchemaPath,$m12SqlPinPath)) { if (-not (Test-Path -LiteralPath $m12SqlPath -PathType Leaf)) { throw 'M12 SQL Server passive contract asset is missing.' } }
    $m12SqlContractHash = (Get-FileHash -LiteralPath $m12SqlContractPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $m12SqlSchemaHash = (Get-FileHash -LiteralPath $m12SqlSchemaPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($m12SqlContractHash -cne '337dd24a1fc115973d4131e6bdc748c588c54d3b97fb037bd2046856293ec5cb' -or $m12SqlSchemaHash -cne '5539bba4fe0139b92aadbcd6203526cb3377b689d732e68c70aff60bac843406') { throw 'M12 SQL Server passive contract/schema checksum does not match the approved pin.' }
    $m12SqlPin = Get-Content -LiteralPath $m12SqlPinPath -Raw
    if ($m12SqlPin -cne "$m12SqlContractHash  m12-sqlserver-passive-contract.v1.json`n$m12SqlSchemaHash  m12-sqlserver-passive-contract.v1.schema.json`n") { throw 'M12 SQL Server passive contract pin is not exact LF-closed.' }
    $m12SqlContract = Get-Content -LiteralPath $m12SqlContractPath -Raw | ConvertFrom-Json
    $m12SqlSchema = Get-Content -LiteralPath $m12SqlSchemaPath -Raw | ConvertFrom-Json
    if ($m12SqlSchema.additionalProperties -ne $false -or $m12SqlContract.'$schema' -cne 'm12-sqlserver-passive-contract.v1.schema.json') { throw 'M12 SQL Server passive contract/schema must be closed and self-identifying.' }
    $expectedM12SqlCollectors = @('engine.core:1:2','database.inventory:2:2','database.files:3:2','activity.sessions:4:2','activity.requests:5:2','waits.server:6:2','blocking.current:7:2','deadlocks.system-health:8:1','queries.performance:9:1','backups.status:10:1','sql-agent.failures:11:1','tempdb.health:12:1','availability-groups.health:13:1','replication.health:15:1')
    $actualM12SqlCollectors = @($m12SqlContract.collectors | Sort-Object executionOrder | ForEach-Object { "$($_.collectorId):$($_.executionOrder):$($_.assetVersion)" })
    if (($actualM12SqlCollectors -join '|') -cne ($expectedM12SqlCollectors -join '|') -or @($m12SqlContract.collectors).Count -ne 14) { throw 'M12 SQL Server passive collector tuple/order is not the approved closed set.' }
    if (((@($m12SqlContract.assetBundles.PSObject.Properties.Name) | Sort-Object) -join '|') -cne 'm10-replication|m4-core-health|m5-activity|m6-deadlocks|m7-query-performance|m9-operational-health') { throw 'M12 SQL Server passive asset bundle inventory is not exact.' }
    $m12ReportsContractPath = Join-Path $repositoryRoot 'release/certification/m12-reports-contract.v1.json'
    $m12ReportsSchemaPath = Join-Path $repositoryRoot 'release/certification/m12-reports-contract.v1.schema.json'
    $m12ReportsPinPath = Join-Path $repositoryRoot 'release/certification/m12-reports-contract.v1.sha256'
    $m12ReportsProducerPath = Join-Path $repositoryRoot 'tools/run-m12-reports-certification.ps1'
    $m12ReportsTestPath = Join-Path $repositoryRoot 'tests/SqlObserver.IntegrationTests.PostgreSql/M12ReportsCertificationTests.cs'
    foreach ($m12ReportsPath in @($m12ReportsContractPath, $m12ReportsSchemaPath, $m12ReportsPinPath, $m12ReportsProducerPath, $m12ReportsTestPath)) { if (-not (Test-Path -LiteralPath $m12ReportsPath -PathType Leaf)) { throw 'M12 reports certification asset is missing.' } }
    $m12ReportsContractHash = (Get-FileHash -LiteralPath $m12ReportsContractPath -Algorithm SHA256).Hash.ToLowerInvariant(); $m12ReportsSchemaHash = (Get-FileHash -LiteralPath $m12ReportsSchemaPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($m12ReportsContractHash -cne '776a612492e28d9a784e610cca47928e6b5db3f717efc40bc55f3d08f77f0818' -or $m12ReportsSchemaHash -cne '80defd27f366b29b46e48bba9d9a7eec2934a41a2aaf5ca38a79ab979e4354ad') { throw 'M12 reports contract/schema checksum does not match the approved pin.' }
    if ([IO.File]::ReadAllText($m12ReportsPinPath) -cne "$m12ReportsContractHash  m12-reports-contract.v1.json`n$m12ReportsSchemaHash  m12-reports-contract.v1.schema.json`n") { throw 'M12 reports contract pin is not exact LF-closed.' }
    $m12ReportsSchema = Get-Content -LiteralPath $m12ReportsSchemaPath -Raw | ConvertFrom-Json; $m12ReportsContract = Get-Content -LiteralPath $m12ReportsContractPath -Raw | ConvertFrom-Json
    if ($m12ReportsSchema.additionalProperties -ne $false -or $m12ReportsContract.'$schema' -cne 'm12-reports-contract.v1.schema.json' -or $m12ReportsContract.producerId -cne 'm12-reports-harness') { throw 'M12 reports contract/schema must be closed and self-identifying.' }
    $m12ReportsAssets = [ordered]@{
        'database/migrations/0021_reports_exports.sql' = 'e861400591c82000d9bb329c52a983680c3c2a0fc2f66fa93e37472be8b49986'
        'database/migrations/0022_runtime_startup_repairs.sql' = '1c2726afe570ff7a8db169afc6469196d3c27ad73d33b2f5702de5130b31d64d'
        'database/migrations/0023_report_expiry_lock_privilege.sql' = '1647cdaa465f1e216a87f8d47c50575ba7fdc5c43198285b8bdc5fa453ad02b0'
        'database/migrations/0024_report_materialization_column_binding.sql' = '8786730998c3e120664a046519796a24afb8851d41fc5689e66b917aacd068a6'
        'database/migrations/0025_report_run_scoped_read.sql' = 'dd397e02f0faa079befc1a9a804fd0b82eceacb9598a12c48cf9c9a98b3d88e6'
        'database/migrations/checksums.sha256' = '49e6abea1e929bcc898b467d1628c8da52661a936bf92addc2423ce695b17754'
        'src/SqlObserver.Reporting/ReportContracts.cs' = '95613db9340aba8120066a88c5a7062c5f6377c64d08c3d8a1d1fc2c43eb5af8'
        'src/SqlObserver.Reporting/ReportRendering.cs' = '6c89ce15c5463b8e56bc72cf78f28f979579e69719e36ef64b40f32b0ce9de61'
        'src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlReportRepository.cs' = '6dce0d1b5dc6bbd930367059153872e5df3eaa38aeeb31650b0fdb1afd73b8ee'
        'src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlReportAuditPort.cs' = '9e7c8a3af79809f955f8fa7042d095a950a016c43a318bf26676caa3f1c16b74'
        'tests/SqlObserver.IntegrationTests.PostgreSql/M12ReportsCertificationTests.cs' = '5a743f8e7500fa9cc7d058a76f60c58d66d76d5b6c760ba0664251b404a33e22'
        'tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj' = '1e42d8ee02bc687988cc17b44f6200f4bf04dc48d1c93b7e298b195e6c521910'
    }
    if (((@($m12ReportsContract.assetPins.PSObject.Properties.Name) -join '|') -cne (@($m12ReportsAssets.Keys) -join '|'))) { throw 'M12 reports execution asset pin inventory is not exact.' }
    foreach ($m12ReportsAsset in $m12ReportsAssets.Keys) {
        if ($m12ReportsContract.assetPins.$m12ReportsAsset -cne $m12ReportsAssets[$m12ReportsAsset] -or (Get-FileHash -LiteralPath (Join-Path $repositoryRoot $m12ReportsAsset) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $m12ReportsAssets[$m12ReportsAsset]) { throw "M12 reports asset pin mismatch: $m12ReportsAsset" }
    }
    $m12ReportsProducerText = Get-Content -LiteralPath $m12ReportsProducerPath -Raw; $m12ReportsTestText = Get-Content -LiteralPath $m12ReportsTestPath -Raw
    if ((Get-FileHash -LiteralPath $m12ReportsProducerPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne '847d6be473f6d435904801f4e4724f97d90da7f7c1305045ea1964029d6a77f5') { throw 'M12 reports producer source does not match the approved external execution pin.' }
    foreach ($m12ReportsMarker in @('Assert-M12Contract', 'Assert-M12ContractValues', 'ContractOnly', 'ConnectionOnly', 'VerifyFull', 'Assert-ReportsConnection', 'Assert-M12CleanTree', 'Invoke-M12GitStatus', '/t:Rebuild', '--output', 'Remove-M12RawArtifactsDirectory', 'GIT_CONFIG_NOSYSTEM', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_SYSTEM', 'GIT_TERMINAL_PROMPT', '--no-optional-locks', 'detailedWindowDays=7', 'trendWindowDays=31', 'm12-reports-harness', 'reports-exports-evidence', 'LiveReleaseReportsExportsRepresentativeVolumeIsBounded', 'LiveReleaseReportContractIsSnapshotScopedAndAudited', 'LiveReleaseExportContractIsInertAndFormulaSafe', 'Read-M12LockedBytes', 'FileMode]::CreateNew', 'FileShare]::None', 'Assert-M12NoDescendants', 'Read-M12ReportsTrx', 'XmlResolver', 'DocumentType', 'Counters', 'expectedCounters', 'TestCount=$trxResult.TestCount', 'testCount-ne$ExpectedTestCount', 'notExecuted=0', 'warning=0', 'Assert-M12ObservedPidsExited', '[IO.Directory]::Move($build,$verify)', '[IO.Directory]::Move($verify,$final)')) { if (-not $m12ReportsProducerText.Contains($m12ReportsMarker, [StringComparison]::Ordinal)) { throw "M12 reports producer is missing invariant: $m12ReportsMarker" } }
    if ($m12ReportsTestText -notmatch '\[Trait\("Category",\s*"RequiresM12ReportsRelease"\)\]' -or $m12ReportsTestText -notmatch 'ReportRenderer\.RenderHtml' -or $m12ReportsTestText -notmatch 'formulaNeutralization') { throw 'M12 reports release tests must be explicit release-only product-path proofs.' }
    $m12ObservabilityContractPath = Join-Path $repositoryRoot 'release/certification/m12-observability-contract.v1.json'
    $m12ObservabilitySchemaPath = Join-Path $repositoryRoot 'release/certification/m12-observability-contract.v1.schema.json'
    $m12ObservabilityPinPath = Join-Path $repositoryRoot 'release/certification/m12-observability-contract.v1.sha256'
    $m12ObservabilityProducerPath = Join-Path $repositoryRoot 'tools/run-m12-observability-certification.ps1'
    $m12ObservabilityTestPath = Join-Path $repositoryRoot 'tests/SqlObserver.IntegrationTests.PostgreSql/M12ObservabilityCertificationTests.cs'
    foreach ($m12ObservabilityPath in @($m12ObservabilityContractPath, $m12ObservabilitySchemaPath, $m12ObservabilityPinPath, $m12ObservabilityProducerPath, $m12ObservabilityTestPath)) { if (-not (Test-Path -LiteralPath $m12ObservabilityPath -PathType Leaf)) { throw 'M12 observability certification asset is missing.' } }
    $m12ObservabilityContractHash = (Get-FileHash -LiteralPath $m12ObservabilityContractPath -Algorithm SHA256).Hash.ToLowerInvariant(); $m12ObservabilitySchemaHash = (Get-FileHash -LiteralPath $m12ObservabilitySchemaPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($m12ObservabilityContractHash -cne '196b292fe05d13f041d295a975bd0876e45c26276e2deb053c002d333849dbc2' -or $m12ObservabilitySchemaHash -cne '67bb52221c61df57b463842623f9ccd9f9770d8d7bde78017adc3067ce1b9a58') { throw 'M12 observability contract/schema checksum does not match the approved pin.' }
    if ([IO.File]::ReadAllText($m12ObservabilityPinPath) -cne "$m12ObservabilityContractHash  m12-observability-contract.v1.json`n$m12ObservabilitySchemaHash  m12-observability-contract.v1.schema.json`n") { throw 'M12 observability contract pin is not exact LF-closed.' }
    $m12ObservabilityContract = Get-Content -LiteralPath $m12ObservabilityContractPath -Raw | ConvertFrom-Json; $m12ObservabilitySchema = Get-Content -LiteralPath $m12ObservabilitySchemaPath -Raw | ConvertFrom-Json
    if ($m12ObservabilitySchema.additionalProperties -ne $false -or $m12ObservabilityContract.'$schema' -cne 'm12-observability-contract.v1.schema.json' -or $m12ObservabilityContract.producerId -cne 'm12-observability-harness' -or $m12ObservabilityContract.artifactKind -cne 'observability-evidence') { throw 'M12 observability contract/schema must be closed and self-identifying.' }
    $m12ObservabilityAssets = [ordered]@{
        'Directory.Packages.props' = '5699d9704833cac0565ca7f91db7545f85594859dad1bc2c7f263361a9032280'
        'src/SqlObserver.Observability/SqlObserver.Observability.csproj' = '60576c36aab91800264f4e35c58943fded212a53c029a90171a87e897f0828c5'
        'src/SqlObserver.Observability/ObservabilityContracts.cs' = '7113bcc81e331cf73417d4538ac713e03b2cee8e23d2294edf123e945f544a21'
        'src/SqlObserver.Observability/packages.lock.json' = '9921a06f61fbd419a030273ecc8a58e96cffaf02bcf95e1c07088e62098b2468'
        'src/SqlObserver.Server/Program.cs' = '36bc4c6a1301160e42de90bbf328e9fa9747eb42c20abaacbc67043c011190ab'
        'src/SqlObserver.Server/ServerServiceRegistration.cs' = '6486f74a42216f3e8686019afe3a74f3cf991a8af8be9c125c955590560e1939'
        'src/SqlObserver.Server/SqlObserver.Server.csproj' = '168a9680121046f74bc64e2baa9e4313ba4c8279bf3bde89762da30d6385be93'
        'src/SqlObserver.Server/packages.lock.json' = 'f398f1863d3a8164d26059be0b6e82b3c48ebe6f856d675d6d20034452e512fd'
        'src/SqlObserver.Collector/Program.cs' = 'a267f4428236dc0755ce31dac75576175c0190fe685865fd2cbe468726a76090'
        'src/SqlObserver.Collector/CollectorServiceRegistration.cs' = '34008eb721e8c3c39157b1e5aafde1f64ec7bdd6dcdd93671e156980db8d5841'
        'src/SqlObserver.Collector/SqlObserver.Collector.csproj' = '87c4794857248be281692e58b75a402e7b342e341812c2f1d28a0e02f11d1f6b'
        'src/SqlObserver.Collector/packages.lock.json' = 'be84e9538e32ca205ecb96713a33d1fccbe8fbab5c47f514e33c847c4e1f22bf'
        'src/SqlObserver.Collectors/CollectorExecutionEngine.cs' = '838789741108903bc74742aa8fa423bc34304d0de3b17949c8e6447d479e9883'
        'src/SqlObserver.Collectors/CollectorScheduler.cs' = '2a9e3969bfc75b1c224d270be9d99033b8abd283c9d7324318e843fe94793e35'
        'src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlCollectorDataPlane.cs' = 'adf452e64a8156e003334cbea0e54864196497230bb9be8466a38432dcc89608'
        'src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlTargetControlPlane.cs' = '74e51113a857f91ac41acbec88c4eb30c28599bffa5de801c94f48c172e35f73'
        'src/SqlObserver.Infrastructure.PostgreSql/PostgreSqlCompatibilityPort.cs' = '41ef7ad9222fc32fde5a637fe356e5f17961766762f1fd7bcfdec9e12a0e98be'
        'src/SqlObserver.Infrastructure.PostgreSql/packages.lock.json' = '8e59cdd654f5c8f859219cb3c27a94915981e6dec3dec2f6048af724e22de3ec'
        'tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj' = '2638f3ad2478f664acba5af405d2b0effb1af3b9d22a79a710cc9a2bc687592e'
        'tests/SqlObserver.IntegrationTests.PostgreSql/packages.lock.json' = 'ee6fe6dc91f4e777dfa0f14c82cd97fc0ad39b01b367269c46e8ad9c7a133de1'
        'tests/SqlObserver.IntegrationTests.PostgreSql/M12ObservabilityCertificationTests.cs' = 'a0c43cf977cd867df8ca68fd2883249f59b18e1f21e7a0ab63af43f9d3e3fe3a'
        'tests/SqlObserver.UnitTests/SqlObserver.UnitTests.csproj' = '6f9c982af8bb9d2cb3613c8725a87a6d66b4fe0d3d22dc2bf117c02022111ebe'
        'tests/SqlObserver.UnitTests/packages.lock.json' = '2744738e0fdd11a85b738c0f005003e0aeecdb2ffe11f1e41364fda1e82410b5'
        'tests/SqlObserver.UnitTests/M12ObservabilityContractTests.cs' = 'abe0e1230b848f22f534020d8795ac41771bbbcc63d9fdf686d67b979502dd5d'
        'release/certification/m12-certification-matrix.v1.json' = '8b87625c2a56ea07b1dfe826201557e54891341803a75f2a77d2296509f07aac'
    }
    if (((@($m12ObservabilityContract.assetPins.PSObject.Properties.Name) -join '|') -cne (@($m12ObservabilityAssets.Keys) -join '|'))) { throw 'M12 observability asset pin inventory is not exact.' }
    foreach ($m12ObservabilityAsset in $m12ObservabilityAssets.Keys) { if ($m12ObservabilityContract.assetPins.$m12ObservabilityAsset -cne $m12ObservabilityAssets[$m12ObservabilityAsset] -or (Get-FileHash -LiteralPath (Join-Path $repositoryRoot $m12ObservabilityAsset) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $m12ObservabilityAssets[$m12ObservabilityAsset]) { throw "M12 observability asset pin mismatch: $m12ObservabilityAsset" } }
    $m12ObservabilityProducerText = Get-Content -LiteralPath $m12ObservabilityProducerPath -Raw; $m12ObservabilityTestText = Get-Content -LiteralPath $m12ObservabilityTestPath -Raw
    if ((Get-FileHash -LiteralPath $m12ObservabilityProducerPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne '6cb9837dedd0da374bef8df5d640de93a921279358efe5cccfe899b39fb6d3a8') { throw 'M12 observability producer checksum does not match the externally held release pin.' }
    $m12ObservabilityRuntimeText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/SqlObserver.Observability/ObservabilityContracts.cs') -Raw
    $m12ObservabilityServerRegistrationText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/SqlObserver.Server/ServerServiceRegistration.cs') -Raw
    $m12ObservabilityCollectorRegistrationText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src/SqlObserver.Collector/CollectorServiceRegistration.cs') -Raw
    foreach ($m12ObservabilityMarker in @('ContractOnly', 'ConnectionOnly', 'Assert-M12ObservabilityConnection', 'Assert-M12NoExternalOtlp', 'Assert-M12ExactJsonShape', 'Assert-M12ObservabilityFacts', 'Assert-M12ObjectTypes', 'Get-M12LockedSnapshot', 'Assert-M12SameIdentity', 'm12-observability-harness', 'observability-evidence', 'M12SuspendedProcess', 'CreateSuspended', 'KillOnClose', 'ReadAsync', 'FileMode]::CreateNew', 'FileShare]::None', 'Flush($true)', 'Assert-M12CleanTree', 'Invoke-M12GitStatus', 'Invoke-M12GitCommand', 'Get-M12CurrentCommit', 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES', 'GIT_CEILING_DIRECTORIES', 'GIT_COMMON_DIR', 'GIT_NAMESPACE', 'GIT_PREFIX', 'GIT_EXEC_PATH', 'GIT_SSH_COMMAND', 'GIT_CONFIG_COUNT', '/t:Rebuild', '--output', 'Read-M12ObservabilityTrx', 'passedButRunAborted', 'notRunnable', 'disconnected', 'inProgress', 'pending', '[IO.Directory]::Move($build,$verify)', '[IO.Directory]::Move($verify,$final)')) { if (-not $m12ObservabilityProducerText.Contains($m12ObservabilityMarker, [StringComparison]::Ordinal)) { throw "M12 observability producer is missing invariant: $m12ObservabilityMarker" } }
    if ($m12ObservabilityTestText -notmatch '\[Trait\("Category",\s*"RequiresM12ObservabilityRelease"\)\]' -or
        $m12ObservabilityRuntimeText -notmatch 'services\.AddOpenTelemetry\(\)' -or
        $m12ObservabilityRuntimeText -notmatch 'AddOtlpExporter' -or
        $m12ObservabilityServerRegistrationText -notmatch 'AddSqlObserverObservability' -or
        $m12ObservabilityCollectorRegistrationText -notmatch 'AddSqlObserverObservability') { throw 'M12 observability release tests must bind to exporter-based production registrations with explicit release-only coverage.' }
    # M12 supply-chain contract is a pending-only producer: validate its closed
    # assets and exact SBOM lane without promoting any release evidence.
    $m12SbomContractPath = Join-Path $repositoryRoot 'release/certification/m12-supply-chain-contract.v1.json'
    $m12SbomSchemaPath = Join-Path $repositoryRoot 'release/certification/m12-supply-chain-contract.v1.schema.json'
    $m12SbomInputsPath = Join-Path $repositoryRoot 'release/certification/m12-sbom-inputs.v1.json'
    $m12SbomInputsSchemaPath = Join-Path $repositoryRoot 'release/certification/m12-sbom-inputs.v1.schema.json'
    $m12SbomSchema = Join-Path $repositoryRoot 'release/certification/m12-sbom.v1.schema.json'
    $m12SbomPinPath = Join-Path $repositoryRoot 'release/certification/m12-supply-chain-contract.v1.assets.sha256'
    $m12SbomProducerPath = Join-Path $repositoryRoot 'tools/run-m12-supply-chain-certification.ps1'
    $m12SbomGeneratorPath = Join-Path $repositoryRoot 'tools/generate-m12-sbom.mjs'
    $m12SbomProducerSha256 = '3122e2af635dc7cf58ebdfbe6bbaf19f65043e5fcfe847989252c2cb06e86f92'
    $m12SbomAssetManifestSha256 = '4d6c7a292431d26a52b9280cea47c7c40188e085bbae0d668984a167e8370b76'
    foreach ($p in @($m12SbomContractPath,$m12SbomSchemaPath,$m12SbomInputsPath,$m12SbomInputsSchemaPath,$m12SbomSchema,$m12SbomPinPath,$m12SbomProducerPath,$m12SbomGeneratorPath)) { if (-not (Test-Path -LiteralPath $p -PathType Leaf)) { throw 'M12 SBOM certification asset is missing.' } }
    $m12SbomContract = Get-Content -LiteralPath $m12SbomContractPath -Raw | ConvertFrom-Json
    $m12SbomContractSchema = Get-Content -LiteralPath $m12SbomSchemaPath -Raw | ConvertFrom-Json
    $m12SbomInputs = Get-Content -LiteralPath $m12SbomInputsPath -Raw | ConvertFrom-Json
    if ($m12SbomContract.'$schema' -cne 'm12-supply-chain-contract.v1.schema.json' -or $m12SbomContract.producerId -cne 'm12-supply-chain-harness' -or $m12SbomContract.artifactKind -cne 'supply-chain-evidence' -or $m12SbomContract.caseId -cne 'm12-sbom' -or $m12SbomContract.bounds.components -ne 4096 -or $m12SbomContract.bounds.dependencyNodes -ne 8192 -or $m12SbomContract.bounds.jsonBytes -ne 4194304) { throw 'M12 SBOM contract is not the approved closed shape.' }
    $m12SbomApprovedAssets = [ordered]@{
        'release/certification/m12-supply-chain-contract.v1.json' = 'c3d057f5a572e4a68fa0bdfb4d2ca000e1585f69c1c77f2b28db3aef0c50c152'
        'release/certification/m12-supply-chain-contract.v1.schema.json' = 'd15155d6bf41e6b5e90382595d1f2cb7d4ba30d54db293a6be5673aab4ad135d'
        'release/certification/m12-sbom.v1.schema.json' = '1b50c743245f19202639372f1c94335442a2ab52cf5ab298b36e436f4afe6d62'
        'release/certification/m12-sbom-inputs.v1.json' = '132010cbd079e57da1c97d0bef4d436505365d57e9acfa2fed22d9265a8b84be'
        'release/certification/m12-sbom-inputs.v1.schema.json' = 'b01e0d97ca0190d5b814d8254a2079185e8d46792001f3424bfe10c53f0791e8'
        'release/certification/m12-certification-matrix.v1.json' = '8b87625c2a56ea07b1dfe826201557e54891341803a75f2a77d2296509f07aac'
        'BACKLOG.md' = '0b1eba608b357aeb9efd016e7c688c8d754ed3851ecf070d3967d778f48c667a'
        'docs/milestones/M12-reports-installer-release.md' = '8de3b25680aacfef350918d1244c1c4bb3c8c98ab841a7baa71d70f2ab0af383'
        'release/certification/README.md' = '2919035e5332331ede6ca9a33a2f3cdc95792154eda39b1eb25ebe83ed435799'
        'tests/SqlObserver.ReleaseTests/M12SbomCertificationTests.cs' = '99746d8fd4025ede698ea84c3a259218db82ccad889c81dd474a6474cc6cf734'
        'tools/generate-m12-sbom.mjs' = 'ea8887aa4b6ef4c04be0cb4e7e120d2ce67bfe8e967b3b0267a895d2d3de8614'
        'web/tests/m12-sbom-generator-contract.test.mjs' = '1a75390422b14b00dfcbbca0401b0a82bfa5874447b84384a2a9dc003fbc55b1'
        'web/contracts/web-asset-manifest.v1.schema.json' = '1f1e5b785dde79c492773f7298074fbf4668a1cebe75764cecdf97983fed682a'
        'web/tools/web-asset-manifest.mjs' = '04370684e850b3cf9aaa0c5ed6db61dcdd6fb6fbbb7d0e67c8f5cb3197dcf17a'
        'release/certification/m12-provenance-contract.v1.json' = 'c7d4c9e5dd56adfa61ec5e871e2f7759936b555001504bcd49c2a540c3a12c40'
        'release/certification/m12-provenance-contract.v1.schema.json' = '4546d686a21d46234982c2814019bda4f45ca3d8c0ff4c4758d3c2d7f859b7b9'
        'release/certification/m12-provenance-subjects.v1.schema.json' = '91cb87342a323efe412c524da6bae4c6c232cb2070df00ebcecb520cd8941f2e'
        'release/certification/m12-provenance-evidence.v1.schema.json' = 'f263b9c335b96fb81240e48acd35e79b4b3d17d1eaa8db181cbf5c6a86ead203'
        'tools/generate-m12-provenance-evidence.mjs' = '99bd3d560a93f90b5b63c365390d7e56034ddd012b1d11513b56708599fa309b'
        'web/tests/m12-provenance-generator-contract.test.mjs' = '34d1492f2d1fa41bf94c56f174b5acaa4b69da0149df0cfc4390bc42efdf3caa'
        'tests/SqlObserver.ReleaseTests/M12ProvenanceCertificationTests.cs' = '23a19230ce03b50f7f7c89c706b523b76ed0b08529fc7114391e3b8ef3eadcc8'
        'release/certification/m12-runbooks-contract.v1.json' = '45ebf324a497f67198f6e99b69a1a89f8e4092be88ffd35d1634e4e59ac7237d'
        'release/certification/m12-runbooks-contract.v1.schema.json' = 'c646345c41b5ef372b065a2dae03c79b102f83e6f4495a00903e41dc8ece3764'
        'release/certification/m12-runbooks-catalog.v1.json' = 'e02c9e69eea3ce5738444b46f5acc12f9baefab7be7da8fefb333cdc6752460d'
        'release/certification/m12-runbooks-catalog.v1.schema.json' = 'b85173c75c8d7c8b75a59e7060d7badbbbb7cbe2144a2d21158fd1460911b8ac'
        'release/certification/m12-runbooks-inputs.v1.schema.json' = 'e30cb530ff4bedc8785d159b1181f908962f49a848bd5b013b8cd7eadbcc8328'
        'release/certification/m12-runbooks-evidence.v1.schema.json' = '0be97ec20d20928d9bd419b0be9fd771687e509a652194e8d9264ff37edf03ed'
        'docs/runbooks/README.md' = '34a64642595a36b496a0b88d8627b4a7a375a178b3939fa1ed634d58bcd1de86'
        'docs/runbooks/m12-release-preflight.md' = '4790e4124ec1ad70a9b2c7e7634d1bf87cc31f4c3823e5e1a7114b12ca743fa7'
        'docs/runbooks/m12-supply-chain-certification.md' = '42e554d9e78300ea30cd856ae38d207f427291401cab7923e3092f5f13d0fd1b'
        'docs/runbooks/m12-candidate-evidence-verification.md' = '43dd14b4ca5fb712118b85f92e0db21892eb04d59cbaf1737e98c111514be243'
        'docs/runbooks/m12-failed-run-quarantine-and-escalation.md' = '51773b5b1d28f3134913de9aa5aa3c9f832307e43145a6afa1a835f51612856d'
        'tools/generate-m12-runbooks-evidence.mjs' = '06e9dc7a1ff01043b09bbd3a0d37da578c360c32b49a22e446a5af07fe0f9a42'
        'web/tests/m12-runbooks-generator-contract.test.mjs' = '37a55f7ebd97e003ee97115cc4221a4ca14dcda121a4d674fab3dcca1a9def25'
        'tests/SqlObserver.ReleaseTests/M12RunbooksCertificationTests.cs' = '461454385641c8205b76076e3f7d0a21555c8dc3db845c998ea6e0a50613d4aa'
    }
    $m12SbomPinText = [IO.File]::ReadAllText($m12SbomPinPath); $m12SbomExpectedPin = (($m12SbomApprovedAssets.Keys | ForEach-Object { "$($m12SbomApprovedAssets[$_])  $_" }) -join "`n") + "`n"
    if ($m12SbomPinText -cne $m12SbomExpectedPin) { throw 'M12 SBOM contract asset pin is not exact LF-closed.' }
    foreach ($asset in $m12SbomApprovedAssets.Keys) { if ((Get-FileHash -LiteralPath (Join-Path $repositoryRoot $asset) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $m12SbomApprovedAssets[$asset]) { throw "M12 SBOM asset checksum mismatch: $asset" } }
    if ((Get-FileHash -LiteralPath $m12SbomProducerPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $m12SbomProducerSha256) { throw 'M12 SBOM producer source checksum mismatch.' }
    if ((Get-FileHash -LiteralPath $m12SbomPinPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $m12SbomAssetManifestSha256) { throw 'M12 SBOM asset manifest checksum mismatch.' }
    if ($m12SbomContractSchema.additionalProperties -ne $false -or $m12SbomInputs.'$schema' -cne 'm12-sbom-inputs.v1.schema.json' -or @($m12SbomInputs.files).Count -ne 41) { throw 'M12 SBOM schemas or input manifest are not closed.' }
    foreach ($inputAsset in $m12SbomInputs.files) {
        $inputPath = Join-Path $repositoryRoot $inputAsset.path
        if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $inputAsset.sha256) {
            throw "M12 SBOM input checksum mismatch: $($inputAsset.path)"
        }
    }
    $m12SbomProducerText = Get-Content -LiteralPath $m12SbomProducerPath -Raw; $m12SbomGeneratorText = Get-Content -LiteralPath $m12SbomGeneratorPath -Raw
    foreach ($marker in @('ContractOnly','m12-sbom.cdx.json','m12-sbom-test-evidence.json','m12-supply-chain-contract.v1.assets.sha256','Assert-M12TrustedTree','M12SuspendedProcess','CreateSuspended','KillOnClose','ProcessIds','ReadAsync','FileMode]::CreateNew','FileShare]::None','Flush($true)','Assert-M12NoDescendants','XmlResolver','DocumentType','UnitTestResult','Counters','expectedCounters','CycloneDX','1.7','4096','8192')) { if (-not $m12SbomProducerText.Contains($marker, [StringComparison]::Ordinal) -and -not $m12SbomGeneratorText.Contains($marker, [StringComparison]::Ordinal)) { throw "M12 SBOM producer is missing invariant: $marker" } }
    $m12SbomMatrix = Get-Content -LiteralPath (Join-Path $repositoryRoot 'release/certification/m12-certification-matrix.v1.json') -Raw | ConvertFrom-Json
    $m12SbomLane = @($m12SbomMatrix.lanes | Where-Object { $_.laneId -ceq 'supply-chain' }); $m12SbomCase = @($m12SbomLane.cases | Where-Object { $_.caseId -ceq 'm12-sbom' })
    if ($m12SbomLane.Count -ne 1 -or $m12SbomLane[0].implementationStatus -cne 'pending' -or $m12SbomCase.Count -ne 1 -or $m12SbomCase[0].producerId -cne 'm12-supply-chain-harness' -or $m12SbomCase[0].implementationStatus -cne 'pending' -or $m12SbomCase[0].environment.factPredicates.sbom -ne $true) { throw 'M12 SBOM lane must remain pending and exact.' }
    $m12LicenseAssets = [ordered]@{
        'release/certification/m12-license-contract.v1.json' = '019fb05b5bfff8b55947601bbd1dc9e180af5e507136a7da4ce92d39a3196b93'
        'release/certification/m12-license-contract.v1.schema.json' = '94c3c06975ce2a21bf29105cbb2a3b64a5ca0014d6fb082d6f59025eb0bd0390'
        'release/certification/m12-license-evidence.v1.schema.json' = '6c3afae66b9651e0fbae67c95c13df5d5c8adaa0cbbdd7e39374fba1750e6361'
        'tools/generate-m12-license-evidence.mjs' = 'c0442ca8796f142501acf275a9617bade204dd0e0734a7c476b83b13dce540bc'
        'web/tests/m12-license-generator-contract.test.mjs' = '174f3ed497010e5441a1a326588e612e6ad81b3080d6aad4fe73f0c044adcf8d'
        'tests/SqlObserver.ReleaseTests/M12LicenseCertificationTests.cs' = '76f056fe0a543e2ea9b7528db1bc14c2f2662946912661bc1b54a0788f9bdd08'
    }
    $m12LicensePinPath = Join-Path $repositoryRoot 'release/certification/m12-license-contract.v1.assets.sha256'; if (-not (Test-Path -LiteralPath $m12LicensePinPath -PathType Leaf)) { throw 'M12 license certification asset is missing.' }
    foreach ($asset in $m12LicenseAssets.Keys) { if ((Get-FileHash -LiteralPath (Join-Path $repositoryRoot $asset) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $m12LicenseAssets[$asset]) { throw "M12 license asset checksum mismatch: $asset" } }
    $m12LicensePinText = [IO.File]::ReadAllText($m12LicensePinPath); $m12LicenseExpectedPin = (($m12LicenseAssets.Keys | ForEach-Object { "$($m12LicenseAssets[$_])  $_" }) -join "`n") + "`n"; if ($m12LicensePinText -cne $m12LicenseExpectedPin) { throw 'M12 license contract asset pin is not exact LF-closed.' }
    if ((Get-FileHash -LiteralPath $m12LicensePinPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne 'c3aef3256aee2332534f023242300d377aec66361b99b3385b34efd3084c1a57') { throw 'M12 license asset manifest checksum mismatch.' }
    $m12LicenseContract = Get-Content -LiteralPath (Join-Path $repositoryRoot 'release/certification/m12-license-contract.v1.json') -Raw | ConvertFrom-Json; $m12LicenseSchema = Get-Content -LiteralPath (Join-Path $repositoryRoot 'release/certification/m12-license-contract.v1.schema.json') -Raw | ConvertFrom-Json; $m12LicenseEvidenceSchema = Get-Content -LiteralPath (Join-Path $repositoryRoot 'release/certification/m12-license-evidence.v1.schema.json') -Raw | ConvertFrom-Json
    if ($m12LicenseContract.'$schema' -cne 'm12-license-contract.v1.schema.json' -or $m12LicenseContract.contractId -cne 'sqlobserver-m12-licenses' -or $m12LicenseContract.producerId -cne 'm12-supply-chain-harness' -or $m12LicenseContract.artifactKind -cne 'supply-chain-evidence' -or $m12LicenseContract.caseId -cne 'm12-licenses' -or (@($m12LicenseContract.requiredFacts) -join '|') -cne 'os|architecture|licenses' -or $m12LicenseContract.factPredicates.licenses -ne $true -or (@($m12LicenseContract.outputFiles) -join '|') -cne 'm12-licenses.json|m12-licenses-test-evidence.json|m12-licenses-provenance.json' -or (@($m12LicenseContract.licensePolicy.allowedSpdx) -join '|') -cne 'Apache-2.0|MIT|PostgreSQL' -or $m12LicenseContract.bounds.components -ne 4096 -or $m12LicenseSchema.additionalProperties -ne $false -or $m12LicenseEvidenceSchema.additionalProperties -ne $false) { throw 'M12 license contract is not the approved closed shape.' }
    $m12LicenseCase = @($m12SbomLane[0].cases | Where-Object { $_.caseId -ceq 'm12-licenses' }); if ($m12LicenseCase.Count -ne 1 -or $m12LicenseCase[0].producerId -cne 'm12-supply-chain-harness' -or $m12LicenseCase[0].implementationStatus -cne 'pending' -or $m12LicenseCase[0].environment.factPredicates.licenses -ne $true) { throw 'M12 license lane must remain pending and exact.' }
    $m12LicenseTestText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tests/SqlObserver.ReleaseTests/M12LicenseCertificationTests.cs') -Raw; foreach ($marker in @('RequiresM12SupplyChainRelease','LiveReleaseLicenseEvidenceIsCompleteDeterministicAndSbomBound','FileMode.CreateNew','SQLOBSERVER_M12_LICENSE_EVIDENCE_PATH','SQLOBSERVER_M12_LICENSE_SECOND_PATH')) { if (-not $m12LicenseTestText.Contains($marker, [StringComparison]::Ordinal)) { throw "M12 license test is missing invariant: $marker" } }
    # Vulnerability certification remains matrix-pending and is validated by
    # its own closed contract/pin; ordinary validation excludes its live trait.
    $m12VulnerabilityAssets = @{
        'release/certification/m12-vulnerability-scan-contract.v1.json' = 'a706e3dd8eb50f3db41746644c85d5fc6866d977c88a44a4be502dcc38120b53'
        'release/certification/m12-vulnerability-scan-contract.v1.schema.json' = '3aef7a3362dfdc7a503b09bc7cb5995f7eec647c0fe4a3498aa5bca697a3b2e1'
        'release/certification/m12-vulnerability-scan-evidence.v1.schema.json' = '52feab8195f4b0deb0beb4db1d6200289a0816868c5ad75cfc5742ae4cb5fe8f'
        'tools/generate-m12-vulnerability-scan-evidence.mjs' = '27fee3c11e25a32c0eafcbab0358bd41077457abc90f06a78dec7764de4e287b'
        'web/tests/m12-vulnerability-scan-generator-contract.test.mjs' = '41ba7b969af235c8b0c8b1ef5f8cd6cc26315a73a15946f7784f6d8015c21375'
        'tests/SqlObserver.ReleaseTests/M12VulnerabilityCertificationTests.cs' = '56545c0435608cb33bb59acaf4c2e5cb50668a999f0c1fbfc7b71a7193680fd3'
    }
    $m12VulnerabilityPinPath = Join-Path $repositoryRoot 'release/certification/m12-vulnerability-scan-contract.v1.assets.sha256'; if ((Get-FileHash -LiteralPath $m12VulnerabilityPinPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne 'da654c8aede4876ae2d91276e08b6a67769ce58d00beb5e22f97c84743c229b5') { throw 'M12 vulnerability asset manifest checksum mismatch.' }; foreach ($asset in $m12VulnerabilityAssets.GetEnumerator()) { $assetPath = Join-Path $repositoryRoot $asset.Key; if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf) -or (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $asset.Value) { throw "M12 vulnerability certification asset pin mismatch: $($asset.Key)" } }
    $m12VulnerabilityContractText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'release/certification/m12-vulnerability-scan-contract.v1.json') -Raw; foreach ($marker in @('m12-vulnerability-scan','10.0.203','22.22.0','11.19.0','reject-all','registryErrors','unfixable')) { if (-not $m12VulnerabilityContractText.Contains($marker, [StringComparison]::Ordinal)) { throw "M12 vulnerability contract is missing exact marker: $marker" } }
    $m12VulnerabilityTestText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tests/SqlObserver.ReleaseTests/M12VulnerabilityCertificationTests.cs') -Raw; foreach ($marker in @('RequiresM12SupplyChainRelease','LiveReleaseVulnerabilityScanIsCleanDeterministicAndSbomBound','FileMode.CreateNew','SQLOBSERVER_M12_VULNERABILITY_EVIDENCE_PATH','SQLOBSERVER_M12_VULNERABILITY_SECOND_PATH')) { if (-not $m12VulnerabilityTestText.Contains($marker, [StringComparison]::Ordinal)) { throw "M12 vulnerability test is missing invariant: $marker" } }
    $m12ProvenanceAssets = @{
        'release/certification/m12-provenance-contract.v1.json' = 'c7d4c9e5dd56adfa61ec5e871e2f7759936b555001504bcd49c2a540c3a12c40'
        'release/certification/m12-provenance-contract.v1.schema.json' = '4546d686a21d46234982c2814019bda4f45ca3d8c0ff4c4758d3c2d7f859b7b9'
        'release/certification/m12-provenance-subjects.v1.schema.json' = '91cb87342a323efe412c524da6bae4c6c232cb2070df00ebcecb520cd8941f2e'
        'release/certification/m12-provenance-evidence.v1.schema.json' = 'f263b9c335b96fb81240e48acd35e79b4b3d17d1eaa8db181cbf5c6a86ead203'
        'tools/generate-m12-provenance-evidence.mjs' = '99bd3d560a93f90b5b63c365390d7e56034ddd012b1d11513b56708599fa309b'
        'web/tests/m12-provenance-generator-contract.test.mjs' = '34d1492f2d1fa41bf94c56f174b5acaa4b69da0149df0cfc4390bc42efdf3caa'
        'tests/SqlObserver.ReleaseTests/M12ProvenanceCertificationTests.cs' = '23a19230ce03b50f7f7c89c706b523b76ed0b08529fc7114391e3b8ef3eadcc8'
    }
    $m12ProvenancePinPath = Join-Path $repositoryRoot 'release/certification/m12-provenance-contract.v1.assets.sha256'; if ((Get-FileHash -LiteralPath $m12ProvenancePinPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne '4202952515d9c5be6ad6dfdaf5c1ee01f94912ee9affe44da6289b92f4884e6a') { throw 'M12 provenance asset manifest checksum mismatch.' }; foreach ($asset in $m12ProvenanceAssets.GetEnumerator()) { $assetPath = Join-Path $repositoryRoot $asset.Key; if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf) -or (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $asset.Value) { throw "M12 provenance certification asset pin mismatch: $($asset.Key)" } }
    $m12ProvenanceContractText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'release/certification/m12-provenance-contract.v1.json') -Raw; foreach ($marker in @('m12-provenance','exact-four-product-build','full-transitive','inputManifestFiles','not-claimed','m12-provenance-subjects.v1.schema.json')) { if (-not $m12ProvenanceContractText.Contains($marker, [StringComparison]::Ordinal)) { throw "M12 provenance contract is missing exact marker: $marker" } }
    $m12ProvenanceTestText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tests/SqlObserver.ReleaseTests/M12ProvenanceCertificationTests.cs') -Raw; foreach ($marker in @('RequiresM12SupplyChainRelease','LiveReleaseProvenanceIsDeterministicAndBuildBound','FileMode.CreateNew','SQLOBSERVER_M12_PROVENANCE_EVIDENCE_PATH','SQLOBSERVER_M12_PROVENANCE_SECOND_PATH','SQLOBSERVER_M12_PROVENANCE_INPUT_MANIFEST_PATH')) { if (-not $m12ProvenanceTestText.Contains($marker, [StringComparison]::Ordinal)) { throw "M12 provenance test is missing invariant: $marker" } }
    $m12ProvenanceCase = @($m12SbomLane[0].cases | Where-Object { $_.caseId -ceq 'm12-provenance' }); if ($m12ProvenanceCase.Count -ne 1 -or $m12ProvenanceCase[0].producerId -cne 'm12-supply-chain-harness' -or $m12ProvenanceCase[0].implementationStatus -cne 'pending' -or $m12ProvenanceCase[0].environment.factPredicates.provenance -ne $true) { throw 'M12 provenance lane must remain pending and exact.' }
    # Runbooks are a separate pending-only contract: the catalog is inert
    # documentation and its producer emits only sanitized evidence identities.
    $m12RunbooksAssets = [ordered]@{
        'release/certification/m12-runbooks-contract.v1.json' = '45ebf324a497f67198f6e99b69a1a89f8e4092be88ffd35d1634e4e59ac7237d'
        'release/certification/m12-runbooks-contract.v1.schema.json' = 'c646345c41b5ef372b065a2dae03c79b102f83e6f4495a00903e41dc8ece3764'
        'release/certification/m12-runbooks-catalog.v1.json' = 'e02c9e69eea3ce5738444b46f5acc12f9baefab7be7da8fefb333cdc6752460d'
        'release/certification/m12-runbooks-catalog.v1.schema.json' = 'b85173c75c8d7c8b75a59e7060d7badbbbb7cbe2144a2d21158fd1460911b8ac'
        'release/certification/m12-runbooks-inputs.v1.schema.json' = 'e30cb530ff4bedc8785d159b1181f908962f49a848bd5b013b8cd7eadbcc8328'
        'release/certification/m12-runbooks-evidence.v1.schema.json' = '0be97ec20d20928d9bd419b0be9fd771687e509a652194e8d9264ff37edf03ed'
        'docs/runbooks/README.md' = '34a64642595a36b496a0b88d8627b4a7a375a178b3939fa1ed634d58bcd1de86'
        'docs/runbooks/m12-release-preflight.md' = '4790e4124ec1ad70a9b2c7e7634d1bf87cc31f4c3823e5e1a7114b12ca743fa7'
        'docs/runbooks/m12-supply-chain-certification.md' = '42e554d9e78300ea30cd856ae38d207f427291401cab7923e3092f5f13d0fd1b'
        'docs/runbooks/m12-candidate-evidence-verification.md' = '43dd14b4ca5fb712118b85f92e0db21892eb04d59cbaf1737e98c111514be243'
        'docs/runbooks/m12-failed-run-quarantine-and-escalation.md' = '51773b5b1d28f3134913de9aa5aa3c9f832307e43145a6afa1a835f51612856d'
        'tools/generate-m12-runbooks-evidence.mjs' = '06e9dc7a1ff01043b09bbd3a0d37da578c360c32b49a22e446a5af07fe0f9a42'
        'web/tests/m12-runbooks-generator-contract.test.mjs' = '37a55f7ebd97e003ee97115cc4221a4ca14dcda121a4d674fab3dcca1a9def25'
        'tests/SqlObserver.ReleaseTests/M12RunbooksCertificationTests.cs' = '461454385641c8205b76076e3f7d0a21555c8dc3db845c998ea6e0a50613d4aa'
    }
    $m12RunbooksPinPath = Join-Path $repositoryRoot 'release/certification/m12-runbooks-contract.v1.assets.sha256'; $m12RunbooksPinHash = '7642720a58b636639bef61332a638aa6587f2ac90f60a45dfbd25e2b6dc6961f'; if (-not (Test-Path -LiteralPath $m12RunbooksPinPath -PathType Leaf) -or (Get-FileHash -LiteralPath $m12RunbooksPinPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $m12RunbooksPinHash) { throw 'M12 runbooks asset manifest checksum mismatch.' }
    foreach ($asset in $m12RunbooksAssets.GetEnumerator()) { $assetPath = Join-Path $repositoryRoot $asset.Key; if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf) -or (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $asset.Value) { throw "M12 runbooks certification asset pin mismatch: $($asset.Key)" } }
    $m12RunbooksContract = Get-Content -LiteralPath (Join-Path $repositoryRoot 'release/certification/m12-runbooks-contract.v1.json') -Raw | ConvertFrom-Json; $m12RunbooksCatalog = Get-Content -LiteralPath (Join-Path $repositoryRoot 'release/certification/m12-runbooks-catalog.v1.json') -Raw | ConvertFrom-Json
    if ($m12RunbooksContract.'$schema' -cne 'm12-runbooks-contract.v1.schema.json' -or $m12RunbooksContract.contractId -cne 'sqlobserver-m12-runbooks' -or $m12RunbooksContract.producerId -cne 'm12-supply-chain-harness' -or $m12RunbooksContract.artifactKind -cne 'supply-chain-evidence' -or $m12RunbooksContract.caseId -cne 'm12-runbooks' -or (@($m12RunbooksContract.requiredFacts) -join '|') -cne 'os|architecture|runbooks' -or $m12RunbooksContract.factPredicates.runbooks -ne $true -or (@($m12RunbooksContract.prerequisites) -join '|') -cne 'm12-sbom|m12-licenses|m12-vulnerability-scan|m12-provenance' -or (@($m12RunbooksContract.outputFiles) -join '|') -cne 'm12-runbooks.json|m12-runbooks-test-evidence.json|m12-runbooks-provenance.json' -or $m12RunbooksContract.bounds.documents -ne 4 -or $m12RunbooksContract.bounds.sectionsPerDocument -ne 12 -or $m12RunbooksContract.bounds.prerequisites -ne 4 -or $m12RunbooksContract.bounds.links -ne 64 -or $m12RunbooksContract.bounds.documentBytes -ne 131072 -or $m12RunbooksContract.bounds.markdownBytes -ne 524288 -or $m12RunbooksContract.bounds.jsonBytes -ne 4194304 -or $m12RunbooksContract.bounds.stringLength -ne 512 -or $m12RunbooksContract.bounds.depth -ne 32) { throw 'M12 runbooks contract is not the approved closed shape.' }
    if ($m12RunbooksCatalog.documents.Count -ne 4 -or (@($m12RunbooksCatalog.allowedPrerequisiteCases) -join '|') -cne 'm12-sbom|m12-licenses|m12-vulnerability-scan|m12-provenance') { throw 'M12 runbooks catalog is not exact.' }
    foreach ($runbook in $m12RunbooksCatalog.documents) { $runbookPath = Join-Path $repositoryRoot ([string]$runbook.path); if ($runbook.sectionCount -ne 12 -or ([regex]::Matches((Get-Content -LiteralPath $runbookPath -Raw), '(?m)^## ')).Count -ne 12) { throw 'M12 runbook must contain exactly twelve sections.' } }
    $m12RunbooksTestText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tests/SqlObserver.ReleaseTests/M12RunbooksCertificationTests.cs') -Raw; foreach ($marker in @('RequiresM12SupplyChainRelease','LiveReleaseRunbooksAreClosedVersionedAndExercised','FileMode.CreateNew','SQLOBSERVER_M12_RUNBOOKS_EVIDENCE_PATH','SQLOBSERVER_M12_RUNBOOKS_RESULT_PATH')) { if (-not $m12RunbooksTestText.Contains($marker, [StringComparison]::Ordinal)) { throw "M12 runbooks test is missing invariant: $marker" } }
    $m12RunbooksCase = @($m12SbomLane[0].cases | Where-Object { $_.caseId -ceq 'm12-runbooks' }); if ($m12RunbooksCase.Count -ne 1 -or $m12RunbooksCase[0].producerId -cne 'm12-supply-chain-harness' -or $m12RunbooksCase[0].implementationStatus -cne 'pending' -or (@($m12RunbooksCase[0].environment.requiredFacts) -join '|') -cne 'os|architecture|runbooks' -or $m12RunbooksCase[0].environment.factPredicates.runbooks -ne $true) { throw 'M12 runbooks lane must remain pending and exact.' }
    $sqlContractText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tests/SqlObserver.IntegrationTests.SqlServer/SqlServerLabContract.cs') -Raw
    if ($sqlContractText -notmatch 'SQLOBSERVER_RELEASE_SQLSERVER' -or
        $sqlContractText -notmatch 'SqlConnectionStringBuilder' -or
        $sqlContractText -notmatch 'IntegratedSecurity' -or
        $sqlContractText -notmatch 'TrustServerCertificate' -or
        $sqlContractText -notmatch 'must validate the server certificate') {
        throw 'SQL Server local/release contract must use builder validation, Windows authentication, and reject certificate bypass.'
    }
    $postgresFixtureText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tests/SqlObserver.IntegrationTests.PostgreSql/PostgreSql18Fixture.cs') -Raw
    if ($postgresFixtureText -notmatch 'SQLOBSERVER_RELEASE_POSTGRES' -or
        $postgresFixtureText -notmatch 'string\.Equals\(profile,\s*"Release"' -or
        $postgresFixtureText -notmatch 'new PostgreSqlBuilder') {
        throw 'PostgreSQL fixture must explicitly switch between the validated Release connection and local Testcontainers.'
    }
    $externalResolutionOffset = $postgresFixtureText.IndexOf('string? externalConnection = ResolveExternalConnection(', [StringComparison]::Ordinal)
    $containerOffset = $postgresFixtureText.IndexOf('new PostgreSqlBuilder', [StringComparison]::Ordinal)
    if ($externalResolutionOffset -lt 0 -or $containerOffset -le $externalResolutionOffset -or $postgresFixtureText.Substring($externalResolutionOffset, $containerOffset - $externalResolutionOffset) -notmatch 'if\s*\(externalConnection is not null\)\s*\{\s*_adminConnectionString = externalConnection;\s*return;') {
        throw 'PostgreSQL validated external connection must return before constructing a Testcontainers container.'
    }
}

function Assert-ReleasePreflight {
    if ($Profile -ne 'Release') { return }
    if (-not $IsWindows -or -not [Environment]::Is64BitOperatingSystem) {
        throw 'Release certification requires a supported 64-bit Windows host.'
    }
    $matrixPath = Join-Path $repositoryRoot 'release/certification/m12-certification-matrix.v1.json'
    $matrix = Get-Content -LiteralPath $matrixPath -Raw | ConvertFrom-Json -DateKind String
    $trustedPwshPath = Get-TrustedPowerShellPath
    try {
        Invoke-CheckedCommand -Executable $trustedPwshPath -Arguments @(
            '-NoProfile', '-NonInteractive', '-File', (Join-Path $repositoryRoot 'tools/verify-test-results.ps1'),
            '-MatrixOnly', '-RepositoryRoot', $repositoryRoot
        ) -WorkingDirectory $repositoryRoot
    }
    catch {
        throw 'Release preflight failed: pending certification producer(s): certification matrix/verifier readiness.'
    }
    $implementedReleaseLanes = @('repository-contract', 'release-build', 'unit', 'api', 'security', 'performance', 'end-to-end', 'frontend')
    $pendingLanes = @($matrix.profiles.Release.requiredLanes | Where-Object { $implementedReleaseLanes -notcontains [string]$_ })
    if ($pendingLanes.Count -ne 0) {
        throw "Release preflight failed: pending certification producer(s): $($pendingLanes -join ', ')"
    }
    foreach ($requiredEnvironmentVariable in @(
        'SQLOBSERVER_RELEASE_POSTGRES',
        'SQLOBSERVER_RELEASE_SQLSERVER',
        'SQLOBSERVER_RELEASE_BROWSER',
        'SQLOBSERVER_RELEASE_INSTALLER',
        'SQLOBSERVER_RELEASE_SIGNING',
        'SQLOBSERVER_CERTIFICATION_MANIFEST')) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($requiredEnvironmentVariable))) {
            throw "Release certification preflight is missing required evidence/lab configuration: $requiredEnvironmentVariable"
        }
    }
    $releasePostgreSql = [Environment]::GetEnvironmentVariable('SQLOBSERVER_RELEASE_POSTGRES')
    if ($releasePostgreSql -notmatch '(?i)(^|;)\s*(host|server|data source)\s*=') {
        throw 'Release PostgreSQL contract is malformed.'
    }
    $releaseSqlServer = [Environment]::GetEnvironmentVariable('SQLOBSERVER_RELEASE_SQLSERVER')
    if ($releaseSqlServer -notmatch '(?i)(^|;)\s*(server|data source)\s*=') {
        throw 'Release SQL Server contract is malformed.'
    }
    $docker = Get-Command docker -ErrorAction SilentlyContinue
    if ($null -eq $docker) { throw 'Release certification requires a Docker/PostgreSQL lab; docker is unavailable.' }
    $dockerInfo = & docker info --format '{{.ServerVersion}}' 2>&1
    if ($LASTEXITCODE -ne 0 -or ($dockerInfo -match '(?i)error|access is denied|cannot connect|daemon unavailable')) {
        throw 'Release certification requires a running Docker/PostgreSQL lab.'
    }
    $manifestPath = [Environment]::GetEnvironmentVariable('SQLOBSERVER_CERTIFICATION_MANIFEST')
    Invoke-CheckedCommand -Executable $trustedPwshPath -Arguments @(
        '-NoProfile', '-NonInteractive', '-File', (Join-Path $repositoryRoot 'tools/verify-test-results.ps1'),
        '-ManifestPath', $manifestPath, '-Profile', 'Release', '-RepositoryRoot', $repositoryRoot
    ) -WorkingDirectory $repositoryRoot
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

function Assert-ReleaseIdentityAssessment {
    $assessmentScript = Join-Path $repositoryRoot 'tools/assess-release-identity.ps1'
    $assessmentSchemaPath = Join-Path $repositoryRoot 'release/contracts/release-identity-assessment.v1.schema.json'
    $assessmentChecksumPath = Join-Path $repositoryRoot 'release/contracts/checksums.sha256'
    foreach ($path in @($assessmentScript, $assessmentSchemaPath, $assessmentChecksumPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'M12 release identity assessment contract is missing.' }
    }
    $checksumLines = [IO.File]::ReadAllLines($assessmentChecksumPath)
    if ($checksumLines.Count -ne 1 -or $checksumLines[0] -cnotmatch '^[0-9a-f]{64}  release-identity-assessment\.v1\.schema\.json$') { throw 'M12 release identity assessment checksum manifest is invalid.' }
    $expectedHash = $checksumLines[0].Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0]
    $actualHash = (Get-FileHash -LiteralPath $assessmentSchemaPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expectedHash -cne $actualHash) { throw 'M12 release identity assessment schema checksum mismatch.' }

    $pwshPath = Get-TrustedPowerShellPath
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $pwshPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', $assessmentScript, '-RepositoryRoot', $repositoryRoot)) {
        [void]$start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'M12 release identity assessment did not start.' }
    try {
        $streamState = Read-CappedProcessStreams $process 65536 4096 20000
        if ($streamState.TimedOut) { throw 'M12 release identity assessment timed out.' }
        $rawOutput = $streamState.Output.ToString()
        $rawError = $streamState.Error.ToString()
        if ($streamState.OutputTooLarge -or $streamState.ErrorTooLarge -or $rawError.Length -ne 0) { throw 'M12 release identity assessment emitted unsafe output.' }
        $output = $rawOutput.Trim()
        if ($process.ExitCode -ne 0 -or $output -match '\r?\n' -or [string]::IsNullOrWhiteSpace($output)) {
            throw 'M12 release identity assessment did not produce one JSON record.'
        }
        $assessment = $output | ConvertFrom-Json -DateKind String
        $expectedAssessmentProperties = '$schema', 'schemaVersion', 'assessmentId', 'assessedAtUtc', 'status', 'releaseEvidence', 'readyToRelease', 'commitSha', 'policyId', 'policyVersion', 'matrixId', 'counts', 'checks'
        if ((@($assessment.PSObject.Properties.Name) -join '|') -cne ($expectedAssessmentProperties -join '|') -or
            $assessment.'$schema' -cne 'release-identity-assessment.v1.schema.json' -or
            $assessment.schemaVersion -ne 1 -or $assessment.assessmentId -cne 'm12-release-identity' -or
            $assessment.status -cne 'not_ready' -or $assessment.releaseEvidence -ne $false -or
            $assessment.readyToRelease -ne $false -or $assessment.policyId -cne 'sqlobserver-m12-policy-v1' -or
            $assessment.matrixId -cne 'sqlobserver-m12' -or
            $assessment.assessedAtUtc -notmatch '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{7}Z$' -or
            $assessment.commitSha -notmatch '^[0-9a-f]{40}$' -or $assessment.policyVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
            @($assessment.checks).Count -ne 29 -or
            $assessment.counts.matrixLanes -ne 20 -or $assessment.counts.matrixCases -ne 35 -or
            $assessment.counts.implementedLanes -ne 8 -or $assessment.counts.implementedCases -ne 8 -or
            $assessment.counts.pendingLanes -ne 12 -or $assessment.counts.pendingCases -ne 27 -or $assessment.counts.checks -ne 29) {
            throw 'M12 release identity assessment failed its closed contract.'
        }
        try {
            $assessmentTime = [DateTimeOffset]::ParseExact($assessment.assessedAtUtc, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal)
            if ([Math]::Abs(([DateTimeOffset]::UtcNow - $assessmentTime).TotalMinutes) -gt 2) { throw 'stale' }
        }
        catch { throw 'M12 release identity assessment timestamp is not canonical/current.' }
        $expectedAssessmentIds = @('head-commit','policy-identity','matrix-identity','matrix-inventory','profile-inventory','schema-checksum','matrix-only-verification','repository-contract','product-version','artifact-digests','publisher-identity','license','versioning-policy','signing-identity','key-custody','timestamping','key-rotation','revocation-response','release-approver','artifact-retention','evidence-access','canonical-build-environment','windows-certification','postgresql-certification','sqlserver-certification','browser-certification','installer-certification','sustained-load-recovery','release-manifest')
        $expectedAssessmentCodes = @('HEAD_VERIFIED','POLICY_ID_VERIFIED','MATRIX_ID_VERIFIED','MATRIX_INVENTORY_VERIFIED','PROFILE_INVENTORY_VERIFIED','ASSESSMENT_SCHEMA_PINNED','MATRIX_ONLY_VERIFIED','REPOSITORY_CONTRACT_OBSERVED','PRODUCT_IDENTITY_PENDING','ARTIFACT_DIGESTS_PENDING') + @('OWNER_DECISION_REQUIRED') * 12 + @('MATRIX_PENDING') * 7
        $orders = @($assessment.checks | ForEach-Object { [int]$_.order })
        if (($orders -join ',') -cne ((1..29) -join ',')) { throw 'M12 release identity check order is invalid.' }
        $ids = @($assessment.checks | ForEach-Object { [string]$_.checkId })
        $codes = @($assessment.checks | ForEach-Object { [string]$_.code })
        if (($ids -join '|') -cne ($expectedAssessmentIds -join '|') -or ($codes -join '|') -cne ($expectedAssessmentCodes -join '|')) { throw 'M12 release identity check catalog is invalid.' }
        $expectedCountProperties = 'matrixLanes', 'matrixCases', 'implementedLanes', 'implementedCases', 'pendingLanes', 'pendingCases', 'checks'
        if ((@($assessment.counts.PSObject.Properties.Name) -join '|') -cne ($expectedCountProperties -join '|')) { throw 'M12 release identity count shape is invalid.' }
        foreach ($check in @($assessment.checks)) {
            if ((@($check.PSObject.Properties.Name) -join '|') -cne 'checkId|order|status|code' -or
                [string]$check.status -notin @('observed', 'blocked')) { throw 'M12 release identity check shape is invalid.' }
        }
        if (@($assessment.checks | Where-Object { $_.order -le 8 -and $_.status -ne 'observed' }).Count -ne 0 -or
            @($assessment.checks | Where-Object { $_.order -ge 9 -and $_.status -ne 'blocked' }).Count -ne 0) {
            throw 'M12 release identity observed or blocked status semantics are invalid.'
        }
        $head = Resolve-RepositoryHeadSha $repositoryRoot
        if ($null -eq $head -or $assessment.commitSha -cne $head) { throw 'M12 release identity commit does not equal current HEAD.' }
        if ($assessment.policyVersion -cne '1.2.0') { throw 'M12 release identity policy version is invalid.' }
    }
    catch [System.Text.Json.JsonException] { throw 'M12 release identity assessment JSON is invalid.' }
    finally { $process.Dispose() }
}

function Invoke-TestProject {
    param(
        [Parameter(Mandatory = $true)] [string] $Project,
        [string] $Filter
    )

    $resolved = if ([IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $repositoryRoot $Project }
    $listArguments = @('test', $resolved, '--configuration', 'Release', '--no-build', '--no-restore', '--list-tests')
    if (-not [string]::IsNullOrWhiteSpace($Filter)) { $listArguments += @('--filter', $Filter) }
    Push-Location $repositoryRoot
    try {
        $listed = @(& dotnet @listArguments 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "Unable to enumerate tests for $Project." }
        $testLines = @($listed | Where-Object { [string]$_ -match '^\s{2,}\S' -and [string]$_ -notmatch '^\s*(Test run|Passed|Failed|Total tests|\S+\.dll)' })
        if ($testLines.Count -eq 0) { throw "Test selection produced zero tests for $Project; refusing a silent pass." }
    }
    finally { Pop-Location }
    $arguments = @('test', $resolved, '--configuration', 'Release', '--no-build', '--no-restore')
    if (-not [string]::IsNullOrWhiteSpace($Filter)) { $arguments += @('--filter', $Filter) }
    if (-not [string]::IsNullOrWhiteSpace($TestResultsDirectory)) {
        $resultsPath = [IO.Path]::GetFullPath($TestResultsDirectory, $repositoryRoot)
        [void] [IO.Directory]::CreateDirectory($resultsPath)
        $resultName = [IO.Path]::GetFileNameWithoutExtension($resolved) + '.trx'
        $arguments += @('--logger', "trx;LogFileName=$resultName", '--results-directory', $resultsPath)
    }
    Invoke-CheckedCommand -Executable 'dotnet' -Arguments $arguments -WorkingDirectory $repositoryRoot
}

Get-Command dotnet -ErrorAction Stop | Out-Null
Get-Command pnpm -ErrorAction Stop | Out-Null

Assert-RepositoryShape
Assert-ReleaseIdentityAssessment
Assert-ReleasePreflight

# Child tests resolve their endpoint/connection contracts from the same profile
# selected by this invocation; no release value is presence-only.
$env:SQLOBSERVER_VALIDATION_PROFILE = $Profile

Invoke-CheckedCommand -Executable 'dotnet' -Arguments @(
    'restore', $solutionPath,
    '--configfile', $nugetConfigPath,
    '--locked-mode'
) -WorkingDirectory $repositoryRoot

Invoke-CheckedCommand -Executable 'dotnet' -Arguments @(
    'build', $solutionPath,
    '--configuration', 'Release',
    '--no-restore'
) -WorkingDirectory $repositoryRoot

$testProjectsToRun = @(
        'tests/SqlObserver.UnitTests/SqlObserver.UnitTests.csproj',
        'tests/SqlObserver.IntegrationTests.PostgreSql/SqlObserver.IntegrationTests.PostgreSql.csproj',
        'tests/SqlObserver.IntegrationTests.SqlServer/SqlObserver.IntegrationTests.SqlServer.csproj',
        'tests/SqlObserver.ApiContractTests/SqlObserver.ApiContractTests.csproj',
        'tests/SqlObserver.SecurityTests/SqlObserver.SecurityTests.csproj',
        'tests/SqlObserver.PerformanceTests/SqlObserver.PerformanceTests.csproj',
        'tests/SqlObserver.EndToEndTests/SqlObserver.EndToEndTests.csproj',
        'tests/SqlObserver.McpContractTests/SqlObserver.McpContractTests.csproj',
        'tests/SqlObserver.ReleaseTests/SqlObserver.ReleaseTests.csproj'
    )
foreach ($testProject in $testProjectsToRun) {
    $filter = $null
    if ($Profile -eq 'Local' -and $testProject -like '*SqlObserver.EndToEndTests.csproj') {
        $filter = 'Category!=RequiresPostgreSql'
    }
    if ($testProject -like '*SqlObserver.ReleaseTests.csproj') {
        # Producer-owned M12 release proofs are selected only by their
        # producer; ordinary validation must never run the live category.
        $filter = 'Category!=RequiresM12SupplyChainRelease'
    }
    if ($Profile -eq 'Local' -and $testProject -like '*SqlObserver.McpContractTests.csproj') {
        $filter = 'Category!=RequiresM12McpRelease'
    }
    if ($Profile -eq 'Local' -and $testProject -like '*SqlObserver.IntegrationTests.PostgreSql.csproj') {
        $filter = 'Category!=RequiresPostgreSql'
    }
    if ($testProject -like '*SqlObserver.IntegrationTests.PostgreSql.csproj') {
        # The reports producer is the sole selector for release reports
        # evidence; ordinary Local and Release sweeps must not run it.
        $filter = if ([string]::IsNullOrWhiteSpace($filter)) { 'Category!=RequiresM12ReportsRelease' } else { "($filter)&Category!=RequiresM12ReportsRelease" }
        # The observability producer is the sole selector for live M12
        # readiness/telemetry evidence; ordinary sweeps remain local-only.
        $filter = if ([string]::IsNullOrWhiteSpace($filter)) { 'Category!=RequiresM12ObservabilityRelease' } else { "($filter)&Category!=RequiresM12ObservabilityRelease" }
    }
    if ($Profile -eq 'Local' -and $testProject -like '*SqlObserver.IntegrationTests.SqlServer.csproj' -and [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('SQLOBSERVER_LOCAL_SQLSERVER'))) {
        $filter = 'Category!=RequiresSqlServer'
    }
    if ($testProject -like '*SqlObserver.IntegrationTests.SqlServer.csproj') {
        # The M12 release producer is the sole selector for the live passive
        # certification. Ordinary Local and Release sweeps must never invoke
        # it (and must not turn a missing release lab into a skip).
        $filter = if ([string]::IsNullOrWhiteSpace($filter)) { 'Category!=RequiresM12SqlServerRelease' } else { "($filter)&Category!=RequiresM12SqlServerRelease" }
    }
    Invoke-TestProject -Project $testProject -Filter $filter
}

Invoke-CheckedCommand -Executable 'pnpm' -Arguments @(
    '--dir', $webPath,
    'install',
    '--frozen-lockfile'
) -WorkingDirectory $repositoryRoot

$trustedPowerShellEnvironmentName = 'SQLOBSERVER_M12_TRUSTED_PWSH_PATH'
$trustedPowerShellWasPresent = Test-Path -LiteralPath "Env:$trustedPowerShellEnvironmentName"
$previousTrustedPowerShellPath = [Environment]::GetEnvironmentVariable($trustedPowerShellEnvironmentName)
try {
    Set-Item -LiteralPath "Env:$trustedPowerShellEnvironmentName" -Value (Get-TrustedPowerShellPath)
    foreach ($script in @('typecheck', 'test', 'build')) {
        Invoke-CheckedCommand -Executable 'pnpm' -Arguments @(
            '--dir', $webPath,
            'run', $script
        ) -WorkingDirectory $repositoryRoot
    }
}
finally {
    if ($trustedPowerShellWasPresent) {
        Set-Item -LiteralPath "Env:$trustedPowerShellEnvironmentName" -Value $previousTrustedPowerShellPath
    }
    else {
        Remove-Item -LiteralPath "Env:$trustedPowerShellEnvironmentName" -ErrorAction SilentlyContinue
    }
}

if ($Profile -eq 'Local') {
    $summaryMatrix = Get-Content -LiteralPath (Join-Path $repositoryRoot 'release/certification/m12-certification-matrix.v1.json') -Raw | ConvertFrom-Json -DateKind String
    $omittedExternalLanes = @($summaryMatrix.profiles.Local.omittedExternalLanes | ForEach-Object { [string]$_ })
    $omittedText = if ($omittedExternalLanes.Count -eq 0) { '(none)' } else { $omittedExternalLanes -join ', ' }
    $localSqlConfigured = -not [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable('SQLOBSERVER_LOCAL_SQLSERVER'))
    if ($localSqlConfigured) {
        Write-Host 'Local SQL Server dev lab: attempted (a failure is fatal; no green result is emitted).'
    } else {
        Write-Host 'Local SQL Server dev lab: omitted because SQLOBSERVER_LOCAL_SQLSERVER is not configured; environment-independent SQL tests ran.'
    }
    Write-Host "Local profile omitted external lanes (from matrix): $omittedText"
    Write-Host 'SqlObserver local validation completed successfully. This result is NOT release certification.'
} else {
    Write-Host 'SqlObserver Release profile checks completed. No release certification claim is made unless the verified evidence manifest covers every producer lane.'
}
