function Read-TransferPollSnapshot {
    param([Parameter(Mandatory)][string]$Path)
    try { return [IO.File]::ReadAllText($Path) | ConvertFrom-Json -AsHashtable }
    catch [IO.IOException] {
        # A replacement can coincide with observation. The phase has its own deadline.
        if ($_.Exception.HResult -notin @(-2147024894, -2147024893, -2147024864)) { throw }
        return $null
    }
}

function Invoke-TransferPollAudit {
    param([Parameter(Mandatory)][string]$BatchId)
    $observed = & dotnet $script:transferProbe --audit-cli-poll-restart $fixture $BatchId
    if ($LASTEXITCODE -ne 0) { throw 'The stopped polling recovery observation failed.' }
    return ($observed | ConvertFrom-Json)
}

function Invoke-TransferPollDisable {
    param([object]$Left, [object]$Right, [string]$LeftSnapshot, [string]$RightSnapshot,
        [string]$LeftRemote, [string]$RightRemote)
    $beforeLeft = Read-TransferPollSnapshot $LeftSnapshot
    $beforeRight = Read-TransferPollSnapshot $RightSnapshot
    $disabled = $false
    try {
        Invoke-TransferCli @('--json', '--no-start', 'instance', 'disable', '--instance-id', $Left.instanceId) | Out-Null
        $disabled = $true
        Invoke-TransferCli @('--json', 'host', 'restart') | Out-Null; Wait-TransferHostStopped
        Invoke-TransferCli @('--json', 'host', 'start') | Out-Null
        $workers = @(Wait-TransferWorkersConnected -DisabledInstanceId $Left.instanceId)
        [IO.File]::WriteAllText($LeftRemote, 'A later independent edit made while polling was disabled.')
        [IO.File]::WriteAllText($RightRemote, 'The enabled deployment advanced during the disabled deployment edit.')
        $status = (Invoke-TransferCli @('--json', '--no-start', 'sync', 'refresh', '--force')).data
        $leftStatus = $status.instances | Where-Object instanceId -ceq $Left.instanceId
        $rightStatus = $status.instances | Where-Object instanceId -ceq $Right.instanceId
        $leftRootStatus = $leftStatus.remotePolling | Where-Object rootId -ceq $Left.rootId
        $rightRootStatus = $rightStatus.remotePolling | Where-Object rootId -ceq $Right.rootId
        $duringLeft = Read-TransferPollSnapshot $LeftSnapshot
        $duringRight = Read-TransferPollSnapshot $RightSnapshot
        if ($leftRootStatus.phase -cne 'Offline' -or $rightRootStatus.phase -cne 'Current' -or
            $duringLeft.sha256 -cne $beforeLeft.sha256 -or $duringRight.cursor -ceq $beforeRight.cursor) {
            throw 'A forced refresh did not preserve the disabled snapshot while the active deployment advanced.'
        }
        $activity = [ordered]@{workers=$workers;leftPhase=$leftRootStatus.phase;rightPhase=$rightRootStatus.phase
            disabledLeftCursor=$duringLeft.cursor;oldRightCursor=$beforeRight.cursor;activeRightCursor=$duringRight.cursor}
        Write-TransferOriginal -Name 'poll-disabled-independent-refresh' -Value $activity

        Invoke-TransferCli @('--json', '--no-start', 'instance', 'enable', '--instance-id', $Left.instanceId) | Out-Null
        $disabled = $false
        Invoke-TransferCli @('--json', 'host', 'restart') | Out-Null; Wait-TransferHostStopped
        Invoke-TransferCli @('--json', 'host', 'start') | Out-Null
        Wait-TransferWorkersConnected
        Assert-TransferRootsReady
        Invoke-TransferCli @('--json', '--no-start', 'sync', 'refresh', '--force') | Out-Null
        $activeLeft = Read-TransferPollSnapshot $LeftSnapshot
        $activeRight = Read-TransferPollSnapshot $RightSnapshot
        if (-not $activeLeft.cursor -or $activeLeft.cursor -ceq $duringLeft.cursor -or $activeRight.cursor -cne $duringRight.cursor) {
            throw 'The reenabled deployment did not catch up independently from its retained cursor.'
        }
        $activity.reenabledLeftCursor = $activeLeft.cursor
        return $activity
    } finally {
        if ($disabled) {
            try { Invoke-TransferCli @('--json', 'instance', 'enable', '--instance-id', $Left.instanceId) | Out-Null }
            catch { Write-Host 'The test fixture retained its disabled deployment after the original failure.' }
        }
    }
}

