function Invoke-CliRootLabelCancellationLifecycle {
    param([Parameter(Mandatory)][scriptblock]$InvokeCli,
        [Parameter(Mandatory)][string]$RootId, [Parameter(Mandatory)][string]$InstanceId,
        [Parameter(Mandatory)][string]$DirectoryName, [Parameter(Mandatory)][string]$SyncRoot,
        [Parameter(Mandatory)][string]$SourceRoot, [AllowNull()][string]$EvidencePath)

    $originals = if ($EvidencePath) {
        Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath))) 'cli-regression.originals/root-cancellations'
    } else { $null }
    if ($originals) { New-Item -ItemType Directory -Path $originals -Force | Out-Null }
    function Invoke-CancellationCli([string]$Phase, [string[]]$Arguments) {
        $response = & $InvokeCli (@('--json', '--developer-mode') + $Arguments)
        if ($response.kind -cne 'result' -or $null -eq $response.data) { throw 'Require a typed public cancellation fixture response.' }
        if ($originals) {
            $response | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $originals ($Phase + '.json')) -Encoding utf8
        }
        return $response.data
    }
    function Read-CancellationState([string]$Phase, [string]$Name, [bool]$Disabled, [AllowNull()][string]$PendingOperation) {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $roots = @(Invoke-CancellationCli ($Phase + '-roots') @('root', 'list'))
            $status = Invoke-CancellationCli ($Phase + '-status') @('status')
            $root = @($roots | Where-Object { $_.root.rootId -ceq $RootId })
            $instance = @($status.instances | Where-Object instanceId -ceq $InstanceId)
            if ($root.Count -eq 1 -and $instance.Count -eq 1 -and
                $root[0].root.directoryName -ceq $Name -and $root[0].root.label -ceq $Name -and
                $root[0].root.state -ceq $(if ($Disabled) { 'Disabled' } else { 'Active' }) -and
                $root[0].syncState -ceq $(if ($PendingOperation) { 'NamespaceRecovery' } elseif ($Disabled) { 'Disabled' } else { 'Active' }) -and
                $(if ($PendingOperation) {
                    $root[0].pendingRename.operationId -ceq $PendingOperation -and
                    $root[0].pendingRename.phase -ceq 'Prepared' -and $root[0].namespaceReady -eq $false
                  } else { $null -eq $root[0].pendingRename -and $root[0].namespaceReady -eq (-not $Disabled) }) -and
                $(if ($Disabled) { $instance[0].phase -ceq 'Offline' -and $instance[0].workerConnected -eq $false }
                  elseif ($PendingOperation) { $true } else { $instance[0].workerConnected -eq $true })) { return }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
        throw 'The actual Host did not retain the expected pending or cancelled root availability.'
    }
    function Restart-CancellationHost([string]$Phase) {
        $before = Invoke-CancellationCli ($Phase + '-before') @('host', 'status')
        if ($before.processId -le 0) { throw 'The original cancellation Host process identity is missing.' }
        $process = [Diagnostics.Process]::GetProcessById($before.processId)
        try {
            Invoke-CancellationCli ($Phase + '-stop') @('host', 'stop') | Out-Null
            if (-not $process.WaitForExit(30000)) { throw 'The preparing Host did not fully exit.' }
        } finally { $process.Dispose() }
        Invoke-CancellationCli ($Phase + '-start') @('host', 'start') | Out-Null
        $after = Invoke-CancellationCli ($Phase + '-after') @('host', 'status')
        if ($after.processId -le 0 -or $after.processId -eq $before.processId) { throw 'Cancellation requires an independent new Host process.' }
        return [ordered]@{previousProcessId=$before.processId;currentProcessId=$after.processId;previousProcessExited=$true}
    }

    Read-CancellationState 'initial' $DirectoryName $false $null
    $member = Invoke-CancellationCli 'initial-member' @('namespace', 'member', 'inspect', $DirectoryName, '--directory')
    $tree = @(Invoke-CancellationCli 'initial-tree' @('namespace', 'member', 'tree', $member.memberId))
    if ($member.rootId -cne $RootId -or [string]::IsNullOrWhiteSpace($member.nativeItemId) -or
        $tree.Count -lt 3 -or @($tree.memberId | Select-Object -Unique).Count -ne $tree.Count) { throw 'Require the complete original protected root graph.' }
    $bytes = @(foreach ($name in @('queued-upload.txt', 'cli-roundtrip.txt')) {
        $localHash = (Get-FileHash -LiteralPath (Join-Path (Join-Path $SyncRoot $DirectoryName) $name)).Hash
        $sourceHash = (Get-FileHash -LiteralPath (Join-Path $SourceRoot $name)).Hash
        if ($localHash -cne $sourceHash) { throw 'The initial cancellation fixture bytes disagree with the signed Local source.' }
        [ordered]@{name=$name;localSha256=$localHash;sourceSha256=$sourceHash}
    })
    function Assert-CancellationTree([string]$Phase, [string]$Name) {
        $current = Invoke-CancellationCli ($Phase + '-member') @('namespace', 'member', 'inspect', $Name, '--directory')
        $entries = @(Invoke-CancellationCli ($Phase + '-tree') @('namespace', 'member', 'tree', $member.memberId))
        if ($current.memberId -cne $member.memberId -or $current.nativeItemId -cne $member.nativeItemId -or
            $current.actualParentMemberId -cne $member.actualParentMemberId -or $current.rootId -cne $RootId -or
            $current.relativePath -cne $Name -or $entries.Count -ne $tree.Count) { throw 'Cancellation replaced a stable identity or lost the complete root graph.' }
        foreach ($entry in $tree) {
            if ($entry.relativePath -cne $DirectoryName -and -not $entry.relativePath.StartsWith($DirectoryName + '/', [StringComparison]::Ordinal)) { throw 'The original cancellation inventory escaped its root.' }
            $expected = $Name + $entry.relativePath.Substring($DirectoryName.Length)
            $matches = @($entries | Where-Object memberId -ceq $entry.memberId)
            if ($matches.Count -ne 1 -or $matches[0].parentMemberId -cne $entry.parentMemberId -or
                $matches[0].isDirectory -ne $entry.isDirectory -or $matches[0].relativePath -cne $expected -or
                $matches[0].depth -ne $entry.depth) { throw 'Cancellation or replay changed the original complete member graph.' }
        }
        $observed = @(foreach ($payload in $bytes) {
            $localHash = (Get-FileHash -LiteralPath (Join-Path (Join-Path $SyncRoot $Name) $payload.name)).Hash
            $sourceHash = (Get-FileHash -LiteralPath (Join-Path $SourceRoot $payload.name)).Hash
            if ($localHash -cne $payload.localSha256 -or $sourceHash -cne $payload.sourceSha256) { throw 'Cancellation or replay changed resident or source bytes.' }
            [ordered]@{name=$payload.name;localSha256=$localHash;sourceSha256=$sourceHash}
        })
        if ($originals) { [ordered]@{schemaVersion=1;phase=$Phase;payloads=$observed} | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath (Join-Path $originals ($Phase + '-bytes.json')) -Encoding utf8 }
    }
    function Prepare-CancellationLabel([string]$Phase, [string]$SourceName, [string]$TargetName) {
        $prepared = Invoke-CancellationCli ($Phase + '-prepare') @('root', 'prepare-rename', $RootId, $TargetName)
        if ($prepared.rootId -cne $RootId -or $prepared.phase -cne 'Prepared' -or
            $prepared.sourceName -cne $SourceName -or $prepared.targetName -cne $TargetName -or
            [guid]::Parse($prepared.operationId) -eq [guid]::Empty) { throw 'Require the original typed public root Label preparation.' }
        return $prepared
    }
    function Execute-CancellationLabel([string]$Phase, [string]$SourceName, [string]$TargetName, [bool]$Disabled) {
        $prepared = Prepare-CancellationLabel $Phase $SourceName $TargetName
        $completed = Invoke-CancellationCli ($Phase + '-execute') @('root', 'execute-rename', $prepared.operationId)
        if ($completed.operationId -cne $prepared.operationId -or $completed.rootId -cne $RootId -or
            $completed.sourceName -cne $SourceName -or $completed.targetName -cne $TargetName -or
            $completed.phase -cne 'Completed' -or $completed.currentDirectoryName -cne $TargetName -or
            $completed.memberCount -ne $tree.Count -or $completed.currentTreeAudited -ne $true -or
            $completed.namespaceReady -ne (-not $Disabled)) { throw 'The later public Label did not complete with a fresh independent audit.' }
        Read-CancellationState $Phase $TargetName $Disabled $null
        Assert-CancellationTree $Phase $TargetName
        return $completed
    }

    $cases = @(foreach ($phase in @('active', 'disabled')) {
        $disabled = $phase -ceq 'disabled'
        if ($disabled) { Invoke-CancellationCli 'disable' @('instance', 'disable', '--instance-id', $InstanceId) | Out-Null }
        Read-CancellationState ($phase + '-before') $DirectoryName $disabled $null
        $prepared = Prepare-CancellationLabel $phase $DirectoryName ($DirectoryName + ' Cancel ' + $phase)
        Read-CancellationState ($phase + '-prepared') $DirectoryName $disabled $prepared.operationId
        $restart = Restart-CancellationHost ($phase + '-restart')
        Read-CancellationState ($phase + '-restarted') $DirectoryName $disabled $prepared.operationId
        $cancelled = Invoke-CancellationCli ($phase + '-cancel') @('root', 'cancel-rename', $prepared.operationId)
        if ($cancelled.operationId -cne $prepared.operationId -or $cancelled.rootId -cne $RootId -or
            $cancelled.phase -cne 'Cancelled' -or $cancelled.sourceName -cne $prepared.sourceName -or
            $cancelled.targetName -cne $prepared.targetName -or [DateTimeOffset]$cancelled.cancelledAt -le [DateTimeOffset]::UnixEpoch -or
            $cancelled.currentDirectoryName -cne $DirectoryName -or $cancelled.memberCount -ne $tree.Count -or
            $cancelled.currentTreeAudited -ne $true -or $cancelled.namespaceReady -ne (-not $disabled)) { throw 'The new Host did not cancel the original preparation with separately audited current admission.' }
        Read-CancellationState ($phase + '-cancelled') $DirectoryName $disabled $null
        Assert-CancellationTree ($phase + '-cancelled') $DirectoryName
        $laterName = $DirectoryName + ' After ' + $phase
        $later = Execute-CancellationLabel ($phase + '-later') $DirectoryName $laterName $disabled
        $replay = Invoke-CancellationCli ($phase + '-replay') @('root', 'cancel-rename', $prepared.operationId)
        foreach ($field in @('operationId', 'rootId', 'phase', 'sourceName', 'targetName', 'cancelledAt')) {
            if ($replay.$field -cne $cancelled.$field) { throw 'Cancellation replay changed its original immutable receipt.' }
        }
        if ($replay.currentDirectoryName -cne $laterName -or $replay.memberCount -ne $tree.Count -or
            $replay.currentTreeAudited -ne $true -or $replay.namespaceReady -ne (-not $disabled)) { throw 'Cancellation replay reverted the later Label or confused historical evidence with current admission.' }
        Read-CancellationState ($phase + '-replayed') $laterName $disabled $null
        Assert-CancellationTree ($phase + '-replayed') $laterName
        $restored = Execute-CancellationLabel ($phase + '-restore') $laterName $DirectoryName $disabled
        [ordered]@{phase=$phase;disabled=$disabled;preparation=$prepared;restart=$restart;
            cancellation=$cancelled;laterExecution=$later;historicalReplay=$replay;restoredExecution=$restored}
    })
    Invoke-CancellationCli 'enable' @('instance', 'enable', '--instance-id', $InstanceId) | Out-Null
    Read-CancellationState 'restored' $DirectoryName $false $null
    Assert-CancellationTree 'restored' $DirectoryName
    $result = [ordered]@{schemaVersion=1;rootId=$RootId;instanceId=$InstanceId;memberId=$member.memberId;
        nativeItemId=$member.nativeItemId;memberCount=$tree.Count;directoryName=$DirectoryName;
        residentAndSourceBytes=$bytes;cases=$cases;defaultHostIntegrated=$true;actualWorkerRequired=$true;
        preparedIntentAcrossIndependentHost=$true;interruptedNativeExecution=$false;journalAckReceiptInspected=$false}
    if ($originals) { $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $originals 'completed.json') -Encoding utf8 }
    return $result
}
