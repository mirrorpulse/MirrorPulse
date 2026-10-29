[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Prepare', 'Collect')][string]$Phase,
    [string]$ResultsDirectory = 'artifacts/test-results/managed',
    [string]$OutputDirectory = 'artifacts/test-results/managed-originals'
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')))
$comparison = [StringComparison]::OrdinalIgnoreCase

function Get-ContainedPath([string]$Path, [string]$Parent) {
    $full = [IO.Path]::GetFullPath($Path, $workspace)
    if (-not $full.StartsWith($Parent + [IO.Path]::DirectorySeparatorChar, $comparison)) {
        throw 'Managed test originals must remain in their declared workspace scope.'
    }
    $current = $full
    while ($current.Length -ge $workspace.Length) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Managed test originals cannot traverse a reparse point.'
            }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $full
}

function Get-RelativePath([string]$Path) {
    return [IO.Path]::GetRelativePath($workspace, $Path).Replace('\', '/')
}

function Read-Xml([string]$Path) {
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 128MB
    $reader = [Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return ,$document
    } finally { $reader.Dispose() }
}

function Copy-Original([string]$Source, [string]$Destination) {
    $sourcePath = Get-ContainedPath $Source $workspace
    $destinationPath = Get-ContainedPath $Destination $output
    $file = Get-Item -LiteralPath $sourcePath -Force
    if ($file.PSIsContainer -or $file.Length -gt 128MB -or (Test-Path -LiteralPath $destinationPath)) {
        throw 'An original must be a bounded file with an unused destination.'
    }
    $before = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destinationPath)) -Force | Out-Null
    Copy-Item -LiteralPath $sourcePath -Destination $destinationPath
    if ((Get-FileHash -LiteralPath $destinationPath).Hash -cne $before -or
        (Get-FileHash -LiteralPath $sourcePath).Hash -cne $before) {
        throw 'An original changed while being retained.'
    }
    return [ordered]@{ path = Get-RelativePath $sourcePath; archivePath = [IO.Path]::GetRelativePath($output, $destinationPath).Replace('\', '/'); length = $file.Length; sha256 = $before }
}

$artifacts = Join-Path $workspace 'artifacts'
$results = Get-ContainedPath $ResultsDirectory $artifacts
$output = Get-ContainedPath $OutputDirectory $artifacts
if ($output -eq $results -or $output.StartsWith($results + [IO.Path]::DirectorySeparatorChar, $comparison) -or
    $results.StartsWith($output + [IO.Path]::DirectorySeparatorChar, $comparison)) {
    throw 'Originals and live test results must have separate scopes.'
}
$source = & git -C $workspace rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $source -cnotmatch '^[0-9a-f]{40}$') { throw 'A source revision is required.' }
$top = & git -C $workspace rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($top) -ine $workspace) { throw 'The script must own its Git workspace.' }
if ($env:GITHUB_SHA -and $env:GITHUB_SHA -cne $source) { throw 'The test workspace differs from the CI source revision.' }
$changes = @(& git -C $workspace status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0) { throw 'The tracked source state could not be inspected.' }
$clean = $changes.Count -eq 0
if ($env:GITHUB_ACTIONS -ceq 'true' -and -not $clean) { throw 'CI test originals require unchanged tracked source.' }
$preparePath = Join-Path $output 'prepared.json'
$completedPath = Join-Path $output 'collected.json'

