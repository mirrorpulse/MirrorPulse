$script:NativeOriginalsCollectorPath = $PSCommandPath

function Get-NativeRuntimeInputNames {
    @('MirrorPulse.CloudFiles.CfSharp.Tests.dll', 'MirrorPulse.CloudFiles.CfSharp.Tests.deps.json',
        'MirrorPulse.CloudFiles.CfSharp.Tests.runtimeconfig.json', 'MirrorPulse.Core.dll', 'MirrorPulse.CloudFiles.CfSharp.dll',
        'MirrorPulse.CfSharp.CrashProbe.dll', 'MirrorPulse.CfSharp.CrashProbe.deps.json', 'MirrorPulse.CfSharp.CrashProbe.runtimeconfig.json',
        'CfSharp.dll', 'CfSharp.Native.dll', 'CfSharp.Storage.Sqlite.dll', 'MirrorPulse.Adapter.Sdk.dll')
}

function Assert-NativeProductSource([string]$Path, [string]$SourceSha) {
    if ([IO.Path]::GetFileName($Path) -in @('MirrorPulse.CloudFiles.CfSharp.Tests.dll', 'MirrorPulse.Core.dll',
            'MirrorPulse.CloudFiles.CfSharp.dll', 'MirrorPulse.CfSharp.CrashProbe.dll') -and
        [Diagnostics.FileVersionInfo]::GetVersionInfo($Path).ProductVersion -cnotmatch ('\+' + $SourceSha + '$')) {
        throw 'The actual native product binary does not embed the selected source revision.'
    }
}

function Get-NativeOriginalPath([string]$Path, [string]$Workspace) {
    $root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Workspace))
    $full = [IO.Path]::GetFullPath($Path, $root)
    if (-not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Native originals must remain in their declared workspace.'
    }
    $current = $full
    while ($current.Length -ge $root.Length) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Native originals cannot traverse a reparse point.'
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $full
}

function Copy-NativeOriginal([string]$Path, [string]$Destination, [string]$Workspace, [string]$OutputDirectory) {
    $source = Get-NativeOriginalPath $Path $Workspace
    $target = Get-NativeOriginalPath $Destination $Workspace
    $file = Get-Item -LiteralPath $source -Force
    if ($file.PSIsContainer -or $file.Length -gt 128MB -or (Test-Path -LiteralPath $target)) {
        throw 'Native originals require bounded regular files and unused destinations.'
    }
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    if ((Get-FileHash -LiteralPath $source).Hash -cne $hash -or (Get-FileHash -LiteralPath $target).Hash -cne $hash) {
        throw 'An original changed while being retained.'
    }
    return [ordered]@{ path = $source; archivePath = [IO.Path]::GetRelativePath($OutputDirectory, $target).Replace('\', '/')
        length = $file.Length; sha256 = $hash }
}

function Start-NativeTestOriginals {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Workspace,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceSha,
        [Parameter(Mandatory)][ValidateSet('native', 'namespace', 'namespace-nonadmin')][string]$Suite,
        [Parameter(Mandatory)][string]$TestAssembly,
        [Parameter(Mandatory)][string]$ResultsDirectory,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedTestAssemblySha256
    )
    $assembly = Get-NativeOriginalPath $TestAssembly $Workspace
    $results = Get-NativeOriginalPath $ResultsDirectory $Workspace
    $output = Get-NativeOriginalPath $OutputDirectory $Workspace
    $collector = Get-NativeOriginalPath $script:NativeOriginalsCollectorPath $Workspace
    if ([IO.Path]::GetFileName($assembly) -cne 'MirrorPulse.CloudFiles.CfSharp.Tests.dll' -or
        (Test-Path -LiteralPath $output) -or $results -ieq $output -or
        $output.StartsWith($results + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $results.StartsWith($output + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        ((Test-Path -LiteralPath $results) -and @(Get-ChildItem -LiteralPath $results -Recurse -File -Filter '*.trx').Count)) {
        throw 'Require the exact native test assembly, unused originals and fresh separate results.'
    }
    if ($ExpectedTestAssemblySha256 -and (Get-FileHash -LiteralPath $assembly).Hash -ine $ExpectedTestAssemblySha256) {
        throw 'The copied native test assembly differs from its controller input.'
    }
    $directory = [IO.Path]::GetDirectoryName($assembly)
    $names = @(Get-NativeRuntimeInputNames)
    foreach ($name in $names) {
        $path = Get-NativeOriginalPath (Join-Path $directory $name) $Workspace
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'A native runtime input is missing.' }
        Assert-NativeProductSource $path $SourceSha
    }
    New-Item -ItemType Directory -Path $output | Out-Null
    $inputs = @(foreach ($name in $names) {
        $record = Copy-NativeOriginal (Join-Path $directory $name) (Join-Path $output ('inputs/' + $name)) $Workspace $output
        if ($name.EndsWith('.dll', [StringComparison]::Ordinal)) {
            $record.identity = [Reflection.AssemblyName]::GetAssemblyName($record.path).FullName
            $record.informationalVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($record.path).ProductVersion
        }
        $record
    })
    $scriptInput = Copy-NativeOriginal $collector (Join-Path $output 'policy/native-test-originals.ps1') $Workspace $output
    [ordered]@{ schemaVersion = 1; sourceSha = $SourceSha; suite = $Suite; workspace = [IO.Path]::GetFullPath($Workspace)
        testAssembly = $assembly; resultsDirectory = $results; outputDirectory = $output; inputs = $inputs
        expectedControllerAssemblySha256 = $ExpectedTestAssemblySha256; collector = $scriptInput
        preparedUtc = [DateTimeOffset]::UtcNow.ToString('o'); testExecutionRequestedByCollector = $false } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'prepared.json') -Encoding utf8
}

