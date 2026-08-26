[CmdletBinding()]
param(
    # Retained for the canonical desktop validation invocation. The repository
    # validation gate does not require Godot; accepting the path keeps the command
    # stable across milestone slices.
    [string] $GodotPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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
        'SqlObserver.Alerting',
        'SqlObserver.Analytics',
        'SqlObserver.Security',
        'SqlObserver.Audit',
        'SqlObserver.Server',
        'SqlObserver.Collector',
        'SqlObserver.Mcp',
        'SqlObserver.McpStdio',
        'SqlObserver.Cli'
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
        'SqlObserver.EndToEndTests'
    )

    foreach ($projectName in $requiredTestProjects) {
        $projectPath = Join-Path $repositoryRoot "tests/$projectName/$projectName.csproj"
        if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
            throw "Required test project is missing: $projectName"
        }
    }

    $sourceProjects = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -Filter '*.csproj' -File)
    $testProjects = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests') -Recurse -Filter '*.csproj' -File)
    if ($sourceProjects.Count -ne 16 -or $testProjects.Count -ne 8) {
        throw "Expected 16 source and 8 test projects; found $($sourceProjects.Count) source and $($testProjects.Count) test projects."
    }

    [xml] $solution = Get-Content -LiteralPath $solutionPath -Raw
    $solutionProjects = @($solution.SelectNodes('//Project'))
    if ($solutionProjects.Count -ne 24) {
        throw "Expected 24 projects in SqlObserver.slnx, found $($solutionProjects.Count)."
    }

    $expectedSolutionProjects = @(
        $requiredSourceProjects | ForEach-Object { "src/$($_)/$($_).csproj" }
        $requiredTestProjects | ForEach-Object { "tests/$($_)/$($_).csproj" }
    ) | Sort-Object -Unique
    $actualSolutionProjects = @(
        $solutionProjects | ForEach-Object { $_.GetAttribute('Path').Replace('\', '/') }
    ) | Sort-Object -Unique
    $solutionDifferences = @(Compare-Object $expectedSolutionProjects $actualSolutionProjects)
    if ($actualSolutionProjects.Count -ne 24 -or $solutionDifferences.Count -ne 0) {
        throw 'SqlObserver.slnx membership differs from the exact required 16 source and 8 test projects.'
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
    if ($packageLocks.Count -ne 24) {
        throw "Expected one NuGet lock file per project (24), found $($packageLocks.Count)."
    }

    $allowedPackagesByProject = @{
        'SqlObserver.Collector' = @(
            'Microsoft.Extensions.Hosting',
            'Microsoft.Extensions.Hosting.WindowsServices'
        )
        'SqlObserver.Infrastructure.PostgreSql' = @('Npgsql')
        'SqlObserver.Infrastructure.SqlServer' = @('Microsoft.Data.SqlClient')
        'SqlObserver.Infrastructure.Windows' = @('System.Diagnostics.EventLog')
        'SqlObserver.Server' = @(
            'Microsoft.AspNetCore.Authentication.Negotiate',
            'Microsoft.Extensions.Hosting.WindowsServices'
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
        'ADR-0013-host-observation-boundary.md', 'ADR-0014-analytics-retention-boundary.md'
    )
    $adrFiles = @(Get-ChildItem -LiteralPath $adrPath -Filter 'ADR-*.md' -File | Sort-Object Name)
    $actualAdrNames = @($adrFiles | ForEach-Object { $_.Name })
    if (($actualAdrNames -join '|') -cne ($expectedAdrNames -join '|')) {
        throw "ADR inventory is not the canonical ordered set. Expected $($expectedAdrNames.Count) files, found $($adrFiles.Count): $($actualAdrNames -join ', ')"
    }

    foreach ($adrFile in $adrFiles) {
        $adrContent = Get-Content -LiteralPath $adrFile.FullName -Raw
        if ($adrContent -notmatch '(?m)^- Status: (Accepted|Proposed)\r?$') {
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

    $placeholderOnlyDirectories = @(
        'database/seeds',
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
    ) | Sort-Object
    if (($collectorSqlFiles.Name -join '|') -cne ($expectedCollectorSqlNames -join '|')) {
        throw 'Collector SQL must contain exactly the reviewed M3/M4/M5/M6/M7/M9/M10 assets.'
    }

    foreach ($collectorSqlFile in $collectorSqlFiles) {
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
            if (@($manifest.requiredPermissionsByMajor.$major).Count -lt 1 -or @($manifest.requiredPermissionsByMajor.$major).Count -gt 2) { throw "M9 manifest permission bounds are invalid: $manifestId/$major" }
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
    $m9MigrationSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'database/migrations/0013_backups_jobs_tempdb_availability_groups.sql') -Raw
    if (([regex]::Matches($runtimeRepositorySource, [regex]::Escape('"' + $m9BundleDigest + '"'))).Count -ne 4 -or
        ([regex]::Matches($m9MigrationSource, [regex]::Escape("'$m9BundleDigest'"))).Count -ne 4) {
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
    if ($readmeStatus -notmatch 'Milestones 0 through (?:8|9|10) are implemented' -or
        $readmeStatus -notmatch '(?i)M5 activity' -or
        $readmeStatus -notmatch '(?i)M6.*(?:system_health|deadlock)' -or
         $readmeStatus -notmatch '(?i)M7.*(?:Query Store|query-performance)' -or
         $readmeStatus -notmatch '(?i)M9.*operational-health' -or
        $readmeStatus -match '(?i)quick start[^\r\n]*(?:through|only).*M4') {
         throw 'README repository status must explicitly identify M0-M10 and the bounded M5-M10 activity/operational-health/analytics scope.'
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
        'SqlObserver.SecurityTests')) {
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
    $forbiddenTargetSecurity = @(
        $productionCSharp | Select-String -Pattern '(?i)TrustServerCertificate\s*=\s*true|User\s*ID\s*=|Password\s*=|SqlException\.Message'
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
        -not $workflow.Contains('runs-on: [self-hosted, Windows, X64, sql-observer]') -or
        -not $workflow.Contains('DOTNET_INSTALL_DIR: ${{ runner.temp }}/dotnet') -or
        -not $workflow.Contains('global-json-file: global.json') -or
        -not $workflow.Contains('run: ./tools/validate.ps1')) {
        throw 'CI must use the labeled local runner, least permissions, the pinned SDK, and the canonical validator.'
    }

    $pinnedActions = [regex]::Matches(
        $workflow,
        '(?m)^\s+uses:\s+\S+@([0-9a-f]{40})(?:\s+#.*)?$')
    if ($pinnedActions.Count -ne 4) {
        throw 'Every third-party GitHub Action must be pinned to an immutable commit SHA.'
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
}

Get-Command dotnet -ErrorAction Stop | Out-Null
Get-Command pnpm -ErrorAction Stop | Out-Null

Assert-RepositoryShape

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

Invoke-CheckedCommand -Executable 'dotnet' -Arguments @(
    'test', $solutionPath,
    '--configuration', 'Release',
    '--no-build',
    '--no-restore'
) -WorkingDirectory $repositoryRoot

Invoke-CheckedCommand -Executable 'pnpm' -Arguments @(
    '--dir', $webPath,
    'install',
    '--frozen-lockfile'
) -WorkingDirectory $repositoryRoot

foreach ($script in @('typecheck', 'test', 'build')) {
    Invoke-CheckedCommand -Executable 'pnpm' -Arguments @(
        '--dir', $webPath,
        'run', $script
    ) -WorkingDirectory $repositoryRoot
}

Write-Host 'SqlObserver repository validation completed successfully.'
