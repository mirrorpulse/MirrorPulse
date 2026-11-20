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
    $expected = @{
        NativeSignedLocalTransfersNestedLocalOnlyFileAcrossTwoSingleRootInstances = 'SignedLocalOnlyFileTransfer: packageSha256=' + $PackageSha256 + '; distinctSingleRootInstances=True; originalNotificationsCaptured=True; originalLocalParentRetained=True; remoteSourceEmpty=True; targetBytesVerified=True; sourceAbsenceRetained=True; originalJournalAcknowledged=True.'
        NativeSignedLocalDirectoryTransferResumesAcrossTwoSingleRootInstances = 'SignedDirectoryTransfer: packageSha256=' + $PackageSha256 + '; distinctSingleRootInstances=True; sourceMembers=3; atomicSourceReceipt=True; interruptedNativeRetirement=True; reopenedRoots=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.'
        NativeSignedLocalDirectoryTransferResumesAfterNativeDeleteAcrossHostRoles = 'SignedDirectoryTransfer: packageSha256=' + $PackageSha256 + '; originalNativeRetirementSettled=True; originalStartRetained=True; bothRootsFenced=True; reopenedRoots=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.'
        NativeSignedLocalDirectoryTransferResumesBeforeNativeDeleteAcrossHostRoles = 'SignedDirectoryTransfer: packageSha256=' + $PackageSha256 + '; liveOriginalSurvivedRestart=True; originalStartRetained=True; sdkLocalFileIdRecovered=True; lateDeleteAcknowledged=True; bothRootsFenced=True; reopenedRoots=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.'
        NativeSignedLocalDirectoryTransferResumesAfterWorkerDeleteAcrossHostRoles = 'SignedDirectoryTransfer: packageSha256=' + $PackageSha256 + '; originalWorkerDeleteAccepted=True; nativeStartsBeforeRestart=0; bothRootsFenced=True; reopenedRoots=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.'
        NativeSignedLocalDirectoryTransferResumesBeforeWorkerReceiptAcrossHostRoles = 'SignedDirectoryTransfer: packageSha256=' + $PackageSha256 + '; workerResultBeforeHostReceipt=True; exactWorkerReceiptReplayed=True; nativeStartsBeforeRestart=0; bothRootsFenced=True; reopenedRoots=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.'
        NativeSignedLocalDirectoryTransferResumesBeforeWorkerDispatchAcrossHostRoles = 'SignedDirectoryTransfer: packageSha256=' + $PackageSha256 + '; sourceIntactBeforeWorkerDispatch=True; originalWorkerRequestRetained=True; bothRootsFenced=True; reopenedRoots=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.'
        NativeSignedLocalDirectoryTransferResumesBeforeWorkerStartAcrossHostRoles = 'SignedDirectoryTransfer: packageSha256=' + $PackageSha256 + '; publishedBirthsRetained=3; sourceIntactBeforeWorkerStart=True; bothRootsFenced=True; reopenedRoots=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.'
        NativeSignedLocalDirectoryTransferResumesPartialTargetPublicationAcrossHostRoles = 'SignedDirectoryTransfer: packageSha256=' + $PackageSha256 + '; firstBirthRetained=True; twoBirthsUnderAuditedRole=True; sourceIntactBeforeRecovery=True; bothRootsFenced=True; reopenedRoots=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.'
        NativeSignedLocalDirectoryTransferRecoversObservedTargetBeforeRegistrationAcrossHostRoles = 'SignedDirectoryTransfer: packageSha256=' + $PackageSha256 + '; observedBirthRegisteredAtStartup=True; originalBindingRetained=True; twoBirthsUnderAuditedRole=True; sourceIntactBeforeRecovery=True; bothRootsFenced=True; reopenedRoots=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.'
        NativeSignedLocalDirectoryTransferPublishesSuccessorsAtReusedBackendIds = 'SignedDirectoryTransfer: packageSha256=' + $PackageSha256 + '; reusedBackendIds=3; generationsRetained=3; oldCallbacksRejected=True; replacementSourceBytesVerified=True; originalTargetBytesVerified=True; completedTransferReplayPreservedSuccessors=True.'
        NativeSignedLocalDirectoryTransferToWebDavRejectsUnsupportedReverseBeforeMutation = 'SignedCrossPackageDirectoryTransfer: localSha256=' + $PackageSha256 + '; webDavSha256=' + $WebDavPackageSha256 + '; targetBytesVerified=True; reverseRejectedBeforeAdmission=True; reverseSourcePreserved=True.'
    }
    foreach ($ordinal in @(0, 1)) {
        $name = 'NativeSignedLocalDirectoryTransferResumesSelectedTargetBeforeFirstNativeStartAcrossHostRoles (' + $ordinal + ')'
        $expected[$name] = 'SignedDirectoryTransfer: packageSha256=' + $PackageSha256 + '; selectedTargetOrdinal=' + $ordinal +
            '; originalBirthRetained=True; disabledRoot=' + @('Source', 'Target')[$ordinal] +
            '; disabledResumeRejected=True; originalCopyRetained=True; bothRootAuditsBeforeStart=True; startWriteRollbackVerified=' + ($ordinal -eq 0) +
            '; firstStartImmutable=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.'
    }
    foreach ($ordinal in @(0, 2)) {
        $started = 'NativeSignedLocalDirectoryTransferContinuesOriginalStartBeforeSdkPreparation (' + $ordinal + ')'
        $expected[$started] = 'SignedDirectoryTransfer: packageSha256=' + $PackageSha256 + '; startedTargetOrdinal=' + $ordinal +
            '; absentBeforeSdk=True; originalStartRetained=True; originalCopyRetained=True; originalBindingVerified=True;' +
            ' sourceIntactBeforeRecovery=True; targetBytesVerified=True; originalAcknowledgementCompleted=True.'
        $restarted = 'NativeSignedLocalDirectoryTransferRecoversStartedTargetBeforeSdkPreparationAcrossHostRoles (' + $ordinal + ')'
        $expected[$restarted] = 'SignedDirectoryTransfer: packageSha256=' + $PackageSha256 + '; restartedTargetOrdinal=' + $ordinal +
            '; absentBeforeSdk=True; bothOriginalRootsAudited=True; startWriteRollbackVerified=' + ($ordinal -eq 0) +
            '; originalStartRetained=True; originalCopyRetained=True; originalBindingVerified=True; sourceIntactBeforeRecovery=True;' +
            ' targetBytesVerified=True; acknowledgementAcrossRoles=True.'
        $name = 'NativeSignedLocalDirectoryTransferRecoversOriginalCreationBeforeFirstObservationAcrossHostRoles (' + $ordinal + ')'
        $expected[$name] = 'SignedDirectoryTransfer: packageSha256=' + $PackageSha256 + '; unobservedTargetOrdinal=' + $ordinal +
            '; originalPublicReceiptRecovered=True; originalBindingRetained=True; originalStartRetained=True;' +
            ' sourceIntactBeforeRecovery=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.'
    }
    if ($rows.Count -ne $expected.Count -or @($rows | Where-Object outcome -CNE 'Passed').Count) { throw 'All signed cross-instance transfer cases must pass.' }
    foreach ($row in $rows) {
        $name = [string]$row.testName
        $facts = @(([string]$row.Output.StdOut -split '\r?\n') | Where-Object { $_ -cmatch '^Signed(?:(?:CrossPackage)?Directory|LocalOnlyFile)Transfer: ' })
        if (!$expected.ContainsKey($name) -or $facts.Count -ne 1 -or $facts[0] -cne $expected[$name]) {
            throw 'The transfer report lost the original package, recovery or capability evidence.'
        }
        $expected.Remove($name)
    }
}

