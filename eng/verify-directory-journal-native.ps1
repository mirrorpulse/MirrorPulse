[CmdletBinding()]
param(
    [switch]$BudgetOnly,
    [ValidateSet('X64', 'Arm64')][string]$ExpectedArchitecture
)

$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'test-execution-policy.ps1')
$catalogPath = Join-Path $PSScriptRoot 'test-suites.json'
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
$fixture = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.'
$selected = @(
    ($fixture + 'NativeDirectoryConfirmationRetainsOriginalBindingAndTypedIdentityAcrossOwners')
    ($fixture + 'NativeProductionDirectoryGroupSettlesOriginalCreateMoveOrRetirementAcrossOwners')
    ($fixture + 'NativeProductionDirectoryGroupRereadsRealLateChildOriginalsBeforeDispatchAcrossOwners')
    ($fixture + 'NativeProductionDirectoryAbsencePreservesALaterBackendFileOrDirectoryAcrossOwners')
    ($fixture + 'NativeProductionDirectoryGroupRecoversAcceptedEffectsBeforeOriginalAckAcrossFreshRoles')
    'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseDirectoryJournalTests.NativeFullRescanAcceptsNestedDirectoriesBeforeSettlingTheirOriginalJournal'
    'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseDirectoryJournalTests.NativeOfflineEmptyAndNestedDirectoryJournalResumesAfterRuntimeRestart'
    'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseJournalUploadCompletionTests.NativeFailureRetryAndSuccessUseCfSharpJournalAcrossRestart'
)
if (@($selected | Where-Object { $_ -cnotin $catalog.required.native }).Count) { throw 'Retain every exact directory and original native regression method.' }
$count = Get-RequiredTestExecutionCount -Catalog $catalog -Names $selected
if ($count -ne 17) { throw 'Require four confirmation cases, ten journal/fresh-role cases and all three original regressions.' }
# Preserve four60-second confirmation fixtures, ten95-second journal fixtures,
# both original45-second directory fixtures and the original completion envelope.
$fixtureBudgetMilliseconds = 4 * 60000 + 10 * 95000 + 2 * 45000 + 65000
if ($BudgetOnly) { [int][Math]::Ceiling(($fixtureBudgetMilliseconds + 120000) / 60000.0); exit 0 }
if (-not $IsWindows) { throw 'Directory native verification requires Windows.' }
$architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
if ($ExpectedArchitecture -and $architecture -cne $ExpectedArchitecture) { throw 'The selected native process architecture differs from its runner.' }
$source = (& git -C $workspace rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $source -cnotmatch '^[0-9a-f]{40}$' -or ($env:GITHUB_SHA -and $env:GITHUB_SHA -cne $source) -or
    @(& git -C $workspace status --porcelain --untracked-files=no).Count) { throw 'Directory verification requires the exact clean selected source.' }
. (Join-Path $PSScriptRoot 'native-test-originals.ps1')
$project = Join-Path $workspace 'tests/MirrorPulse.CloudFiles.CfSharp.Tests/MirrorPulse.CloudFiles.CfSharp.Tests.csproj'
$assembly = Join-Path $workspace 'tests/MirrorPulse.CloudFiles.CfSharp.Tests/bin/Release/net10.0-windows10.0.26100.0/MirrorPulse.CloudFiles.CfSharp.Tests.dll'
$base = Join-Path $workspace 'artifacts/test-results/directory-journal-native'
if (Test-Path -LiteralPath $base) { throw 'Preserve the original directory verification attempt.' }
$results = Join-Path $base 'results'; $originals = Join-Path $base 'executing-originals'
New-Item -ItemType Directory -Path $base | Out-Null
$filter = ($selected | ForEach-Object { 'FullyQualifiedName=' + $_ }) -join '|'
$scopePath = Join-Path $base 'scope.json'
[ordered]@{ schemaVersion = 1; sourceSha = $source; boundary = 'DirectoryJournal'; filter = $filter
    declaredMethods = $selected; expectedActualCases = $count; suitePolicySha256 = (Get-FileHash -LiteralPath $catalogPath).Hash
    processArchitecture = $architecture; osVersion = [Environment]::OSVersion.Version.ToString()
    osProductType = [int](Get-CimInstance Win32_OperatingSystem).ProductType; trackedSourceClean = $true
    fullNativeSuiteAcceptance = $false; fullS06Accepted = $false
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $scopePath -Encoding utf8
$previousNative = $env:MIRRORPULSE_NATIVE_TEST; $previousArchitecture = $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE
try {
    $env:MIRRORPULSE_NATIVE_TEST = '1'; $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE = $architecture
    Invoke-NativeTestWithOriginals -Workspace $workspace -SourceSha $source -Suite native -TestAssembly $assembly `
        -ResultsDirectory $results -OutputDirectory $originals -ExpectedTestAssemblySha256 (Get-FileHash -LiteralPath $assembly).Hash -Action {
        & dotnet test $project -c Release --no-build --no-restore --filter $filter --logger trx --results-directory $results
        if ($LASTEXITCODE -ne 0) { throw 'Directory native verification failed; retain actual original reports and executing inputs.' }
        $reports = @(Get-ChildItem -LiteralPath $results -File -Filter '*.trx')
        if ($reports.Count -ne 1) { throw 'Require one original directory verification report.' }
        $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null; $settings.MaxCharactersInDocument = 128MB
        $reader = [Xml.XmlReader]::Create($reports[0].FullName, $settings)
        try { $xml = [Xml.XmlDocument]::new(); $xml.XmlResolver = $null; $xml.Load($reader) } finally { $reader.Dispose() }
        $definitions = @{}
        foreach ($definition in $xml.TestRun.TestDefinitions.UnitTest) { $definitions[[string]$definition.id] = $definition.TestMethod }
        $rows = @(foreach ($row in $xml.TestRun.Results.UnitTestResult) {
            $method = $definitions[[string]$row.testId]
            if ($null -eq $method) { throw 'An actual directory result lost its original method definition.' }
            @{ name = $method.className + '.' + $method.name; case = [string]$row.testName; outcome = [string]$row.outcome
                testId = [string]$row.testId; executionId = [string]$row.executionId }
        })
        if ($rows.Count -ne $count -or @($rows | Where-Object outcome -CNE 'Passed').Count) { throw 'Require every actual directory result without skips.' }
        Assert-RequiredTestResults -Catalog @{ required = @{ native = $selected }; requiredCases = $catalog.requiredCases } -Suite native -Results $rows
        [ordered]@{ exitCode = 0; sourceSha = $source; boundary = 'DirectoryJournal'; actualPassed = $rows.Count
            originalTrxSha256 = (Get-FileHash -LiteralPath $reports[0].FullName).Hash; scopeSha256 = (Get-FileHash -LiteralPath $scopePath).Hash
            fullNativeSuiteAcceptance = $false; fullS06Accepted = $false
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $base 'boundary-result.json') -Encoding utf8
    }
} finally { $env:MIRRORPULSE_NATIVE_TEST = $previousNative; $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE = $previousArchitecture }