function Complete-NativeTestOriginals {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Workspace,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceSha,
        [Parameter(Mandatory)][string]$OutputDirectory
    )
    $output = Get-NativeOriginalPath $OutputDirectory $Workspace
    $preparedPath = Get-NativeOriginalPath (Join-Path $output 'prepared.json') $Workspace
    $preparedHash = (Get-FileHash -LiteralPath $preparedPath).Hash
    $prepared = Get-Content -LiteralPath $preparedPath -Raw | ConvertFrom-Json
    if ($prepared.schemaVersion -ne 1 -or $prepared.sourceSha -cne $SourceSha -or
        $prepared.outputDirectory -ine $output -or $prepared.workspace -ine [IO.Path]::GetFullPath($Workspace) -or
        $prepared.inputs.Count -ne 12 -or (Test-Path -LiteralPath (Join-Path $output 'collected.json'))) {
        throw 'Require the original exact-source native preparation and unused collection.'
    }
    $assembly = Get-NativeOriginalPath $prepared.testAssembly $Workspace
    $directory = [IO.Path]::GetDirectoryName($assembly)
    $names = @(Get-NativeRuntimeInputNames)
    if ([IO.Path]::GetFileName($assembly) -cne $names[0] -or $prepared.suite -cnotin @('native', 'namespace', 'namespace-nonadmin') -or
        $prepared.collector.path -ine $script:NativeOriginalsCollectorPath -or
        $prepared.collector.archivePath -cne 'policy/native-test-originals.ps1' -or
        $prepared.testExecutionRequestedByCollector -ne $false) { throw 'Native preparation inputs or collector were substituted.' }
    foreach ($name in $names) {
        $binding = @($prepared.inputs | Where-Object { $_.path -ieq (Join-Path $directory $name) -and
            $_.archivePath -ceq ('inputs/' + $name) })
        if ($binding.Count -ne 1) { throw 'A native runtime input was omitted, duplicated or replaced.' }
        Assert-NativeProductSource $binding[0].path $SourceSha
    }
    if ($prepared.expectedControllerAssemblySha256 -and (Get-FileHash -LiteralPath $assembly).Hash -ine $prepared.expectedControllerAssemblySha256) {
        throw 'The native test assembly lost its original controller binding.'
    }
    foreach ($binding in @($prepared.inputs) + @($prepared.collector)) {
        foreach ($path in @($binding.path, (Join-Path $output $binding.archivePath))) {
            $regular = Get-NativeOriginalPath $path $Workspace
            if ((Get-FileHash -LiteralPath $regular).Hash -cne $binding.sha256 -or (Get-Item -LiteralPath $regular).Length -ne $binding.length) {
                throw 'A native input or retained original changed after preparation.'
            }
        }
    }
    $resultsDirectory = Get-NativeOriginalPath $prepared.resultsDirectory $Workspace
    $files = @(Get-ChildItem -LiteralPath $resultsDirectory -File -Recurse -Filter '*.trx')
    if ($files.Count -lt 1 -or $files.Count -gt 32) { throw 'Require bounded actual native TRX originals.' }
    $originals = @(); $rows = [Collections.Generic.List[object]]::new()
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($resultsDirectory, $file.FullName)
        $original = Copy-NativeOriginal $file.FullName (Join-Path $output ('results/' + $relative)) $Workspace $output
        $originals += $original
        $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null; $settings.MaxCharactersInDocument = 128MB
        $reader = [Xml.XmlReader]::Create((Join-Path $output $original.archivePath), $settings)
        try { $xml = [Xml.XmlDocument]::new(); $xml.XmlResolver = $null; $xml.Load($reader) }
        finally { $reader.Dispose() }
        $started = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse([string]$xml.TestRun.Times.start, [ref]$started) -or
            $started -lt [DateTimeOffset]::Parse($prepared.preparedUtc)) { throw 'A native TRX predates its original input preparation.' }
        $definitions = @{}
        foreach ($definition in $xml.TestRun.TestDefinitions.UnitTest) {
            $id = [string]$definition.id
            if ($definitions.ContainsKey($id) -or $definition.TestMethod.codeBase -ine $prepared.testAssembly) {
                throw 'A native test definition is duplicated or names another executing assembly.'
            }
            $definitions[$id] = $definition.TestMethod
        }
        foreach ($result in $xml.TestRun.Results.UnitTestResult) {
            $method = $definitions[[string]$result.testId]
            if ($null -eq $method -or $result.outcome -cnotin @('Passed', 'Failed', 'NotExecuted')) {
                throw 'A native original outcome or definition is missing.'
            }
            $rows.Add([ordered]@{ testId = [string]$result.testId; executionId = [string]$result.executionId
                className = [string]$method.className; method = [string]$method.name
                name = [string]$result.testName; outcome = [string]$result.outcome; codeBase = [string]$method.codeBase
                originalTrx = $relative })
        }
    }
    if ($rows.Count -eq 0 -or (Get-FileHash -LiteralPath $preparedPath).Hash -cne $preparedHash) {
        throw 'Native collection requires actual cases and unchanged original preparation.'
    }
    foreach ($binding in @($prepared.inputs) + @($prepared.collector)) {
        foreach ($path in @($binding.path, (Join-Path $output $binding.archivePath))) {
            $regular = Get-NativeOriginalPath $path $Workspace
            if ((Get-FileHash -LiteralPath $regular).Hash -cne $binding.sha256) { throw 'A native input changed during collection.' }
        }
    }
    [ordered]@{ schemaVersion = 1; sourceSha = $SourceSha; suite = $prepared.suite; preparedSha256 = $preparedHash
        testAssembly = $prepared.testAssembly; actualInputBytesUnchanged = $true; originals = $originals; results = @($rows)
        resultCount = $rows.Count; gateSuccessEstablishedByCollection = $false; collectedUtc = [DateTimeOffset]::UtcNow.ToString('o') } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'collected.json') -Encoding utf8
}

function Invoke-NativeTestWithOriginals {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Workspace,
        [Parameter(Mandatory)][string]$SourceSha,
        [Parameter(Mandatory)][string]$Suite,
        [Parameter(Mandatory)][string]$TestAssembly,
        [Parameter(Mandatory)][string]$ResultsDirectory,
        [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$ExpectedTestAssemblySha256,
        [Parameter(Mandatory)][scriptblock]$Action
    )
    Start-NativeTestOriginals -Workspace $Workspace -SourceSha $SourceSha -Suite $Suite -TestAssembly $TestAssembly `
        -ResultsDirectory $ResultsDirectory -OutputDirectory $OutputDirectory -ExpectedTestAssemblySha256 $ExpectedTestAssemblySha256
    $primary = $null
    try { & $Action }
    catch { $primary = $_.Exception; throw }
    finally {
        try { Complete-NativeTestOriginals -Workspace $Workspace -SourceSha $SourceSha -OutputDirectory $OutputDirectory }
        catch {
            if ($null -ne $primary) {
                throw [AggregateException]::new('Native verification and original collection both failed.', [Exception[]]@($primary, $_.Exception))
            }
            throw
        }
    }
}