function Confirm-SignedDirectoryCopyRecoveryReport {
    param([Parameter(Mandatory)][string]$ReportPath, [Parameter(Mandatory)][string]$PackageSha256)
    if ($PackageSha256 -cnotmatch '^[A-F0-9]{64}$') { throw 'Require the SHA-256 of the original signed Local package.' }
    [xml]$report = Get-Content -LiteralPath $ReportPath -Raw
    $rows = @($report.TestRun.Results.UnitTestResult)
    $prefix = 'SignedDirectoryCopyRecovery: packageSha256=' + $PackageSha256 + '; '
    $resumed = '; copyEffectsRetained=3; onlyUnstartedEffectsDispatched=True; remainderStartedAfterBothRootAudits=True; sourceIntactBeforeRecovery=True; bothRootsFenced=True; reopenedRoots=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.'
    $expected = @{
        NativeSignedLocalDirectoryTransferStartsOriginalCopyAfterHostRoleChange = $prefix + 'acceptedBeforeRestart=0' + $resumed
        NativeSignedLocalDirectoryTransferResumesAfterAcceptedCopyAcrossHostRoles = $prefix + 'acceptedBeforeRestart=1' + $resumed
        NativeSignedLocalDirectoryTransferRetainsFencesForUnreceiptedCopyAcrossHostRoles = @(
            $prefix + 'targetDirectoryExists=True; originalCopyUnreceipted=True; noLaterCopyStarted=True; sourceIntact=True; bothRootsFenced=True.'
            $prefix + 'durableCopyNegotiated=True; originalStartRetained=True; originalReceiptRevisionRetained=True; copyEffectsRetained=3; currentRootAudits=2; sourceIntactBeforeRecovery=True; bothRootsFenced=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.'
        )
    }
    if ($rows.Count -ne $expected.Count -or @($rows | Where-Object outcome -CNE 'Passed').Count) {
        throw 'All signed directory copy recovery cases must pass.'
    }
    foreach ($row in $rows) {
        $name = [string]$row.testName
        $facts = @(([string]$row.Output.StdOut -split '\r?\n') | Where-Object { $_.StartsWith('SignedDirectoryCopyRecovery: ', [StringComparison]::Ordinal) })
        if (!$expected.ContainsKey($name) -or $facts.Count -ne 1 -or @($expected[$name]) -cnotcontains $facts[0]) {
            throw 'The copy recovery report lost its package, original copy order or fence evidence.'
        }
        $expected.Remove($name)
    }
}

