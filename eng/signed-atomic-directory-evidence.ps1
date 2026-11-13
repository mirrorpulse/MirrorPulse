function Confirm-SignedAtomicDirectoryReport {
    param([Parameter(Mandatory)][string]$ReportPath, [Parameter(Mandatory)][string]$PackageSha256)
    if ($PackageSha256 -cnotmatch '^[A-F0-9]{64}$') { throw 'Require the SHA-256 of the original signed package.' }
    [xml]$report = Get-Content -LiteralPath $ReportPath -Raw
    $rows = @($report.TestRun.Results.UnitTestResult)
    if ($rows.Count -ne 3 -or @($rows | Where-Object outcome -CNE 'Passed').Count) { throw 'All three signed atomic directory cases must pass.' }
    $scenarios = @(foreach ($row in $rows) {
        $facts = @(([string]$row.Output.StdOut -split "`r?`n") | Where-Object { $_.StartsWith('SignedAtomicDirectory: ', [StringComparison]::Ordinal) })
        if ($facts.Count -ne 1 -or $facts[0] -cnotmatch ('^SignedAtomicDirectory: packageSha256=' + $PackageSha256 + '; originalCount=2; failOfficialCommit=(True|False); failHostReceiptCommit=(True|False)\.$')) {
            throw 'The original execution does not bind its signed package and both journal originals.'
        }
        $Matches[1] + '/' + $Matches[2]
    })
    if (@(Compare-Object @('False/False','True/False','False/True') $scenarios -CaseSensitive).Count) { throw 'The three original recovery scenarios differ.' }
}
