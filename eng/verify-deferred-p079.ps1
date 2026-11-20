[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('X64', 'Arm64')][string]$ExpectedArchitecture)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Retained P079 compatibility verification requires Windows.' }
$testProject = Join-Path $PSScriptRoot '../tests/MirrorPulse.CloudFiles.CfSharp.Tests/MirrorPulse.CloudFiles.CfSharp.Tests.csproj'
$evidenceDirectory = Join-Path $PSScriptRoot ('../artifacts/test-results/deferred-p079-' + $ExpectedArchitecture.ToLowerInvariant())
$resultsDirectory = Join-Path $evidenceDirectory ([guid]::NewGuid().ToString('N'))
$previousNative = $env:MIRRORPULSE_NATIVE_TEST
$previousArchitecture = $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE
try {
    $env:MIRRORPULSE_NATIVE_TEST = '1'
    $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE = $ExpectedArchitecture
    $catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'test-suites.json') -Raw | ConvertFrom-Json
    $required = @($catalog.required.deferredP079)
    if ($required.Count -ne 5 -or @($required | Select-Object -Unique).Count -ne 5 -or
        @($required | Where-Object { $_ -cin @($catalog.required.native) -or $_ -cin @($catalog.required.namespace) }).Count) {
        throw 'P079 compatibility must remain an explicit separate five-method selection.'
    }
    $filter = ($required | ForEach-Object { 'FullyQualifiedName=' + $_ }) -join '|'
    & dotnet test $testProject --configuration Release --no-build --filter $filter --logger trx --results-directory $resultsDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Retained P079 compatibility has a negative result; preserve it separately from S06 acceptance.' }
    & (Join-Path $PSScriptRoot 'verify-test-results.ps1') -Suite deferredP079 -ResultsDirectory $resultsDirectory `
        -OutputPath (Join-Path $evidenceDirectory 'test-evidence.json')
} finally {
    $env:MIRRORPULSE_NATIVE_TEST = $previousNative
    $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE = $previousArchitecture
}
