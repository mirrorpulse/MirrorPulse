function Assert-CliRootLabelLifecycleEvidence {
    param([Parameter(Mandatory)][object]$Evidence,
        [Parameter(Mandatory)][string]$OriginalsDirectory)

    function Read-RootLabelOriginal([string]$Phase) {
        $path = Join-Path $OriginalsDirectory ($Phase + '.json')
        $file = Get-Item -LiteralPath $path -ErrorAction Stop
        if ($file.Length -gt 4MB -or $file.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Root Label originals must be bounded ordinary files.' }
        $envelope = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($envelope.kind -cne 'result' -or $null -eq $envelope.data) { throw 'Require an original typed CLI result.' }
        return $envelope.data
    }
    function Require-RootLabelGuid([object]$Value) {
        if ([string]$Value -cnotmatch '\A[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\z' -or
            [guid]::Parse([string]$Value) -eq [guid]::Empty) { throw 'An original stable root identity is missing.' }
    }
    foreach ($field in @('defaultHostIntegrated','actualWorkerRequired','preparedIntentAcrossNewRole','interruptedNativeExecution','journalAckReceiptInspected')) {
        if ($Evidence.$field -isnot [bool]) { throw 'Lifecycle evidence requires actual Boolean observations.' }
    }
    if ($Evidence.schemaVersion -ne 1 -or $Evidence.defaultHostIntegrated -ne $true -or $Evidence.actualWorkerRequired -ne $true -or
        $Evidence.preparedIntentAcrossNewRole -ne $false -or $Evidence.interruptedNativeExecution -ne $false -or
        $Evidence.journalAckReceiptInspected -ne $false -or $Evidence.memberCount -lt 3) { throw 'Require the exact public Host lifecycle boundary without unexecuted recovery or ACK claims.' }
    foreach ($field in @('rootId','instanceId','memberId','nativeItemId')) { Require-RootLabelGuid $Evidence.$field }
    $executions = @($Evidence.executions)
    if ($executions.Count -ne 3 -or @($executions.operationId | Select-Object -Unique).Count -ne 3) { throw 'Require three distinct original Label commands.' }
    $initial = Read-RootLabelOriginal 'initial-member'
    $tree = @(Read-RootLabelOriginal 'initial-tree')
    if ($initial.memberId -cne $Evidence.memberId -or $initial.nativeItemId -cne $Evidence.nativeItemId -or
        $initial.rootId -cne $Evidence.rootId -or $tree.Count -ne $Evidence.memberCount -or
        @($tree.memberId | Select-Object -Unique).Count -ne $tree.Count) { throw 'The reported original root disagrees with the actual public inventory.' }
    $previous = $Evidence.restoredDirectoryName
    foreach ($phase in @('active','disabled-case','restore')) {
        $rows = @($executions | Where-Object phase -ceq $phase)
        if ($rows.Count -ne 1) { throw 'Missing or duplicate public root command phase.' }
        $row = $rows[0]; Require-RootLabelGuid $row.operationId
        $disabled = $phase -cne 'active'
        $prepared = Read-RootLabelOriginal ($phase + '-prepare')
        $completed = Read-RootLabelOriginal ($phase + '-execute')
        if ($row.disabled -isnot [bool] -or $row.currentTreeAudited -isnot [bool] -or
            $completed.currentTreeAudited -isnot [bool] -or $completed.namespaceReady -isnot [bool]) { throw 'Execution admission fields must retain their typed values.' }
        if ($row.rootId -cne $Evidence.rootId -or $row.memberId -cne $Evidence.memberId -or $row.nativeItemId -cne $Evidence.nativeItemId -or
            $row.sourceName -cne $previous -or $row.memberCount -ne $tree.Count -or $row.currentTreeAudited -ne $true -or
            $row.disabled -ne $disabled -or $prepared.operationId -cne $row.operationId -or $prepared.rootId -cne $Evidence.rootId -or
            $prepared.sourceName -cne $row.sourceName -or $prepared.targetName -cne $row.targetName -or $prepared.phase -cne 'Prepared' -or
            $completed.operationId -cne $row.operationId -or $completed.rootId -cne $Evidence.rootId -or $completed.phase -cne 'Completed' -or
            $completed.sourceName -cne $row.sourceName -or $completed.targetName -cne $row.targetName -or
            $completed.currentDirectoryName -cne $row.targetName -or $completed.memberCount -ne $tree.Count -or
            $completed.currentTreeAudited -ne $true -or ($disabled -and $completed.namespaceReady -ne $false) -or
            ($row.originalReceipt | ConvertTo-Json -Depth 12 -Compress) -cne ($completed | ConvertTo-Json -Depth 12 -Compress)) { throw 'A public execution claim disagrees with its original preparation, completion or current audit.' }
        $previous = $row.targetName
    }
    $active = @($executions | Where-Object phase -ceq 'active')[0]
    $case = @($executions | Where-Object phase -ceq 'disabled-case')[0]
    if ($case.targetName -ceq $case.sourceName -or -not $case.targetName.Equals($case.sourceName, [StringComparison]::OrdinalIgnoreCase) -or
        $previous -cne $Evidence.restoredDirectoryName) { throw 'Require an actual case-only Label and restoration of the fixture name.' }
    foreach ($pair in @(@{phase='active-restart';record=$Evidence.activeRestart},
        @{phase='disabled-restart';record=$Evidence.disabledRestart},@{phase='restored-restart';record=$Evidence.restoredRestart})) {
        $before = Read-RootLabelOriginal ($pair.phase + '-before')
        $after = Read-RootLabelOriginal ($pair.phase + '-after')
        if ($pair.record.previousProcessExited -isnot [bool] -or $pair.record.previousProcessExited -ne $true -or $before.processId -le 0 -or $after.processId -le 0 -or
            $before.processId -eq $after.processId -or $pair.record.previousProcessId -ne $before.processId -or
            $pair.record.currentProcessId -ne $after.processId) { throw 'Require the actual original and independently restarted Host process identities.' }
    }
    foreach ($phase in @('active','active-restarted','disabled-case','disabled-restarted','restore','restored')) {
        $disabled = $phase -cin @('disabled-case','disabled-restarted','restore')
        $name = if ($phase -cin @('active','active-restarted')) { $active.targetName }
            elseif ($phase -cin @('disabled-case','disabled-restarted')) { $case.targetName } else { $Evidence.restoredDirectoryName }
        $roots = @(Read-RootLabelOriginal ($phase + '-roots'))
        $status = Read-RootLabelOriginal ($phase + '-status')
        $root = @($roots | Where-Object { $_.root.rootId -ceq $Evidence.rootId })
        $instance = @($status.instances | Where-Object instanceId -ceq $Evidence.instanceId)
        if ($root.Count -ne 1 -or $instance.Count -ne 1 -or $root[0].root.directoryName -cne $name -or $root[0].root.label -cne $name -or
            $instance[0].workerConnected -isnot [bool] -or $root[0].namespaceReady -isnot [bool] -or
            $root[0].syncState -cne $(if ($disabled) { 'Disabled' } else { 'Active' }) -or $null -ne $root[0].pendingRename -or
            $(if ($disabled) { $instance[0].phase -cne 'Offline' -or $instance[0].workerConnected -ne $false }
              else { $root[0].namespaceReady -ne $true -or $instance[0].workerConnected -ne $true })) { throw 'The original lifecycle state does not prove active readiness or disabled source isolation.' }
    }
    foreach ($phase in @('active','active-restarted','disabled-case','disabled-restarted','historical-replay','restore','restored')) {
        $name = switch ($phase) {
            { $_ -cin @('active','active-restarted') } { $active.targetName; break }
            { $_ -cin @('disabled-case','disabled-restarted','historical-replay') } { $case.targetName; break }
            default { $Evidence.restoredDirectoryName }
        }
        $member = Read-RootLabelOriginal ($phase + '-member')
        $current = @(Read-RootLabelOriginal ($phase + '-tree'))
        if ($member.memberId -cne $initial.memberId -or $member.nativeItemId -cne $initial.nativeItemId -or $member.relativePath -cne $name -or
            $member.actualParentMemberId -cne $initial.actualParentMemberId -or $member.rootId -cne $initial.rootId -or
            $current.Count -ne $tree.Count) { throw 'A restart or Label execution replaced a root, anchor or complete inventory.' }
        foreach ($entry in $tree) {
            $match = @($current | Where-Object memberId -ceq $entry.memberId)
            $originalName = $Evidence.restoredDirectoryName
            if ($entry.relativePath -cne $originalName -and -not $entry.relativePath.StartsWith($originalName + '/', [StringComparison]::Ordinal)) { throw 'The original member graph escapes its root.' }
            $expected = $name + $entry.relativePath.Substring($originalName.Length)
            if ($entry.isDirectory -isnot [bool] -or $match.Count -ne 1 -or $match[0].isDirectory -isnot [bool] -or
                $match[0].parentMemberId -cne $entry.parentMemberId -or $match[0].relativePath -cne $expected -or
                $match[0].isDirectory -ne $entry.isDirectory -or $match[0].depth -ne $entry.depth) { throw 'The original whole member graph was not retained.' }
        }
        $bytePath = Join-Path $OriginalsDirectory ($phase + '-bytes.json')
        $byteFile = Get-Item -LiteralPath $bytePath -ErrorAction Stop
        if ($byteFile.Length -gt 4096 -or $byteFile.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Require bounded original byte observations.' }
        $observed = Get-Content -LiteralPath $bytePath -Raw | ConvertFrom-Json
        if ($observed.schemaVersion -ne 1 -or $observed.phase -cne $phase -or
            ($observed.payloads | ConvertTo-Json -Depth 5 -Compress) -cne ($Evidence.residentAndSourceBytes | ConvertTo-Json -Depth 5 -Compress)) { throw 'The original per-phase resident/source bytes disagree with their first retained hashes.' }
    }
    $historical = Read-RootLabelOriginal 'historical-replay'
    if ($historical.currentTreeAudited -isnot [bool] -or $historical.namespaceReady -isnot [bool] -or
        $historical.operationId -cne $active.operationId -or $historical.phase -cne 'Completed' -or
        $historical.sourceName -cne $active.sourceName -or $historical.targetName -cne $active.targetName -or
        $historical.currentDirectoryName -cne $case.targetName -or $historical.memberCount -ne $tree.Count -or
        $historical.currentTreeAudited -ne $true -or $historical.namespaceReady -ne $false -or
        ($Evidence.historicalReplay | ConvertTo-Json -Depth 12 -Compress) -cne ($historical | ConvertTo-Json -Depth 12 -Compress)) { throw 'The original replay did not retain its immutable result and the later current name.' }
    $payloads = @($Evidence.residentAndSourceBytes)
    if ($payloads.Count -ne 2) { throw 'Require both resident/source payload comparisons.' }
    foreach ($name in @('queued-upload.txt','cli-roundtrip.txt')) {
        $payload = @($payloads | Where-Object name -ceq $name)
        if ($payload.Count -ne 1 -or $payload[0].localSha256 -cnotmatch '\A[A-F0-9]{64}\z' -or
            $payload[0].sourceSha256 -cne $payload[0].localSha256) { throw 'An original private/source byte comparison is missing.' }
    }
}
