function Invoke-CliRootLabelLifecycle {
    param([Parameter(Mandatory)][scriptblock]$InvokeCli,
        [Parameter(Mandatory)][string]$RootId, [Parameter(Mandatory)][string]$InstanceId,
        [Parameter(Mandatory)][string]$DirectoryName, [Parameter(Mandatory)][string]$SyncRoot,
        [Parameter(Mandatory)][string]$SourceRoot, [AllowNull()][string]$EvidencePath)

    $records = [Collections.Generic.List[object]]::new()
    $originals = if ($EvidencePath) {
        Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath))) 'cli-regression.originals/root-labels'
    } else { $null }
    if ($originals) { New-Item -ItemType Directory -Path $originals -Force | Out-Null }
    function Invoke-RootLabelCli([string]$Phase, [string[]]$Arguments) {
        $response = & $InvokeCli (@('--json', '--developer-mode') + $Arguments)
        if ($response.kind -cne 'result') { throw 'The root Label command did not return a typed result.' }
        if ($originals) {
            $response | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $originals ($Phase + '.json')) -Encoding utf8
        }
        return $response.data
    }
    function Read-RootLabelState([string]$Phase, [string]$Name, [bool]$Disabled) {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $roots = Invoke-RootLabelCli ($Phase + '-roots') @('root', 'list')
            $selected = @($roots | Where-Object { $_.root.rootId -ceq $RootId })
            $status = Invoke-RootLabelCli ($Phase + '-status') @('status')
            $instance = @($status.instances | Where-Object instanceId -ceq $InstanceId)
            if ($selected.Count -eq 1 -and $instance.Count -eq 1 -and
                $selected[0].root.directoryName -ceq $Name -and $selected[0].root.label -ceq $Name -and
                $selected[0].syncState -ceq $(if ($Disabled) { 'Disabled' } else { 'Active' }) -and
                $null -eq $selected[0].pendingRename -and
                $(if ($Disabled) { $instance[0].phase -ceq 'Offline' -and -not $instance[0].workerConnected }
                  else { $selected[0].namespaceReady -eq $true -and $instance[0].workerConnected -eq $true })) {
                return $selected[0]
            }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
        throw 'The actual Host did not retain the expected root Label, availability and Worker state.'
    }
    function Restart-RootLabelHost([string]$Phase) {
        $before = Invoke-RootLabelCli ($Phase + '-before') @('host', 'status')
        if ($before.processId -le 0) { throw 'The actual Host process identity is missing.' }
        $process = [Diagnostics.Process]::GetProcessById($before.processId)
        try {
            Invoke-RootLabelCli ($Phase + '-stop') @('host', 'stop') | Out-Null
            if (-not $process.WaitForExit(30000)) { throw 'The original Host process did not fully exit.' }
        } finally { $process.Dispose() }
        Invoke-RootLabelCli ($Phase + '-start') @('host', 'start') | Out-Null
        $after = Invoke-RootLabelCli ($Phase + '-after') @('host', 'status')
        if ($after.processId -le 0 -or $after.processId -eq $before.processId) { throw 'The root fixture did not start an independent new Host process.' }
        return [ordered]@{previousProcessId=$before.processId;currentProcessId=$after.processId;previousProcessExited=$true}
    }

    $initial = Read-RootLabelState 'initial' $DirectoryName $false
    $member = Invoke-RootLabelCli 'initial-member' @('namespace', 'member', 'inspect', $DirectoryName, '--directory')
    if ($member.rootId -cne $RootId -or [string]::IsNullOrWhiteSpace($member.nativeItemId)) { throw 'The root lost its original native member identity.' }
    $tree = @(Invoke-RootLabelCli 'initial-tree' @('namespace', 'member', 'tree', $member.memberId))
    if ($tree.Count -lt 3 -or @($tree.memberId | Select-Object -Unique).Count -ne $tree.Count) { throw 'The original whole root inventory is incomplete.' }
    $known = @('queued-upload.txt', 'cli-roundtrip.txt')
    $bytes = @(foreach ($name in $known) {
        $local = Join-Path (Join-Path $SyncRoot $DirectoryName) $name
        $remote = Join-Path $SourceRoot $name
        $localHash = (Get-FileHash -LiteralPath $local).Hash
        $remoteHash = (Get-FileHash -LiteralPath $remote).Hash
        if ($localHash -cne $remoteHash) { throw 'The original synthetic resident bytes disagree with the signed Local source.' }
        [ordered]@{name=$name;localSha256=$localHash;sourceSha256=$remoteHash}
    })
    function Assert-RootLabelTree([string]$Phase, [string]$Name) {
        $current = Invoke-RootLabelCli ($Phase + '-member') @('namespace', 'member', 'inspect', $Name, '--directory')
        if ($current.memberId -cne $member.memberId -or $current.nativeItemId -cne $member.nativeItemId -or
            $current.actualParentMemberId -cne $member.actualParentMemberId -or $current.rootId -cne $RootId) { throw 'Root Label execution replaced a stable root, public item or physical anchor identity.' }
        $entries = @(Invoke-RootLabelCli ($Phase + '-tree') @('namespace', 'member', 'tree', $member.memberId))
        if ($entries.Count -ne $tree.Count) { throw 'Root Label execution lost or invented a current member.' }
        foreach ($entry in $tree) {
            if ($entry.relativePath -cne $DirectoryName -and -not $entry.relativePath.StartsWith($DirectoryName + '/', [StringComparison]::Ordinal)) { throw 'The original inventory escaped its configured root.' }
            $expected = $Name + $entry.relativePath.Substring($DirectoryName.Length)
            $matches = @($entries | Where-Object memberId -ceq $entry.memberId)
            if ($matches.Count -ne 1 -or $matches[0].parentMemberId -cne $entry.parentMemberId -or
                $matches[0].isDirectory -ne $entry.isDirectory -or $matches[0].relativePath -cne $expected -or
                $matches[0].depth -ne $entry.depth) { throw 'Root Label execution changed the original complete member graph.' }
        }
        $observedBytes = @(foreach ($payload in $bytes) {
            $localHash = (Get-FileHash -LiteralPath (Join-Path (Join-Path $SyncRoot $Name) $payload.name)).Hash
            $sourceHash = (Get-FileHash -LiteralPath (Join-Path $SourceRoot $payload.name)).Hash
            if ($localHash -cne $payload.localSha256 -or $sourceHash -cne $payload.sourceSha256) { throw 'Root Label execution changed resident or source bytes.' }
            [ordered]@{name=$payload.name;localSha256=$localHash;sourceSha256=$sourceHash}
        })
        if ($originals) { [ordered]@{schemaVersion=1;phase=$Phase;payloads=$observedBytes} | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath (Join-Path $originals ($Phase + '-bytes.json')) -Encoding utf8 }
    }
    function Execute-RootLabel([string]$Phase, [string]$SourceName, [string]$TargetName, [bool]$Disabled) {
        $prepared = Invoke-RootLabelCli ($Phase + '-prepare') @('root', 'prepare-rename', $RootId, $TargetName)
        if ($prepared.rootId -cne $RootId -or $prepared.sourceName -cne $SourceName -or
            $prepared.targetName -cne $TargetName -or $prepared.phase -cne 'Prepared' -or
            [guid]::Parse($prepared.operationId) -eq [guid]::Empty) { throw 'The public preparation did not preserve its original root and endpoints.' }
        $completed = Invoke-RootLabelCli ($Phase + '-execute') @('root', 'execute-rename', $prepared.operationId)
        if ($completed.operationId -cne $prepared.operationId -or $completed.rootId -cne $RootId -or
            $completed.phase -cne 'Completed' -or $completed.sourceName -cne $SourceName -or $completed.targetName -cne $TargetName -or
            $completed.currentDirectoryName -cne $TargetName -or $completed.memberCount -ne $tree.Count -or
            $completed.currentTreeAudited -ne $true -or ($Disabled -and $completed.namespaceReady -ne $false)) { throw 'The public root command did not return its complete original local receipt and fresh audit.' }
        Read-RootLabelState $Phase $TargetName $Disabled | Out-Null
        Assert-RootLabelTree $Phase $TargetName
        $records.Add([ordered]@{phase=$Phase;operationId=$prepared.operationId;rootId=$RootId;memberId=$member.memberId;
            nativeItemId=$member.nativeItemId;sourceName=$SourceName;targetName=$TargetName;memberCount=$tree.Count;
            currentTreeAudited=$true;disabled=$Disabled;originalReceipt=$completed})
        return $completed
    }
    $label = $DirectoryName + ' Label'
    $first = Execute-RootLabel 'active' $DirectoryName $label $false
    $activeRestart = Restart-RootLabelHost 'active-restart'
    Read-RootLabelState 'active-restarted' $label $false | Out-Null
    Assert-RootLabelTree 'active-restarted' $label
    $replay = Invoke-RootLabelCli 'active-replay' @('root', 'execute-rename', $first.operationId)
    if ($replay.phase -cne 'Completed' -or $replay.currentDirectoryName -cne $label -or
        $replay.operationId -cne $first.operationId -or $replay.currentTreeAudited -ne $true) { throw 'A new Host did not replay the original completed Label.' }

    Invoke-RootLabelCli 'disable' @('instance', 'disable', '--instance-id', $InstanceId) | Out-Null
    Read-RootLabelState 'disabled' $label $true | Out-Null
    $caseLabel = $label.ToUpperInvariant()
    if ($caseLabel -ceq $label) { throw 'The synthetic case-only Label must have distinct spelling.' }
    $second = Execute-RootLabel 'disabled-case' $label $caseLabel $true
    $disabledRestart = Restart-RootLabelHost 'disabled-restart'
    Read-RootLabelState 'disabled-restarted' $caseLabel $true | Out-Null
    Assert-RootLabelTree 'disabled-restarted' $caseLabel
    $historical = Invoke-RootLabelCli 'historical-replay' @('root', 'execute-rename', $first.operationId)
    if ($historical.operationId -cne $first.operationId -or $historical.phase -cne 'Completed' -or
        $historical.sourceName -cne $first.sourceName -or $historical.targetName -cne $first.targetName -or
        $historical.currentDirectoryName -cne $caseLabel -or $historical.memberCount -ne $tree.Count -or
        $historical.currentTreeAudited -ne $true -or $historical.namespaceReady -ne $false) { throw 'An old public request lost its original receipt or reverted the later Label.' }
    Assert-RootLabelTree 'historical-replay' $caseLabel
    Execute-RootLabel 'restore' $caseLabel $DirectoryName $true | Out-Null
    Invoke-RootLabelCli 'enable' @('instance', 'enable', '--instance-id', $InstanceId) | Out-Null
    $finalRestart = Restart-RootLabelHost 'restored-restart'
    Read-RootLabelState 'restored' $DirectoryName $false | Out-Null
    Assert-RootLabelTree 'restored' $DirectoryName
    $result = [ordered]@{schemaVersion=1;rootId=$RootId;instanceId=$InstanceId;memberId=$member.memberId;nativeItemId=$member.nativeItemId;
        memberCount=$tree.Count;executions=$records.ToArray();residentAndSourceBytes=$bytes;
        activeRestart=$activeRestart;disabledRestart=$disabledRestart;restoredRestart=$finalRestart;
        historicalReplay=$historical;restoredDirectoryName=$DirectoryName;defaultHostIntegrated=$true;
        actualWorkerRequired=$true;preparedIntentAcrossNewRole=$false;interruptedNativeExecution=$false;journalAckReceiptInspected=$false}
    if ($originals) { $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $originals 'completed.json') -Encoding utf8 }
    return $result
}
