[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ResultsDirectory,
    [ValidateSet("managed", "native", "official", "signed", "packaged", "namespace")][string]$Suite = "managed",
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot "test-suites.json") -Raw | ConvertFrom-Json
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
            foreach ($candidate in @("native", "official", "signed", "packaged")) {
                if ($catalog.required.$candidate -contains $name) { $category = $candidate; break }
            }
            [pscustomobject][ordered]@{
                name = $name
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
    foreach ($required in $catalog.required.$Suite) {
        $matched = @($results | Where-Object name -eq $required)
        if ($matched.Count -ne 1 -or $matched[0].outcome -ne "Passed") {
            throw "Required $Suite test did not execute successfully exactly once: $required"
        }
    }
    if ($skipped.Count -ne 0) { throw "The $Suite gate contains skipped tests." }
    if ($Suite -eq 'namespace' -and $results.Count -ne @($catalog.required.namespace).Count) {
        throw 'The namespace gate must select exactly the required tests.'
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
