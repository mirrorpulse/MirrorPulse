function Assert-CliRootLabelCancellationEvidence {
    param([Parameter(Mandatory)][object]$Evidence,
        [Parameter(Mandatory)][string]$OriginalsDirectory)

    function Read-CancellationOriginal([string]$Phase) {
        $file = Get-Item -LiteralPath (Join-Path $OriginalsDirectory ($Phase + '.json')) -ErrorAction Stop
        if ($file.Length -gt 4MB -or $file.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Require bounded ordinary cancellation originals.' }
        $envelope = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if ($envelope.kind -cne 'result' -or $null -eq $envelope.data) { throw 'Require the original typed public cancellation response.' }
        return $envelope.data
    }
    function Require-CancellationGuid([object]$Value) {
        if ([string]$Value -cnotmatch '\A[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\z' -or
            [guid]::Parse([string]$Value) -eq [guid]::Empty) { throw 'An original cancellation identity is missing.' }
    }
    function Same-CancellationObject([object]$Actual, [object]$Expected) {
        if (($Actual | ConvertTo-Json -Depth 12 -Compress) -cne ($Expected | ConvertTo-Json -Depth 12 -Compress)) { throw 'A cancellation summary disagrees with its original public receipt.' }
    }
    foreach ($field in @('defaultHostIntegrated', 'actualWorkerRequired', 'preparedIntentAcrossIndependentHost', 'interruptedNativeExecution', 'journalAckReceiptInspected')) {
        if ($Evidence.$field -isnot [bool]) { throw 'Cancellation evidence requires actual Boolean observations.' }
    }
    if ($Evidence.schemaVersion -ne 1 -or $Evidence.memberCount -lt 3 -or
        $Evidence.defaultHostIntegrated -ne $true -or $Evidence.actualWorkerRequired -ne $true -or
        $Evidence.preparedIntentAcrossIndependentHost -ne $true -or $Evidence.interruptedNativeExecution -ne $false -or
        $Evidence.journalAckReceiptInspected -ne $false -or [string]::IsNullOrWhiteSpace($Evidence.directoryName)) { throw 'Require the independent public Host cancellation boundary without unexecuted fault or ACK claims.' }
    foreach ($field in @('rootId', 'instanceId', 'memberId', 'nativeItemId')) { Require-CancellationGuid $Evidence.$field }
    $initial = Read-CancellationOriginal 'initial-member'
    $tree = @(Read-CancellationOriginal 'initial-tree')
    if ($initial.rootId -cne $Evidence.rootId -or $initial.memberId -cne $Evidence.memberId -or
        $initial.nativeItemId -cne $Evidence.nativeItemId -or $initial.relativePath -cne $Evidence.directoryName -or
        $tree.Count -ne $Evidence.memberCount -or @($tree.memberId | Select-Object -Unique).Count -ne $tree.Count -or
        @($tree | Where-Object memberId -ceq $initial.memberId).Count -ne 1) { throw 'Require the complete original root and member inventory.' }
    foreach ($entry in $tree) {
        Require-CancellationGuid $entry.memberId
        if ($entry.isDirectory -isnot [bool] -or $entry.depth -lt 0 -or
            ($entry.relativePath -cne $Evidence.directoryName -and -not $entry.relativePath.StartsWith($Evidence.directoryName + '/', [StringComparison]::Ordinal))) { throw 'An original cancellation member escaped its complete root graph.' }
    }
    $bytes = @($Evidence.residentAndSourceBytes)
    if ($bytes.Count -ne 2 -or @($bytes.name | Select-Object -Unique).Count -ne 2) { throw 'Require both original resident/source payloads.' }
    foreach ($payload in $bytes) {
        if ($payload.name -cnotin @('queued-upload.txt', 'cli-roundtrip.txt') -or
            $payload.localSha256 -cnotmatch '\A[0-9A-F]{64}\z' -or $payload.sourceSha256 -cne $payload.localSha256) { throw 'The original cancellation payload hashes disagree.' }
    }
    function Require-CancellationState([string]$Phase, [string]$Name, [bool]$Disabled, [AllowNull()][object]$Preparation) {
        $roots = @(Read-CancellationOriginal ($Phase + '-roots'))
        $status = Read-CancellationOriginal ($Phase + '-status')
        $root = @($roots | Where-Object { $_.root.rootId -ceq $Evidence.rootId })
        $instance = @($status.instances | Where-Object instanceId -ceq $Evidence.instanceId)
        if ($root.Count -ne 1 -or $instance.Count -ne 1 -or $root[0].root.directoryName -cne $Name -or
            $root[0].root.label -cne $Name -or $root[0].namespaceReady -isnot [bool] -or
            $root[0].root.state -cne $(if ($Disabled) { 'Disabled' } else { 'Active' }) -or
            $instance[0].workerConnected -isnot [bool] -or
            $root[0].syncState -cne $(if ($null -ne $Preparation) { 'NamespaceRecovery' } elseif ($Disabled) { 'Disabled' } else { 'Active' }) -or
            ($Disabled -and ($instance[0].phase -cne 'Offline' -or $instance[0].workerConnected -ne $false))) { throw 'The original cancellation state lost its root availability or disabled source isolation.' }
        if ($null -ne $Preparation) {
            $pending = $root[0].pendingRename
            if ($pending.operationId -cne $Preparation.operationId -or $pending.phase -cne 'Prepared' -or
                $pending.sourceName -cne $Preparation.sourceName -or $pending.targetName -cne $Preparation.targetName -or
                $root[0].namespaceReady -ne $false) { throw 'The independent Host did not retain the exact closed original preparation.' }
        } elseif ($null -ne $root[0].pendingRename -or $root[0].namespaceReady -ne (-not $Disabled) -or
            (-not $Disabled -and $instance[0].workerConnected -ne $true)) { throw 'Historical cancellation did not retain separately observed current admission.' }
    }
    function Require-CancellationGraph([string]$Phase, [string]$Name) {
        $member = Read-CancellationOriginal ($Phase + '-member')
        $entries = @(Read-CancellationOriginal ($Phase + '-tree'))
        if ($member.memberId -cne $initial.memberId -or $member.rootId -cne $initial.rootId -or
            $member.nativeItemId -cne $initial.nativeItemId -or $member.actualParentMemberId -cne $initial.actualParentMemberId -or
            $member.relativePath -cne $Name -or $entries.Count -ne $tree.Count) { throw 'Cancellation changed a root, anchor or complete member inventory.' }
        foreach ($entry in $tree) {
            $matches = @($entries | Where-Object memberId -ceq $entry.memberId)
            $expectedPath = $Name + $entry.relativePath.Substring($Evidence.directoryName.Length)
            if ($matches.Count -ne 1 -or $matches[0].parentMemberId -cne $entry.parentMemberId -or
                $matches[0].isDirectory -isnot [bool] -or $matches[0].isDirectory -ne $entry.isDirectory -or
                $matches[0].relativePath -cne $expectedPath -or $matches[0].depth -ne $entry.depth) { throw 'Cancellation or historical replay changed the original member graph.' }
        }
        $file = Get-Item -LiteralPath (Join-Path $OriginalsDirectory ($Phase + '-bytes.json')) -ErrorAction Stop
        if ($file.Length -gt 4096 -or $file.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Require bounded original cancellation byte observations.' }
        $observed = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if ($observed.schemaVersion -ne 1 -or $observed.phase -cne $Phase) { throw 'The original cancellation byte phase is missing.' }
        Same-CancellationObject $observed.payloads $bytes
    }
    Require-CancellationState 'initial' $Evidence.directoryName $false $null
    $cases = @($Evidence.cases)
    if ($cases.Count -ne 2) { throw 'Require enabled and disabled independent-process cancellation cases.' }
    $operations = [Collections.Generic.List[string]]::new()
    foreach ($phase in @('active', 'disabled')) {
        $selected = @($cases | Where-Object phase -ceq $phase)
        $disabled = $phase -ceq 'disabled'
        if ($selected.Count -ne 1 -or $selected[0].disabled -isnot [bool] -or $selected[0].disabled -ne $disabled) { throw 'The cancellation case availability is missing or duplicated.' }
        $row = $selected[0]
        $prepared = Read-CancellationOriginal ($phase + '-prepare')
        Require-CancellationGuid $prepared.operationId
        $operations.Add($prepared.operationId)
        Same-CancellationObject $row.preparation $prepared
        if ($prepared.phase -cne 'Prepared' -or $prepared.rootId -cne $Evidence.rootId -or
            $prepared.sourceName -cne $Evidence.directoryName -or $prepared.targetName -cne ($Evidence.directoryName + ' Cancel ' + $phase)) { throw 'The original public cancellation preparation changed its endpoints.' }
        Require-CancellationState ($phase + '-before') $Evidence.directoryName $disabled $null
        Require-CancellationState ($phase + '-prepared') $Evidence.directoryName $disabled $prepared
        Require-CancellationState ($phase + '-restarted') $Evidence.directoryName $disabled $prepared
        $before = Read-CancellationOriginal ($phase + '-restart-before')
        $after = Read-CancellationOriginal ($phase + '-restart-after')
        Read-CancellationOriginal ($phase + '-restart-stop') | Out-Null
        Read-CancellationOriginal ($phase + '-restart-start') | Out-Null
        if ($before.processId -le 0 -or $after.processId -le 0 -or $before.processId -eq $after.processId -or
            $row.restart.previousProcessId -ne $before.processId -or $row.restart.currentProcessId -ne $after.processId -or
            $row.restart.previousProcessExited -isnot [bool] -or $row.restart.previousProcessExited -ne $true) { throw 'Require the original preparing process to exit and an independent new Host.' }
        $cancelled = Read-CancellationOriginal ($phase + '-cancel')
        Same-CancellationObject $row.cancellation $cancelled
        if ($cancelled.operationId -cne $prepared.operationId -or $cancelled.rootId -cne $prepared.rootId -or
            $cancelled.phase -cne 'Cancelled' -or $cancelled.sourceName -cne $prepared.sourceName -or
            $cancelled.targetName -cne $prepared.targetName -or $null -eq $cancelled.cancelledAt -or
            [DateTimeOffset]$cancelled.cancelledAt -le [DateTimeOffset]::UnixEpoch -or
            $cancelled.currentDirectoryName -cne $Evidence.directoryName -or $cancelled.memberCount -ne $tree.Count -or
            $cancelled.currentTreeAudited -isnot [bool] -or $cancelled.currentTreeAudited -ne $true -or
            $cancelled.namespaceReady -isnot [bool] -or $cancelled.namespaceReady -ne (-not $disabled)) { throw 'Cancellation requires its original immutable receipt and separate fresh current audit.' }
        Require-CancellationState ($phase + '-cancelled') $Evidence.directoryName $disabled $null
        Require-CancellationGraph ($phase + '-cancelled') $Evidence.directoryName
        foreach ($step in @('later', 'restore')) {
            $source = if ($step -ceq 'later') { $Evidence.directoryName } else { $Evidence.directoryName + ' After ' + $phase }
            $target = if ($step -ceq 'later') { $Evidence.directoryName + ' After ' + $phase } else { $Evidence.directoryName }
            $preparation = Read-CancellationOriginal ($phase + '-' + $step + '-prepare')
            $execution = Read-CancellationOriginal ($phase + '-' + $step + '-execute')
            Require-CancellationGuid $preparation.operationId
            $operations.Add($preparation.operationId)
            if ($preparation.rootId -cne $Evidence.rootId -or $preparation.phase -cne 'Prepared' -or
                $preparation.sourceName -cne $source -or $preparation.targetName -cne $target -or
                $execution.operationId -cne $preparation.operationId -or $execution.rootId -cne $Evidence.rootId -or
                $execution.phase -cne 'Completed' -or $execution.sourceName -cne $source -or $execution.targetName -cne $target -or
                $execution.currentDirectoryName -cne $target -or $execution.memberCount -ne $tree.Count -or
                $execution.currentTreeAudited -isnot [bool] -or $execution.currentTreeAudited -ne $true -or
                $execution.namespaceReady -isnot [bool] -or $execution.namespaceReady -ne (-not $disabled)) { throw 'Require the distinct later Label and restoration with their fresh current audits.' }
            Same-CancellationObject $(if ($step -ceq 'later') { $row.laterExecution } else { $row.restoredExecution }) $execution
            Require-CancellationState ($phase + '-' + $step) $target $disabled $null
            Require-CancellationGraph ($phase + '-' + $step) $target
        }
        $replay = Read-CancellationOriginal ($phase + '-replay')
        Same-CancellationObject $row.historicalReplay $replay
        foreach ($field in @('operationId', 'rootId', 'phase', 'sourceName', 'targetName', 'cancelledAt')) {
            if ($replay.$field -cne $cancelled.$field) { throw 'Historical cancellation changed its original identity, endpoints or timestamp.' }
        }
        $laterName = $Evidence.directoryName + ' After ' + $phase
        if ($replay.currentDirectoryName -cne $laterName -or $replay.memberCount -ne $tree.Count -or
            $replay.currentTreeAudited -isnot [bool] -or $replay.currentTreeAudited -ne $true -or
            $replay.namespaceReady -isnot [bool] -or $replay.namespaceReady -ne (-not $disabled)) { throw 'An immutable cancellation replay must retain the later current Label and independently audited admission.' }
        Require-CancellationState ($phase + '-replayed') $laterName $disabled $null
        Require-CancellationGraph ($phase + '-replayed') $laterName
    }
    if (@($operations | Select-Object -Unique).Count -ne 6) { throw 'Require six distinct original cancellation and execution operations.' }
    Require-CancellationState 'restored' $Evidence.directoryName $false $null
    Require-CancellationGraph 'restored' $Evidence.directoryName
    $completed = Get-Item -LiteralPath (Join-Path $OriginalsDirectory 'completed.json') -ErrorAction Stop
    if ($completed.Length -gt 128KB -or $completed.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'Require the bounded complete cancellation result.' }
    Same-CancellationObject $Evidence (Get-Content -LiteralPath $completed.FullName -Raw | ConvertFrom-Json)
}
