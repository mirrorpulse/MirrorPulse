[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('X64', 'Arm64')][string]$ExpectedArchitecture)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() -cne $ExpectedArchitecture) {
    throw 'SDK Delete verification requires the requested native Windows architecture.'
}
$testProject = Join-Path $PSScriptRoot '../tests/MirrorPulse.CloudFiles.CfSharp.Tests/MirrorPulse.CloudFiles.CfSharp.Tests.csproj'
$evidenceDirectory = Join-Path $PSScriptRoot ('../artifacts/test-results/cfsharp-delete-' + $ExpectedArchitecture.ToLowerInvariant())
$resultsDirectory = Join-Path $evidenceDirectory ([guid]::NewGuid().ToString('N'))
$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'test-suites.json') -Raw | ConvertFrom-Json
$required = @($catalog.required.cfsharpDelete)
if ($required.Count -ne 4 -or @($required | Where-Object { $_ -notin $catalog.required.native -or $_ -notin $catalog.required.namespace }).Count -ne 0) {
    throw 'SDK Delete verification must retain the four mandatory native and namespace regressions.'
}
$previousNative = $env:MIRRORPULSE_NATIVE_TEST
$previousArchitecture = $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE
try {
    & dotnet build $testProject --configuration Release --no-restore -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'SDK Delete verification build failed.' }
    $env:MIRRORPULSE_NATIVE_TEST = '1'
    $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE = $ExpectedArchitecture
    $filter = ($required | ForEach-Object { 'FullyQualifiedName=' + $_ }) -join '|'
    & dotnet test $testProject --configuration Release --no-build --filter $filter --logger trx --results-directory $resultsDirectory
    if ($LASTEXITCODE -ne 0) { throw 'SDK Delete verification failed; retain the original TRX.' }
    & (Join-Path $PSScriptRoot 'verify-test-results.ps1') -Suite cfsharpDelete -ResultsDirectory $resultsDirectory `
        -OutputPath (Join-Path $evidenceDirectory 'test-evidence.json')
} finally {
    $env:MIRRORPULSE_NATIVE_TEST = $previousNative
    $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE = $previousArchitecture
}
