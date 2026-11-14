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

function Confirm-SignedDirectoryMoveReport {
    param([Parameter(Mandatory)][string]$ReportPath, [Parameter(Mandatory)][string]$PackageSha256)
    if ($PackageSha256 -cnotmatch '^[A-F0-9]{64}$') { throw 'Require the SHA-256 of the original signed package.' }
    [xml]$report = Get-Content -LiteralPath $ReportPath -Raw
    $rows = @($report.TestRun.Results.UnitTestResult)
    if ($rows.Count -ne 1 -or $rows[0].outcome -cne 'Passed') { throw 'The signed directory Move case must pass.' }
    $facts = @(([string]$rows[0].Output.StdOut -split '\r?\n') | Where-Object { $_.StartsWith('SignedDirectoryMove: ', [StringComparison]::Ordinal) })
    $expected = 'SignedDirectoryMove: packageSha256=' + $PackageSha256 + '; originalCount=1; members=3; hydrationReads=0; interruptedProjection=1; createdMembers=3; createOriginals=3; createdFileBytes=7; laterSourceContentPreserved=True.'
    if ($facts.Count -ne 1 -or $facts[0] -cne $expected) {
        throw 'The original execution does not bind the signed package, original Move and all three projections.'
    }
}

function Confirm-SignedDirectoryTransferReport {
    param([Parameter(Mandatory)][string]$ReportPath, [Parameter(Mandatory)][string]$PackageSha256,
        [Parameter(Mandatory)][string]$WebDavPackageSha256)
    if ($PackageSha256 -cnotmatch '^[A-F0-9]{64}$' -or $WebDavPackageSha256 -cnotmatch '^[A-F0-9]{64}$') {
        throw 'Require the SHA-256 of both original signed packages.'
    }
    [xml]$report = Get-Content -LiteralPath $ReportPath -Raw
    $rows = @($report.TestRun.Results.UnitTestResult)
    if ($rows.Count -ne 2 -or @($rows | Where-Object outcome -CNE 'Passed').Count) { throw 'Both signed cross-instance directory cases must pass.' }
    $expected = @{
        NativeSignedLocalDirectoryTransferResumesAcrossTwoSingleRootInstances = 'SignedDirectoryTransfer: packageSha256=' + $PackageSha256 + '; distinctSingleRootInstances=True; sourceMembers=3; atomicSourceReceipt=True; interruptedNativeRetirement=True; reopenedRoots=True; targetBytesVerified=True.'
        NativeSignedLocalDirectoryTransferToWebDavRejectsUnsupportedReverseBeforeMutation = 'SignedCrossPackageDirectoryTransfer: localSha256=' + $PackageSha256 + '; webDavSha256=' + $WebDavPackageSha256 + '; targetBytesVerified=True; reverseRejectedBeforeAdmission=True; reverseSourcePreserved=True.'
    }
    foreach ($row in $rows) {
        $name = [string]$row.testName
        $facts = @(([string]$row.Output.StdOut -split '\r?\n') | Where-Object { $_ -cmatch '^Signed(?:CrossPackage)?DirectoryTransfer: ' })
        if (!$expected.ContainsKey($name) -or $facts.Count -ne 1 -or $facts[0] -cne $expected[$name]) {
            throw 'The transfer report lost the original package, recovery or capability evidence.'
        }
        $expected.Remove($name)
    }
}
