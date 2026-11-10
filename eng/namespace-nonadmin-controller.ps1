function Get-OrdinaryUserNamespaceBudgetMilliseconds {
    param([Parameter(Mandatory)][ValidateRange(1, 155)][int]$TestCount, [string[]]$TestNames,
        [ValidateSet('AllRetained', 'S06')][string]$Scope = 'AllRetained')
    if ($Scope -ceq 'S06' -and -not $PSBoundParameters.ContainsKey('TestNames')) {
        throw 'S06 scope requires its explicit selected test names.'
    }
    $preparation = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseManagedRootNamespaceTests.NativeRootRenamePreparationCutsRetainOnlyCompleteOriginalProductProofAcrossOwners'
    $approval = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseManagedRootNamespaceTests.NativeDefaultProviderApprovesOnlyTheCurrentOriginalRootRenameAndRetainsRecoveryAcrossOwners'
    $rootPreparation = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeRootLabelPreparationSealsOnlyItsOriginalProtectedSubtreeAcrossOwners'
    $rootExecution = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeProtectedRootLabelExecutionRecoversOriginalSubtreeAndCaseOnlyHistoryAcrossOwners'
    $rootRecovery = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeObservedRootLabelRecoverySettlesOnlyOriginalMovedMembersUnderANewRoleAcrossOwners'
    $rootRuntime = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeRuntimeSettlesOriginalObservedRootLabelBeforeNewRoleStartupAudit'
    $rootAdmission = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeRootLabelJournalAdmissionRequiresCurrentTreeAndActualSdkReturnBeforeReadmission'
    $rootPublic = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativePublicRuntimeExecutesOnlyOriginalProtectedRootLabelsAndAuditsCurrentReadmission'
    $rootCancellation = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativePublicRuntimeCancelsOnlyUnstartedOriginalRootLabelsAcrossRolesAndRetainsLaterNames'
    $unsealedCancellation = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativePublicRuntimeCancelsOriginalUnsealedRootPreparationsAfterIndependentRoleDisposal'
    $legacyInitializer = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativePublicCancellationRetainsAndResumesItsOriginalPartiallyRotatedInitializer'
    $bornCancellation = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativePublicCancellationRecoversAnInheritedBornRootThroughItsOriginalPurposeAfterRoleDisposal'
    $disabledImport = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeAutomaticStartupKeepsDisabledImportLocalAcrossOwnersWithoutOpeningOrdinaryWork'
    $disabledCreation = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeDisabledRootControlledCreationRetainsFilesDirectoriesAndOriginalsAcrossOwnersWithoutDispatch'
    $disabledCreationRole = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeDisabledRootControlledCreationRetainsFilesDirectoriesAndOriginalsAcrossFreshRolesWithoutDispatch'
    $disabledMove = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeDisabledRootControlledMoveRetainsFilesDirectoriesAndOriginalsAcrossFreshRolesWithoutDispatch'
    $disabledRetirement = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeDisabledRootControlledRetirementRetainsOriginalJournalAcrossFreshRolesWithoutDispatch'
    $disabledReplacement = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeDisabledRootControlledReplacementRetainsBytesBackupAndJournalAcrossFreshRolesWithoutDispatch'
    $residentReplacement = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.NativeResidentInSyncReplacementRetainsOriginalBytesJournalAndDirtyContentWithoutSourceCalls'
    $journalRescan = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseJournalUploadSourceTests.NativeProductionPumpSettlesOriginalJournalBeforeAnExplicitRescanAcrossOwners'
    $prefix = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.'
    $keepRemote = @{
        ($prefix+'NativeKeepRemoteFirstSealRequiresTheOriginalFileAndJournalAndReplaysWithoutWriting') = 3
        ($prefix+'NativeKeepRemoteResidentByteCopyRetainsPlaceholderAndOriginalJournal') = 3
        ($prefix+'NativeKeepRemoteResidentWriterKeepsItsWriteAndCompletionFactsAcrossRetry') = 3
        ($prefix+'NativeKeepRemoteResidentWriterPreservesChangedLocalBytesBeforeItsFirstWrite') = 1
        ($prefix+'NativeKeepRemoteConfirmationRetainsOriginalAttemptsAndRecoversLostSdkReturns') = 4
        ($prefix+'NativeKeepRemoteRestorationRetainsActualAccessResultsAndPreservesOrdinaryLaterEdits') = 5
        ($prefix+'NativeKeepRemoteNewRoleResumesOriginalWriteAndConfirmsSelectedBytes') = 1
        ($prefix+'NativeKeepRemoteDefaultCommandResumesFirstPermissionIntentBeforeRootAudit') = 1
        ($prefix+'NativeKeepRemoteCurrentRoleRestoresItsRegisteredInheritedDescriptor') = 1
        ($prefix+'NativeKeepRemoteAfterAcknowledgedMoveRetainsItsPrefixAndSettlesTheOriginalJournal') = 1
        ($prefix+'NativeKeepRemoteControlledCreatePreservesItsAbsentBaselineAndAllOriginalJournalEffects') = 1
        ($prefix+'NativeKeepRemoteIndependentRuntimeSettlesOriginalCommandBeforeReadmission') = 1
        ($prefix+'NativeKeepRemoteUploadPumpResumesAcceptedCommandAfterRootAudit') = 1
        ($prefix+'NativeKeepRemoteCfSharpAccessOnlyPreservesOriginalJournalAndRealSameLengthWrite') = 1
        ($prefix+'NativeKeepRemoteAcknowledgementRecoversLostSdkReturnAndPreservesLaterEdit') = 1
        ($prefix+'NativeKeepRemoteRoleHandoffRecoversLostReadbackWithoutReapplyingContent') = 1
    }
    $deleteRejections = @{
        ($prefix+'NativeMissingOriginalRemoteDeleteRetainsActualSdkConflictWithoutInventingMemberRetirementAcrossOwners') = 1
        ($prefix+'NativeMiddleMissingRemoteDeleteSettlesOriginalRejectionBeforeLaterSdkEntryAndCursorAcrossOwners') = 1
        ($prefix+'NativeMissingRemoteDeleteRecoversOriginalAdmissionBeforeSdkAcrossFreshRoles') = 1
        ($prefix+'NativeMissingRemoteDeleteRecoversActualSdkConflictBeforeProductResultAcrossFreshRoles') = 1
        ($prefix+'NativeDefaultRuntimeRecoversReceivedMissingDeleteBeforeNewRoleAuditWithoutAcknowledgingLocalContentOriginals') = 4
        ($prefix+'NativeDefaultRuntimePublishesActualMissingDeleteConflictAndCursorAcrossFreshRolesWithoutChangingLocalOriginals') = 1
        ($prefix+'NativeCfSharpPreservingDeleteCharacterizesItsActualLocalTombstoneBeforeAndAfterConsumerAcknowledgement') = 4
    }
    $additionalSelections = @(@($preparation,$approval,$rootPreparation,$rootExecution,$rootRecovery,$rootRuntime,$rootAdmission,$rootPublic,$rootCancellation,$unsealedCancellation,$legacyInitializer,$bornCancellation,$disabledImport,$disabledCreation,$disabledCreationRole,$disabledMove,$disabledRetirement,$disabledReplacement,$residentReplacement,$journalRescan)+@($keepRemote.Keys)+@($deleteRejections.Keys) | Where-Object {$_ -cin $TestNames}).Count
    if ($TestCount -gt 112 -and (-not $PSBoundParameters.ContainsKey('TestNames') -or $additionalSelections -lt $TestCount-112)) {
        throw 'The additional namespace case requires its explicit original preparation selection.'
    }
    $executions = $TestCount
    $transferDeadlineMilliseconds = 0
    if ($PSBoundParameters.ContainsKey('TestNames')) {
        if ($TestNames.Count -ne $TestCount) { throw 'The namespace budget must describe the exact selected catalog.' }
        $selected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $repeated = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
        $repeated.Add($prefix + 'NativeHostTransferRetainsSourceAndBothFencesAfterOriginalCopyAndWholeTargetVerification', 2)
        $repeated.Add($prefix + 'NativeHostTransferDeletesOnlyOriginalRemoteSourceWhileRetainingGuardedLocalBytes', 2)
        $repeated.Add($prefix + 'NativeHostTransferRetiresOriginalSourceThroughTheSdkAndKeepsPrivateBytesAndBothFences', 2)
        $repeated.Add($prefix + 'NativeHostTransferSourceRetirementResumesOriginalNativeAndReceiptBoundariesAcrossStoreOwners', 10)
        $repeated.Add($prefix + 'NativeHostTransferAcknowledgesOriginalClaimAndRecoversSdkReturnBeforeReceiptAcrossStoreOwners', 2)
        if ($Scope -ceq 'S06') {
            # The original methods now select only their cross-instance leg. The
            # other nine 45-second executions remain in the separate P079 suite.
            foreach ($method in @('NativeHostTransferRetainsSourceAndBothFencesAfterOriginalCopyAndWholeTargetVerification',
                'NativeHostTransferDeletesOnlyOriginalRemoteSourceWhileRetainingGuardedLocalBytes',
                'NativeHostTransferRetiresOriginalSourceThroughTheSdkAndKeepsPrivateBytesAndBothFences',
                'NativeHostTransferSourceRetirementResumesOriginalNativeAndReceiptBoundariesAcrossStoreOwners',
                'NativeHostTransferAcknowledgesOriginalClaimAndRecoversSdkReturnBeforeReceiptAcrossStoreOwners')) {
                $repeated[$prefix + $method] = [int]($repeated[$prefix + $method] / 2)
            }
        }
        $repeated.Add($preparation, 2)
        $repeated.Add($approval, 2)
        $repeated.Add($rootPreparation, 2)
        $repeated.Add($rootExecution, 14)
        $repeated.Add($rootRecovery, 8)
        $repeated.Add($rootRuntime, 6)
        $repeated.Add($rootAdmission, 6)
        $repeated.Add($rootPublic, 4)
        $repeated.Add($rootCancellation, 6)
        $repeated.Add($unsealedCancellation, 2)
        $repeated.Add($legacyInitializer, 16)
        $repeated.Add($bornCancellation, 2)
        foreach ($name in $TestNames) {
            if ([string]::IsNullOrWhiteSpace($name) -or $name.Length -gt 512 -or $name -cnotmatch '^[A-Za-z0-9_.]+$' -or -not $selected.Add($name)) {
                throw 'The namespace budget requires distinct, bounded test names.'
            }
            # This additional method owns one actual 65-second native deadline.
            # Its ordinary 15-second estimate already contributes to executions.
            if ($name -ceq $disabledImport) { $transferDeadlineMilliseconds += 50000 }
            if ($name -ceq $disabledCreation) { $transferDeadlineMilliseconds += 50000 }
            if ($name -ceq $disabledCreationRole) { $transferDeadlineMilliseconds += 50000 }
            if ($name -ceq $disabledMove) { $transferDeadlineMilliseconds += 50000 }
            if ($name -ceq $disabledRetirement) { $transferDeadlineMilliseconds += 50000 }
            if ($name -ceq $disabledReplacement) { $transferDeadlineMilliseconds += 50000 }
            if ($name -ceq $journalRescan) { $transferDeadlineMilliseconds += 50000 }
            # Two independent 45-second original identity starts retain their full deadlines.
            if ($name -ceq $residentReplacement) { $executions++; $transferDeadlineMilliseconds += 60000 }
            if ($deleteRejections.ContainsKey($name)) {
                # Each component/runtime/SDK characterization row keeps its actual65-second deadline.
                $executions += $deleteRejections[$name]-1
                $transferDeadlineMilliseconds += $deleteRejections[$name]*50000
            }
            if ($keepRemote.ContainsKey($name)) {
                # Every selected data row retains its actual65-second fixture deadline.
                $executions += $keepRemote[$name]-1
                $transferDeadlineMilliseconds += $keepRemote[$name]*50000
            }
            if ($repeated.ContainsKey($name)) {
                $executions += $repeated[$name] - 1
                # Each original transfer scope/cutpoint already owns a 45-second
                # native deadline. The aggregate controller must preserve it;
                # its ordinary 15-second per-case estimate cannot replace it.
                $additional = if ($name -cin @($rootRuntime,$rootPublic,$rootCancellation,$unsealedCancellation,$legacyInitializer,$bornCancellation)) { 45000 } elseif ($name -cin @($preparation,$approval,$rootPreparation,$rootExecution,$rootRecovery,$rootAdmission)) { 15000 } else { 30000 }
                $transferDeadlineMilliseconds += $repeated[$name] * $additional
            }
        }
    }
    # Every selected native fixture keeps its original deadline. The prior125-name
    # catalog retains72 repeated executions and adds one65-second disabled-import
    # execution to the original95m15s child envelope. The step
    # adds five minutes for controller diagnostics and disposable-user cleanup.
    # The legacy-initializer fixture retains its two original 60-second
    # executions, four Prepared/Applied executions and ten born-recovery cut executions.
    # Each original deadline remains; the cut matrix adds exactly ten minutes.
    # The inherited born-root cancellation adds two 60-second executions;
    # the original 123-name catalog retains its exact prior bound.
    # AllRetained keeps every historical envelope. S06 retains63 repeated
    # executions and adds the same65 seconds to its original88m30s envelope.
    # The sixteen KeepRemote methods add exactly twenty-nine 65-second executions.
    # Disabled controlled creation adds one further65-second file/directory/replay case.
    # Its separate disposed-role recovery case adds its own65-second deadline.
    # Disabled controlled file/directory move adds one further65-second execution.
    # Explicit disabled file/empty-directory retirement adds one65-second execution.
    # Disabled resident replacement adds one65-second execution with cold-file refusals.
    # Count-only callers still retain their original112-name/30-minute bound.
    return [Math]::Max(600000, 120000 + $executions * 15000 + $transferDeadlineMilliseconds)
}