function Invoke-TransferPollRecovery {
    $left = $script:transferRoutes | Where-Object fixtureSide -ceq 'left'
    $right = $script:transferRoutes | Where-Object fixtureSide -ceq 'right'
    $leftSnapshot = Join-Path $data "remote-poll/$($left.instanceId)/$($left.rootId).json"
    $rightSnapshot = Join-Path $data "remote-poll/$($right.instanceId)/$($right.rootId).json"
    $leftLocal = Join-Path $sync ($left.directoryName + '/forward.bin')
    $rightLocal = Join-Path $sync ($right.directoryName + '/reverse.bin')
    $leftRemote = Join-Path $fixture 'source/left/forward.bin'
    $rightRemote = Join-Path $fixture 'source/right/reverse.bin'
    Get-ChildItem -LiteralPath (Split-Path -Parent $leftLocal) | Out-Null
    Get-ChildItem -LiteralPath (Split-Path -Parent $rightLocal) | Out-Null
    # Establish protected online-only placeholders before the snapshot test.
    # File content must first be read after the original remote changes settle.
    if (-not (Test-Path -LiteralPath $leftLocal -PathType Leaf) -or -not (Test-Path -LiteralPath $rightLocal -PathType Leaf)) {
        throw 'The initial enumeration did not project both signed source files.'
    }

    $leftBefore = Read-TransferPollSnapshot $leftSnapshot
    $rightBefore = Read-TransferPollSnapshot $rightSnapshot
    if ($null -eq $leftBefore -or $null -eq $rightBefore -or
        $leftBefore.rootId -cne $left.rootId -or $rightBefore.rootId -cne $right.rootId) {
        throw 'The initial pass did not create both stable deployment snapshots.'
    }
    $hostId = (Invoke-TransferCli @('--json', '--no-start', 'host', 'status')).data.processId
    $hostProcess = Get-Process -Id $hostId
    try {
        if ($hostProcess.MainModule.FileName -ine (Join-Path $fixture 'host/MirrorPulse.Host.exe')) {
            throw 'The active process is outside the signed polling fixture.'
        }
        $primary = [IO.File]::Open($leftSnapshot, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            [IO.File]::WriteAllText($leftRemote, 'Original left deployment update across snapshot interruption.')
            [IO.File]::WriteAllText($rightRemote, 'Independent right deployment update during the interruption.')
            Invoke-TransferCli @('--json', '--no-start', 'sync', 'refresh', '--force') | Out-Null
            $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
            do {
                $candidate = Read-TransferPollSnapshot ($leftSnapshot + '.recovery')
                $peer = Read-TransferPollSnapshot $rightSnapshot
                if ($candidate.cursor -and $candidate.cursor -cne $leftBefore.cursor -and
                    $peer.cursor -and $peer.cursor -cne $rightBefore.cursor) { break }
                Start-Sleep -Milliseconds 200
            } while ([DateTimeOffset]::UtcNow -lt $deadline)
            $blocked = Read-TransferPollSnapshot $leftSnapshot
            if (-not $candidate.cursor -or $candidate.cursor -ceq $leftBefore.cursor -or
                -not $peer.cursor -or $peer.cursor -ceq $rightBefore.cursor -or $blocked.sha256 -cne $leftBefore.sha256) {
                throw 'The primary replacement did not stop at the original snapshot while its peer advanced.'
            }
            Write-TransferOriginal -Name 'poll-snapshot-cut' -Value ([ordered]@{sourceSha=$sourceSha;runId=$env:GITHUB_RUN_ID
                hostProcessId=$hostId;leftRootId=$left.rootId;rightRootId=$right.rootId
                oldLeftCursor=$blocked.cursor;originalLeftCandidate=$candidate.cursor
                oldRightCursor=$rightBefore.cursor;independentRightCursor=$peer.cursor})
            $hostProcess.Kill()
            if (-not $hostProcess.WaitForExit(12000)) { throw 'The exact fixture Host stayed alive after interruption.' }
            Wait-TransferHostStopped
            $stopped = Invoke-TransferPollAudit 'pending'
            Write-TransferOriginal -Name 'poll-stopped-original' -Value $stopped
            if ($stopped.routes[0].publishedCursor -cne $candidate.cursor -or $stopped.routes[1].publishedCursor -cne $peer.cursor) {
                throw 'The original applied batch and independent deployment disagree with their flushed snapshot cursors.'
            }
        } finally { $primary.Dispose() }
    } finally { $hostProcess.Dispose() }

    Invoke-TransferCli @('--json', 'host', 'start') | Out-Null
    Wait-TransferWorkersConnected
    Assert-TransferRootsReady
    $replacement = (Invoke-TransferCli @('--json', '--no-start', 'host', 'status')).data.processId
    if ($replacement -eq $hostId) { throw 'Recovery requires a different real Host process.' }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $repaired = Read-TransferPollSnapshot $leftSnapshot
        if ($repaired.sha256 -ceq $candidate.sha256) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($repaired.sha256 -cne $candidate.sha256) { throw 'The original snapshot did not converge after Host restart.' }
    Invoke-TransferCli @('--json', '--no-start', 'host', 'stop') | Out-Null
    Wait-TransferHostStopped
    $replayed = Invoke-TransferPollAudit $stopped.batchId
    Write-TransferOriginal -Name 'poll-stopped-replayed' -Value $replayed
    if ($replayed.batchId -cne $stopped.batchId -or $replayed.fingerprint -cne $stopped.fingerprint -or
        $replayed.routes[0].publishedCursor -cne $candidate.cursor -or $replayed.routes[1].publishedCursor -cne $peer.cursor) {
        throw 'The restarted Host changed the original batch or the independent deployment cursor.'
    }
    Invoke-TransferCli @('--json', 'host', 'start') | Out-Null
    Wait-TransferWorkersConnected
    Assert-TransferRootsReady
    $disabledRefresh = Invoke-TransferPollDisable -Left $left -Right $right -LeftSnapshot $leftSnapshot -RightSnapshot $rightSnapshot `
        -LeftRemote $leftRemote -RightRemote $rightRemote
    $status = (Invoke-TransferCli @('--json', '--no-start', 'status')).data
    if ($status.pendingRemoteConflicts -ne 0 -or $status.pendingUploadConflicts -ne 0) {
        throw 'The clean online-only updates created unexpected local or remote content conflicts.'
    }
    $phase = 'RemoteSyncRecovery.LeftProjection'
    Wait-TransferHash $leftLocal (Read-TransferHash $leftRemote)
    $phase = 'RemoteSyncRecovery.RightProjection'
    Wait-TransferHash $rightLocal (Read-TransferHash $rightRemote)
    return [ordered]@{interruptedHostProcessId=$hostId;recoveringHostProcessId=$replacement
        retained=$stopped;replayed=$replayed;disabledRefresh=$disabledRefresh;leftProjectionSha256=(Read-TransferHash $leftLocal)
        rightProjectionSha256=(Read-TransferHash $rightLocal)}
}
