function Assert-CliPreparedTransferObservation {
    param([object]$Observation, [object]$Prepared, [string]$SourceSha256)

    $expected = $Prepared | ConvertTo-Json -Depth 5 -Compress
    if ($null -eq $Prepared -or $Prepared.stage -cne 'Prepared' -or !$Prepared.preparedAt -or
        $Prepared.rootsFenced -isnot [bool] -or -not $Prepared.rootsFenced -or
        $Prepared.completed -isnot [bool] -or $Prepared.completed -or
        $Prepared.journalAcknowledged -isnot [bool] -or $Prepared.journalAcknowledged -or
        ($null -ne $Prepared.isDirectory -and ($Prepared.isDirectory -isnot [bool] -or $Prepared.isDirectory)) -or $null -ne $Prepared.targetMemberId -or
        @(@('copyStartedAt', 'copyAcceptedAt', 'sourceDeleteStartedAt', 'sourceDeleteAcceptedAt', 'sourceNativeRetiredAt', 'sourceNameSettledAt') |
            Where-Object { $null -ne $Prepared.$_ }).Count -or
        ($Observation.status | ConvertTo-Json -Depth 5 -Compress) -cne $expected -or
        @($Observation.sourcePending).Count -ne 1 -or @($Observation.targetPending).Count -ne 1 -or
        ($Observation.sourcePending[0] | ConvertTo-Json -Depth 5 -Compress) -cne $expected -or
        ($Observation.targetPending[0] | ConvertTo-Json -Depth 5 -Compress) -cne $expected -or
        $Observation.sourceLocalSha256 -ine $SourceSha256 -or $Observation.sourceRemoteSha256 -ine $SourceSha256 -or
        $Observation.targetLocalMissing -isnot [bool] -or -not $Observation.targetLocalMissing -or
        $Observation.targetRemoteMissing -isnot [bool] -or -not $Observation.targetRemoteMissing) {
        throw 'The original prepared transfer must remain fenced on both roots, with intact source bytes and no target or started copy.'
    }
}

function Assert-CliPreparedTransferRestart {
    param([object]$Restart, [object]$Case, [object]$Evidence)

    $prepared = $Restart.command
    Assert-CliPreparedTransferObservation -Observation $Restart.beforeRestart -Prepared $prepared -SourceSha256 $Case.sourceSha256
    Assert-CliPreparedTransferObservation -Observation $Restart.afterRestart -Prepared $prepared -SourceSha256 $Case.sourceSha256
    $status = $Case.status
    foreach ($field in @('operationId', 'sourceRootId', 'targetRootId', 'sourceInstanceId', 'targetInstanceId',
        'sourceMemberId', 'sourceRelativePath', 'targetRelativePath', 'preparedAt')) {
        if ($prepared.$field -cne $status.$field) { throw 'The resumed effect belongs to a different prepared transfer.' }
    }
    $prior = [DateTimeOffset]::MinValue; $copy = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse([string]$prepared.preparedAt, [ref]$prior) -or
        -not [DateTimeOffset]::TryParse([string]$status.copyStartedAt, [ref]$copy) -or $copy -lt $prior) {
        throw 'The retained copy cannot precede the original preparation.'
    }
    $workers = @($Restart.resumeWorkers)
    if (($Restart.preparedBy -isnot [int] -and $Restart.preparedBy -isnot [long]) -or $Restart.preparedBy -le 0 -or
        $Restart.preparedBy -ne $Evidence.hostProcessBeforeRestart -or
        ($Restart.resumedBy -isnot [int] -and $Restart.resumedBy -isnot [long]) -or $Restart.resumedBy -le 0 -or
        $Restart.resumedBy -in @($Restart.preparedBy, $Evidence.hostProcessAfterRestart) -or $workers.Count -ne 2 -or
        @($workers.processId | Select-Object -Unique).Count -ne 2 -or
        @($workers | Where-Object { ($_.processId -isnot [int] -and $_.processId -isnot [long]) -or $_.processId -le 0 -or
            $_.processId -in @($Evidence.workerProcessIds) -or $_.adapterId -cnotin @('com.mirrorpulse.adapter.local', 'com.mirrorpulse.adapter.webdav') }).Count) {
        throw 'Resumed first copy requires independent Host and signed Worker processes.'
    }
    if ($Evidence.schemaVersion -eq 3) {
        foreach ($package in $Evidence.packages) {
            if (@($workers | Where-Object { $_.installId -ceq $package.installId -and $_.adapterId -ceq $package.adapterId }).Count -ne 1) {
                throw 'First copy after restart must use the same two signed package installations.'
            }
        }
    } else {
        $install = [guid]::Empty
        if (-not [guid]::TryParseExact([string]$Evidence.localInstallId, 'D', [ref]$install) -or $install -eq [guid]::Empty -or
            @($workers | Where-Object { $_.adapterId -cne 'com.mirrorpulse.adapter.local' -or $_.installId -cne $Evidence.localInstallId }).Count) {
            throw 'Both resumed Local Workers must come from the original signed Local installation.'
        }
    }
}

