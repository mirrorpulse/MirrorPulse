# Actual signed-Worker conflict choices use only public CLI commands and a stopped-Host test oracle.
function Invoke-CliCoalescingContentConflict {
    param(
        [Parameter(Mandatory)][scriptblock]$InvokeCli,
        [Parameter(Mandatory)][scriptblock]$WaitHostStopped,
        [Parameter(Mandatory)][scriptblock]$ReadStoppedAudit,
        [Parameter(Mandatory)][scriptblock]$ResolveKeepBoth,
        [Parameter(Mandatory)][string]$InstanceId,
        [Parameter(Mandatory)][string]$MappedRoot,
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$EvidencePath
    )
    $originals=Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath))) 'coalescing.originals'
    New-Item -ItemType Directory -Path $originals -Force|Out-Null
    function Invoke-CoalescingCli([string]$Name,[string[]]$Arguments) {
        $result=& $InvokeCli $Arguments
        if($result.kind -cne 'result'){throw 'The signed coalescing fixture requires an original result envelope.'}
        $result|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $originals ($Name+'.json')) -Encoding utf8
        $result.data
    }
    function Assert-CoalescingHash([string]$Path,[string]$Expected,[bool]$Refresh) {
        $deadline=[DateTimeOffset]::UtcNow.AddSeconds(30)
        do {
            if($Refresh){& $InvokeCli @('--json','--developer-mode','sync','refresh')|Out-Null}
            try{if((Test-Path -LiteralPath $Path -PathType Leaf) -and (Get-FileHash -LiteralPath $Path).Hash -ceq $Expected){return}}
            catch [IO.IOException]{if($_.Exception.HResult -notin @(-2147024864,-2147024863)){throw}}
            Start-Sleep -Milliseconds 100
        }while([DateTimeOffset]::UtcNow -lt $deadline)
        throw 'The real signed coalescing fixture did not observe the expected complete bytes within30s.'
    }
    $source=Join-Path $SourceRoot 'cli-roundtrip.txt';$local=Join-Path $MappedRoot 'cli-roundtrip.txt'
    $seed=[Text.Encoding]::UTF8.GetBytes('signed coalescing original accepted bytes')
    $first=[Text.Encoding]::UTF8.GetBytes('signed coalescing first local edit')
    $latest=[Text.Encoding]::UTF8.GetBytes('signed coalescing final local bytes retained by KeepBoth')
    $remote=[Text.Encoding]::UTF8.GetBytes('signed coalescing independently selected remote version')
    $seedHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($seed))
    $latestHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($latest))
    $remoteHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($remote))
    [IO.File]::WriteAllBytes($source,$seed)
    Assert-CoalescingHash $local $seedHash $true
    Invoke-CoalescingCli 'disable' @('--json','--developer-mode','instance','disable','--instance-id',$InstanceId)|Out-Null
    Invoke-CoalescingCli 'disabled-restart' @('--json','--developer-mode','host','restart')|Out-Null
    & $WaitHostStopped
    $disabled=Invoke-CoalescingCli 'disabled-status' @('--json','--developer-mode','status')
    $entry=@($disabled.instances|Where-Object instanceId -CEQ $InstanceId)
    if($entry.Count -ne 1 -or $entry[0].enabled -ne $false){throw 'The real instance must be disabled before edits.'}
    $baseline=[long]$disabled.pendingUploads
    [IO.File]::WriteAllBytes($local,$first)
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $status=& $InvokeCli @('--json','--developer-mode','status')
        if($status.data.pendingUploads -gt $baseline){break}
        Start-Sleep -Milliseconds 100
    }while([DateTimeOffset]::UtcNow -lt $deadline)
    if($status.data.pendingUploads -le $baseline){throw 'The first actual local edit did not enter the original journal.'}
    $firstCount=[long]$status.data.pendingUploads
    [IO.File]::WriteAllBytes($local,$latest)
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $status=& $InvokeCli @('--json','--developer-mode','status')
        if($status.data.pendingUploads -gt $firstCount){break}
        Start-Sleep -Milliseconds 100
    }while([DateTimeOffset]::UtcNow -lt $deadline)
    if($status.data.pendingUploads -le $firstCount){throw 'The second actual local edit did not retain a distinct original notification.'}
    Invoke-CoalescingCli 'queued-status' @('--json','--developer-mode','status')|Out-Null
    [IO.File]::WriteAllBytes($source,$remote)
    Invoke-CoalescingCli 'enable' @('--json','--developer-mode','instance','enable','--instance-id',$InstanceId)|Out-Null
    Invoke-CoalescingCli 'enabled-restart' @('--json','--developer-mode','host','restart')|Out-Null
    & $WaitHostStopped
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(30);$conflict=$null
    do {
        $listed=& $InvokeCli @('--json','--developer-mode','conflict','list')
        $selected=@($listed.data.items|Where-Object {$_.instanceId -ceq $InstanceId -and $_.relativePath.Replace('\','/').EndsWith('/cli-roundtrip.txt',[StringComparison]::Ordinal)})
        if($selected.Count -gt 0){
            $conflict=$selected[0]
            if(-not [string]::IsNullOrEmpty($conflict.localRevision) -and -not [string]::IsNullOrEmpty($conflict.remoteRevision)){break}
        }
        Start-Sleep -Milliseconds 100
    }while([DateTimeOffset]::UtcNow -lt $deadline)
    if($null -eq $conflict -or $conflict.source -cne 'Upload' -or $conflict.reason -cne 'StaleRemoteRevision'){
        throw 'The signed Worker did not expose the expected real conditional upload conflict.'
    }
    Invoke-CoalescingCli 'conflicts-before-choice' @('--json','--developer-mode','conflict','list')|Out-Null
    Invoke-CoalescingCli 'stop-before-choice' @('--json','--developer-mode','host','stop')|Out-Null
    & $WaitHostStopped
    $pending=& $ReadStoppedAudit $false
    if($pending.rawEffect -cne 'UpdateFile' -or $pending.originalCount -lt 2 -or
        $pending.pendingOriginalCount -ne $pending.originalCount -or $pending.originalExpectedRevisionPresent -ne $true -or
        $pending.originalEffectState -cne 'Conflict'){
        throw 'The stopped pre-choice oracle did not retain every actual original under the rejected UpdateFile effect.'
    }
    $pending|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $originals 'pending-before-choice.json') -Encoding utf8
    if([string]::IsNullOrEmpty($conflict.localRevision) -or [string]::IsNullOrEmpty($conflict.remoteRevision)){
        throw 'The actual conflict did not expose the selected revisions required for KeepBoth; original intent evidence was retained without fabricating response revisions.'
    }
    $observation=$pending.selectedRemoteObservation
    if($null -ne $observation){
        $selectedRevisionHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($conflict.remoteRevision)))
        $requestRevisionHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($conflict.localRevision)))
        if($observation.version -ne 1 -or $observation.rejectedEffectId -cne $pending.rejectedEffectId -or
            $null -ne $observation.previousObservationId -or $null -ne $observation.refreshCommandId -or
            $observation.originalErrorActualRevisionPresent -ne $false -or $null -ne $observation.originalErrorActualRevisionSha256 -or
            $observation.selectedRemoteRevisionSha256 -cne $selectedRevisionHash -or
            $observation.originalRequestExpectedRevisionSha256 -cne $requestRevisionHash -or
            @($observation.originalOperationIds).Count -ne $pending.originalCount -or
            @(Compare-Object @($observation.originalOperationIds) @($pending.originalOperationIds) -CaseSensitive).Count -ne 0 -or
            @($observation.selectedConflictIds).Count -ne $pending.originalCount -or
            @($observation.selectedConflictIds|Where-Object {$_ -ceq $conflict.conflictId}).Count -ne 1){
            throw 'The selected current version did not retain its separate observation and exact original error/group provenance.'
        }
    }
    Invoke-CoalescingCli 'restart-before-choice' @('--json','--developer-mode','host','start')|Out-Null
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $status=& $InvokeCli @('--json','--developer-mode','status')
        $connected=@($status.data.instances|Where-Object {$_.instanceId -ceq $InstanceId -and $_.workerConnected -eq $true})
        if($connected.Count -eq 1){break}
        Start-Sleep -Milliseconds 100
    }while([DateTimeOffset]::UtcNow -lt $deadline)
    if($connected.Count -ne 1){throw 'The actual Worker did not reconnect for the retained conflict choice.'}
    $retained=Invoke-CoalescingCli 'conflicts-after-restart' @('--json','--developer-mode','conflict','list')
    if(@($retained.items|Where-Object conflictId -CEQ $conflict.conflictId).Count -ne 1){throw 'The original selected conflict did not survive the real Host restart.'}
    # The injected boundary accepts only result/exit0 or result/exit11 for this
    # exact KeepBoth request. Other CLI commands retain strict original handling.
    $choice=& $ResolveKeepBoth $conflict.conflictId
    $choice|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $originals 'keep-both.json') -Encoding utf8
    $command=[guid]::Empty
    if($choice.kind -cne 'result' -or $choice.data.conflictCommand.conflictId -cne $conflict.conflictId -or
        -not [guid]::TryParse([string]$choice.data.conflictCommand.commandId,[ref]$command) -or $command -eq [guid]::Empty -or
        $choice.data.conflictCommand.state -cnotin @('pending','resolved')){
        throw 'The actual CLI did not return the selected conflict command identity and an accepted state.'
    }
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $operation=& $InvokeCli @('--json','--developer-mode','--no-start','operation','get','--operation-id',$command.ToString('D'))
        if($operation.data.state -cne 'pending'){break}
        Start-Sleep -Milliseconds 100
    }while([DateTimeOffset]::UtcNow -lt $deadline)
    $operation|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $originals 'completed-command.json') -Encoding utf8
    if($operation.data.operationId -cne $command.ToString('D') -or $operation.data.action -cne 'keepboth' -or
        $operation.data.targetId -cne $conflict.conflictId -or $operation.data.state -cne 'resolved'){
        throw 'The exact KeepBoth command did not actually resolve within the original30s observation window.'
    }
    Assert-CoalescingHash $source $latestHash $false
    Assert-CoalescingHash $local $latestHash $false
    Invoke-CoalescingCli 'stop-before-audit' @('--json','--developer-mode','host','stop')|Out-Null
    & $WaitHostStopped
    $before=& $ReadStoppedAudit $true
    if($before.rawEffect -cne 'UpdateFile' -or $before.originalCount -lt 2 -or
        $before.acknowledgedOriginalCount -ne $before.originalCount -or $before.localSha256 -cne $latestHash -or
        $before.preservedRemoteSha256 -cne $remoteHash -or $before.actualNativeConfirmation -ne $true -or
        $before.planId -cne $pending.planId -or $before.commandId -cne $command.ToString('D') -or
        (@($before.originalOperationIds) -join ',') -cne (@($pending.originalOperationIds) -join ',')){
        throw 'The stopped original oracle did not prove the complete signed UpdateFile/KeepBoth boundary.'
    }
    $before|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $originals 'stopped-before-restart.json') -Encoding utf8
    Invoke-CoalescingCli 'start-after-choice' @('--json','--developer-mode','host','start')|Out-Null
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $status=& $InvokeCli @('--json','--developer-mode','status')
        $connected=@($status.data.instances|Where-Object {$_.instanceId -ceq $InstanceId -and $_.workerConnected -eq $true})
        if($connected.Count -eq 1){break}
        Start-Sleep -Milliseconds 100
    }while([DateTimeOffset]::UtcNow -lt $deadline)
    if($connected.Count -ne 1){throw 'The actual signed Worker did not reconnect after the choice.'}
    Assert-CoalescingHash $source $latestHash $false
    Assert-CoalescingHash $local $latestHash $false
    Invoke-CoalescingCli 'stop-after-restart' @('--json','--developer-mode','host','stop')|Out-Null
    & $WaitHostStopped
    $after=& $ReadStoppedAudit $true
    $after|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $originals 'stopped-after-restart.json') -Encoding utf8
    if(($before|ConvertTo-Json -Depth 8 -Compress) -cne ($after|ConvertTo-Json -Depth 8 -Compress)){
        throw 'The restarted Host changed the completed original execution, private copy or acknowledgement facts.'
    }
    [ordered]@{schemaVersion=1;sourceSha=$env:GITHUB_SHA;runId=$env:GITHUB_RUN_ID;defaultHostIntegrated=$true;signedWorker=$true;
        rawEffect='UpdateFile';action='KeepBoth';originalCount=$before.originalCount;actualNativeConfirmation=$true;
        localSha256=$latestHash;preservedRemoteSha256=$remoteHash;unchangedRetainedFactsAfterActualRestart=$true;
        fullSevenEffectMatrixAccepted=$false;fullP076Accepted=$false;teardownCompleted=$false}
}
