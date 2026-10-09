function Assert-CliRegressionObservations {
    param([Parameter(Mandatory)][object]$Evidence)

    foreach ($field in @('rangeRead', 'offlineQueueRetained', 'uploadJournalDrained', 'conflictPersisted', 'cursorPersisted')) {
        if ($Evidence.$field -isnot [bool] -or $Evidence.$field -ne $true) { throw "The CLI regression observation is missing: $field" }
    }
    $reads = @($Evidence.hydration.reads)
    $audits = @($Evidence.hydration.audits)
    if ($reads.Count -ne 4 -or $audits.Count -ne 2) { throw 'The repeated hydration observations are incomplete.' }
    foreach ($phase in @('Online', 'HostStopped', 'HostRestarted', 'InstanceDisabled')) {
        $match = @($reads | Where-Object phase -ceq $phase)
        if ($match.Count -ne 1) { throw "The external consumer phase is missing or repeated: $phase" }
        $read = $match[0]
        foreach ($field in @('wholeFileReads', 'rangeReads', 'binaryLength')) {
            if ($read.$field -isnot [int] -and $read.$field -isnot [long]) { throw "The external read count is missing: $field" }
        }
        if ($read.schemaVersion -ne 1 -or $read.wholeFileReads -ne 6 -or $read.rangeReads -ne 21 -or
            $read.binaryLength -ne 2097409 -or $read.binarySha256 -cnotmatch '\A[0-9a-f]{64}\z') {
            throw "The external consumer did not verify all full and boundary reads: $phase"
        }
    }
    if (@($reads.binarySha256 | Select-Object -Unique).Count -ne 1) { throw 'The resident file bytes changed across Host or instance lifecycle changes.' }
    foreach ($phase in @('BeforeLocalWrite', 'AfterLocalWrite')) {
        $match = @($audits | Where-Object phase -ceq $phase)
        if ($match.Count -ne 1) { throw "The retained intent audit is missing or repeated: $phase" }
        $audit = $match[0]
        foreach ($field in @('enumeratedMutations', 'readOnlyFileMutations', 'queuedFileMutations', 'acknowledgedQueuedFileMutations')) {
            if ($audit.$field -isnot [int] -and $audit.$field -isnot [long] -or $audit.$field -lt 0) {
                throw "The retained intent count is missing or invalid: $field"
            }
        }
        if ($audit.schemaVersion -ne 1 -or $audit.routeVerified -isnot [bool] -or $audit.routeVerified -ne $true -or
            $audit.readOnlyFileMutations -ne 0 -or $audit.enumeratedMutations -lt $audit.queuedFileMutations -or
            $audit.acknowledgedQueuedFileMutations -ne $audit.queuedFileMutations -or
            ($phase -ceq 'BeforeLocalWrite' -and $audit.queuedFileMutations -ne 0) -or
            ($phase -ceq 'AfterLocalWrite' -and $audit.queuedFileMutations -lt 1)) {
            throw "Hydration recorded an upload or the real queued write was not acknowledged: $phase"
        }
    }
}
