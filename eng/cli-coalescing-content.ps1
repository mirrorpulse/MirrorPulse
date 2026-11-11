# Actual signed-Worker conflict choices use only public CLI commands and a stopped-Host test oracle.
function Get-CliCoalescingSeedBytes {
    return ,([Text.Encoding]::UTF8.GetBytes('signed coalescing original accepted bytes'))
}

function Invoke-CliCoalescingContentConflict {
    param(
        [Parameter(Mandatory)][scriptblock]$InvokeCli,
        [Parameter(Mandatory)][scriptblock]$WaitHostStopped,
        [Parameter(Mandatory)][scriptblock]$ReadStoppedAudit,
        [Parameter(Mandatory)][scriptblock]$ReadStoppedEdit,
        [Parameter(Mandatory)][scriptblock]$InspectOnlineSeed,
        [Parameter(Mandatory)][scriptblock]$ResolveChoice,
        [ValidateSet('KeepBoth','KeepRemote', IgnoreCase = $false)][string]$Action = 'KeepBoth',
        [switch]$ControlledCreate,
        [switch]$EmptyCreate,
        [Parameter(Mandatory)][string]$InstanceId,
        [Parameter(Mandatory)][string]$MappedRoot,
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$EvidencePath
    )
    if($ControlledCreate -and $Action -cne 'KeepRemote'){throw 'The controlled-Create fixture requires KeepRemote.'}
    if($EmptyCreate -and -not $ControlledCreate){throw 'Empty creation requires its controlled-Create fixture.'}
    $expectedEffect=if($ControlledCreate){'CreateFile'}else{'UpdateFile'}
    $originals=Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath))) 'coalescing.originals'
    New-Item -ItemType Directory -Path $originals -Force|Out-Null
    function Invoke-CoalescingCli([string]$Name,[string[]]$Arguments) {
        $result=& $InvokeCli $Arguments
        if($result.kind -cne 'result'){throw 'The signed coalescing fixture requires an original result envelope.'}
        $result|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $originals ($Name+'.json')) -Encoding utf8
        $result.data
    }
    function Read-CoalescingEdit([string]$Name) {
        $observation=& $ReadStoppedEdit
        Assert-CliCoalescingEditSnapshot $observation
        $observation|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $originals ($Name+'.json')) -Encoding utf8
        return $observation
    }
    function Confirm-CoalescingDisabled([string]$Name) {
        $status=Invoke-CoalescingCli $Name @('--json','--developer-mode','status')
        $instance=@($status.instances|Where-Object instanceId -CEQ $InstanceId)
        if($instance.Count -ne 1 -or $instance[0].enabled -ne $false -or $instance[0].workerConnected -ne $false){
            throw 'Both actual edits require the retained disabled instance without a connected Worker.'
        }
        return $status
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
    function Write-CoalescingEdit([byte[]]$Bytes) {
        if($Action -ceq 'KeepBoth'){
            [IO.File]::WriteAllBytes($local,$Bytes)
            return
        }
        $stream=[IO.File]::Open($local,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        try{
            $stream.Write($Bytes,0,$Bytes.Length)
            $stream.SetLength($Bytes.Length)
            $stream.Flush($true)
        }finally{$stream.Dispose()}
    }
    function Assert-KeepRemoteResident($Observation,[string]$ExpectedIdentity) {
        if($Action -ceq 'KeepRemote' -and (-not $Observation.nativeInspected -or
            $Observation.native.isPlaceholder -ne $true -or $Observation.native.contentAvailability -cne 'FullyAvailable' -or
            $Observation.nativeIdentitySha256 -cnotmatch '^[0-9A-F]{64}$' -or
            ($ExpectedIdentity -and $Observation.nativeIdentitySha256 -cne $ExpectedIdentity))){
            throw 'The signed KeepRemote fixture requires the same fully resident native placeholder throughout its original local edits.'
        }
    }
    function Wait-CoalescingEdit([string]$Phase,$Before,[DateTimeOffset]$WrittenAt,[byte[]]$Bytes,[long]$Baseline,[guid[]]$Required=@()) {
        $state=@{lastStatus=$null}
        $completed=Wait-CliCoalescingEditWitness -Before $Before -WrittenAt $WrittenAt -LocalPath $local -ExpectedContent $Bytes `
            -Deadline ([DateTimeOffset]::UtcNow.AddSeconds(30)) -RequiredOriginalIds $Required -ReadStoppedSnapshot {
                param($attempt)
                $state.lastStatus=Invoke-CoalescingCli ($Phase+'-edit-attempt-'+$attempt+'-status') @('--json','--developer-mode','status')
                $stop=if($attempt -eq 1){'stop-after-'+$Phase+'-edit'}else{'stop-after-'+$Phase+'-edit-attempt-'+$attempt}
                Invoke-CoalescingCli $stop @('--json','--developer-mode','host','stop')|Out-Null
                & $WaitHostStopped
                $snapshot=Read-CoalescingEdit ($Phase+'-edit-attempt-'+$attempt+'-originals')
                Assert-KeepRemoteResident $snapshot $editBaseline.nativeIdentitySha256
                $snapshot
            } -ResumeHost {
                param($attempt)
                Invoke-CoalescingCli ('resume-'+$Phase+'-edit-after-attempt-'+$attempt) @('--json','--developer-mode','host','start')|Out-Null
            }
        $state.lastStatus|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $originals ($Phase+'-edit-status.json')) -Encoding utf8
        @{schemaVersion=1;writtenAt=$WrittenAt.ToString('o');expectedSha256=$completed.witness.localSha256;
            actualSha256=(Get-FileHash -LiteralPath $local).Hash;length=(Get-Item -LiteralPath $local).Length;
            baselinePendingUploads=$Baseline;observedPendingUploads=$state.lastStatus.pendingUploads}|
            ConvertTo-Json|Set-Content -LiteralPath (Join-Path $originals ($Phase+'-edit-bytes.json')) -Encoding utf8
        $completed.snapshot|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $originals ($Phase+'-edit-originals.json')) -Encoding utf8
        $completed.witness|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $originals ($Phase+'-edit-witness.json')) -Encoding utf8
        @{schemaVersion=1;attempts=$completed.attempts;elapsedMilliseconds=$completed.elapsedMilliseconds;
            maximumWaitSeconds=$completed.maximumWaitSeconds;globalPendingCountUsedAsProof=$false}|
            ConvertTo-Json|Set-Content -LiteralPath (Join-Path $originals ($Phase+'-edit-observation.json')) -Encoding utf8
        $completed
    }
    $source=Join-Path $SourceRoot 'cli-roundtrip.txt';$local=Join-Path $MappedRoot 'cli-roundtrip.txt'
    $seed=Get-CliCoalescingSeedBytes
    if($EmptyCreate){$seed=[byte[]]::new(0)}
    $first=[Text.Encoding]::UTF8.GetBytes('signed coalescing first local edit')
    $latest=[Text.Encoding]::UTF8.GetBytes('signed coalescing final local bytes retained by KeepBoth')
    $remote=[Text.Encoding]::UTF8.GetBytes('signed coalescing independently selected remote version')
    $seedHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($seed))
    $latestHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($latest))
    $remoteHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($remote))
    $onlineSeedIdentity=''
    if($ControlledCreate){
        if((Test-Path -LiteralPath $local) -or (Test-Path -LiteralPath $source)){
            throw 'The controlled-Create fixture requires the original absence of both destinations.'
        }
    }elseif($Action -ceq 'KeepRemote'){
        # The signed Worker exposes this existing remote item through the normal initial discovery.
        Assert-CoalescingHash $source $seedHash $false
        Assert-CoalescingHash $local $seedHash $true
        $onlineSeed=& $InspectOnlineSeed
        $onlineSeed|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $originals 'keep-remote-seed-online.json') -Encoding utf8
        Assert-KeepRemoteResident $onlineSeed ''
        $onlineSeedIdentity=$onlineSeed.nativeIdentitySha256
    }else{
        [IO.File]::WriteAllBytes($source,$seed)
        Assert-CoalescingHash $local $seedHash $true
    }
    Invoke-CoalescingCli 'disable' @('--json','--developer-mode','instance','disable','--instance-id',$InstanceId)|Out-Null
    Invoke-CoalescingCli 'disabled-restart' @('--json','--developer-mode','host','restart')|Out-Null
    & $WaitHostStopped
    $disabled=Invoke-CoalescingCli 'disabled-status' @('--json','--developer-mode','status')
    $entry=@($disabled.instances|Where-Object instanceId -CEQ $InstanceId)
    if($entry.Count -ne 1 -or $entry[0].enabled -ne $false){throw 'The real instance must be disabled before edits.'}
    $baseline=[long]$disabled.pendingUploads
    $importOperation=$null
    if($ControlledCreate){
        # Public creation/import runs through the default Host while its instance
        # stays disabled. No test code manufactures a journal original.
        $parent=Invoke-CoalescingCli 'create-parent' @('--json','--developer-mode','namespace','member','inspect',([IO.Path]::GetFileName($MappedRoot)),'--directory')
        $importOperation=[guid]::NewGuid().ToString('D')
        if($EmptyCreate){
            $created=Invoke-CoalescingCli 'execute-create' @('--json','--developer-mode','namespace','member','create',([string]$parent.memberId),'cli-roundtrip.txt','--operation-id',$importOperation)
        }else{
            $input=Join-Path ([IO.Path]::GetDirectoryName($SourceRoot)) 'create-input.bin'
            [IO.File]::WriteAllBytes($input,$seed)
            Invoke-CoalescingCli 'prepare-create' @('--json','--developer-mode','namespace','member','prepare-import',([string]$parent.memberId),'cli-roundtrip.txt',$input,'--operation-id',$importOperation)|Out-Null
            $created=Invoke-CoalescingCli 'execute-create' @('--json','--developer-mode','namespace','member','import',$importOperation)
        }
        if($created.operationId -cne $importOperation){throw 'The actual public import lost its original operation.'}
        Assert-CoalescingHash $local $seedHash $false
        if(Test-Path -LiteralPath $source){throw 'The disabled controlled Create touched the backend.'}
        Confirm-CoalescingDisabled 'after-create-status'|Out-Null
    }
    Invoke-CoalescingCli 'stop-before-edit-baseline' @('--json','--developer-mode','host','stop')|Out-Null
    & $WaitHostStopped
    $editBaseline=Read-CoalescingEdit 'edit-baseline'
    Assert-KeepRemoteResident $editBaseline $onlineSeedIdentity
    Invoke-CoalescingCli 'start-before-first-edit' @('--json','--developer-mode','host','start')|Out-Null
    $baseline=[long](Confirm-CoalescingDisabled 'before-first-edit-status').pendingUploads
    $firstWrittenAt=[DateTimeOffset]::UtcNow
    Write-CoalescingEdit $first
    $firstObserved=Wait-CoalescingEdit 'first' $editBaseline $firstWrittenAt $first $baseline
    $firstOriginals=$firstObserved.snapshot;$firstWitness=$firstObserved.witness
    Invoke-CoalescingCli 'start-before-second-edit' @('--json','--developer-mode','host','start')|Out-Null
    $firstCount=[long](Confirm-CoalescingDisabled 'before-second-edit-status').pendingUploads
    $secondWrittenAt=[DateTimeOffset]::UtcNow
    Write-CoalescingEdit $latest
    $secondObserved=Wait-CoalescingEdit 'second' $firstOriginals $secondWrittenAt $latest $firstCount $firstWitness.originalOperationIds
    $secondOriginals=$secondObserved.snapshot;$secondWitness=$secondObserved.witness
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
            if(($ControlledCreate -or -not [string]::IsNullOrEmpty($conflict.localRevision)) -and -not [string]::IsNullOrEmpty($conflict.remoteRevision)){break}
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
    foreach($id in @($firstWitness.originalOperationIds)+@($secondWitness.originalOperationIds)){
        if($id -cnotin @($pending.originalOperationIds)){throw 'The retained conflict group lost an actual first or second edit original.'}
    }
    if($pending.rawEffect -cne $expectedEffect -or $pending.originalCount -lt 2 -or
        $pending.pendingOriginalCount -ne $pending.originalCount -or $pending.originalExpectedRevisionPresent -ne (-not $ControlledCreate) -or
        $pending.originalEffectState -cne 'Conflict'){
        throw 'The stopped pre-choice oracle did not retain every actual original under its exact rejected effect.'
    }
    $pending|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $originals 'pending-before-choice.json') -Encoding utf8
    if(((-not $ControlledCreate) -and [string]::IsNullOrEmpty($conflict.localRevision)) -or [string]::IsNullOrEmpty($conflict.remoteRevision)){
        throw "The actual conflict did not expose the selected revisions required for $Action."
    }
    $observation=$pending.selectedRemoteObservation
    if($null -ne $observation){
        $selectedRevisionHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($conflict.remoteRevision)))
        $requestRevisionHash=if($ControlledCreate){$null}else{[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($conflict.localRevision)))}
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
    $choice=& $ResolveChoice $conflict.conflictId
    $choiceFile=if($Action -ceq 'KeepRemote'){'keep-remote.json'}else{'keep-both.json'}
    $choice|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $originals $choiceFile) -Encoding utf8
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
    if($operation.data.operationId -cne $command.ToString('D') -or $operation.data.action -cne $Action.ToLowerInvariant() -or
        $operation.data.targetId -cne $conflict.conflictId -or $operation.data.state -cne 'resolved'){
        throw "The exact $Action command did not resolve within the original 30-second observation window."
    }
    $resolvedHash=if($Action -ceq 'KeepRemote'){$remoteHash}else{$latestHash}
    Assert-CoalescingHash $source $resolvedHash $false
    Assert-CoalescingHash $local $resolvedHash $false
    Invoke-CoalescingCli 'stop-before-audit' @('--json','--developer-mode','host','stop')|Out-Null
    & $WaitHostStopped
    $before=& $ReadStoppedAudit $true
    if($before.rawEffect -cne $expectedEffect -or $before.originalCount -lt 2 -or
        $before.acknowledgedOriginalCount -ne $before.originalCount -or $before.localSha256 -cne $resolvedHash -or
        $before.preservedRemoteSha256 -cne $remoteHash -or $before.actualNativeConfirmation -ne $true -or
        $before.planId -cne $pending.planId -or $before.commandId -cne $command.ToString('D') -or
        (@($before.originalOperationIds) -join ',') -cne (@($pending.originalOperationIds) -join ',') -or
        ($Action -ceq 'KeepRemote' -and ($before.preservedLocalSha256 -cne $latestHash -or -not $before.acknowledgementId))){
        throw "The stopped original oracle did not prove the complete signed UpdateFile/$Action boundary."
    }
    if($ControlledCreate -and ($before.originalBirthOperationId -cne $importOperation -or
        $before.originalBirthSha256 -cnotmatch '^[A-F0-9]{64}$' -or $before.backendAddressDiffersFromOriginalBirth -ne $true -or
        $before.currentNativeIdentitySha256 -cnotmatch '^[A-F0-9]{64}$' -or $before.currentNativeOriginalBindingVerified -ne $true -or
        $before.originalExpectedRevisionPresent -ne $false)){
        throw 'The default Host choice lost the original unaccepted local birth or adopted the backend address.'
    }
    if($ControlledCreate){
        $expectedBirthOrigin=if($EmptyCreate){'ControlledCreation'}else{'Import'}
        if($before.originalBirthOrigin -cne $expectedBirthOrigin){throw 'The public Host operation lost its exact original local birth origin.'}
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
    Assert-CoalescingHash $source $resolvedHash $false
    Assert-CoalescingHash $local $resolvedHash $false
    Invoke-CoalescingCli 'stop-after-restart' @('--json','--developer-mode','host','stop')|Out-Null
    & $WaitHostStopped
    $after=& $ReadStoppedAudit $true
    $after|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $originals 'stopped-after-restart.json') -Encoding utf8
    if(($before|ConvertTo-Json -Depth 8 -Compress) -cne ($after|ConvertTo-Json -Depth 8 -Compress)){
        throw 'The restarted Host changed the completed original execution, private copy or acknowledgement facts.'
    }
    $report=[ordered]@{schemaVersion=1;sourceSha=$env:GITHUB_SHA;runId=$env:GITHUB_RUN_ID;defaultHostIntegrated=$true;signedWorker=$true;
        rawEffect=$expectedEffect;action=$Action;originalCount=$before.originalCount;actualNativeConfirmation=$true;
        firstEditOriginalOperationIds=@($firstWitness.originalOperationIds);secondEditOriginalOperationIds=@($secondWitness.originalOperationIds);
        actualEditOriginalJournalWitness=$true;globalPendingCountUsedAsEditProof=$false;
        localSha256=$resolvedHash;preservedRemoteSha256=$remoteHash;unchangedRetainedFactsAfterActualRestart=$true;
        fullSevenEffectMatrixAccepted=$false;fullP076Accepted=$false;teardownCompleted=$false}
    if($Action -ceq 'KeepRemote'){
        $report.preservedLocalSha256=$before.preservedLocalSha256
        $report.acknowledgementId=$before.acknowledgementId
    }
    if($ControlledCreate){
        $report.originalBirthOperationId=$before.originalBirthOperationId
        $report.originalBirthOrigin=$before.originalBirthOrigin
        $report.originalBirthSha256=$before.originalBirthSha256
        $report.backendAddressDiffersFromOriginalBirth=$true
        $report.currentNativeIdentitySha256=$before.currentNativeIdentitySha256
        $report.currentNativeOriginalBindingVerified=$true
        $report.originalExpectedRevisionPresent=$false
    }
    $report
}
