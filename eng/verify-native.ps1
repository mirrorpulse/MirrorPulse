[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
if (-not $IsWindows) {
    throw "Native Cloud Files verification requires Windows."
}

$testProject = Join-Path $PSScriptRoot "..\tests\MirrorPulse.CloudFiles.CfSharp.Tests\MirrorPulse.CloudFiles.CfSharp.Tests.csproj"
$previous = $env:MIRRORPULSE_NATIVE_TEST
$evidenceDirectory = Join-Path $PSScriptRoot "../artifacts/test-results/native"
$resultsDirectory = Join-Path $evidenceDirectory ([guid]::NewGuid().ToString("N"))
try {
    $env:MIRRORPULSE_NATIVE_TEST = "1"
    & dotnet test $testProject --configuration Release --no-build --filter "FullyQualifiedName~Native|FullyQualifiedName~RetainedHandleRejectsExistingAndNewlyObservedAliasesWithoutAclWrites" `
        --logger trx --results-directory $resultsDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Native Cloud Files verification failed with exit code $LASTEXITCODE."
    }
    & (Join-Path $PSScriptRoot "verify-test-results.ps1") -Suite native -ResultsDirectory $resultsDirectory `
        -OutputPath (Join-Path $evidenceDirectory "test-evidence.json")
}
finally {
    $env:MIRRORPULSE_NATIVE_TEST = $previous
}