if ($Phase -ceq 'Prepare') {
    if (Test-Path -LiteralPath $output) { throw 'Preserve the existing managed test originals.' }
    if ((Test-Path -LiteralPath $results) -and @(Get-ChildItem -LiteralPath $results -Force).Count) {
        throw 'Managed test preparation requires an empty results directory.'
    }
    New-Item -ItemType Directory -Path $output | Out-Null
    $assemblies = @(
        foreach ($name in @('MirrorPulse.Core.Tests', 'MirrorPulse.CloudFiles.CfSharp.Tests', 'MirrorPulse.Cli.Tests')) {
            $projectPath = Get-ContainedPath ('tests/' + $name + '/' + $name + '.csproj') $workspace
            $project = Read-Xml $projectPath
            $frameworks = @($project.SelectNodes('/Project/PropertyGroup/TargetFramework'))
            if ($frameworks.Count -ne 1 -or $frameworks[0].InnerText -cnotmatch '^net[0-9.]+-windows[0-9.]+$') {
                throw 'Each managed test project requires one explicit Windows target framework.'
            }
            $assemblyPath = Get-ContainedPath ('tests/' + $name + '/bin/Release/' + $frameworks[0].InnerText + '/' + $name + '.dll') $workspace
            $original = Copy-Original $assemblyPath (Join-Path $output ('assemblies/' + $name + '.dll'))
            # Read metadata without loading or executing the test assembly.
            $identity = [Reflection.AssemblyName]::GetAssemblyName($assemblyPath)
            if ($identity.Name -cne $name) { throw 'The prepared assembly does not belong to its test project.' }
            $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($assemblyPath)
            $original.identity = $identity.FullName
            $original.informationalVersion = $version.ProductVersion
            $original.project = Get-RelativePath $projectPath
            $original
        }
    )
    $policy = Copy-Original (Join-Path $PSScriptRoot 'test-suites.json') (Join-Path $output 'policy/test-suites.json')
    [ordered]@{
        schemaVersion = 1; sourceSha = $source; trackedSourceClean = $clean
        expectedCommand = 'dotnet test MirrorPulse.sln --configuration Release --no-build --logger trx --results-directory artifacts/test-results/managed'
        preparedUtc = [DateTimeOffset]::UtcNow.ToString('o'); assemblies = $assemblies; policy = $policy
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $preparePath -Encoding utf8
    Write-Output 'Retained the three original managed test assemblies and the same-source test policy before execution.'
    exit 0
}

if (-not (Test-Path -LiteralPath $preparePath) -or (Test-Path -LiteralPath $completedPath)) {
    throw 'Collect requires an original preparation and an unused completion receipt.'
}
$prepared = Get-Content -LiteralPath $preparePath -Raw | ConvertFrom-Json
if ($prepared.schemaVersion -ne 1 -or $prepared.sourceSha -cne $source -or $prepared.trackedSourceClean -ne $clean -or $prepared.assemblies.Count -ne 3) {
    throw 'The test preparation belongs to another source state.'
}
$bindings = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($assembly in $prepared.assemblies) {
    $path = Get-ContainedPath $assembly.path $workspace
    $retained = Get-ContainedPath (Join-Path $output $assembly.archivePath) $output
    if ((Get-FileHash -LiteralPath $path).Hash -cne $assembly.sha256 -or
        (Get-FileHash -LiteralPath $retained).Hash -cne $assembly.sha256 -or
        (Get-Item -LiteralPath $path).Length -ne $assembly.length -or
        [Reflection.AssemblyName]::GetAssemblyName($retained).FullName -cne $assembly.identity) {
        throw 'A prepared test assembly changed before evidence collection.'
    }
    $bindings.Add($path, $assembly)
}
$policyPath = Get-ContainedPath $prepared.policy.path $workspace
$retainedPolicy = Get-ContainedPath (Join-Path $output $prepared.policy.archivePath) $output
if ((Get-FileHash -LiteralPath $policyPath).Hash -cne $prepared.policy.sha256 -or
    (Get-FileHash -LiteralPath $retainedPolicy).Hash -cne $prepared.policy.sha256) {
    throw 'The test policy changed during execution.'
}
# Traverse only this declared results directory. Check each directory before entering it.
$queue = [Collections.Generic.Queue[string]]::new()
$queue.Enqueue($results)
$files = [Collections.Generic.List[string]]::new()
$visited = 0
while ($queue.Count) {
    $directory = Get-ContainedPath $queue.Dequeue() $artifacts
    if (++$visited -gt 128) { throw 'The managed result directory bound was exceeded.' }
    foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
        $path = Get-ContainedPath $item.FullName $results
        if ($item.PSIsContainer) { $queue.Enqueue($path) }
        elseif ($item.Extension -ieq '.trx') { $files.Add($path) }
    }
}
if ($files.Count -lt 1 -or $files.Count -gt 16) { throw 'Expected between one and sixteen original managed TRX files.' }
$inventory = [Collections.Generic.List[object]]::new()
$originals = @(
    foreach ($file in $files | Sort-Object) {
        $relative = [IO.Path]::GetRelativePath($results, $file)
        $original = Copy-Original $file (Join-Path $output ('results/' + $relative))
        $xml = Read-Xml (Join-Path $output $original.archivePath)
        $times = $xml.SelectSingleNode('/*[local-name()="TestRun"]/*[local-name()="Times"]')
        if ($null -eq $times) { throw 'An original TRX must retain its run times.' }
        $started = [DateTimeOffset]::Parse($times.GetAttribute('start'), [Globalization.CultureInfo]::InvariantCulture)
        $finished = [DateTimeOffset]::Parse($times.GetAttribute('finish'), [Globalization.CultureInfo]::InvariantCulture)
        if ($started -lt [DateTimeOffset]::Parse($prepared.preparedUtc, [Globalization.CultureInfo]::InvariantCulture) -or $finished -lt $started) {
            throw 'An original TRX predates the prepared assemblies or has invalid run times.'
        }
        $definitions = @{}
        foreach ($definition in $xml.SelectNodes('/*[local-name()="TestRun"]/*[local-name()="TestDefinitions"]/*[local-name()="UnitTest"]')) {
            $id = $definition.GetAttribute('id')
            if (-not $id -or $definitions.ContainsKey($id)) { throw 'A TRX definition ID is absent or ambiguous.' }
            $definitions.Add($id, $definition)
        }
        foreach ($result in $xml.SelectNodes('/*[local-name()="TestRun"]/*[local-name()="Results"]/*[local-name()="UnitTestResult"]')) {
            $definition = $definitions[$result.GetAttribute('testId')]
            if ($null -eq $definition) { throw 'An original result has no unique test definition.' }
            $method = $definition.SelectSingleNode('*[local-name()="TestMethod"]')
            if ($null -eq $method) { throw 'An original result has no test method.' }
            $codeBase = Get-ContainedPath $method.GetAttribute('codeBase') $workspace
            $storage = Get-ContainedPath $definition.GetAttribute('storage') $workspace
            if ($codeBase -ine $storage -or -not $bindings.ContainsKey($codeBase)) {
                throw 'An original test source does not match a prepared executing assembly.'
            }
            $assembly = $bindings[$codeBase]
            $inventory.Add([ordered]@{
                trx = $original.archivePath; testId = $result.GetAttribute('testId'); executionId = $result.GetAttribute('executionId')
                name = $result.GetAttribute('testName'); className = $method.GetAttribute('className'); method = $method.GetAttribute('name')
                outcome = $result.GetAttribute('outcome'); assembly = $assembly.path; assemblySha256 = $assembly.sha256
            })
            if ($inventory.Count -gt 100000) { throw 'The managed result count bound was exceeded.' }
        }
        $original
    }
)
if ($inventory.Count -eq 0) { throw 'No original managed cases were retained.' }
$summaryPath = Join-Path $results 'test-evidence.json'
$summary = if (Test-Path -LiteralPath $summaryPath) {
    Copy-Original $summaryPath (Join-Path $output 'results/test-evidence.json')
} else { $null }
# The original gate may fail before producing its summary. Retain failed/skipped
# outcomes verbatim; collection must never reinterpret them as a passing gate.
[ordered]@{
    schemaVersion = 1; sourceSha = $source; trackedSourceClean = $clean
    collectedUtc = [DateTimeOffset]::UtcNow.ToString('o'); preparedSha256 = (Get-FileHash -LiteralPath $preparePath).Hash
    originals = $originals; originalSummary = $summary; resultCount = $inventory.Count
    outcomes = @($inventory | Group-Object outcome | Sort-Object Name | ForEach-Object { [ordered]@{ outcome = $_.Name; count = $_.Count } })
    results = $inventory.ToArray(); gateSuccessEstablishedByCollection = $false
} | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $completedPath -Encoding utf8
Write-Output "Retained $($files.Count) original managed TRX files and $($inventory.Count) outcomes bound to the unchanged prepared assemblies."