function Assert-CliTransferObservations {
    param([Parameter(Mandatory)][object]$Evidence, [switch]$BeforeCleanup, [switch]$RequireSingleRoot,
        [switch]$RequireDirectory, [switch]$RequirePreparedRestart)

    $crossPackage = $Evidence.schemaVersion -eq 3
    $singleRoot = $Evidence.schemaVersion -in @(2, 3)
    if ($Evidence.schemaVersion -notin @(1, 2, 3) -or (($RequireSingleRoot -or $RequirePreparedRestart) -and -not $singleRoot) -or
        $Evidence.sourceSha -cnotmatch '\A[0-9a-f]{40}\z' -or $Evidence.runtime -notin @('win-x64', 'win-arm64') -or
        $Evidence.signedInstallation -isnot [bool] -or -not $Evidence.signedInstallation -or
        $Evidence.packageSha256 -cnotmatch '\A[0-9a-fA-F]{64}\z' -or
        $Evidence.privateWorkerRuntime -isnot [bool] -or -not $Evidence.privateWorkerRuntime -or
        @($Evidence.workerProcessIds).Count -ne 2 -or @($Evidence.workerProcessIds | Select-Object -Unique).Count -ne 2 -or
        @($Evidence.workerProcessIds | Where-Object { ($_ -isnot [int] -and $_ -isnot [long]) -or $_ -le 0 }).Count) {
        throw 'The transfer evidence requires its actual signed installation and two independent private-runtime Worker processes.'
    }
    if ($crossPackage) {
        $packages = @($Evidence.packages); $bindings = @($Evidence.workerBindings); $server = $Evidence.webDavServer
        if ($packages.Count -ne 2 -or $bindings.Count -ne 2 -or @($packages.installId | Select-Object -Unique).Count -ne 2 -or
            @($packages.adapterId | Select-Object -Unique).Count -ne 2 -or
            @($packages | Where-Object { $_.adapterId -cnotin @('com.mirrorpulse.adapter.local', 'com.mirrorpulse.adapter.webdav') -or
                $_.isSigned -isnot [bool] -or -not $_.isSigned -or $_.packageSha256 -cnotmatch '\A[0-9a-fA-F]{64}\z' }).Count -or
            @($bindings.installId | Select-Object -Unique).Count -ne 2 -or
            @(Compare-Object @($bindings.processId) @($Evidence.workerProcessIds)).Count -or
            $server.schemaVersion -ne 1 -or $server.stopped -isnot [bool] -or -not $server.stopped -or
            $server.fileBacked -isnot [bool] -or -not $server.fileBacked -or
            @(@($server.reads, $server.puts, $server.deletes) | Where-Object { $_ -isnot [int] -and $_ -isnot [long] }).Count -or
            $server.reads -lt 1 -or $server.puts -lt 2 -or $server.deletes -lt 1) {
            throw 'Cross-package evidence requires both signed package identities, independent Workers and actual conditional HTTP file effects.'
        }
        foreach ($binding in $bindings) {
            $package = @($packages | Where-Object { $_.installId -ceq $binding.installId -and $_.adapterId -ceq $binding.adapterId })
            $install = [guid]::Empty
            if ($package.Count -ne 1 -or -not [guid]::TryParseExact([string]$binding.installId, 'D', [ref]$install) -or $install -eq [guid]::Empty) {
                throw 'Each actual Worker must belong to its exact signed installation.'
            }
        }
        $local = @($packages | Where-Object adapterId -ceq 'com.mirrorpulse.adapter.local')
        if ($local[0].packageSha256 -ine $Evidence.packageSha256) { throw 'The cross-package Local identity changed.' }
    }
    $cases = @($Evidence.cases)
    $rootCount = if ($singleRoot) { 2 } else { 3 }
    if ($cases.Count -ne 2 -or @($cases.operationId | Select-Object -Unique).Count -ne 2 -or
        @($Evidence.roots).Count -ne $rootCount -or @($Evidence.roots.rootId | Select-Object -Unique).Count -ne $rootCount -or
        @($Evidence.roots.instanceId | Select-Object -Unique).Count -ne 2) {
        throw 'The transfer evidence requires exactly two original commands across three stable roots and two instances.'
    }
    $caseRoots = @(@($cases.status.sourceRootId) + @($cases.status.targetRootId) | Select-Object -Unique)
    if ($caseRoots.Count -ne $rootCount -or @(Compare-Object $caseRoots @($Evidence.roots.rootId)).Count) { throw 'The original command roots disagree with the retained fixture routes.' }
    $caseKeys = if ($singleRoot) { @('Forward', 'Reverse') } else { @('SameInstance', 'CrossInstance') }
    foreach ($key in $caseKeys) {
        # The producer holds live ordered dictionaries; assigning an if result
        # unwraps a one-element array and exposes the dictionary's field Count.
        # Capture the complete pipeline so Count always measures matched objects.
        $selected = @(if ($singleRoot) { $cases | Where-Object direction -ceq $key } else { $cases | Where-Object scope -ceq $key })
        $receipt = @(if ($singleRoot) { $Evidence.audit.transfers | Where-Object direction -ceq $key } else { $Evidence.audit.transfers | Where-Object scope -ceq $key })
        if ($selected.Count -ne 1 -or $receipt.Count -ne 1) { throw 'Both original transfer scopes must execute exactly once.' }
        $case = $selected[0]; $retained = $receipt[0]; $status = $case.status
        $scope = if ($singleRoot) { 'CrossInstance' } else { $key }
        if ($singleRoot) {
            $sourceSide = if ($key -ceq 'Forward') { 'left' } else { 'right' }
            $targetSide = if ($key -ceq 'Forward') { 'right' } else { 'left' }
            $source = @($Evidence.roots | Where-Object fixtureSide -ceq $sourceSide)
            $target = @($Evidence.roots | Where-Object fixtureSide -ceq $targetSide)
            $sourceKey = if ($crossPackage -and $key -ceq 'Reverse') { 'webdav' } else { 'local' }
            $targetKey = if ($crossPackage -and $key -ceq 'Forward') { 'webdav' } else { 'local' }
            if ($source.Count -ne 1 -or $target.Count -ne 1 -or $source[0].rootKey -cne $sourceKey -or $target[0].rootKey -cne $targetKey -or
                $case.scope -cne 'CrossInstance' -or $retained.scope -cne 'CrossInstance' -or
                $status.sourceRootId -cne $source[0].rootId -or $status.targetRootId -cne $target[0].rootId -or
                $status.sourceInstanceId -cne $source[0].instanceId -or $status.targetInstanceId -cne $target[0].instanceId) {
                throw 'Each single-root direction must retain its actual distinct instances and declared local roots.'
            }
        }
        foreach ($id in @($case.operationId, $status.sourceRootId, $status.targetRootId, $status.sourceMemberId,
            $status.targetMemberId, $status.sourceInstanceId, $status.targetInstanceId, $case.sourceItemId, $case.targetItemId, $case.laterSourceMemberId, $case.laterSourceItemId)) {
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
        if ($singleRoot -and $key -ceq 'Forward' -and ($RequirePreparedRestart -or $null -ne $case.preparedRestart)) {
            Assert-CliPreparedTransferRestart -Restart $case.preparedRestart -Case $case -Evidence $Evidence
        }
    }
    if ($Evidence.audit.schemaVersion -ne $Evidence.schemaVersion -or $Evidence.audit.exclusiveCatalogOwner -isnot [bool] -or -not $Evidence.audit.exclusiveCatalogOwner -or
        @($Evidence.audit.transfers).Count -ne 2 -or $Evidence.hostProcessBeforeRestart -le 0 -or $Evidence.hostProcessAfterRestart -le 0 -or
        $Evidence.hostProcessBeforeRestart -eq $Evidence.hostProcessAfterRestart) { throw 'The transfer oracle requires exclusive catalog ownership and an actual Host process restart.' }
    if ($RequireDirectory -or $null -ne $Evidence.directory -or $null -ne $Evidence.audit.directoryTransfer) {
        $case = $Evidence.directory
        $receipt = $Evidence.audit.directoryTransfer
        $deleteReceipt = [guid]::Empty
        if ($Evidence.schemaVersion -ne 2 -or !$case -or !$receipt -or $case.operationId -cne $receipt.operationId -or
            $case.sourceRootId -cne $receipt.sourceRootId -or $case.targetRootId -cne $receipt.targetRootId -or
            $case.sourceMemberId -cne $receipt.sourceMemberId -or $case.sourceRootId -ceq $case.targetRootId -or
            $case.sourceLocalMissing -ne $true -or $case.sourceRemoteMissing -ne $true -or
            $null -eq $case.pendingSource -or $null -eq $case.pendingTarget -or $case.pendingSource -ne 0 -or $case.pendingTarget -ne 0 -or
            $case.originalSha256 -cnotmatch '\A[A-F0-9]{64}\z' -or
            @(@($case.targetLocalSha256, $case.targetRemoteSha256, $case.restartedTargetSha256, $receipt.sha256) | Where-Object { $_ -cne $case.originalSha256 }).Count -or
            $receipt.originalMemberCount -ne 3 -or $receipt.length -le 0 -or $null -eq $receipt.originalJournalCount -or $receipt.originalJournalCount -lt 0 -or
            ![guid]::TryParseExact($receipt.sourceDeleteReceiptId, 'D', [ref]$deleteReceipt) -or $deleteReceipt -eq [guid]::Empty -or !$receipt.acknowledgedAt) {
            throw 'Directory CLI evidence lost its actual copied bytes, retained original receipt, source absence or root status.'
        }
        foreach ($status in @($case.move, $case.status, $case.restarted)) {
            if ($status.operationId -cne $receipt.operationId -or $status.sourceRootId -cne $receipt.sourceRootId -or
                $status.targetRootId -cne $receipt.targetRootId -or $status.sourceMemberId -cne $receipt.sourceMemberId -or
                $status.targetMemberId -cne $receipt.targetMemberId -or $status.sourceRelativePath -cne $case.sourceRelativePath -or
                $status.targetRelativePath -cne $case.targetRelativePath -or $status.isDirectory -ne $true -or
                $status.completed -ne $true -or $status.rootsFenced -ne $false -or
                $status.journalAcknowledged -ne ($receipt.originalJournalCount -gt 0)) {
                throw 'Directory move, status and restarted resume must report the same original directory completion.'
            }
        }
    }
    if (-not $BeforeCleanup -and ($Evidence.cleanup.exclusiveCatalogOwner -ne $true -or $Evidence.cleanup.protectedNamespace -ne $true -or
        $Evidence.cleanup.validatedObjects -lt 4 -or $Evidence.cleanup.restoredObjects -ne $Evidence.cleanup.validatedObjects -or
        $Evidence.cleanup.cloudRootUnregistered -ne $true -or $Evidence.fixtureRemoved -ne $true)) {
        throw 'The final transfer report requires completed authenticated permission cleanup and fixture removal.'
    }
}
