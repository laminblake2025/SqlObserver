[CmdletBinding()]
param()

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
        'docs/milestones/M2-postgresql-repository.md'
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
        'SqlObserver.Server' = @('Microsoft.Extensions.Hosting.WindowsServices')
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
    $adrFiles = @(Get-ChildItem -LiteralPath $adrPath -Filter 'ADR-*.md' -File)
    if ($adrFiles.Count -ne 12) {
        throw "Expected exactly 12 ADR files, found $($adrFiles.Count)."
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
        'collectors/sql',
        'collectors/manifests',
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
        if (-not $migrationSql.Contains($requiredSqlInvariant, [StringComparison]::OrdinalIgnoreCase)) {
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

    $collectorImplementationPath = Join-Path $repositoryRoot 'src/SqlObserver.Collectors'
    $unexpectedCollectorSource = @(
        Get-ChildItem -LiteralPath $collectorImplementationPath -File |
            Where-Object { $_.Name -notin @('AssemblyMarker.cs', 'packages.lock.json', 'SqlObserver.Collectors.csproj') }
    )
    if ($unexpectedCollectorSource.Count -ne 0) {
        throw 'Production collector source is outside the authorized first-assignment scope.'
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