function Get-OrdinaryUserNamespaceStepTimeoutMinutes {
    param([Parameter(Mandatory)][ValidateRange(1, 155)][int]$TestCount, [string[]]$TestNames,
        [ValidateSet('AllRetained', 'S06')][string]$Scope = 'AllRetained')
    # The workflow must let the existing child budget expire and allow the
    # controller to preserve diagnostics and remove its disposable user/profile.
    $parameters = @{ TestCount = $TestCount }
    if ($PSBoundParameters.ContainsKey('TestNames')) { $parameters.TestNames = $TestNames }
    if ($PSBoundParameters.ContainsKey('Scope')) { $parameters.Scope = $Scope }
    $childMilliseconds = Get-OrdinaryUserNamespaceBudgetMilliseconds @parameters
    return [int][Math]::Ceiling($childMilliseconds / 60000.0) + 5
}

function Copy-OrdinaryUserNamespaceFailureEvidence {
    param([Parameter(Mandatory)][string]$Workspace, [Parameter(Mandatory)][string]$Identifier,
        [Parameter(Mandatory)][string]$EvidenceDirectory)
    $workspacePath = [IO.Path]::GetFullPath($Workspace)
    $evidencePath = [IO.Path]::GetFullPath($EvidenceDirectory)
    $markerPath = Join-Path $workspacePath '.mp-namespace-ci'
    $journalRescan = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseJournalUploadSourceTests.NativeProductionPumpSettlesOriginalJournalBeforeAnExplicitRescanAcrossOwners'
    $prefix = $workspacePath + [IO.Path]::DirectorySeparatorChar
    if ($Identifier -cnotmatch '^[0-9a-f]{32}$' -or [IO.Path]::GetFileName($workspacePath) -cne $Identifier -or
        $evidencePath.Equals($workspacePath, [StringComparison]::OrdinalIgnoreCase) -or
        $evidencePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or
        ((Get-Item -LiteralPath $workspacePath).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        ((Get-Item -LiteralPath $markerPath).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        [IO.File]::ReadAllText($markerPath) -cne $Identifier) {
        throw 'Failure evidence requires the original marked disposable workspace and an external evidence directory.'
    }
    New-Item -ItemType Directory -Path $evidencePath -Force | Out-Null
    foreach ($entry in @(
        @{source='work/context.json';target='context.json'},
        @{source='work/results/namespace.trx';target='namespace.trx'},
        @{source='stdout.log';target='child-stdout.log'},
        @{source='stderr.log';target='child-stderr.log'})) {
        $sourcePath = Join-Path $workspacePath $entry.source
        $ancestor = [IO.Path]::GetDirectoryName($sourcePath)
        while ($ancestor -cne $workspacePath) {
            if (-not $ancestor.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Failure evidence escaped its workspace.' }
            if ((Test-Path -LiteralPath $ancestor) -and
                ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Failure evidence must not traverse a reparse point.'
            }
            $ancestor = [IO.Path]::GetDirectoryName($ancestor)
        }
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { continue }
        $file = Get-Item -LiteralPath $sourcePath
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint -or $file.Length -gt 10MB) {
            throw 'Failure evidence must be a bounded ordinary diagnostic file.'
        }
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $evidencePath $entry.target)
    }
}
