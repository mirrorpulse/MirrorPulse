[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [ValidateSet("managed", "native", "official", "signed", "packaged", "namespace", "cfsharpDelete", "cfsharpRecreation", "deferredP079", "signedAtomicDirectory")][string]$Suite = "managed",
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot "test-suites.json") -Raw | ConvertFrom-Json
. (Join-Path $PSScriptRoot 'test-execution-policy.ps1')
$files = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter "*.trx" -File -Recurse)
if ($files.Count -eq 0) { throw "No TRX results were produced for the requested gate." }
$results = @(
    foreach ($file in $files) {
        [xml]$document = Get-Content -LiteralPath $file.FullName -Raw
        $definitions = @{}
        foreach ($test in $document.SelectNodes("//*[local-name()='UnitTest']")) {
            $definitions[$test.id] = $test
        }
        foreach ($result in $document.SelectNodes("//*[local-name()='UnitTestResult']")) {
            $definition = $definitions[$result.testId]
            if ($null -eq $definition) { throw "A TRX result has no test definition." }
            $name = "$($definition.TestMethod.className.Split(',')[0]).$($definition.TestMethod.name)"
            $category = "managed"
            foreach ($candidate in @("native", "official", "signed", "packaged", "deferredP079", "signedAtomicDirectory")) {
                if ($catalog.required.$candidate -contains $name) { $category = if ($candidate -in @('deferredP079','signedAtomicDirectory')) { 'native' } else { $candidate }; break }
            }
            [pscustomobject][ordered]@{
                name = $name
                case = [string]$result.testName
                testId = [string]$result.testId
                executionId = [string]$result.executionId
                assembly = [IO.Path]::GetFileNameWithoutExtension($definition.storage)
                category = $category
                outcome = [string]$result.outcome
            }
        }
    }
)
if ($results.Count -eq 0) { throw "The test gate selected no tests." }
$failures = @($results | Where-Object outcome -notin @("Passed", "NotExecuted", "Inconclusive"))
if ($failures.Count -gt 0) { throw "$($failures.Count) test results failed the gate." }
$skipped = @($results | Where-Object outcome -ne "Passed")
foreach ($result in $skipped) {
    if ($catalog.allowSkipped -notcontains $result.name) {
        throw "An undeclared test was skipped: $($result.name)"
    }
}
if ($Suite -eq "managed") {
    foreach ($assembly in @("MirrorPulse.Core.Tests", "MirrorPulse.Cli.Tests", "MirrorPulse.CloudFiles.CfSharp.Tests")) {
        if (@($results | Where-Object { $_.assembly -eq $assembly -and $_.outcome -eq "Passed" }).Count -eq 0) {
            throw "The managed gate did not execute $assembly."
        }
    }
} else {
    Assert-RequiredTestResults -Catalog $catalog -Suite $Suite -Results $results
    if ($skipped.Count -ne 0) { throw "The $Suite gate contains skipped tests." }
    if ($Suite -in @('namespace', 'cfsharpDelete', 'cfsharpRecreation', 'deferredP079', 'signedAtomicDirectory') -and
        $results.Count -ne (Get-RequiredTestExecutionCount -Catalog $catalog -Names @($catalog.required.$Suite))) {
        throw "The $Suite gate must select exactly the required tests."
    }
}
$categories = @(
    foreach ($group in $results | Group-Object category | Sort-Object Name) {
        [ordered]@{
            category = $group.Name
            selected = $group.Count
            executed = @($group.Group | Where-Object outcome -eq "Passed").Count
            skipped = @($group.Group | Where-Object outcome -ne "Passed").Count
        }
    }
)
$report = [ordered]@{
    schemaVersion = 1
    suite = $Suite
    selected = $results.Count
    executed = $results.Count - $skipped.Count
    skipped = $skipped.Count
    categories = $categories
    results = $results
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $ResultsDirectory "test-evidence.json" }
New-Item -ItemType Directory -Path ([IO.Path]::GetFullPath((Split-Path $OutputPath -Parent))) -Force | Out-Null
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output "$Suite gate: selected $($results.Count), executed $($results.Count - $skipped.Count), skipped $($skipped.Count)."
