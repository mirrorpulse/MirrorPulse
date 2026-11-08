[CmdletBinding()]
param(
    [switch]$BudgetOnly,
    [ValidateSet('keep-remote', 'access-journal')][string]$Scope = 'keep-remote',
    [ValidateSet('X64', 'Arm64')][string]$ExpectedArchitecture
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'test-execution-policy.ps1')
$catalogPath = Join-Path $PSScriptRoot 'test-suites.json'
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
$selected = @($catalog.required.native | Where-Object { $_ -like '*.NativeKeepRemote*' })
if ($selected.Count -eq 0 -or @($selected | Where-Object { $_ -cnotmatch '^MirrorPulse\.CloudFiles\.CfSharp\.Tests\.[A-Za-z0-9_.]+\.NativeKeepRemote[A-Za-z0-9_]+$' }).Count) {
    throw 'Require the exact declared KeepRemote native methods.'
}
$count = Get-RequiredTestExecutionCount -Catalog $catalog -Names $selected
if ($count -lt 1 -or $count -gt 26) { throw 'Review an empty or expanded diagnostic boundary before execution.' }
$boundary = 'KeepRemote'
$filter = 'FullyQualifiedName~NativeKeepRemote'
if ($Scope -eq 'access-journal') {
    $fixture = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.'
    $selected = @(
        ($fixture + 'NativeKeepRemoteCfSharpAccessOnlyPreservesOriginalJournalAndRealSameLengthWrite')
        ($fixture + 'NativeKeepRemoteUploadPumpResumesAcceptedCommandAfterRootAudit')
    )
    if (@($selected | Where-Object { $_ -cnotin $catalog.required.native }).Count) {
        throw 'The Access journal qualification requires both exact declared methods.'
    }
    $count = Get-RequiredTestExecutionCount -Catalog $catalog -Names $selected
    if ($count -ne 2) { throw 'The Access journal qualification requires exactly two native cases.' }
    $boundary = 'KeepRemoteAccessJournal'
    $filter = ($selected | ForEach-Object { 'FullyQualifiedName=' + $_ }) -join '|'
}
if ($BudgetOnly) {
    # Preserve every fixture's original65-second budget plus collection/cleanup.
    [int][Math]::Ceiling(($count * 65000 + 120000) / 60000.0)
    exit 0
}
if (-not $IsWindows) { throw 'Native diagnostics require Windows.' }
$architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
if ($ExpectedArchitecture -and $architecture -ne $ExpectedArchitecture) {
    throw 'The native test process architecture differs from the selected runner.'
}
$source = (& git -C $workspace rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $source -cnotmatch '^[0-9a-f]{40}$' -or
    ($env:GITHUB_SHA -and $env:GITHUB_SHA -cne $source) -or @(& git -C $workspace status --porcelain --untracked-files=no).Count) {
    throw 'Native diagnostics require the exact clean selected source.'
}
. (Join-Path $PSScriptRoot 'native-test-originals.ps1')
$project = Join-Path $workspace 'tests/MirrorPulse.CloudFiles.CfSharp.Tests/MirrorPulse.CloudFiles.CfSharp.Tests.csproj'
$assembly = Join-Path $workspace 'tests/MirrorPulse.CloudFiles.CfSharp.Tests/bin/Release/net10.0-windows10.0.26100.0/MirrorPulse.CloudFiles.CfSharp.Tests.dll'
$base = Join-Path $workspace 'artifacts/test-results/keep-remote-native'
if (Test-Path -LiteralPath $base) { throw 'Preserve the original diagnostic attempt.' }
$results = Join-Path $base 'results'
$originals = Join-Path $base 'executing-originals'
New-Item -ItemType Directory -Path $base | Out-Null
$scopePath = Join-Path $base 'scope.json'
[ordered]@{ schemaVersion = 1; sourceSha = $source; boundary = $boundary; filter = $filter
    declaredMethods = $selected; expectedActualCases = $count; suitePolicySha256 = (Get-FileHash -LiteralPath $catalogPath).Hash
    processArchitecture = $architecture
    osVersion = [Environment]::OSVersion.Version.ToString(); osProductType = [int](Get-CimInstance Win32_OperatingSystem).ProductType
    trackedSourceClean = $true
    fullNativeSuiteAcceptance = $false; fullS06Accepted = $false
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $scopePath -Encoding utf8
$previousNative = $env:MIRRORPULSE_NATIVE_TEST
$previousArchitecture = $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE
try {
    $env:MIRRORPULSE_NATIVE_TEST = '1'
    $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE = $architecture
    Invoke-NativeTestWithOriginals -Workspace $workspace -SourceSha $source -Suite native -TestAssembly $assembly `
        -ResultsDirectory $results -OutputDirectory $originals -ExpectedTestAssemblySha256 (Get-FileHash -LiteralPath $assembly).Hash -Action {
        & dotnet test $project -c Release --no-build --no-restore --filter $filter --logger trx --results-directory $results
        if ($LASTEXITCODE -ne 0) { throw 'KeepRemote native diagnostics failed; actual SDK receipts and original reports are retained.' }
        $reports = @(Get-ChildItem -LiteralPath $results -File -Filter '*.trx')
        if ($reports.Count -ne 1) { throw 'Require one actual original diagnostic report.' }
        $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null; $settings.MaxCharactersInDocument = 128MB
        $reader = [Xml.XmlReader]::Create($reports[0].FullName, $settings)
        try { $xml = [Xml.XmlDocument]::new(); $xml.XmlResolver = $null; $xml.Load($reader) } finally { $reader.Dispose() }
        $definitions = @{}
        foreach ($definition in $xml.TestRun.TestDefinitions.UnitTest) { $definitions[[string]$definition.id] = $definition.TestMethod }
        $rows = @(foreach ($result in $xml.TestRun.Results.UnitTestResult) {
            $method = $definitions[[string]$result.testId]
            if ($null -eq $method) { throw 'An actual diagnostic result lost its original definition.' }
            @{ name = $method.className + '.' + $method.name; case = [string]$result.testName; outcome = [string]$result.outcome
                testId = [string]$result.testId; executionId = [string]$result.executionId }
        })
        if ($rows.Count -ne $count -or @($rows | Where-Object outcome -CNE 'Passed').Count) { throw 'Require every actual diagnostic row without skips.' }
        $subset = @{ required = @{ native = $selected }; requiredCases = $catalog.requiredCases }
        Assert-RequiredTestResults -Catalog $subset -Suite native -Results $rows
        [ordered]@{ exitCode = 0; sourceSha = $source; boundary = $boundary; actualPassed = $rows.Count
            originalTrxSha256 = (Get-FileHash -LiteralPath $reports[0].FullName).Hash
            scopeSha256 = (Get-FileHash -LiteralPath $scopePath).Hash; fullNativeSuiteAcceptance = $false; fullS06Accepted = $false
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $base 'boundary-result.json') -Encoding utf8
    }
} finally {
    $env:MIRRORPULSE_NATIVE_TEST = $previousNative
    $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE = $previousArchitecture
}
