[CmdletBinding()]
param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime='win-arm64',
    [string]$PackagePath,
    [ValidateSet('Debug','Release')][string]$Configuration='Release',
    [string]$EvidencePath,
    [switch]$MovedSourceConflict,
    [ValidateSet('Absent','Successor')][string]$MovedSourceAddress='Absent',
    [switch]$EditedSourceConflict,
    [switch]$LargeBacklog,
    [switch]$BudgetOnly
)
if($BudgetOnly){15;exit 0}
$ErrorActionPreference='Stop'
$withSuccessor=$MovedSourceAddress -ieq 'Successor'
if($withSuccessor -and !$MovedSourceConflict){
    throw 'The successor address requires the moved-source conflict scenario.'
}
$hasSourceConflict=$MovedSourceConflict -or $EditedSourceConflict
if(($LargeBacklog -and $hasSourceConflict) -or ($MovedSourceConflict -and $EditedSourceConflict)){
    throw 'Select one disabled fixture scenario.'
}
if($env:MIRRORPULSE_NATIVE_TEST -cne '1' -or [string]::IsNullOrWhiteSpace($EvidencePath) -or -not (Test-Path -LiteralPath $PackagePath -PathType Leaf)){
    throw 'The signed disabled-operation fixture requires its exact package, evidence path and disposable native environment.'
}
$repository=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourceSha=(& git -C $repository rev-parse HEAD|Out-String).Trim()
if($LASTEXITCODE -ne 0 -or $sourceSha -cnotmatch '^[0-9a-f]{40}$' -or ($env:GITHUB_SHA -and $env:GITHUB_SHA -cne $sourceSha)){
    throw 'Bind the actual selected source before running the disabled fixture.'
}
. (Join-Path $PSScriptRoot 'cli-integration-failure-policy.ps1')
. (Join-Path $PSScriptRoot 'cli-disabled-settlement-evidence.ps1')
. (Join-Path $PSScriptRoot 'cli-root-label-host-start.ps1')
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('MirrorPulse-cli-disabled-'+[guid]::NewGuid().ToString('N'))
$sync=Join-Path $fixture 'sync';$data=Join-Path $fixture 'data'
$evidenceFile=[IO.Path]::GetFullPath($EvidencePath)
$originals=Join-Path ([IO.Path]::GetDirectoryName($evidenceFile)) 'disabled.originals'
New-Item -ItemType Directory -Path $originals -Force|Out-Null
$markerPath=Join-Path $fixture '.mp-cli-disabled-fixture.json'
$savedEnvironment=@{}
foreach($name in @('MIRRORPULSE_DATA_ROOT','MIRRORPULSE_SYNC_ROOT','MIRRORPULSE_HOST_PATH','MIRRORPULSE_DEVELOPER_MODE')){
    $savedEnvironment[$name]=[Environment]::GetEnvironmentVariable($name,'Process')
}
$cliBinary=$null;$probeAssembly=$null;$marker=$null;$primaryFailure=$null;$report=$null;$phase='Setup';$ownedDisabledHost=$null
$lifetimes=[Collections.Generic.List[object]]::new()
$replacementInput=Join-Path $fixture 'replacement.bin'
$keptBytes=[Text.Encoding]::UTF8.GetBytes('original accepted file before explicit offline replacement')
$replacementBytes=[Text.Encoding]::UTF8.GetBytes('explicit offline replacement bytes')
$latestBytes=[Text.Encoding]::UTF8.GetBytes('later offline edit survives replacement and moved-name replay')
$bornBytes=[Text.Encoding]::UTF8.GetBytes('new offline file remains complete under its moved name')
$activeBytes=[Text.Encoding]::UTF8.GetBytes('the independent active instance progresses past offline originals')
$journalPageSize=64
$backlogKeys=@(1..80|ForEach-Object {'backlog-'+$_.ToString('D3',[Globalization.CultureInfo]::InvariantCulture)})
function Write-DisabledOriginal([string]$Name,[object]$Value){
    if($Name -cnotmatch '^[a-z0-9-]{1,90}$'){throw 'Bound the original fixture artifact name.'}
    $Value|ConvertTo-Json -Depth 40|Set-Content -LiteralPath (Join-Path $originals ($Name+'.json')) -Encoding utf8
}
function Save-DisabledMarker{$marker|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $markerPath -Encoding utf8}
function Invoke-DisabledCli([string]$Name,[string[]]$Arguments,[int[]]$ExpectedExitCodes=@(0),[switch]$ReturnEnvelope){
    $raw=(& $cliBinary --json --developer-mode @Arguments 2>&1|Out-String).Trim();$exitCode=$LASTEXITCODE
    if($raw){try{$value=$raw|ConvertFrom-Json;Write-DisabledOriginal $Name $value}catch{}}
    if($exitCode -notin $ExpectedExitCodes -or -not $raw){throw ('The actual disabled fixture CLI failed at '+$Name+' (exit '+$exitCode+').')}
    $value=$raw|ConvertFrom-Json
    if($ReturnEnvelope){return $value}
    if($value.kind -cne 'result'){throw 'The public CLI returned an unexpected envelope.'}
    $value.data
}
function Stop-DisabledHost([string]$Name){
    if($null -eq $script:ownedDisabledHost){throw 'There is no authenticated owned Host lifetime to stop.'}
    $process=Get-Process -Id $script:ownedDisabledHost.processId
    try{
        if($process.StartTime.ToUniversalTime().ToString('o') -cne $script:ownedDisabledHost.startedUtc){throw 'The owned Host process identity was replaced.'}
        if($LargeBacklog){try{$null=$process.Handle}catch [InvalidOperationException]{}catch [ComponentModel.Win32Exception]{}}
        Invoke-DisabledCli ($Name+'-stop') @('--no-start','host','stop')|Out-Null
        if(-not $process.WaitForExit(20000)){throw 'The owned disabled fixture Host did not stop within its lifecycle budget.'}
        $script:ownedDisabledHost.exitObserved=$true
        if($LargeBacklog){
            $script:ownedDisabledHost.exitCode=$null
            try{$script:ownedDisabledHost.exitCode=$process.ExitCode}catch [InvalidOperationException]{}catch [ComponentModel.Win32Exception]{}
        }
        & $cliBinary --json --developer-mode --no-start host status 2>$null|Out-Null
        if($LASTEXITCODE -eq 0){throw 'The stopped owned Host retained a live lifecycle channel.'}
        $script:ownedDisabledHost=$null
    }finally{$process.Dispose()}
}
function Register-DisabledHost([string]$Name,[object]$Status){
    $pidValue=[int]$status.processId;$process=Get-Process -Id $pidValue
    try{
        $image=$process.MainModule.FileName
        if($image -ine (Join-Path $fixture 'host/MirrorPulse.Host.exe')){throw 'The default Host ran outside its owned published image.'}
        $clr=@($process.Modules|Where-Object ModuleName -ieq 'coreclr.dll')
        if($clr.Count -ne 1 -or $clr[0].FileName -ine (Join-Path $fixture 'host/coreclr.dll')){throw 'The actual Host did not load its private runtime.'}
        $script:ownedDisabledHost=[ordered]@{phase=$Name;processId=$pidValue;startedUtc=$process.StartTime.ToUniversalTime().ToString('o');imageSha256=(Get-FileHash -LiteralPath $image).Hash;privateRuntime=$true;exitObserved=$false}
        $lifetimes.Add($script:ownedDisabledHost)
    }finally{$process.Dispose()}
}
function Start-DisabledHost([string]$Name){
    if($LargeBacklog -and $lifetimes.Count){
        $previousProcessId=$lifetimes[-1].processId
        $status=Wait-CliRootLabelHostStart -Phase $Name -PreviousProcessId $previousProcessId -OriginalsDirectory $originals -InvokeCli {
            param($arguments,$expectedExitCodes=@(0))
            $command=$arguments[-1]
            $envelope=Invoke-DisabledCli ($Name+'-'+$command+'-observation') @($arguments|Where-Object {$_ -cne '--json'}) $expectedExitCodes -ReturnEnvelope
            if($null -eq $script:ownedDisabledHost -and $envelope.kind -ceq 'result' -and
                $envelope.data.processId -gt 0 -and $envelope.data.processId -ne $previousProcessId){
                Register-DisabledHost $Name $envelope.data
            }
            return $envelope
        }
        Assert-CliRootLabelHostStartOriginal -OriginalsDirectory $originals -Phase $Name -After $status
        if($script:ownedDisabledHost.processId -ne $status.processId){throw 'The ready backlog Host differs from its authenticated starting process.'}
    }else{
        $status=Invoke-DisabledCli ($Name+'-start') @('host','start')
        Register-DisabledHost $Name $status
    }
    if($null -ne $marker){Wait-DisabledComposition $Name}
}
function Wait-DisabledComposition([string]$Name){
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(30)
    do{
        $state=Invoke-DisabledCli ($Name+'-composition-status') @('--no-start','status')
        $topology=Invoke-DisabledCli ($Name+'-composition-topology') @('--no-start','adapter','list')
        $ready=@($topology.instances).Count -eq 2 -and @($state.instances).Count -eq 2
        foreach($instance in $topology.instances){
            $actual=@($state.instances|Where-Object instanceId -ceq $instance.instanceId)
            if($actual.Count -ne 1 -or $actual[0].workerConnected -ne $instance.enabled){$ready=$false}
        }
        if($ready){
            $roots=Invoke-DisabledCli ($Name+'-composition-roots') @('--no-start','root','list')
            foreach($route in $marker.routes){
                $root=@($roots|Where-Object {$_.root.rootId -ceq $route.rootId})
                $instance=@($topology.instances|Where-Object instanceId -ceq $route.instanceId)
                if($root.Count -ne 1 -or $instance.Count -ne 1 -or $root[0].namespaceReady -ne $instance[0].enabled){$ready=$false}
            }
        }
        if($ready){return}
        Start-Sleep -Milliseconds 100
    }while([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'The real Host composition did not connect its enabled Workers and freshly admit its exact roots within30seconds.'
}
function Invoke-DisabledProbe([ValidateSet('audit','cleanup')][string]$Mode,[string]$Name){
    $start=[Diagnostics.ProcessStartInfo]::new('dotnet');$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in @($probeAssembly,('--'+$Mode+'-cli-disabled-fixture'),$fixture)){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start)
    try{
        $output=$process.StandardOutput.ReadToEndAsync();$errors=$process.StandardError.ReadToEndAsync()
        $timeoutMilliseconds=if($LargeBacklog){90000}else{30000}
        if(-not $process.WaitForExit($timeoutMilliseconds)){throw 'The actual stopped disabled oracle exceeded its observation deadline.'}
        $raw=$output.GetAwaiter().GetResult()
        if($process.ExitCode -ne 0){
            if($LargeBacklog -and $raw){
                $partial=$raw|ConvertFrom-Json -ErrorAction SilentlyContinue
                if($partial.probeFailureKind -ceq 'OfficialJournalReadLimit' -and $partial.sourceSha -ceq $sourceSha){
                    Write-DisabledOriginal ($Name+'-partial') $partial
                }
            }
            throw ('The stopped disabled oracle failed: '+$errors.GetAwaiter().GetResult())
        }
        $value=$raw|ConvertFrom-Json;Write-DisabledOriginal $Name $value;$value
    }finally{if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()};$process.Dispose()}
}
function Get-DisabledHash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function Get-DisabledRevisionHash([string]$Revision){
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Revision)))
}
function Get-DisabledSourceSnapshot([string]$Path,[int]$MaximumEntries=64){
    $resolved=[IO.Path]::GetFullPath($Path)
    if(-not $resolved.StartsWith(([IO.Path]::GetFullPath((Join-Path $fixture 'source'))+[IO.Path]::DirectorySeparatorChar),[StringComparison]::OrdinalIgnoreCase)){
        throw 'A source observation escaped the exact private fixture.'
    }
    foreach($parent in @($fixture,(Join-Path $fixture 'source'),$resolved)){
        if((Get-Item -LiteralPath $parent -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){throw 'The private source root was redirected.'}
    }
    $items=[Collections.Generic.List[object]]::new()
    $directories=[Collections.Generic.Queue[string]]::new();$directories.Enqueue($resolved)
    while($directories.Count){
        foreach($entry in Get-ChildItem -LiteralPath $directories.Dequeue() -Force){
            if($entry.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -or $items.Count -ge $MaximumEntries){throw 'A private source changed to an unbounded or redirected tree.'}
            $items.Add($entry)
            if($entry.PSIsContainer){$directories.Enqueue($entry.FullName)}
        }
    }
    @($items|Sort-Object FullName|ForEach-Object{
        if(-not $_.PSIsContainer -and $_.Length -gt 4MB){throw 'The synthetic source file exceeded its byte bound.'}
        [ordered]@{relativePath=[IO.Path]::GetRelativePath($resolved,$_.FullName).Replace('\','/');isDirectory=[bool]$_.PSIsContainer;
            sha256=$(if($_.PSIsContainer){$null}else{Get-DisabledHash $_.FullName});modifiedUtc=$(if($_.PSIsContainer){$null}else{$_.LastWriteTimeUtc.ToString('o')})}
    })
}
function Write-DisabledBytes([string]$Path,[byte[]]$Bytes){
    $stream=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try{$stream.Write($Bytes,0,$Bytes.Length);$stream.SetLength($Bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
}
function Wait-DisabledHash([string]$Path,[string]$Expected,[string]$Name){
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(30)
    do{
        try{if((Test-Path -LiteralPath $Path -PathType Leaf) -and (Get-DisabledHash $Path) -ceq $Expected){return}}catch [IO.IOException]{}
        Start-Sleep -Milliseconds 100
    }while([DateTimeOffset]::UtcNow -lt $deadline)
    throw ('The actual source/local bytes did not reach '+$Name+' within30seconds.')
}
function Record-DisabledMember([string]$Key,[string]$Relative,[bool]$Directory){
    $arguments=@('--no-start','namespace','member','inspect',$Relative);if($Directory){$arguments+='--directory'}
    $member=Invoke-DisabledCli ('inspect-'+$Key.ToLowerInvariant()) $arguments
    if(-not $member.nativeItemId){throw 'An original public native identity is missing.'}
    $marker.members[$Key]=[ordered]@{memberId=$member.memberId;itemId=$member.nativeItemId};Save-DisabledMarker;$member
}
function Read-DisabledState([string]$Name){
    $status=Invoke-DisabledCli ($Name+'-status') @('--no-start','status')
    $topology=Invoke-DisabledCli ($Name+'-topology') @('--no-start','adapter','list')
    [ordered]@{status=$status;topology=$topology}
}
function Read-DisabledWorkers([string]$Name,[int]$Expected){
    $directory=[IO.Path]::GetFullPath($installation[0].installationDirectory).TrimEnd('\')
    if(-not $directory.StartsWith(([IO.Path]::GetFullPath($data).TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase)){
        throw 'The signed Worker installation escaped the private fixture.'
    }
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(30)
    do{
        $bindings=[Collections.Generic.List[object]]::new()
        foreach($process in Get-Process){
            try{
                $image=$process.MainModule.FileName
                if(-not $image -or -not $image.StartsWith(($directory+'\'),[StringComparison]::OrdinalIgnoreCase)){continue}
                $clr=@($process.Modules|Where-Object ModuleName -ieq 'coreclr.dll')
                if($clr.Count -ne 1 -or $clr[0].FileName -ine (Join-Path ([IO.Path]::GetDirectoryName($image)) 'coreclr.dll')){throw 'An actual signed Worker did not load its private runtime.'}
                $bindings.Add([ordered]@{processId=$process.Id;startedUtc=$process.StartTime.ToUniversalTime().ToString('o');imageSha256=Get-DisabledHash $image;privateRuntime=$true})
            }catch [ComponentModel.Win32Exception]{}catch [InvalidOperationException]{}finally{$process.Dispose()}
        }
        if($bindings.Count -eq $Expected){Write-DisabledOriginal ($Name+'-workers') ([ordered]@{actualCount=$Expected;bindings=$bindings.ToArray()});return}
        Start-Sleep -Milliseconds 100
    }while([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'The actual signed Worker process inventory did not reach its retained instance state.'
}
function Assert-DisabledSourceUnchanged([string]$Name){
    $current=Get-DisabledSourceSnapshot (Join-Path $fixture 'source/offline')
    Write-DisabledOriginal ($Name+'-source') $current
    if(($baseline|ConvertTo-Json -Depth 8 -Compress) -cne ($current|ConvertTo-Json -Depth 8 -Compress)){
        throw 'The retained disabled backend changed across an actual Host or active-instance lifetime.'
    }
}
function Assert-DisabledInstances($State,[bool]$OfflineEnabled,[bool]$ActiveEnabled){
    foreach($side in @('offline','active')){
        $route=@($marker.routes|Where-Object fixtureSide -ceq $side)[0]
        $instance=@($State.status.instances|Where-Object instanceId -ceq $route.instanceId)
        $enabled=if($side -ceq 'offline'){$OfflineEnabled}else{$ActiveEnabled}
        if($instance.Count -ne 1 -or $instance[0].enabled -ne $enabled -or $instance[0].workerConnected -ne $enabled){throw 'The actual instance/Worker state differs from the requested offline boundary.'}
    }
}
function Assert-DisabledOriginals($Before,$After){
    foreach($row in @($Before.journal)){
        $retained=@($After.journal|Where-Object operationId -ceq $row.operationId)
        if($retained.Count -ne 1 -or $retained[0].itemId -cne $row.itemId -or $retained[0].kind -cne $row.kind -or
            $retained[0].sequence -ne $row.sequence -or $retained[0].attemptCount -ne $row.attemptCount -or
            ([DateTimeOffset]$retained[0].createdAt) -ne ([DateTimeOffset]$row.createdAt) -or $retained[0].payloadSha256 -cne $row.payloadSha256){
            throw 'An actual original SDK operation changed or disappeared across disabled ownership.'
        }
    }
}
function Assert-DisabledSettled($Original,[string]$Kind,$Header,$Before,$After,[object[]]$Events){
    if($null -eq $Original){throw 'The actual original operation outcome is missing.'}
    if($Original.pending){throw 'The original SDK operation remains pending after its actual source/unsupported outcome.'}
    if($Original.blockReason -ceq 'UnsupportedMetadataChange'){
        if($Kind -cne 'MetadataUpdate'){throw 'An unsupported metadata result cannot settle another original operation kind.'}
        return
    }
    if(@($Original.effects).Count -eq 1 -and $Original.effects[0].state -ceq 'Missing' -and $Kind -cin @('Create','ContentUpdate')){
        Assert-CliDisabledAcceptedObservation -Before $Before -After $After -Original $Header -Outcome $Original -Events $Events
        return
    }
    if(@($Original.effects).Count -lt 1 -or @($Original.effects|Where-Object state -cne 'Acknowledged').Count){throw 'Missing SDK work is not a durable original source acknowledgement.'}
}
function Assert-DisabledLocalReceipts($Before,$After){
    foreach($collection in @('members','births','moves','retirements')){
        $left=@($Before.$collection|ConvertTo-Json -Depth 20 -Compress)
        $right=@($After.$collection|ConvertTo-Json -Depth 20 -Compress)
        if(($left -join '') -cne ($right -join '')){throw 'An immutable original local receipt or member binding changed across Host lifetimes.'}
    }
    if(($Before.replacement|ConvertTo-Json -Depth 20 -Compress) -cne ($After.replacement|ConvertTo-Json -Depth 20 -Compress)){throw 'The original local replacement receipt or backup changed.'}
}
function Confirm-DisabledPublishedInputs {
    foreach($publishedInput in $inputs){
        if((Get-DisabledHash (Join-Path $fixture ($publishedInput.owner+'/'+$publishedInput.file))) -cne $publishedInput.sha256){throw 'Actual published input bytes changed during the fixture.'}
    }
    Write-DisabledOriginal 'published-inputs-after' ([ordered]@{sourceSha=$sourceSha;inputs=$inputs.ToArray();actualBytesUnchanged=$true;actualManagedModuleLoadClaimed=$false})
}

function Wait-DisabledBacklog([string]$Name,[int]$Minimum){
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(45)
    $previous=-1;$changedAt=[DateTimeOffset]::UtcNow
    do{
        $status=Invoke-DisabledCli ($Name+'-status') @('--no-start','status')
        $now=[DateTimeOffset]::UtcNow
        if($status.pendingUploads -ne $previous){$previous=$status.pendingUploads;$changedAt=$now}
        if($previous -ge $Minimum -and ($now-$changedAt).TotalSeconds -ge 2){return $status}
        Start-Sleep -Milliseconds 150
    }while([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'The public pending upload count did not settle at the required fixture boundary.'
}

function Read-DisabledBacklogContinuation($Previous,[long]$PreviousSequence,$Current,[string[]]$OfflineItems){
    $rows=@($Current.journal|Where-Object itemId -CIn $OfflineItems)
    Assert-DisabledOriginals @{journal=$Previous} @{journal=$rows}
    $newRows=@($rows|Where-Object operationId -CNotIn @($Previous.operationId))
    if(@($newRows|Where-Object {$_.kind -cne 'MetadataUpdate' -or $_.sequence -le $PreviousSequence -or $_.attemptCount -ne 0}).Count){
        throw 'Restart recorded additional offline work beyond fresh, unattempted metadata on the same files.'
    }
    [pscustomobject]@{journal=$rows;additionalMetadataCount=$newRows.Count}
}

function Assert-DisabledBacklogOutcomes($OfflineRows,$Observed){
    $kinds=@{}
    foreach($row in $OfflineRows){$kinds.Add($row.operationId,$row.kind)}
    $outcomes=@($Observed.originals|Where-Object {$kinds.ContainsKey($_.operationId)})
    if($outcomes.Count -ne $OfflineRows.Count){throw 'The stopped audit lacks an original offline operation outcome.'}
    foreach($outcome in $outcomes){
        if(!$outcome.pending -or $outcome.planId -or @($outcome.effects|Where-Object state -CNE 'Missing').Count -or
            ($outcome.blockReason -and ($outcome.blockReason -cne 'UnsupportedMetadataChange' -or $kinds[$outcome.operationId] -cne 'MetadataUpdate'))){
            throw 'An original offline operation acquired an unexpected disposition or remote dispatch state.'
        }
    }
    @($outcomes|Where-Object blockReason -CEQ 'UnsupportedMetadataChange').Count
}

function Invoke-DisabledLargeBacklog {
    $script:phase='LargeBacklogPrime'
    $sourceRoot=Join-Path $fixture 'source/offline'
    $activeLocal=Join-Path $activeDirectory 'progress.txt'
    $activeSource=Join-Path $fixture 'source/active/progress.txt'
    $activeOriginalDigest=Get-DisabledHash $activeSource
    $baseline=Get-DisabledSourceSnapshot $sourceRoot $backlogKeys.Count
    if($baseline.Count -ne $backlogKeys.Count){throw 'The exact synthetic backlog source inventory is incomplete.'}
    Write-DisabledOriginal 'backlog-source-before' $baseline
    foreach($key in $backlogKeys){
        $leaf=$key+'.bin'
        Wait-DisabledHash (Join-Path $offlineDirectory $leaf) (Get-DisabledHash (Join-Path $sourceRoot $leaf)) $key
        Record-DisabledMember $key ($offlineRoute.directoryName+'/'+$leaf) $false|Out-Null
    }
    Record-DisabledMember 'activeFile' ($activeRoute.directoryName+'/progress.txt') $false|Out-Null
    Invoke-DisabledCli 'backlog-disable-offline' @('--no-start','instance','disable','--instance-id',$offlineRoute.instanceId)|Out-Null
    Invoke-DisabledCli 'backlog-disable-active' @('--no-start','instance','disable','--instance-id',$activeRoute.instanceId)|Out-Null
    Stop-DisabledHost 'backlog-configured';Start-DisabledHost 'backlog-disabled'
    $state=Read-DisabledState 'backlog-disabled';Assert-DisabledInstances $state $false $false
    Read-DisabledWorkers 'backlog-disabled' 0

    $script:phase='LargeBacklogCapture'
    $localDigests=[ordered]@{}
    foreach($key in $backlogKeys){
        $path=Join-Path $offlineDirectory ($key+'.bin')
        $edited=[Text.Encoding]::UTF8.GetBytes('independent offline content for '+$key)
        Write-DisabledBytes $path $edited
        $localDigests[$key]=Get-DisabledHash $path
        if($localDigests[$key] -cne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($edited))){throw 'An original offline edit did not retain its exact bytes.'}
    }
    Write-DisabledOriginal 'backlog-edited-local-bytes' $localDigests
    $beforeStatus=Wait-DisabledBacklog 'backlog-capture' $backlogKeys.Count
    Stop-DisabledHost 'backlog-capture';$marker.phase='Before';Save-DisabledMarker
    $before=Invoke-DisabledProbe audit 'backlog-before'
    $expectedItems=@($backlogKeys|ForEach-Object {$marker.members[$_].itemId})
    $offlineOriginals=@($before.journal|Where-Object itemId -CIn $expectedItems)
    $contentItemIds=@($offlineOriginals|Where-Object kind -CEQ 'ContentUpdate'|ForEach-Object itemId|Select-Object -Unique)
    $activeItemId=$marker.members.activeFile.itemId
    $activeBaseline=@($before.journal|Where-Object itemId -CEQ $activeItemId)
    if($offlineOriginals.Count -le $journalPageSize -or $beforeStatus.pendingUploads -lt @($before.journal).Count -or
        @(Compare-Object $expectedItems $contentItemIds -CaseSensitive).Count -or
        @($before.journal).Count -ne ($offlineOriginals.Count+$activeBaseline.Count) -or
        @($activeBaseline|Where-Object kind -CNE 'MetadataUpdate').Count){
        throw 'The capture requires one content change per offline file and only preexisting metadata for the unwritten peer.'
    }
    $marker.originals=@($before.journal.operationId);Save-DisabledMarker

    $script:phase='LargeBacklogDisabledRestart'
    Start-DisabledHost 'backlog-disabled-restart'
    Read-DisabledWorkers 'backlog-disabled-restart' 0
    Write-DisabledBytes $activeLocal $activeBytes
    $queuedStatus=Wait-DisabledBacklog 'backlog-peer-capture' ($marker.originals.Count+1)
    Stop-DisabledHost 'backlog-peer-capture';$marker.phase='DisabledRestart';Save-DisabledMarker
    $restarted=Invoke-DisabledProbe audit 'backlog-after-disabled-restart'
    Assert-DisabledOriginals $before $restarted
    Assert-DisabledLocalReceipts $before $restarted
    $restartBacklog=Read-DisabledBacklogContinuation $offlineOriginals $before.journal[-1].sequence $restarted $expectedItems
    $activeOriginals=@($restarted.journal|Where-Object itemId -CEQ $activeItemId)
    $laterActive=@($activeOriginals|Where-Object operationId -CNotIn @($activeBaseline.operationId))
    if(!@($laterActive|Where-Object kind -CEQ 'ContentUpdate').Count -or
        @($laterActive|Where-Object sequence -le $before.journal[-1].sequence).Count -or
        @($restarted.journal).Count -ne ($restartBacklog.journal.Count+$activeOriginals.Count) -or
        $queuedStatus.pendingUploads -lt @($restarted.journal).Count -or (Get-DisabledHash $activeSource) -cne $activeOriginalDigest){
        throw 'The later active peer write did not remain a separate queued SDK operation after the disabled page.'
    }
    $marker.originals=@($restarted.journal.operationId);Save-DisabledMarker

    $script:phase='LargeBacklogIndependentProgress'
    Start-DisabledHost 'backlog-before-active-enable'
    Invoke-DisabledCli 'backlog-enable-active' @('--no-start','instance','enable','--instance-id',$activeRoute.instanceId)|Out-Null
    Stop-DisabledHost 'backlog-enable-active';Start-DisabledHost 'backlog-only-peer-active'
    Read-DisabledWorkers 'backlog-only-peer-active' 1
    $activeDigest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($activeBytes))
    Wait-DisabledHash $activeSource $activeDigest 'backlog-peer-accepted'
    # A Host restart can enqueue more offline metadata. The stopped audit verifies
    # the exact active originals and their outcomes after the backend accepts the bytes.
    $peerAccepted=Wait-DisabledBacklog 'backlog-peer-accepted' $restartBacklog.journal.Count
    Assert-DisabledInstances @{status=$peerAccepted} $false $true
    Stop-DisabledHost 'backlog-finished';$marker.phase='ActiveProgress';Save-DisabledMarker
    $after=Invoke-DisabledProbe audit 'backlog-after-active-progress'
    $finalBacklog=Read-DisabledBacklogContinuation $restartBacklog.journal $restarted.journal[-1].sequence $after $expectedItems
    Assert-DisabledLocalReceipts $before $after
    if(@($after.journal).Count -ne $finalBacklog.journal.Count){throw 'The active peer left a selected operation pending after its accepted write.'}
    $offlineUnsupportedMetadata=Assert-DisabledBacklogOutcomes $restartBacklog.journal $after
    $events=@(Read-CliDisabledAcknowledgementEvents -DataRoot $data)
    Write-DisabledOriginal 'backlog-active-acknowledgement-events' $events
    foreach($row in $activeOriginals){
        Assert-DisabledSettled (@($after.originals|Where-Object operationId -CEQ $row.operationId)[0]) $row.kind $row $restarted $after $events
    }
    $actualActiveDigest=Get-DisabledHash $activeSource
    if($actualActiveDigest -cne $activeDigest -or $actualActiveDigest -ceq $activeOriginalDigest){throw 'The accepted active source bytes did not survive the Host stop.'}
    $finalSource=Get-DisabledSourceSnapshot $sourceRoot $backlogKeys.Count
    Write-DisabledOriginal 'backlog-source-after' $finalSource
    if(($finalSource|ConvertTo-Json -Depth 8 -Compress) -cne ($baseline|ConvertTo-Json -Depth 8 -Compress)){
        throw 'The disabled source changed while its peer progressed.'
    }
    $finalLocalDigests=[ordered]@{}
    foreach($key in $backlogKeys){
        $finalLocalDigests[$key]=Get-DisabledHash (Join-Path $offlineDirectory ($key+'.bin'))
        if($finalLocalDigests[$key] -cne $localDigests[$key]){throw 'An original offline edit changed during the active peer upload.'}
    }
    Write-DisabledOriginal 'backlog-retained-local-bytes' $finalLocalDigests
    Confirm-DisabledPublishedInputs
    [ordered]@{schemaVersion=1;sourceSha=$sourceSha;runId=$env:GITHUB_RUN_ID;runtime=$Runtime;signedPackageSha256=$installation[0].packageSha256;
        actualHostLifetimes=$lifetimes.ToArray();largeBacklog=$true;offlineFileCount=$backlogKeys.Count;journalPageSize=$journalPageSize;
        offlineOriginalCount=$offlineOriginals.Count;offlineMetadataAddedAfterRestart=$restartBacklog.additionalMetadataCount;
        offlineMetadataAddedWhilePeerAdvanced=$finalBacklog.additionalMetadataCount;
        offlineUnsupportedMetadataCount=$offlineUnsupportedMetadata;
        activeBaselineMetadataCount=$activeBaseline.Count;laterActiveOriginalCount=$laterActive.Count;
        activeOriginalCount=$activeOriginals.Count;activeRemoteSha256=$actualActiveDigest;
        originalsPreservedAcrossDisabledRestart=$true;independentActiveSourceProgress=$true;sourceUnchangedWhileDisabled=$true;
        allPublishedInputBytesUnchanged=$true;actualManagedModuleLoadClaimed=$false;fullS06Accepted=$false}
}

function Invoke-DisabledSourceConflict {
    $script:phase='MovedSourceOfflineCapture'
    $path=$offlineRoute.directoryName+'/kept.txt'
    $localPath=Join-Path $offlineDirectory 'kept.txt'
    $sourceRoot=Join-Path $fixture 'source/offline'
    $oldSource=Join-Path $sourceRoot 'kept.txt'
    $movedSource=Join-Path $sourceRoot 'externally-moved.txt'
    $sourceDigest=Get-DisabledHash $oldSource
    $member=Record-DisabledMember 'keptFile' $path $false
    Invoke-DisabledCli 'moved-source-disable' @('--no-start','instance','disable','--instance-id',$offlineRoute.instanceId)|Out-Null
    Stop-DisabledHost 'moved-source-configured'
    Start-DisabledHost 'moved-source-disabled'
    $disabledState=Read-DisabledState 'moved-source-disabled'
    Assert-DisabledInstances $disabledState $false $true
    Read-DisabledWorkers 'moved-source-disabled' 1
    $localBytes=[Text.Encoding]::UTF8.GetBytes('offline edit after the accepted source generation')
    Write-DisabledBytes $localPath $localBytes
    $localDigest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($localBytes))
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $status=Invoke-DisabledCli 'moved-source-pending' @('--no-start','status')
        if($status.pendingUploads -gt $disabledState.status.pendingUploads){break}
        Start-Sleep -Milliseconds 100
    }while([DateTimeOffset]::UtcNow -lt $deadline)
    Stop-DisabledHost 'moved-source-before-original'
    $before=Invoke-DisabledProbe audit 'moved-source-before'
    $originalsForMove=@($before.journal|Where-Object {$_.itemId -ceq $member.nativeItemId -and $_.kind -ceq 'ContentUpdate'})
    $baseline=$before.movedSource.acknowledgedRevisionSha256
    $sourceEntry=@($before.remoteSnapshots|Where-Object rootId -ceq $offlineRoute.rootId|
        ForEach-Object {$_.entries}|Where-Object relativePath -ceq $path)
    if(!$originalsForMove.Count -or $before.movedSource.itemId -cne $member.nativeItemId -or
        $baseline -cnotmatch '^[0-9A-F]{64}$' -or $sourceEntry.Count -ne 1 -or $sourceEntry[0].remoteRevisionSha256 -cne $baseline -or
        (Get-DisabledHash $oldSource) -cne $sourceDigest -or
        (Get-DisabledHash $localPath) -cne $localDigest){throw 'The offline edit did not retain its exact original notification and acknowledged source bytes.'}
    $marker.originals=@($originalsForMove.operationId)
    Save-DisabledMarker
    $backendBefore=Get-DisabledSourceSnapshot $sourceRoot
    if(Test-Path -LiteralPath $movedSource){throw 'The fixed backend move destination must be vacant.'}
    $editedSourceDigest=$null
    if($EditedSourceConflict){
        $script:phase='EditedSourceBackendWrite'
        Write-DisabledOriginal 'edited-source-backend-before-write' $backendBefore
        $bytes=[Text.Encoding]::UTF8.GetBytes('independent in-place edit of the original backend file while disabled')
        $editedSourceDigest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        Write-DisabledBytes $oldSource $bytes
        $expectedBackend=Get-DisabledSourceSnapshot $sourceRoot
        Write-DisabledOriginal 'edited-source-backend-after-write' $expectedBackend
        $untouchedBefore=@($backendBefore|Where-Object relativePath -CNE 'kept.txt')
        $untouchedAfter=@($expectedBackend|Where-Object relativePath -CNE 'kept.txt')
        if($editedSourceDigest -cin @($sourceDigest,$localDigest) -or (Get-DisabledHash $oldSource) -cne $editedSourceDigest -or
            ($untouchedBefore|ConvertTo-Json -Depth 8 -Compress) -cne ($untouchedAfter|ConvertTo-Json -Depth 8 -Compress)){
            throw 'The in-place backend write must change only the original source file to distinct bytes.'
        }
    }else{
        $script:phase='MovedSourceBackendMove'
        [IO.File]::Move($oldSource,$movedSource)
        $expectedBackend=Get-DisabledSourceSnapshot $sourceRoot
        Write-DisabledOriginal 'moved-source-backend-after-move' $expectedBackend
        if((Test-Path -LiteralPath $oldSource) -or (Get-DisabledHash $movedSource) -cne $sourceDigest){throw 'The actual backend move did not preserve the original bytes at the new address.'}
    }
    $successorDigest=$null
    if($withSuccessor){
        $script:phase='MovedSourceBackendSuccessor'
        $bytes=[Text.Encoding]::UTF8.GetBytes('independent later source generation at the original address')
        $stream=[IO.File]::Open($oldSource,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
        $successorDigest=Get-DisabledHash $oldSource
        $expectedBackend=Get-DisabledSourceSnapshot $sourceRoot
        Write-DisabledOriginal 'moved-source-backend-with-successor' $expectedBackend
        if($successorDigest -cin @($sourceDigest,$localDigest)){throw 'The same-name backend file must have its own distinct content.'}
    }
    Start-DisabledHost 'moved-source-reopen-disabled'
    Assert-DisabledInstances (Read-DisabledState 'moved-source-reopen-disabled') $false $true
    Read-DisabledWorkers 'moved-source-reopen-disabled' 1
    Invoke-DisabledCli 'moved-source-enable' @('--no-start','instance','enable','--instance-id',$offlineRoute.instanceId)|Out-Null
    Stop-DisabledHost 'moved-source-before-enable'
    $afterDisabledRestart=Invoke-DisabledProbe audit 'moved-source-after-disabled-restart'
    Assert-DisabledOriginals ([pscustomobject]@{journal=$originalsForMove}) $afterDisabledRestart
    if($afterDisabledRestart.movedSource.acknowledgedRevisionSha256 -cne $baseline){
        throw 'The disabled restart changed the original acknowledged revision.'
    }

    $script:phase='MovedSourceReenableConflict'
    Start-DisabledHost 'moved-source-enabled'
    Assert-DisabledInstances (Read-DisabledState 'moved-source-enabled') $true $true
    Read-DisabledWorkers 'moved-source-enabled' 2
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $page=Invoke-DisabledCli 'moved-source-public-conflicts' @('--no-start','conflict','list')
        $conflicts=@($page.items|Where-Object {$_.instanceId -ceq $offlineRoute.instanceId -and $_.relativePath.Replace('\','/') -ceq $path -and
            $_.source -ceq 'Upload' -and $_.reason -ceq 'StaleRemoteRevision' -and $_.status -ceq 'Pending'})
        if($conflicts.Count){break}
        Start-Sleep -Milliseconds 100
    }while([DateTimeOffset]::UtcNow -lt $deadline)
    if(!$conflicts.Count -or @($conflicts|Where-Object {(Get-DisabledRevisionHash $_.localRevision) -cne $baseline}).Count){
        throw 'The reenabled Host did not report the original acknowledged revision in its conditional conflict.'
    }
    $observedRevision=$conflicts[0].remoteRevision
    $observedRevisionHash=$null
    if($withSuccessor -or $EditedSourceConflict){
        if([string]::IsNullOrEmpty($observedRevision) -or (Get-DisabledRevisionHash $observedRevision) -ceq $baseline){
            throw 'The reenabled Host did not observe a distinct revision at the original address.'
        }
        $observedRevisionHash=Get-DisabledRevisionHash $observedRevision
    }elseif($null -ne $observedRevision){
        throw 'The reenabled Host did not observe the absent source address.'
    }
    if(@($conflicts|Where-Object remoteRevision -CNE $observedRevision).Count){
        throw 'Conflicts at the original address disagree about the observed remote revision.'
    }
    $current=Invoke-DisabledCli 'moved-source-retained-member' @('--no-start','namespace','member','inspect',$path)
    if($current.memberId -cne $member.memberId -or $current.nativeItemId -cne $member.nativeItemId -or
        (Get-DisabledHash $localPath) -cne $localDigest){throw 'The original local edit or its native member was replaced.'}
    Stop-DisabledHost 'moved-source-final'
    $after=Invoke-DisabledProbe audit 'moved-source-stopped-conflict'
    $finalBackend=Get-DisabledSourceSnapshot $sourceRoot
    Write-DisabledOriginal 'moved-source-backend-final' $finalBackend
    foreach($original in $originalsForMove){
        $pending=@($after.journal|Where-Object operationId -ceq $original.operationId)
        $outcome=@($after.originals|Where-Object operationId -ceq $original.operationId)
        if($pending.Count -ne 1 -or $pending[0].payloadSha256 -cne $original.payloadSha256 -or $pending[0].sequence -ne $original.sequence -or
            $pending[0].itemId -cne $original.itemId -or $pending[0].kind -cne $original.kind -or
            $outcome.Count -ne 1 -or !$outcome[0].pending -or @($outcome[0].effects|Where-Object state -ceq 'Acknowledged').Count){
            throw 'A conflicted original notification was acknowledged or replaced.'
        }
    }
    if($after.movedSource.acknowledgedRevisionSha256 -cne $baseline -or
        !@($after.movedSource.conflicts|Where-Object {
            $_.conflictId -cin @($conflicts.conflictId) -and $_.localRevisionSha256 -ceq $baseline -and
            $_.remoteRevisionSha256 -ceq $observedRevisionHash -and $_.changeId -cin $marker.originals
        }).Count -or ($MovedSourceConflict -and (Get-DisabledHash $movedSource) -cne $sourceDigest) -or
        (Get-DisabledHash $localPath) -cne $localDigest -or
        ($finalBackend|ConvertTo-Json -Depth 8 -Compress) -cne ($expectedBackend|ConvertTo-Json -Depth 8 -Compress)){
        throw 'The stopped oracle could not bind the same original conflict and unchanged backend bytes.'
    }
    Confirm-DisabledPublishedInputs
    [ordered]@{schemaVersion=1;sourceSha=$sourceSha;runId=$env:GITHUB_RUN_ID;runtime=$Runtime;signedPackageSha256=$installation[0].packageSha256;
        originalCount=$originalsForMove.Count;actualHostLifetimes=$lifetimes.ToArray();memberId=$member.memberId;nativeItemId=$member.nativeItemId;
        acknowledgedRevisionSha256=$baseline;backendOriginalSha256=$sourceDigest;
        backendMovedSha256=$(if($MovedSourceConflict){$sourceDigest});localEditSha256=$localDigest;
        movedSourceConflict=$MovedSourceConflict.IsPresent;editedSourceConflict=$EditedSourceConflict.IsPresent;
        oldAddressAbsent=(-not (Test-Path -LiteralPath $oldSource));
        successorSourceSha256=$successorDigest;successorRemoteRevisionSha256=$(if($withSuccessor){$observedRevisionHash});
        editedSourceSha256=$editedSourceDigest;editedSourceRemoteRevisionSha256=$(if($EditedSourceConflict){$observedRevisionHash});
        originalsRetained=$true;actualManagedModuleLoadClaimed=$false;fullS06Accepted=$false}
}
try{
    New-Item -ItemType Directory -Path $sync,$data,(Join-Path $fixture 'source/offline'),(Join-Path $fixture 'source/active') -Force|Out-Null
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try{$acl=Get-Acl -LiteralPath $sync;$acl.SetOwner($identity.User);Set-Acl -LiteralPath $sync -AclObject $acl}finally{$identity.Dispose()}
    if($LargeBacklog){
        foreach($key in $backlogKeys){
            [IO.File]::WriteAllBytes((Join-Path $fixture ('source/offline/'+$key+'.bin')),[Text.Encoding]::UTF8.GetBytes('accepted seed for '+$key))
        }
    }else{
        New-Item -ItemType Directory -Path (Join-Path $fixture 'source/offline/retire-dir')|Out-Null
        [IO.File]::WriteAllBytes((Join-Path $fixture 'source/offline/kept.txt'),$keptBytes)
        [IO.File]::WriteAllBytes((Join-Path $fixture 'source/offline/retire.bin'),[byte[]]@(1,2,3))
        [IO.File]::WriteAllBytes((Join-Path $fixture 'source/offline/metadata.txt'),[byte[]]@(4,5,6))
        [IO.File]::WriteAllBytes($replacementInput,$replacementBytes)
    }
    [IO.File]::WriteAllBytes((Join-Path $fixture 'source/active/progress.txt'),[byte[]]@(7,8,9))
    foreach($publish in @(@{project='src/MirrorPulse.Cli/MirrorPulse.Cli.csproj';directory='cli';native=$true},
        @{project='src/MirrorPulse.Host/MirrorPulse.Host.csproj';directory='host';native=$true},
        @{project='tests/MirrorPulse.CfSharp.CrashProbe/MirrorPulse.CfSharp.CrashProbe.csproj';directory='probe';native=$false})){
        $arguments=@('publish',(Join-Path $repository $publish.project),'-c',$Configuration,'--no-restore','--output',(Join-Path $fixture $publish.directory))
        if($publish.native){$arguments+=@('--runtime',$Runtime,'--self-contained','true')}else{$arguments+=@('--self-contained','false')}
        & dotnet @arguments;if($LASTEXITCODE -ne 0){throw 'The actual default Host fixture publish failed.'}
    }
    $cliBinary=Join-Path $fixture 'cli/mp.exe';$probeAssembly=Join-Path $fixture 'probe/MirrorPulse.CfSharp.CrashProbe.dll'
    $inputs=[Collections.Generic.List[object]]::new()
    foreach($owner in @('cli','host','probe')){
        foreach($leaf in @('mp.exe','mp.dll','MirrorPulse.Host.exe','MirrorPulse.Host.dll','MirrorPulse.Core.dll','MirrorPulse.Control.dll','MirrorPulse.CloudFiles.CfSharp.dll','CfSharp.dll','CfSharp.Native.dll','CfSharp.Storage.Sqlite.dll','MirrorPulse.Adapter.Sdk.dll','MirrorPulse.CfSharp.CrashProbe.dll')){
            $path=Join-Path $fixture ($owner+'/'+$leaf);if(-not (Test-Path -LiteralPath $path -PathType Leaf)){continue}
            $destination=Join-Path $originals ('inputs/'+$owner+'/'+$leaf);New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force|Out-Null
            Copy-Item -LiteralPath $path -Destination $destination
            $inputs.Add([ordered]@{owner=$owner;file=$leaf;sha256=Get-DisabledHash $path;archivePath='inputs/'+$owner+'/'+$leaf;informationalVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($path).ProductVersion})
        }
    }
    $requiredInputs=@{
        cli=@('mp.exe','mp.dll','MirrorPulse.Core.dll','MirrorPulse.Control.dll')
        host=@('MirrorPulse.Host.exe','MirrorPulse.Host.dll','MirrorPulse.Core.dll','MirrorPulse.Control.dll','MirrorPulse.CloudFiles.CfSharp.dll','CfSharp.dll','CfSharp.Native.dll','CfSharp.Storage.Sqlite.dll','MirrorPulse.Adapter.Sdk.dll')
        probe=@('MirrorPulse.CfSharp.CrashProbe.dll','MirrorPulse.Core.dll','MirrorPulse.CloudFiles.CfSharp.dll','CfSharp.dll','CfSharp.Native.dll','CfSharp.Storage.Sqlite.dll','MirrorPulse.Adapter.Sdk.dll')
    }
    foreach($owner in $requiredInputs.Keys){foreach($leaf in $requiredInputs[$owner]){
        $selected=@($inputs|Where-Object {$_.owner -ceq $owner -and $_.file -ceq $leaf})
        if($selected.Count -ne 1){throw 'A required actual published input is missing.'}
        if(($leaf.StartsWith('MirrorPulse.',[StringComparison]::Ordinal) -or $leaf -ceq 'mp.dll') -and $leaf -cne 'MirrorPulse.Adapter.Sdk.dll' -and
            -not $selected[0].informationalVersion.EndsWith(('+'+$sourceSha),[StringComparison]::Ordinal)){throw 'A required product assembly differs from the selected source.'}
    }}
    Write-DisabledOriginal 'published-inputs' ([ordered]@{sourceSha=$sourceSha;inputs=$inputs.ToArray();actualManagedModuleLoadClaimed=$false})
    $env:MIRRORPULSE_DATA_ROOT=$data;$env:MIRRORPULSE_SYNC_ROOT=$sync;$env:MIRRORPULSE_HOST_PATH=Join-Path $fixture 'host/MirrorPulse.Host.exe';$env:MIRRORPULSE_DEVELOPER_MODE='1'
    $phase='SignedTopology';Start-DisabledHost 'initial'
    $installed=Invoke-DisabledCli 'install-signed-local' @('--no-start','adapter','install','--package',(Resolve-Path $PackagePath).Path)
    $offline=Invoke-DisabledCli 'create-offline-instance' @('--no-start','instance','create','--install-id',$installed.installedAdapterId,'--name','Offline Operations','--root','local=Offline Operations','--config',('root.local.sourceDirectory='+[IO.Path]::Combine($fixture,'source','offline')))
    $active=Invoke-DisabledCli 'create-active-instance' @('--no-start','instance','create','--install-id',$installed.installedAdapterId,'--name','Active Progress','--root','local=Active Progress','--config',('root.local.sourceDirectory='+[IO.Path]::Combine($fixture,'source','active')))
    $topology=Invoke-DisabledCli 'initial-topology' @('--no-start','adapter','list')
    $installation=@($topology.installations|Where-Object installId -ceq $installed.installedAdapterId)
    if($installation.Count -ne 1 -or -not $installation[0].isSigned -or $installation[0].adapterId -cne 'com.mirrorpulse.adapter.local' -or @($topology.instances).Count -ne 2 -or @($topology.roots).Count -ne 2){throw 'The fixture requires two actual signed Local single-root instances.'}
    $routes=@($topology.roots|ForEach-Object{[ordered]@{instanceId=$_.instanceId;rootId=$_.rootId;rootKey=$_.uniquenessKey;directoryName=$_.directoryName;fixtureSide=$(if($_.instanceId -ceq $offline.createdInstanceId){'offline'}elseif($_.instanceId -ceq $active.createdInstanceId){'active'}else{throw 'A foreign route entered the fixture.'})}})
    $commands=[ordered]@{}
    if(!$LargeBacklog){foreach($key in @('createBornFile','createBornDirectory','createEphemeralFile','createEphemeralDirectory','moveKeptFile','moveBornFile','moveBornDirectory','replaceKeptFile','retireFile','retireDirectory','retireEphemeralFile','retireEphemeralDirectory')){$commands[$key]=[guid]::NewGuid().ToString('D')}}
    $marker=[ordered]@{schemaVersion=1;sourceSha=$sourceSha;phase='Setup';routes=$routes;commands=$commands;members=[ordered]@{};originals=@();
        movedSourceConflict=$MovedSourceConflict.IsPresent;editedSourceConflict=$EditedSourceConflict.IsPresent;largeBacklog=$LargeBacklog.IsPresent};Save-DisabledMarker
    Stop-DisabledHost 'configured';Start-DisabledHost 'configured'
    $offlineRoute=@($routes|Where-Object fixtureSide -ceq offline)[0];$activeRoute=@($routes|Where-Object fixtureSide -ceq active)[0]
    $offlineDirectory=Join-Path $sync $offlineRoute.directoryName;$activeDirectory=Join-Path $sync $activeRoute.directoryName
    Invoke-DisabledCli 'initial-refresh' @('--no-start','sync','refresh')|Out-Null
    Get-ChildItem -LiteralPath $offlineDirectory|Out-Null;Get-ChildItem -LiteralPath $activeDirectory|Out-Null
    if(!$LargeBacklog){Wait-DisabledHash (Join-Path $offlineDirectory 'kept.txt') (Get-DisabledHash (Join-Path $fixture 'source/offline/kept.txt')) 'initial-offline-file'}
    Wait-DisabledHash (Join-Path $activeDirectory 'progress.txt') (Get-DisabledHash (Join-Path $fixture 'source/active/progress.txt')) 'initial-active-file'
    Read-DisabledWorkers 'initial' 2
    if($LargeBacklog){
        $report=Invoke-DisabledLargeBacklog
        Write-DisabledOriginal 'completed-execution' $report
        return
    }
    if($hasSourceConflict){
        $report=Invoke-DisabledSourceConflict
        Write-DisabledOriginal 'completed-execution' $report
        return
    }
    foreach($entry in @(@('keptFile','kept.txt',$false),@('retiredFile','retire.bin',$false),@('retiredDirectory','retire-dir',$true),@('metadataFile','metadata.txt',$false))){Record-DisabledMember $entry[0] ($offlineRoute.directoryName+'/'+$entry[1]) $entry[2]|Out-Null}
    Record-DisabledMember 'activeFile' ($activeRoute.directoryName+'/progress.txt') $false|Out-Null
    Invoke-DisabledCli 'disable-offline' @('--no-start','instance','disable','--instance-id',$offlineRoute.instanceId)|Out-Null
    # Instance composition changes take effect on the supported Host restart boundary.
    Stop-DisabledHost 'offline-disabled';Start-DisabledHost 'offline-disabled'
    $state=Read-DisabledState 'offline-disabled';Assert-DisabledInstances $state $false $true
    Read-DisabledWorkers 'offline-disabled' 1
    $baseline=Get-DisabledSourceSnapshot (Join-Path $fixture 'source/offline');Write-DisabledOriginal 'offline-source-baseline' $baseline
    $phase='OfflineLocalOperations'
    $parent=Invoke-DisabledCli 'offline-parent' @('--no-start','namespace','member','inspect',$offlineRoute.directoryName,'--directory')
    Invoke-DisabledCli 'prepare-replacement' @('--no-start','namespace','member','prepare-replace',$marker.members.keptFile.memberId,$replacementInput,'--operation-id',$commands.replaceKeptFile)|Out-Null
    [IO.File]::Delete($replacementInput)
    Invoke-DisabledCli 'execute-replacement' @('--no-start','namespace','member','replace',$commands.replaceKeptFile)|Out-Null
    Write-DisabledBytes (Join-Path $offlineDirectory 'kept.txt') $latestBytes
    foreach($entry in @(@('bornFile','born.txt','createBornFile',$false),@('bornDirectory','born-dir','createBornDirectory',$true),@('ephemeralFile','ephemeral.bin','createEphemeralFile',$false),@('ephemeralDirectory','ephemeral-dir','createEphemeralDirectory',$true))){
        $arguments=@('--no-start','namespace','member','create',$parent.memberId,$entry[1],'--operation-id',$commands[$entry[2]]);if($entry[3]){$arguments+='--directory'}
        Invoke-DisabledCli ('create-'+$entry[0].ToLowerInvariant()) $arguments|Out-Null
        Record-DisabledMember $entry[0] ($offlineRoute.directoryName+'/'+$entry[1]) $entry[3]|Out-Null
    }
    Write-DisabledBytes (Join-Path $offlineDirectory 'born.txt') $bornBytes
    foreach($entry in @(@('keptFile','kept-renamed.txt','moveKeptFile'),@('bornFile','born-renamed.txt','moveBornFile'),@('bornDirectory','moved-dir','moveBornDirectory'))){
        Invoke-DisabledCli ('prepare-'+$entry[2].ToLowerInvariant()) @('--no-start','namespace','member','prepare-move',$marker.members[$entry[0]].memberId,$parent.memberId,$entry[1],'--operation-id',$commands[$entry[2]])|Out-Null
        Invoke-DisabledCli ('execute-'+$entry[2].ToLowerInvariant()) @('--no-start','namespace','member','move',$commands[$entry[2]])|Out-Null
    }
    foreach($entry in @(@('retiredFile','retireFile'),@('retiredDirectory','retireDirectory'),@('ephemeralFile','retireEphemeralFile'),@('ephemeralDirectory','retireEphemeralDirectory'))){
        Invoke-DisabledCli ('prepare-'+$entry[1].ToLowerInvariant()) @('--no-start','namespace','member','prepare-retire',$marker.members[$entry[0]].memberId,'--operation-id',$commands[$entry[1]])|Out-Null
        Invoke-DisabledCli ('execute-'+$entry[1].ToLowerInvariant()) @('--no-start','namespace','member','retire',$commands[$entry[1]])|Out-Null
    }
    [IO.File]::SetLastWriteTimeUtc((Join-Path $offlineDirectory 'metadata.txt'),[DateTime]::new(2020,1,2,3,4,6,[DateTimeKind]::Utc))
    Invoke-DisabledCli 'disable-active-for-original-capture' @('--no-start','instance','disable','--instance-id',$activeRoute.instanceId)|Out-Null
    Stop-DisabledHost 'both-disabled';Start-DisabledHost 'both-disabled'
    Write-DisabledBytes (Join-Path $activeDirectory 'progress.txt') $activeBytes
    Start-Sleep -Seconds 30
    $state=Read-DisabledState 'both-disabled';Assert-DisabledInstances $state $false $false
    Read-DisabledWorkers 'both-disabled' 0
    $afterOffline=Get-DisabledSourceSnapshot (Join-Path $fixture 'source/offline')
    Write-DisabledOriginal 'after-offline-local-source' $afterOffline
    if(($baseline|ConvertTo-Json -Depth 8 -Compress) -cne ($afterOffline|ConvertTo-Json -Depth 8 -Compress)){throw 'Disabled local operations changed the backend tree or metadata.'}
    Stop-DisabledHost 'before-originals';$marker.phase='Before';Save-DisabledMarker
    $before=Invoke-DisabledProbe audit 'before-originals'
    foreach($member in $marker.members.Values){if(@($before.journal|Where-Object itemId -ceq $member.itemId).Count -lt 1){throw 'A declared changed member has no actual original SDK notification.'}}
    if($state.status.pendingUploads -lt @($before.journal).Count){throw 'The public backlog omitted its actual retained original operations.'}
    $marker.originals=@($before.journal|ForEach-Object operationId);Save-DisabledMarker
    $phase='DisabledRoleRestart';Start-DisabledHost 'disabled-restart'
    Invoke-DisabledCli 'replay-replacement-prepare' @('--no-start','namespace','member','prepare-replace',$marker.members.keptFile.memberId,$replacementInput,'--operation-id',$commands.replaceKeptFile)|Out-Null
    Invoke-DisabledCli 'replay-replacement-execute' @('--no-start','namespace','member','replace',$commands.replaceKeptFile)|Out-Null
    if((Get-DisabledHash (Join-Path $offlineDirectory 'kept-renamed.txt')) -cne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($latestBytes))){throw 'Completed replacement replay overwrote a later edit or moved placement.'}
    $state=Read-DisabledState 'restarted-disabled';Assert-DisabledInstances $state $false $false
    Read-DisabledWorkers 'restarted-disabled' 0;Assert-DisabledSourceUnchanged 'restarted-disabled'
    Stop-DisabledHost 'disabled-restart';$marker.phase='DisabledRestart';Save-DisabledMarker
    $restarted=Invoke-DisabledProbe audit 'after-disabled-restart';Assert-DisabledOriginals $before $restarted
    Assert-DisabledLocalReceipts $before $restarted
    $phase='IndependentActiveProgress';Start-DisabledHost 'active-progress'
    Invoke-DisabledCli 'reenable-active-only' @('--no-start','instance','enable','--instance-id',$activeRoute.instanceId)|Out-Null
    Stop-DisabledHost 'active-enabled';Start-DisabledHost 'active-enabled'
    Wait-DisabledHash (Join-Path $fixture 'source/active/progress.txt') ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($activeBytes))) 'independent-active-source'
    Start-Sleep -Seconds 30
    $state=Read-DisabledState 'active-progress';Assert-DisabledInstances $state $false $true
    Read-DisabledWorkers 'active-progress' 1;Assert-DisabledSourceUnchanged 'active-progress'
    Stop-DisabledHost 'active-progress';$marker.phase='ActiveProgress';Save-DisabledMarker
    $progress=Invoke-DisabledProbe audit 'after-active-progress'
    $activeAcknowledgements=@(Read-CliDisabledAcknowledgementEvents -DataRoot $data)
    Write-DisabledOriginal 'active-acknowledgement-events' $activeAcknowledgements
    Assert-DisabledLocalReceipts $before $progress
    $offlineIds=@($before.journal|Where-Object itemId -cne $marker.members.activeFile.itemId)
    Assert-DisabledOriginals ([pscustomobject]@{journal=$offlineIds}) $progress
    foreach($row in @($before.journal|Where-Object itemId -ceq $marker.members.activeFile.itemId)){
        Assert-DisabledSettled (@($progress.originals|Where-Object operationId -ceq $row.operationId)[0]) $row.kind $row $before $progress $activeAcknowledgements
    }
    $phase='OfflineReenable';Start-DisabledHost 'reenable-offline'
    Invoke-DisabledCli 'reenable-offline' @('--no-start','instance','enable','--instance-id',$offlineRoute.instanceId)|Out-Null
    Stop-DisabledHost 'both-enabled';Start-DisabledHost 'both-enabled'
    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(30);$expectedFiles=@{'kept-renamed.txt'=$latestBytes;'born-renamed.txt'=$bornBytes}
    do{
        Invoke-DisabledCli 'reenable-refresh' @('--no-start','sync','refresh')|Out-Null
        $complete=Test-Path -LiteralPath (Join-Path $fixture 'source/offline/moved-dir') -PathType Container
        foreach($name in $expectedFiles.Keys){$path=Join-Path $fixture ('source/offline/'+$name);$complete=$complete -and (Test-Path -LiteralPath $path -PathType Leaf) -and ((Get-DisabledHash $path) -ceq [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($expectedFiles[$name])))}
        foreach($name in @('kept.txt','born.txt','born-dir','retire.bin','retire-dir','ephemeral.bin','ephemeral-dir')){if(Test-Path -LiteralPath (Join-Path $fixture ('source/offline/'+$name))){$complete=$false}}
        if($complete){break};Start-Sleep -Milliseconds 100
    }while([DateTimeOffset]::UtcNow -lt $deadline)
    if(-not $complete){throw 'The real reenabled backlog did not produce every intended file/directory outcome within30seconds.'}
    Start-Sleep -Seconds 30
    $state=Read-DisabledState 'both-reenabled';Assert-DisabledInstances $state $true $true
    if($state.status.pendingUploads -ne 0){throw 'The actual public backlog did not drain after reenable.'}
    Read-DisabledWorkers 'both-reenabled' 2
    $finalInspections=[Collections.Generic.List[object]]::new()
    foreach($entry in @(@('keptFile','kept-renamed.txt',$false),@('bornFile','born-renamed.txt',$false),@('bornDirectory','moved-dir',$true))){
        $arguments=@('--no-start','namespace','member','inspect',($offlineRoute.directoryName+'/'+$entry[1]));if($entry[2]){$arguments+='--directory'}
        $current=Invoke-DisabledCli ('final-inspect-'+$entry[0].ToLowerInvariant()) $arguments
        if($current.memberId -cne $marker.members[$entry[0]].memberId -or $current.relativePath -cne ($offlineRoute.directoryName+'/'+$entry[1])){throw 'A completed moved member lost its original native binding/member or current committed address.'}
        $finalInspections.Add([pscustomobject]@{key=$entry[0];inspection=$current})
    }
    Stop-DisabledHost 'final';$marker.phase='Reenabled';Save-DisabledMarker
    $final=Invoke-DisabledProbe audit 'after-reenable'
    $finalAcknowledgements=@(Read-CliDisabledAcknowledgementEvents -DataRoot $data)
    Write-DisabledOriginal 'reenabled-acknowledgement-events' $finalAcknowledgements
    Assert-DisabledLocalReceipts $before $final
    foreach($inspection in $finalInspections){Assert-CliDisabledCurrentIdentity -Before $before -After $final -Inspection $inspection.inspection -Key $inspection.key}
    foreach($row in $before.journal){Assert-DisabledSettled (@($final.originals|Where-Object operationId -ceq $row.operationId)[0]) $row.kind $row $before $final $finalAcknowledgements}
    if(@($final.journal).Count -or @($final.unsettled).Count -or @($final.blocked|Where-Object reason -cne 'UnsupportedMetadataChange').Count){throw 'The completed operation chain retained unresolved original work or an unexpected block.'}
    if(@($final.roots|Where-Object {$_.dispatchFenced -or @($_.pending).Count}).Count){throw 'The completed operation chain retained an unresolved namespace owner.'}
    $sourceFinal=Get-DisabledSourceSnapshot (Join-Path $fixture 'source/offline');Write-DisabledOriginal 'offline-source-final' $sourceFinal
    $metadataBefore=@($baseline|Where-Object relativePath -ceq 'metadata.txt')[0];$metadataAfter=@($sourceFinal|Where-Object relativePath -ceq 'metadata.txt')[0]
    if(($metadataBefore|ConvertTo-Json -Compress) -cne ($metadataAfter|ConvertTo-Json -Compress) -or @($final.blocked|Where-Object reason -ceq 'UnsupportedMetadataChange').Count -lt 1){throw 'Unsupported metadata must retain its real product result without claiming remote acceptance.'}
    if($before.replacement.backupSha256 -cne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($keptBytes)) -or $final.replacement.backupSha256 -cne $before.replacement.backupSha256){throw 'The complete original replacement backup was lost or changed.'}
    Confirm-DisabledPublishedInputs
    $report=[ordered]@{schemaVersion=1;sourceSha=$sourceSha;runId=$env:GITHUB_RUN_ID;runtime=$Runtime;signedPackageSha256=$installation[0].packageSha256;
        originalCount=@($before.journal).Count;actualHostLifetimes=$lifetimes.ToArray();originalsPreservedAcrossDisabledRestart=$true;independentActiveSourceProgress=$true;
        sourceUnchangedWhileDisabled=$true;allOriginalOutcomesVerified=$true;allPublishedInputBytesUnchanged=$true;metadataRemoteAcceptanceClaimed=$false;actualManagedModuleLoadClaimed=$false;fullS06Accepted=$false}
    Write-DisabledOriginal 'completed-execution' $report
}catch{
    $primaryFailure=$_
    try{
        foreach($relative in @('logs/remote-poll/mirrorpulse.log','logs/cloud-files/mirrorpulse.log','logs/mirrorpulse.log')){
            $log=Join-Path $data $relative
            if((Test-Path -LiteralPath $log -PathType Leaf) -and (Get-Item -LiteralPath $log).Length -le 4MB){
                Get-Content -LiteralPath $log -Tail 96|Set-Content -LiteralPath (Join-Path $originals ('failure-'+$relative.Replace('/','-'))) -Encoding utf8
            }
        }
    }catch{Write-DisabledOriginal 'failure-log-observation-unavailable' ([ordered]@{type=$_.Exception.GetType().Name;hresult=$_.Exception.HResult.ToString('X8')})}
    Write-DisabledOriginal 'execution-failure' ([ordered]@{sourceSha=$sourceSha;phase=$phase;type=$_.Exception.GetType().Name;hresult=$_.Exception.HResult.ToString('X8');message=$_.Exception.Message.Replace($fixture,'<fixture>')})
    throw
}finally{
    try{
        Invoke-CliFixtureTeardown -PrimaryFailure $primaryFailure -RecordFailure {param($failure) Write-DisabledOriginal 'cleanup-failure' ([ordered]@{phase=$phase;type=$failure.Exception.GetType().Name;hresult=$failure.Exception.HResult.ToString('X8')})} -Cleanup {
            if($null -ne $cliBinary -and (Test-Path -LiteralPath $cliBinary)){
                $running=(& $cliBinary --json --developer-mode --no-start host status 2>$null|Out-String).Trim()
                if($running){Write-DisabledOriginal 'cleanup-lifecycle' ($running|ConvertFrom-Json)}
                if($LASTEXITCODE -eq 0){Stop-DisabledHost 'cleanup'}
            }
            if($null -ne $marker -and (Test-Path -LiteralPath $fixture)){
                if($null -ne $primaryFailure){try{Invoke-DisabledProbe audit 'stopped-failure-observation'|Out-Null}catch{}}
                $resolved=[IO.Path]::GetFullPath($fixture)
                if([IO.Path]::GetDirectoryName($resolved) -ine [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -or
                    [IO.Path]::GetFileName($resolved) -cnotmatch '^MirrorPulse-cli-disabled-[0-9a-f]{32}$' -or (Get-Item -LiteralPath $resolved -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){throw 'The cleanup target escaped its exact owned fixture.'}
                $cleanup=Invoke-DisabledProbe cleanup 'authenticated-cleanup'
                if(-not $cleanup.exclusiveCatalogOwner -or -not $cleanup.cloudRootUnregistered -or $cleanup.validatedObjects -lt 3 -or $cleanup.validatedObjects -ne $cleanup.restoredObjects){throw 'The authenticated complete fixture cleanup did not finish.'}
                if($LargeBacklog -and $null -ne $report -and $cleanup.validatedObjects -ne ($backlogKeys.Count+4)){throw 'The completed backlog inventory did not validate both roots, the anchor and every original file.'}
                Remove-Item -LiteralPath $resolved -Recurse -Force
                if(Test-Path -LiteralPath $resolved){throw 'The authenticated fixture remained after cleanup.'}
                if($null -ne $report){$report.cleanup=$cleanup;$report.fixtureRemoved=$true}
            }
        }
    }finally{
        Write-DisabledOriginal 'host-lifetimes' ([ordered]@{sourceSha=$sourceSha;lifetimes=$lifetimes.ToArray();actualManagedModuleLoadClaimed=$false})
        foreach($name in $savedEnvironment.Keys){[Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name],'Process')}
    }
    if($null -ne $report){
        if(!$report.fixtureRemoved){throw 'The signed fixture did not retain authenticated teardown.'}
        $report|ConvertTo-Json -Depth 30|Set-Content -LiteralPath $evidenceFile -Encoding utf8
        if($LargeBacklog){'Verified the signed large offline backlog, later active progress, original receipts and authenticated teardown.'}
        elseif($MovedSourceConflict){'Verified the signed Local source move, original reenable conflict, retained bytes and authenticated teardown.'}
        elseif($EditedSourceConflict){'Verified the signed Local in-place source edit, original reenable conflict, retained bytes and authenticated teardown.'}
        else{'Verified the actual signed default-Host offline-operation chain, independent active progress, original outcomes and authenticated teardown.'}
    }
}
