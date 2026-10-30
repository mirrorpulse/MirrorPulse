[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Discover', 'Reconcile')][string]$Phase,
    [string]$OriginalsDirectory = 'artifacts/test-results/managed-originals')

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')))
. (Join-Path $PSScriptRoot 'compiled-test-inventory-policy.ps1')
function Get-InventoryPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path, $workspace)
    if (-not $full.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A compiled inventory path escapes its original workspace.'
    }
    for ($current = $full; $current.Length -ge $workspace.Length; $current = [IO.Path]::GetDirectoryName($current)) {
        if ((Test-Path -LiteralPath $current) -and (Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'A compiled inventory path traverses a reparse point.'
        }
    }
    return $full
}
$originals = Get-InventoryPath $OriginalsDirectory
if (-not $originals.StartsWith((Join-Path $workspace 'artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Compiled inventory must remain in the original artifact scope.'
}
$preparedPath = Get-InventoryPath (Join-Path $originals 'prepared.json')
$prepared = Get-Content -LiteralPath $preparedPath -Raw | ConvertFrom-Json
$source = git -C $workspace rev-parse HEAD
if ($LASTEXITCODE -or $prepared.schemaVersion -ne 1 -or $source -cne $prepared.sourceSha -or
    ($env:GITHUB_SHA -and $env:GITHUB_SHA -cne $source) -or $prepared.assemblies.Count -ne 3) { throw 'Require same-source prepared executing assemblies.' }
$names = @('MirrorPulse.Core.Tests', 'MirrorPulse.CloudFiles.CfSharp.Tests', 'MirrorPulse.Cli.Tests')
function Assert-InventoryAssemblies {
    foreach ($assembly in $prepared.assemblies) {
        $path = Get-InventoryPath $assembly.path; $retained = Get-InventoryPath (Join-Path $originals $assembly.archivePath)
        if ([IO.Path]::GetFileNameWithoutExtension($path) -cnotin $names -or
            (Get-FileHash -LiteralPath $path).Hash -cne $assembly.sha256 -or
            (Get-FileHash -LiteralPath $retained).Hash -cne $assembly.sha256) { throw 'An original executing assembly changed.' }
    }
    $actualNames = @($prepared.assemblies | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_.path) })
    if (@($prepared.assemblies.path | Select-Object -Unique).Count -ne 3 -or
        @(Compare-Object @($actualNames | Sort-Object -CaseSensitive) @($names | Sort-Object -CaseSensitive) -CaseSensitive).Count) {
        throw 'The original assembly set is incomplete.'
    }
    $changes = @(git -C $workspace status --porcelain --untracked-files=no)
    if ($LASTEXITCODE -or (($changes.Count -eq 0) -ne $prepared.trackedSourceClean) -or
        ($env:GITHUB_ACTIONS -ceq 'true' -and $changes.Count)) { throw 'The prepared tracked source changed.' }
}
Assert-InventoryAssemblies
$directory = Get-InventoryPath (Join-Path $originals 'discovery')
$inventoryPath = Join-Path $directory 'prepared.json'; $resultPath = Join-Path $directory 'reconciled.json'
if ($Phase -ceq 'Discover') {
    if (Test-Path -LiteralPath $directory) { throw 'Preserve the original compiled discovery.' }
    New-Item -ItemType Directory -Path $directory | Out-Null
    $priorNative = $env:MIRRORPULSE_NATIVE_TEST
    try {
        $env:MIRRORPULSE_NATIVE_TEST = '0'
        $inventories = @(foreach ($assembly in $prepared.assemblies) {
            $name = [IO.Path]::GetFileNameWithoutExtension($assembly.path)
            $container = Join-Path $directory $name; New-Item -ItemType Directory -Path $container | Out-Null
            $trace = Join-Path $container 'discovery.trace.log'; $stdout = Join-Path $container 'discovery.stdout.log'
            $project = Get-InventoryPath $assembly.project
            # Discovery only: keep every TestCase ID and data row. The FQN-only
            # VSTest option deduplicates data rows and cannot prove this inventory.
            & dotnet test $project --configuration Release --no-build --no-restore --list-tests --diag $trace *> $stdout
            if ($LASTEXITCODE) { throw 'Unfiltered compiled discovery failed; original output is retained.' }
            $cases = @(Read-CompiledTestDiscovery -TracePath $trace -AssemblyPath (Get-InventoryPath $assembly.path))
            $files = @(Get-ChildItem -LiteralPath $container -File | ForEach-Object {
                [ordered]@{ path = [IO.Path]::GetRelativePath($directory, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash }
            })
            [ordered]@{ assembly = $assembly.path; assemblySha256 = $assembly.sha256; trace = [IO.Path]::GetRelativePath($directory, $trace).Replace('\', '/')
                cases = $cases; originalFiles = $files }
        })
        Assert-InventoryAssemblies
        [ordered]@{ schemaVersion = 1; sourceSha = $source; preparedSha256 = (Get-FileHash -LiteralPath $preparedPath).Hash
            discoveredUtc = [DateTimeOffset]::UtcNow.ToString('o'); assemblies = $inventories; testExecutionRequested = $false
        } | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $inventoryPath -Encoding utf8
    } finally { $env:MIRRORPULSE_NATIVE_TEST = $priorNative }
    Write-Output 'Retained the complete unfiltered case discovery for the three unchanged executing assemblies.'
    exit 0
}
if (Test-Path -LiteralPath $resultPath) { throw 'Preserve the original compiled inventory reconciliation.' }
$inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
$collectedPath = Get-InventoryPath (Join-Path $originals 'collected.json')
$collected = Get-Content -LiteralPath $collectedPath -Raw | ConvertFrom-Json
$preparedHash = (Get-FileHash -LiteralPath $preparedPath).Hash
if ($inventory.sourceSha -cne $source -or $collected.sourceSha -cne $source -or $inventory.schemaVersion -ne 1 -or
    $inventory.preparedSha256 -cne $preparedHash -or $collected.preparedSha256 -cne $preparedHash -or $inventory.assemblies.Count -ne 3) {
    throw 'Require the same prepared source, compiled discovery and original results.'
}
$rawResults = [Collections.Generic.List[object]]::new()
foreach ($file in $collected.originals) {
    $path = Get-InventoryPath (Join-Path $originals $file.archivePath)
    if ((Get-FileHash -LiteralPath $path).Hash -cne $file.sha256 -or (Get-Item -LiteralPath $path).Length -gt 128MB) { throw 'An original result file changed.' }
    $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 128MB; $reader = [Xml.XmlReader]::Create($path, $settings)
    try { $xml = [Xml.XmlDocument]::new(); $xml.XmlResolver = $null; $xml.Load($reader) } finally { $reader.Dispose() }
    $definitions = @{}
    foreach ($definition in $xml.SelectNodes('/*[local-name()="TestRun"]/*[local-name()="TestDefinitions"]/*[local-name()="UnitTest"]')) {
        $id = $definition.GetAttribute('id')
        if (-not $id -or $definitions.ContainsKey($id)) { throw 'An original definition ID is missing or duplicated.' }
        $definitions.Add($id, $definition)
    }
    foreach ($row in $xml.SelectNodes('/*[local-name()="TestRun"]/*[local-name()="Results"]/*[local-name()="UnitTestResult"]')) {
        $definition = $definitions[$row.GetAttribute('testId')]
        if ($null -eq $definition) { throw 'An original result lost its unique definition.' }
        $method = $definition.SelectSingleNode('*[local-name()="TestMethod"]')
        if ($null -eq $method) { throw 'An original result lost its method.' }
        $assemblyPath = Get-InventoryPath $method.GetAttribute('codeBase')
        if ($assemblyPath -ine (Get-InventoryPath $definition.GetAttribute('storage'))) { throw 'An original result changed its executing assembly.' }
        $rawResults.Add(@{ testId = $row.GetAttribute('testId'); fullyQualifiedName = $method.GetAttribute('className') + '.' + $method.GetAttribute('name')
            displayName = $row.GetAttribute('testName'); source = $assemblyPath })
    }
}
$counts = @(foreach ($assembly in $prepared.assemblies) {
    $selected = @($inventory.assemblies | Where-Object assembly -CEQ $assembly.path)
    if ($selected.Count -ne 1 -or $selected[0].assemblySha256 -cne $assembly.sha256) { throw 'The original compiled assembly inventory changed.' }
    $selected = $selected[0]
    foreach ($file in $selected.originalFiles) {
        if ((Get-FileHash -LiteralPath (Get-InventoryPath (Join-Path $directory $file.path))).Hash -cne $file.sha256) { throw 'An original discovery file changed.' }
    }
    $path = Get-InventoryPath $assembly.path
    $discovered = @(Read-CompiledTestDiscovery -TracePath (Get-InventoryPath (Join-Path $directory $selected.trace)) -AssemblyPath $path)
    Assert-CompiledTestCaseInventory -Discovered $discovered -Actual $selected.cases -AssemblyPath $path
    $rows = @($collected.results | Where-Object assembly -CEQ $assembly.path)
    $actual = @(foreach ($row in $rows) {
        if ($row.assemblySha256 -cne $assembly.sha256) { throw 'An original case lost its executing assembly hash.' }
        @{ testId = $row.testId; fullyQualifiedName = $row.className + '.' + $row.method; displayName = $row.name; source = $path }
    })
    Assert-CompiledTestCaseInventory -Discovered $discovered -Actual $actual -AssemblyPath $path
    $rawCases = @($rawResults | Where-Object { $_.source -ieq $path })
    Assert-CompiledTestCaseInventory -Discovered $discovered -Actual $rawCases -AssemblyPath $path
    [ordered]@{ assembly = $assembly.path; assemblySha256 = $assembly.sha256; discovered = $discovered.Count; originalResults = $rows.Count }
})
if (@($collected.results | Where-Object { $_.assembly -cnotin $prepared.assemblies.path }).Count -or
    ($counts.originalResults | Measure-Object -Sum).Sum -ne $collected.resultCount -or $rawResults.Count -ne $collected.resultCount) {
    throw 'An original result escaped the complete compiled assembly inventory.'
}
Assert-InventoryAssemblies
[ordered]@{ schemaVersion = 1; sourceSha = $source; preparedSha256 = $preparedHash; collectedSha256 = (Get-FileHash -LiteralPath $collectedPath).Hash
    discoverySha256 = (Get-FileHash -LiteralPath $inventoryPath).Hash; reconciledUtc = [DateTimeOffset]::UtcNow.ToString('o'); assemblies = $counts
    allDiscoveredCaseIdsNamesAndMultiplicitiesRetained = $true; originalOutcomesReinterpreted = $false; gateSuccessEstablishedByReconciliation = $false
} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $resultPath -Encoding utf8
Write-Output 'Every discovered compiled case has its exact original result; original failures and skips retain their outcomes.'
