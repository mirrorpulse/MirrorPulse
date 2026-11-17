function Invoke-CliActiveReplacement {
    param(
        [Parameter(Mandatory)][scriptblock]$InvokeCli,
        [Parameter(Mandatory)][scriptblock]$WaitForHost,
        [Parameter(Mandatory)][string]$FixtureRoot,
        [Parameter(Mandatory)][string]$DirectoryName,
        [Parameter(Mandatory)][string]$InstanceId,
        [Parameter(Mandatory)][string]$RootId,
        [AllowEmptyString()][string]$EvidencePath
    )

    $originals = if ($EvidencePath) {
        Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath))) 'cli-regression.originals/active-replacement'
    }
    if ($originals) { New-Item -ItemType Directory -Path $originals -Force | Out-Null }
    function Invoke-ReplacementCli([string]$Phase, [string[]]$Arguments, [int]$ExpectedExitCode = 0) {
        $response = & $InvokeCli (@('--json', '--no-start') + $Arguments) $ExpectedExitCode
        if ($response.kind -cne 'result') { throw "The replacement command did not return a typed result ($Phase)." }
        if ($originals) {
            $response | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $originals ($Phase + '.json')) -Encoding utf8
        }
        return $response.data
    }

    $fileName = 'cli-active-replacement.bin'
    $relative = "$DirectoryName/$fileName"
    $backend = Join-Path $FixtureRoot "source/$fileName"
    $local = Join-Path $FixtureRoot "sync/$relative"
    $inputPath = Join-Path $FixtureRoot 'replacement-input.bin'
    $replacement = [Text.Encoding]::UTF8.GetBytes('replacement staged by the original Host')
    $later = [Text.Encoding]::UTF8.GetBytes('later save survives replay of that original replacement')
    $originalHash = (Get-FileHash -LiteralPath $backend).Hash
    $replacementHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($replacement))
    $laterHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($later))
    [IO.File]::WriteAllBytes($inputPath, $replacement)

    $member = Invoke-ReplacementCli 'original-member' @('namespace', 'member', 'inspect', $relative)
    $initial = Invoke-ReplacementCli 'active-instance' @('status')
    $instance = @($initial.instances | Where-Object instanceId -ceq $InstanceId)
    if ($member.rootId -cne $RootId -or $member.isDirectory -ne $false -or
        [guid]::Parse($member.memberId) -eq [guid]::Empty -or [guid]::Parse($member.nativeItemId) -eq [guid]::Empty -or
        $instance.Count -ne 1 -or $instance[0].workerConnected -ne $true) {
        throw 'Replacement requires the original native file in a connected signed Local instance.'
    }
    $operation = [guid]::NewGuid().ToString('D')
    $prepareArguments = @('namespace', 'member', 'prepare-replace', $member.memberId, $inputPath, '--operation-id', $operation)
    $prepared = Invoke-ReplacementCli 'prepared' $prepareArguments
    if ($prepared.operationId -cne $operation -or $prepared.memberId -cne $member.memberId -or
        $prepared.relativePath -cne $relative -or $prepared.length -ne $replacement.Length -or $prepared.sha256 -cne $replacementHash) {
        throw 'The Host did not prepare the exact original replacement.'
    }
    $statusArguments = @('namespace', 'member', 'replacement-status', $operation)
    $pending = Invoke-ReplacementCli 'pending-before-stop' $statusArguments 11
    if ($pending.operationId -cne $operation -or $pending.phase -cne 'Prepared' -or $pending.started -ne $true -or
        $pending.actionRecorded -ne $true -or $pending.requiresRecovery -ne $true -or $pending.writeStarted -ne $false -or
        $pending.backupRecorded -ne $false -or $pending.completed -ne $false) {
        throw 'The original command must be prepared and queryable before backup or writing starts.'
    }
    [IO.File]::Delete($inputPath)
    if ((Get-FileHash -LiteralPath $backend).Hash -cne $originalHash -or
        (Get-FileHash -LiteralPath $local).Hash -cne $originalHash) {
        throw 'Preparation changed the original file bytes.'
    }

    $before = Invoke-ReplacementCli 'host-before-stop' @('host', 'status')
    if ($before.processId -le 0) { throw 'The original Host process identity is missing.' }
    $process = [Diagnostics.Process]::GetProcessById($before.processId)
    try {
        Invoke-ReplacementCli 'host-stop' @('host', 'stop') | Out-Null
        if (-not $process.WaitForExit(30000)) { throw 'The original replacement Host did not exit.' }
    } finally { $process.Dispose() }
    Invoke-ReplacementCli 'host-start' @('host', 'start') | Out-Null
    & $WaitForHost
    $after = Invoke-ReplacementCli 'host-after-start' @('host', 'status')
    if ($after.processId -le 0 -or $after.processId -eq $before.processId) {
        throw 'Replacement recovery requires an independently restarted Host process.'
    }
    $recoveredPending = Invoke-ReplacementCli 'pending-after-restart' $statusArguments 11
    if (($pending | ConvertTo-Json -Compress) -cne ($recoveredPending | ConvertTo-Json -Compress)) {
        throw 'Restart changed the original pending replacement status.'
    }

    $recoverArguments = @('namespace', 'member', 'replace-recover', $operation)
    $completed = Invoke-ReplacementCli 'completed-by-recovery' $recoverArguments
    $completedStatus = Invoke-ReplacementCli 'completed-status' $statusArguments
    if ($completed.operationId -cne $operation -or $completed.memberId -cne $member.memberId -or
        $completed.relativePath -cne $relative -or $completed.rescanRequested -ne $true -or
        $completedStatus.operationId -cne $operation -or $completedStatus.phase -cne 'Completed' -or
        $completedStatus.backupRecorded -ne $true -or $completedStatus.contentRecorded -ne $true -or
        $completedStatus.protectionRestored -ne $true -or $completedStatus.requiresRecovery -ne $false -or
        (Get-FileHash -LiteralPath $local).Hash -cne $replacementHash) {
        throw 'The recovered command did not retain its local completion, restored protection and exact replacement bytes.'
    }

    function Wait-ReplacementUpload([string]$Phase, [string]$ExpectedHash) {
        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        do {
            $state = Invoke-ReplacementCli $Phase @('status')
            $worker = @($state.instances | Where-Object instanceId -ceq $InstanceId)
            if ($worker.Count -eq 1 -and $worker[0].workerConnected -eq $true -and
                $state.pendingUploads -eq 0) {
                try {
                    $stream = [IO.File]::Open($backend, 'Open', 'Read', 'ReadWrite, Delete')
                    try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
                    finally { $stream.Dispose() }
                    if ($actual -ceq $ExpectedHash) { return }
                } catch [IO.IOException] {
                    # The signed Worker may still be atomically publishing the source.
                }
            }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
        throw "The signed Local source did not settle the replacement generation ($Phase)."
    }

    Wait-ReplacementUpload 'replacement-upload' $replacementHash
    [IO.File]::WriteAllBytes($local, $later)
    Wait-ReplacementUpload 'later-save-upload' $laterHash
    $replayedPreparation = Invoke-ReplacementCli 'replayed-preparation' $prepareArguments
    $replayedCompletion = Invoke-ReplacementCli 'replayed-completion' @('namespace', 'member', 'replace', $operation)
    $replayedStatus = Invoke-ReplacementCli 'replayed-status' $statusArguments
    $current = Invoke-ReplacementCli 'current-member' @('namespace', 'member', 'inspect', $relative)
    if (($replayedPreparation | ConvertTo-Json -Compress) -cne ($prepared | ConvertTo-Json -Compress) -or
        ($replayedCompletion | ConvertTo-Json -Compress) -cne ($completed | ConvertTo-Json -Compress) -or
        ($replayedStatus | ConvertTo-Json -Compress) -cne ($completedStatus | ConvertTo-Json -Compress) -or
        $current.memberId -cne $member.memberId -or $current.nativeItemId -cne $member.nativeItemId -or
        $current.rootId -cne $member.rootId -or $current.actualParentMemberId -cne $member.actualParentMemberId -or
        (Get-FileHash -LiteralPath $local).Hash -cne $laterHash -or (Get-FileHash -LiteralPath $backend).Hash -cne $laterHash) {
        throw 'Replacement replay changed original evidence, the file identity or the later saved bytes.'
    }

    return [ordered]@{
        operationId=$operation; memberId=$member.memberId; nativeItemId=$member.nativeItemId
        originalSha256=$originalHash; replacementSha256=$replacementHash; laterSha256=$laterHash
        originalHostProcessId=$before.processId; recoveryHostProcessId=$after.processId
    }
}
