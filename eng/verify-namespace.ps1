[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('X64', 'Arm64')][string]$ExpectedArchitecture)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Namespace verification requires Windows.' }
$testProject = Join-Path $PSScriptRoot '../tests/MirrorPulse.CloudFiles.CfSharp.Tests/MirrorPulse.CloudFiles.CfSharp.Tests.csproj'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceSha = (& git -C $workspace rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceSha -cnotmatch '^[0-9a-f]{40}$' -or ($env:GITHUB_SHA -and $env:GITHUB_SHA -cne $sourceSha)) {
    throw 'Namespace test inputs require the selected source revision.'
}
. (Join-Path $PSScriptRoot 'native-test-originals.ps1')
$testAssembly = Join-Path $workspace 'tests/MirrorPulse.CloudFiles.CfSharp.Tests/bin/Release/net10.0-windows10.0.26100.0/MirrorPulse.CloudFiles.CfSharp.Tests.dll'
$evidenceDirectory = Join-Path $PSScriptRoot ('../artifacts/test-results/namespace-' + $ExpectedArchitecture.ToLowerInvariant())
$identifier = [guid]::NewGuid().ToString('N')
$resultsDirectory = Join-Path $evidenceDirectory $identifier
$originalsDirectory = Join-Path $workspace ('artifacts/test-results/namespace-' + $ExpectedArchitecture.ToLowerInvariant() + '-originals/' + $identifier)
$previousNative = $env:MIRRORPULSE_NATIVE_TEST
$previousArchitecture = $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE
try {
    & dotnet build $testProject --configuration Release --no-restore -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Namespace verification build failed.' }
    $env:MIRRORPULSE_NATIVE_TEST = '1'
    $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE = $ExpectedArchitecture
    $catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'test-suites.json') -Raw | ConvertFrom-Json
    $filter = (@($catalog.required.namespace) | ForEach-Object { 'FullyQualifiedName=' + $_ }) -join '|'
    Invoke-NativeTestWithOriginals -Workspace $workspace -SourceSha $sourceSha -Suite namespace -TestAssembly $testAssembly `
        -ResultsDirectory $resultsDirectory -OutputDirectory $originalsDirectory -ExpectedTestAssemblySha256 (Get-FileHash -LiteralPath $testAssembly).Hash -Action {
        & dotnet test $testProject --configuration Release --no-build --filter $filter --logger trx --results-directory $resultsDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Namespace verification failed.' }
        & (Join-Path $PSScriptRoot 'verify-test-results.ps1') -Suite namespace -ResultsDirectory $resultsDirectory `
            -OutputPath (Join-Path $evidenceDirectory 'test-evidence.json')
    }
} finally {
    $env:MIRRORPULSE_NATIVE_TEST = $previousNative
    $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE = $previousArchitecture
}