function Confirm-SignedPreparedDirectoryCopyReport {
    param([Parameter(Mandatory)][string]$ReportPath, [Parameter(Mandatory)][string]$PackageSha256)
    if ($PackageSha256 -cnotmatch '^[A-F0-9]{64}$') { throw 'Require the SHA-256 of the original signed Local package.' }
    [xml]$report = Get-Content -LiteralPath $ReportPath -Raw
    $rows = @($report.TestRun.Results.UnitTestResult)
    $prefix = "SignedPreparedDirectoryCopy: packageSha256=$PackageSha256; checkpoint="
    $completion = '; originalCopyEffects=3; currentAudits=2; sourceIntactBeforeRecovery=True; targetBytesVerified=True; acknowledgementAcrossRoles=True.'
    $expected = @{
        NativeSignedLocalDirectoryTransferResumesAfterCopyPreparationAcrossHostRoles = $prefix + 'AfterPreparation; initialHostStart=False; initialBackendTarget=False' + $completion
        NativeSignedLocalDirectoryTransferResumesBeforeCopyDispatchAcrossHostRoles = $prefix + 'BeforeDispatch; initialHostStart=True; initialBackendTarget=False' + $completion
        NativeSignedLocalDirectoryTransferReplaysDurableCopyAcrossHostRoles = $prefix + 'BeforeAcceptance; initialHostStart=True; initialBackendTarget=True' + $completion
    }
    if ($rows.Count -ne $expected.Count -or @($rows | Where-Object outcome -CNE 'Passed').Count) {
        throw 'All three signed prepared directory copy recovery cases must pass.'
    }
    foreach ($row in $rows) {
        $name = [string]$row.testName
        $facts = @(([string]$row.Output.StdOut -split '\r?\n') | Where-Object { $_.StartsWith('SignedPreparedDirectoryCopy: ', [StringComparison]::Ordinal) })
        if (!$expected.ContainsKey($name) -or $facts.Count -ne 1 -or $facts[0] -cne $expected[$name]) {
            throw 'The prepared directory copy report lost its package or original recovery evidence.'
        }
        $expected.Remove($name)
    }
}
