[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ExpectedSourceSha,
    [switch]$RequireNative,
    [switch]$RequireInstalled,
    [switch]$RequireNamespace,
    [switch]$RequireNonAdminNamespace,
    [switch]$RequireOfficialCandidate
)

$ErrorActionPreference = "Stop"
$expected = @{
    "build-and-test" = @{ runtime="win-x64";suite="managed" }
    "official-package-arm64" = @{ runtime="win-arm64";suite="signed" }
    "official-adapters" = @{ runtime="win-x64";suite="official" }
}
$manifests = @(Get-ChildItem -LiteralPath $EvidenceDirectory -File -Recurse -Filter "*.json" |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json })
if ($manifests.Count -ne 3) { throw "Exactly three CI job evidence manifests are required." }
foreach ($job in $expected.Keys) {
    $matches = @($manifests | Where-Object job -eq $job)
    if ($matches.Count -ne 1) { throw "Missing or duplicate CI job evidence: $job" }
    $manifest = $matches[0]
    if ($manifest.schemaVersion -ne 1 -or $manifest.sourceSha -cne $ExpectedSourceSha -or
        $manifest.runtime -ne $expected[$job].runtime -or $manifest.artifacts.Count -eq 0) {
        throw "CI evidence does not match the expected source/RID/artifacts: $job"
    }
    $suite = @($manifest.tests | Where-Object suite -eq $expected[$job].suite)
    if ($suite.Count -ne 1 -or $suite[0].executed -le 0 -or
        $suite[0].selected -ne ($suite[0].executed + $suite[0].skipped)) { throw "Required test execution is missing: $job" }
    if ($job -ne "build-and-test" -and $suite[0].skipped -ne 0) { throw "Dedicated tests were skipped: $job" }
    foreach ($artifact in $manifest.artifacts) {
        if ($artifact.sha256 -notmatch '^[0-9a-f]{64}$' -or $artifact.length -lt 0 -or
            $artifact.path -notmatch '^[A-Za-z0-9._/-]+$' -or $artifact.path.StartsWith('/') -or
            @($artifact.path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) {
            throw "Invalid artifact evidence: $job"
        }
    }
    if ($job -eq "official-package-arm64") {
        $ordinary = @($manifest.checks | Where-Object name -ceq 'namespace-nonadmin')
        if ($RequireNonAdminNamespace -or $ordinary.Count -gt 0) {
            if ($ordinary.Count -ne 1 -or $ordinary[0].executed -isnot [bool] -or $ordinary[0].executed -ne $true -or
                $ordinary[0].observations.sourceSha -cne $ExpectedSourceSha) {
                throw 'Actual ordinary-user namespace execution is missing, repeated, or from another source.'
            }
            . (Join-Path $PSScriptRoot 'namespace-nonadmin-evidence-policy.ps1')
            Assert-OrdinaryUserNamespaceEvidence $ordinary[0].observations (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'test-suites.json') -Raw | ConvertFrom-Json) 'Arm64'
            foreach ($item in @(@{path='namespace-nonadmin/context.json';hash=$ordinary[0].observations.contextSha256},
                @{path='namespace-nonadmin/namespace.trx';hash=$ordinary[0].observations.trxSha256})) {
                $original = @($manifest.artifacts | Where-Object path -ceq $item.path)
                if ($original.Count -ne 1 -or $original[0].sha256 -cne $item.hash -or $original[0].length -le 0) {
                    throw 'The ordinary-user context or original TRX artifact is missing or differs from the check.'
                }
            }
        }
        $namespace = @($manifest.tests | Where-Object suite -ceq 'namespace')
        if ($RequireNamespace -or $namespace.Count -gt 0) {
            if ($namespace.Count -ne 1) { throw 'Dedicated ARM64 namespace execution is missing or repeated.' }
            . (Join-Path $PSScriptRoot 'namespace-evidence-policy.ps1')
            Assert-NamespaceTestExecution $namespace[0] (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'test-suites.json') -Raw | ConvertFrom-Json)
        }
        $integration = @($manifest.checks | Where-Object name -ceq 'signed-local-cli-regression')
        if ($integration.Count -ne 1 -or $integration[0].executed -ne $true) { throw 'The ARM64 CLI regression did not execute exactly once.' }
        . (Join-Path $PSScriptRoot 'cli-regression-evidence-policy.ps1')
        Assert-CliRegressionObservations $integration[0]
    }
    if ($job -eq "official-package-arm64" -and $RequireInstalled) {
        $installed = @($manifest.checks | Where-Object name -eq 'installed-msix')
        if ($installed.Count -ne 1 -or $installed[0].executed -ne $true -or $installed[0].uninstalled -ne $true -or
            $installed[0].osProductType -ne 1 -or $installed[0].osVersion -notmatch '^10\.0\.\d+\.\d+$' -or
            [version]$installed[0].osVersion -lt [version]'10.0.26100.0' -or
            $installed[0].minimumVersion -ne '10.0.26100.0') { throw "Supported desktop MSIX verification is missing." }
    }
    if ($job -eq "build-and-test") {
        $rejected = @($manifest.checks | Where-Object name -eq 'unsupported-server')
        if ($rejected.Count -ne 1 -or $rejected[0].executed -ne $true -or $rejected[0].cliRejected -ne $true -or
            $rejected[0].hostRejected -ne $true -or $rejected[0].stateUntouched -ne $true -or
            $rejected[0].osProductType -notin @(2,3)) { throw "Unsupported Windows Server rejection is missing." }
        if ($RequireNative) {
            $suiteCatalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot "test-suites.json") -Raw | ConvertFrom-Json
            $requiredNativeCount = @($suiteCatalog.required.native).Count
            $native = @($manifest.tests | Where-Object suite -eq "native")
            if ($requiredNativeCount -le 0 -or $native.Count -ne 1 -or $native[0].skipped -ne 0 -or
                @($native[0].categories | Where-Object { $_.category -eq "native" -and $_.executed -eq $requiredNativeCount }).Count -ne 1) {
                throw "Native Cloud Files execution is missing."
            }
        }
    }
}
$candidates = @($manifests | Where-Object { $null -ne $_.officialAdapterCandidateSha256 })
if ($RequireOfficialCandidate -or $candidates.Count -gt 0) {
    if ($candidates.Count -ne 3 -or @($candidates.officialAdapterCandidateSha256 | Select-Object -Unique).Count -ne 1 -or
        [string]$candidates[0].officialAdapterCandidateSha256 -cnotmatch '\A[0-9a-f]{64}\z') {
        throw 'All three CI jobs must verify the same frozen official Adapter candidate.'
    }
    foreach ($candidate in $candidates) {
        $records = @($candidate.artifacts | Where-Object { $_.path -ceq 'official-candidate/official-adapter-releases.json' })
        if ($records.Count -ne 1 -or $records[0].sha256 -cne $candidate.officialAdapterCandidateSha256 -or $records[0].length -le 0) {
            throw 'The shared candidate digest is not bound to a verified artifact.'
        }
    }
}
Write-Output "Verified all three CI job manifests for $ExpectedSourceSha."
