[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = (& git -C $workspace rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -or $source -cnotmatch '^[0-9a-f]{40}$' -or
    ($env:GITHUB_SHA -and $env:GITHUB_SHA -cne $source) -or @(& git -C $workspace status --porcelain --untracked-files=no).Count) {
    throw 'The signed post-move probe requires the exact clean selected source.'
}
$package = [IO.Path]::GetFullPath($PackagePath)
if (-not $package.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath ($package + '.signature.json') -PathType Leaf)) {
    throw 'Require one frozen official package and signature inside the workspace.'
}
$base = Join-Path $workspace 'artifacts/test-results/signed-post-move'
if (Test-Path -LiteralPath $base) { throw 'Preserve the original signed post-move attempt.' }
$results = Join-Path $base 'results'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$project = Join-Path $workspace 'tests/MirrorPulse.CloudFiles.CfSharp.Tests/MirrorPulse.CloudFiles.CfSharp.Tests.csproj'
$assembly = Join-Path $workspace 'tests/MirrorPulse.CloudFiles.CfSharp.Tests/bin/Release/net10.0-windows10.0.26100.0/MirrorPulse.CloudFiles.CfSharp.Tests.dll'
dotnet build $project -c Release --no-restore
if ($LASTEXITCODE) { throw 'The current signed post-move test assembly did not build.' }
$priorNative = $env:MIRRORPULSE_NATIVE_TEST
$priorArchitecture = $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE
$priorPackage = $env:MP_SIGNED_LOCAL_PACKAGE
try {
    $env:MIRRORPULSE_NATIVE_TEST = '1'
    $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    $env:MP_SIGNED_LOCAL_PACKAGE = $package
    . (Join-Path $PSScriptRoot 'native-test-originals.ps1')
    $method = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.SignedLocalKeepRemoteAfterAcknowledgedMovePreservesActualVersionsAndOriginalJournalAcrossOwners'
    Invoke-NativeTestWithOriginals -Workspace $workspace -SourceSha $source -Suite native -TestAssembly $assembly `
        -ResultsDirectory $results -OutputDirectory (Join-Path $base 'executing-originals') `
        -ExpectedTestAssemblySha256 (Get-FileHash -LiteralPath $assembly).Hash -Action {
        dotnet test $project -c Release --no-build --no-restore --filter ('FullyQualifiedName=' + $method) --logger trx --results-directory $results
        if ($LASTEXITCODE) { throw 'The actual signed post-move case failed; retain the original report.' }
        $reports = @(Get-ChildItem -LiteralPath $results -Filter '*.trx' -File)
        if ($reports.Count -ne 1) { throw 'Require the one original signed post-move report.' }
        [xml]$report = Get-Content -LiteralPath $reports[0].FullName -Raw
        $rows = @($report.TestRun.Results.UnitTestResult)
        if ($rows.Count -ne 1 -or $rows[0].outcome -cne 'Passed' -or
            $rows[0].testName -cne 'SignedLocalKeepRemoteAfterAcknowledgedMovePreservesActualVersionsAndOriginalJournalAcrossOwners') {
            throw 'The exact signed post-move case must execute without skips.'
        }
        $facts = @(([string]$rows[0].Output.StdOut -split "`r?`n") | Where-Object { $_.StartsWith('SignedPostMove ', [StringComparison]::Ordinal) })
        if ($facts.Count -ne 1) { throw 'Require one actual signed Worker effect observation.' }
        $facts = $facts[0].Substring('SignedPostMove '.Length) | ConvertFrom-Json
        if (-not $facts.actualSignedWorker -or -not $facts.fixtureOwnedOriginalEffects -or
            $facts.defaultHostProcessDispatchedOriginalEffects -ne $false -or $facts.acknowledgedOriginalCount -ne 2 -or
            @($facts.originalOperationIds).Count -ne 2 -or @($facts.originalOperationIds | Sort-Object -Unique).Count -ne 2 -or
            $facts.packageSha256 -cne (Get-FileHash -LiteralPath $package).Hash -or
            $facts.acceptedMoveRevisionSha256 -cnotmatch '^[A-F0-9]{64}$' -or $facts.selectedRevisionSha256 -cnotmatch '^[A-F0-9]{64}$' -or
            $facts.acceptedMoveRevisionSha256 -ceq $facts.selectedRevisionSha256) { throw 'Actual signed versions, original IDs or package provenance differ.' }
        [ordered]@{ exitCode = 0; sourceSha = $source; actualPassed = 1; actualSignedWorker = $true
            fixtureOwnedOriginalEffects = $true; defaultHostProcessDispatchedOriginalEffects = $false
            packageSha256 = $facts.packageSha256; signatureSha256 = (Get-FileHash -LiteralPath ($package + '.signature.json')).Hash
            originalTrxSha256 = (Get-FileHash -LiteralPath $reports[0].FullName).Hash
            originalOperationIds = $facts.originalOperationIds; commandId = $facts.commandId
            acceptedMoveRevisionSha256 = $facts.acceptedMoveRevisionSha256; selectedRevisionSha256 = $facts.selectedRevisionSha256
            fullS06Accepted = $false } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $base 'signed-result.json') -Encoding utf8
    }
} finally {
    $env:MIRRORPULSE_NATIVE_TEST = $priorNative
    $env:MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE = $priorArchitecture
    $env:MP_SIGNED_LOCAL_PACKAGE = $priorPackage
}
