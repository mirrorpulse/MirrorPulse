[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
if (-not $IsWindows) {
    throw "Native Cloud Files verification requires Windows."
}

$testProject = Join-Path $PSScriptRoot "..\tests\MirrorPulse.CloudFiles.CfSharp.Tests\MirrorPulse.CloudFiles.CfSharp.Tests.csproj"
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceSha = (& git -C $workspace rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceSha -cnotmatch '^[0-9a-f]{40}$' -or ($env:GITHUB_SHA -and $env:GITHUB_SHA -cne $sourceSha)) {
    throw 'Native test inputs require the selected source revision.'
}
. (Join-Path $PSScriptRoot 'native-test-originals.ps1')
$testAssembly = Join-Path $workspace 'tests/MirrorPulse.CloudFiles.CfSharp.Tests/bin/Release/net10.0-windows10.0.26100.0/MirrorPulse.CloudFiles.CfSharp.Tests.dll'
$previous = $env:MIRRORPULSE_NATIVE_TEST
$evidenceDirectory = Join-Path $PSScriptRoot "../artifacts/test-results/native"
$identifier = [guid]::NewGuid().ToString('N')
$resultsDirectory = Join-Path $evidenceDirectory $identifier
$originalsDirectory = Join-Path $workspace ('artifacts/test-results/native-originals/' + $identifier)
try {
    $env:MIRRORPULSE_NATIVE_TEST = "1"
    Invoke-NativeTestWithOriginals -Workspace $workspace -SourceSha $sourceSha -Suite native -TestAssembly $testAssembly `
        -ResultsDirectory $resultsDirectory -OutputDirectory $originalsDirectory -ExpectedTestAssemblySha256 (Get-FileHash -LiteralPath $testAssembly).Hash -Action {
        & dotnet test $testProject --configuration Release --no-build --filter "(FullyQualifiedName~Native|FullyQualifiedName~RetainedHandleRejectsExistingAndNewlyObservedAliasesWithoutAclWrites)&TestCategory!=DeferredP079" `
            --logger trx --results-directory $resultsDirectory
        if ($LASTEXITCODE -ne 0) {
            throw "Native Cloud Files verification failed with exit code $LASTEXITCODE."
        }
        & (Join-Path $PSScriptRoot "verify-test-results.ps1") -Suite native -ResultsDirectory $resultsDirectory `
            -OutputPath (Join-Path $evidenceDirectory "test-evidence.json")
    }
}
finally {
    $env:MIRRORPULSE_NATIVE_TEST = $previous
}
