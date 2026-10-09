[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('X64', 'Arm64')][string]$ExpectedArchitecture)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Namespace verification requires Windows.' }
$testProject = Join-Path $PSScriptRoot '../tests/MirrorPulse.CloudFiles.CfSharp.Tests/MirrorPulse.CloudFiles.CfSharp.Tests.csproj'
$evidenceDirectory = Join-Path $PSScriptRoot ('../artifacts/test-results/namespace-' + $ExpectedArchitecture.ToLowerInvariant())
$resultsDirectory = Join-Path $evidenceDirectory ([guid]::NewGuid().ToString('N'))
$previousNative = $env:MIRRORPULSE_NATIVE_TEST
$previousArchitecture = $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE
try {
    & dotnet build $testProject --configuration Release --no-restore -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Namespace verification build failed.' }
    $env:MIRRORPULSE_NATIVE_TEST = '1'
    $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE = $ExpectedArchitecture
    $filter = 'FullyQualifiedName~MirrorPulseNamespaceExecutionSessionTests|FullyQualifiedName~ExplicitPolicyMatchesNtfsAndControlledCreationIsProtectedAtBirth|FullyQualifiedName~NativeNamespaceRoleKeepsAclClosedDuringControlledOperationsAndCfSharpRestart|FullyQualifiedName~NativeStrictTreeAclPreservesLatestBytesHydrationAndConfirmationAcrossRestart|FullyQualifiedName~NativeOwnedPermissionLeaseReconcilesUnrecordedWriteRotationAndRestorationAcrossRestart|FullyQualifiedName~RetainedHandleRejectsExistingAndNewlyObservedAliasesWithoutAclWrites|FullyQualifiedName~NativePlaceholderHardLinkPolicyPreservesUnacceptedBytesAndRejectsExistingAliasesAcrossRestart'
    & dotnet test $testProject --configuration Release --no-build --filter $filter --logger trx --results-directory $resultsDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Namespace verification failed.' }
    & (Join-Path $PSScriptRoot 'verify-test-results.ps1') -Suite namespace -ResultsDirectory $resultsDirectory `
        -OutputPath (Join-Path $evidenceDirectory 'test-evidence.json')
} finally {
    $env:MIRRORPULSE_NATIVE_TEST = $previousNative
    $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE = $previousArchitecture
}
