function Assert-CliTransferObservations {
    param([Parameter(Mandatory)][object]$Evidence, [switch]$BeforeCleanup)

    if ($Evidence.schemaVersion -ne 1 -or $Evidence.sourceSha -cnotmatch '\A[0-9a-f]{40}\z' -or $Evidence.runtime -notin @('win-x64', 'win-arm64') -or
        $Evidence.signedInstallation -isnot [bool] -or -not $Evidence.signedInstallation -or
        $Evidence.packageSha256 -cnotmatch '\A[0-9a-fA-F]{64}\z' -or
        $Evidence.privateWorkerRuntime -isnot [bool] -or -not $Evidence.privateWorkerRuntime -or
        @($Evidence.workerProcessIds).Count -ne 2 -or @($Evidence.workerProcessIds | Select-Object -Unique).Count -ne 2 -or
        @($Evidence.workerProcessIds | Where-Object { ($_ -isnot [int] -and $_ -isnot [long]) -or $_ -le 0 }).Count) {
        throw 'The transfer evidence requires its actual signed installation and two independent private-runtime Worker processes.'
    }
    $cases = @($Evidence.cases)
    if ($cases.Count -ne 2 -or @($cases.operationId | Select-Object -Unique).Count -ne 2 -or
        @($Evidence.roots).Count -ne 3 -or @($Evidence.roots.rootId | Select-Object -Unique).Count -ne 3 -or
        @($Evidence.roots.instanceId | Select-Object -Unique).Count -ne 2) {
        throw 'The transfer evidence requires exactly two original commands across three stable roots and two instances.'
    }
    $caseRoots = @(@($cases.status.sourceRootId) + @($cases.status.targetRootId) | Select-Object -Unique)
    if ($caseRoots.Count -ne 3 -or @(Compare-Object $caseRoots @($Evidence.roots.rootId)).Count) { throw 'The original command roots disagree with the retained fixture routes.' }
    foreach ($scope in @('SameInstance', 'CrossInstance')) {
        $selected = @($cases | Where-Object scope -ceq $scope)
        $receipt = @($Evidence.audit.transfers | Where-Object scope -ceq $scope)
        if ($selected.Count -ne 1 -or $receipt.Count -ne 1) { throw 'Both original transfer scopes must execute exactly once.' }
        $case = $selected[0]; $retained = $receipt[0]; $status = $case.status
        foreach ($id in @($case.operationId, $status.sourceRootId, $status.targetRootId, $status.sourceMemberId,
            $status.targetMemberId, $case.sourceItemId, $case.targetItemId, $case.laterSourceMemberId, $case.laterSourceItemId)) {
            $parsed = [guid]::Empty
            if (-not [guid]::TryParseExact([string]$id, 'D', [ref]$parsed) -or $parsed -eq [guid]::Empty) { throw 'A transfer original identity is missing or invalid.' }
        }
        if ($status.operationId -cne $case.operationId -or $status.sourceRootId -ceq $status.targetRootId -or
            $status.sourceMemberId -ceq $status.targetMemberId -or $case.sourceItemId -ceq $case.targetItemId -or
            $status.completed -isnot [bool] -or -not $status.completed -or $status.rootsFenced -isnot [bool] -or $status.rootsFenced -or
            $status.journalAcknowledged -isnot [bool] -or
            (($status.sourceInstanceId -ceq $status.targetInstanceId) -ne ($scope -ceq 'SameInstance'))) {
            throw 'The original CLI result lost its scope, distinct publication or completed unfenced outcome.'
        }
        if ($case.laterSourceMemberId -ceq $status.sourceMemberId -or $case.laterSourcePublicIdentityReused -isnot [bool] -or
            $case.laterSourcePublicIdentityReused -ne ($case.laterSourceItemId -ceq $case.sourceItemId)) {
            throw 'A later source generation must retain its distinct registered member and accurately report any reused public identity.'
        }
        $priorPhase = [DateTimeOffset]::MinValue
        foreach ($field in @('copyStartedAt', 'copyAcceptedAt', 'sourceDeleteStartedAt', 'sourceDeleteAcceptedAt', 'sourceNativeRetiredAt', 'sourceNameSettledAt')) {
            $instant = [DateTimeOffset]::MinValue
            if (-not [DateTimeOffset]::TryParse([string]$status.$field, [ref]$instant) -or $instant -eq [DateTimeOffset]::MinValue -or $instant -lt $priorPhase) { throw 'An actual retained transfer phase is missing or out of order.' }
            $priorPhase = $instant
        }
        $acknowledged = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse([string]$retained.acknowledgedAt, [ref]$acknowledged) -or $acknowledged -lt $priorPhase) { throw 'The retained acknowledgement cannot precede original source settlement.' }
        $original = $status | ConvertTo-Json -Depth 5 -Compress
        if (($case.resumedStatus | ConvertTo-Json -Depth 5 -Compress) -cne $original -or
            ($case.restartedStatus | ConvertTo-Json -Depth 5 -Compress) -cne $original) { throw 'Original completion history changed across resume or Host restart.' }
        foreach ($field in @('sourceLocalMissing', 'sourceRemoteMissing', 'freshRootsAfterMove', 'freshRootsBeforeReplay', 'laterRemoteSourcePreserved')) {
            if ($case.$field -isnot [bool] -or -not $case.$field) { throw "A real transfer effect or current admission observation is missing: $field" }
        }
        foreach ($field in @('sourceSha256', 'targetLocalSha256', 'targetRemoteSha256', 'laterTargetSha256', 'restartedTargetSha256', 'laterSourceSha256', 'resumedSourceSha256')) {
            if ($case.$field -cnotmatch '\A[0-9a-fA-F]{64}\z') { throw 'A whole-file transfer or later-generation digest is missing.' }
        }
        if ($case.sourceSha256 -ine $case.targetLocalSha256 -or $case.sourceSha256 -ine $case.targetRemoteSha256 -or
            $case.laterTargetSha256 -ine $case.restartedTargetSha256 -or $case.laterSourceSha256 -ine $case.resumedSourceSha256 -or
            $retained.operationId -cne $case.operationId -or $retained.sourceRootId -cne $status.sourceRootId -or
            $retained.targetRootId -cne $status.targetRootId -or $retained.sourceMemberId -cne $status.sourceMemberId -or
            $retained.targetMemberId -cne $status.targetMemberId -or $retained.sourceItemId -cne $case.sourceItemId -or
            $retained.targetItemId -cne $case.targetItemId -or $retained.sha256 -ine $case.sourceSha256 -or
            $retained.privateBytesVerified -isnot [bool] -or -not $retained.privateBytesVerified -or $retained.sourceNameEpoch -lt 2 -or
            $retained.originalJournalCount -lt 0 -or $null -eq $retained.originalJournalCount -or
            ($retained.acknowledgementKind -ceq 'OriginalJournal') -ne ($retained.originalJournalCount -gt 0) -or
            $status.journalAcknowledged -ne ($retained.originalJournalCount -gt 0) -or
            $retained.acknowledgementKind -cnotin @('OriginalJournal', 'CommandWithoutCapturedNotifications')) {
            throw 'Actual file outcomes and stopped-Host original receipts disagree, or an empty claim falsely reports journal acknowledgement.'
        }
    }
    if ($Evidence.audit.schemaVersion -ne 1 -or $Evidence.audit.exclusiveCatalogOwner -isnot [bool] -or -not $Evidence.audit.exclusiveCatalogOwner -or
        @($Evidence.audit.transfers).Count -ne 2 -or $Evidence.hostProcessBeforeRestart -le 0 -or $Evidence.hostProcessAfterRestart -le 0 -or
        $Evidence.hostProcessBeforeRestart -eq $Evidence.hostProcessAfterRestart) { throw 'The transfer oracle requires exclusive catalog ownership and an actual Host process restart.' }
    if (-not $BeforeCleanup -and ($Evidence.cleanup.exclusiveCatalogOwner -ne $true -or $Evidence.cleanup.protectedNamespace -ne $true -or
        $Evidence.cleanup.validatedObjects -lt 4 -or $Evidence.cleanup.restoredObjects -ne $Evidence.cleanup.validatedObjects -or
        $Evidence.cleanup.cloudRootUnregistered -ne $true -or $Evidence.fixtureRemoved -ne $true)) {
        throw 'The final transfer report requires completed authenticated permission cleanup and fixture removal.'
    }
}
