[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('X64', 'Arm64')][string]$ExpectedArchitecture)

$ErrorActionPreference = 'Stop'
if ($env:MIRRORPULSE_NATIVE_TEST -cne '1' -or -not $IsWindows -or
    [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString() -cne $ExpectedArchitecture) {
    throw 'SDK recreation verification requires an explicitly enabled disposable environment of the requested native Windows architecture.'
}
$testProject = Join-Path $PSScriptRoot '../tests/MirrorPulse.CloudFiles.CfSharp.Tests/MirrorPulse.CloudFiles.CfSharp.Tests.csproj'
$evidenceDirectory = Join-Path $PSScriptRoot ('../artifacts/test-results/cfsharp-recreation-' + $ExpectedArchitecture.ToLowerInvariant())
$resultsDirectory = Join-Path $evidenceDirectory ([guid]::NewGuid().ToString('N'))
$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'test-suites.json') -Raw | ConvertFrom-Json
$required = @($catalog.required.cfsharpRecreation)
if ($required.Count -ne 2 -or @($required | Select-Object -Unique).Count -ne 2 -or
    @($required | Where-Object { $_ -cnotin $catalog.required.native }).Count) {
    throw 'SDK recreation verification requires both distinct mandatory native regressions.'
}
$previousArchitecture = $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE
try {
    & dotnet build $testProject --configuration Release --no-restore -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'SDK recreation verification build failed.' }
    $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE = $ExpectedArchitecture
    $filter = ($required | ForEach-Object { 'FullyQualifiedName=' + $_ }) -join '|'
    & dotnet test $testProject --configuration Release --no-build --filter $filter --logger trx --results-directory $resultsDirectory
    if ($LASTEXITCODE -ne 0) { throw 'SDK recreation verification failed; retain the actual original TRX.' }
    & (Join-Path $PSScriptRoot 'verify-test-results.ps1') -Suite cfsharpRecreation -ResultsDirectory $resultsDirectory `
        -OutputPath (Join-Path $evidenceDirectory 'test-evidence.json')
} finally {
    $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE = $previousArchitecture
}
