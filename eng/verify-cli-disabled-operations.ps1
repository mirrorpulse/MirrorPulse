[CmdletBinding()]
param(
    [ValidateSet('win-x64','win-arm64')][string]$Runtime='win-arm64',
    [string]$PackagePath,
    [ValidateSet('Debug','Release')][string]$Configuration='Release',
    [string]$EvidencePath,
    [switch]$BudgetOnly
)
if($BudgetOnly){15;exit 0}
$ErrorActionPreference='Stop'
if($env:MIRRORPULSE_NATIVE_TEST -cne '1' -or [string]::IsNullOrWhiteSpace($EvidencePath) -or -not (Test-Path -LiteralPath $PackagePath -PathType Leaf)){
    throw 'The signed disabled-operation fixture requires its exact package, evidence path and disposable native environment.'
}
$repository=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourceSha=(& git -C $repository rev-parse HEAD|Out-String).Trim()
if($LASTEXITCODE -ne 0 -or $sourceSha -cnotmatch '^[0-9a-f]{40}$' -or ($env:GITHUB_SHA -and $env:GITHUB_SHA -cne $sourceSha)){
    throw 'Bind the actual selected source before running the disabled fixture.'
}
. (Join-Path $PSScriptRoot 'cli-integration-failure-policy.ps1')
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
function Write-DisabledOriginal([string]$Name,[object]$Value){
    if($Name -cnotmatch '^[a-z0-9-]{1,90}$'){throw 'Bound the original fixture artifact name.'}
    $Value|ConvertTo-Json -Depth 40|Set-Content -LiteralPath (Join-Path $originals ($Name+'.json')) -Encoding utf8
}
function Save-DisabledMarker{$marker|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $markerPath -Encoding utf8}
function Invoke-DisabledCli([string]$Name,[string[]]$Arguments){
    $raw=(& $cliBinary --json --developer-mode @Arguments 2>&1|Out-String).Trim();$exitCode=$LASTEXITCODE
    if($raw){try{$value=$raw|ConvertFrom-Json;Write-DisabledOriginal $Name $value}catch{}}
    if($exitCode -ne 0 -or -not $raw){throw ('The actual disabled fixture CLI failed at '+$Name+' (exit '+$exitCode+').')}
    $value=$raw|ConvertFrom-Json
    if($value.kind -cne 'result'){throw 'The public CLI returned an unexpected envelope.'}
    $value.data
}
function Stop-DisabledHost([string]$Name){
    if($null -eq $script:ownedDisabledHost){throw 'There is no authenticated owned Host lifetime to stop.'}
    $process=Get-Process -Id $script:ownedDisabledHost.processId
    try{
        if($process.StartTime.ToUniversalTime().ToString('o') -cne $script:ownedDisabledHost.startedUtc){throw 'The owned Host process identity was replaced.'}
        Invoke-DisabledCli ($Name+'-stop') @('--no-start','host','stop')|Out-Null
        if(-not $process.WaitForExit(20000)){throw 'The owned disabled fixture Host did not stop within its lifecycle budget.'}
        $script:ownedDisabledHost.exitObserved=$true
        & $cliBinary --json --developer-mode --no-start host status 2>$null|Out-Null
        if($LASTEXITCODE -eq 0){throw 'The stopped owned Host retained a live lifecycle channel.'}
        $script:ownedDisabledHost=$null
    }finally{$process.Dispose()}
}
function Start-DisabledHost([string]$Name){
    $status=Invoke-DisabledCli ($Name+'-start') @('host','start')
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
function Invoke-DisabledProbe([ValidateSet('audit','cleanup')][string]$Mode,[string]$Name){
    $start=[Diagnostics.ProcessStartInfo]::new('dotnet');$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in @($probeAssembly,('--'+$Mode+'-cli-disabled-fixture'),$fixture)){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start)
    try{
        $output=$process.StandardOutput.ReadToEndAsync();$errors=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(30000)){throw 'The actual stopped disabled oracle exceeded30seconds.'}
        if($process.ExitCode -ne 0){throw ('The stopped disabled oracle failed: '+$errors.GetAwaiter().GetResult())}
        $value=$output.GetAwaiter().GetResult()|ConvertFrom-Json;Write-DisabledOriginal $Name $value;$value
    }finally{if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()};$process.Dispose()}
}
function Get-DisabledHash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
function Get-DisabledSourceSnapshot([string]$Path){
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
            if($entry.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -or $items.Count -ge 64){throw 'A private source changed to an unbounded or redirected tree.'}
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
function Assert-DisabledSettled($Original,[string]$Kind){
    if($null -eq $Original){throw 'The actual original operation outcome is missing.'}
    if($Original.pending){throw 'The original SDK operation remains pending after its actual source/unsupported outcome.'}
    if($Original.blockReason -ceq 'UnsupportedMetadataChange'){
        if($Kind -cne 'MetadataUpdate'){throw 'An unsupported metadata result cannot settle another original operation kind.'}
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
try{
    New-Item -ItemType Directory -Path $sync,$data,(Join-Path $fixture 'source/offline/retire-dir'),(Join-Path $fixture 'source/active') -Force|Out-Null
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try{$acl=Get-Acl -LiteralPath $sync;$acl.SetOwner($identity.User);Set-Acl -LiteralPath $sync -AclObject $acl}finally{$identity.Dispose()}
    [IO.File]::WriteAllBytes((Join-Path $fixture 'source/offline/kept.txt'),$keptBytes)
    [IO.File]::WriteAllBytes((Join-Path $fixture 'source/offline/retire.bin'),[byte[]]@(1,2,3))
    [IO.File]::WriteAllBytes((Join-Path $fixture 'source/offline/metadata.txt'),[byte[]]@(4,5,6))
    [IO.File]::WriteAllBytes((Join-Path $fixture 'source/active/progress.txt'),[byte[]]@(7,8,9))
    [IO.File]::WriteAllBytes($replacementInput,$replacementBytes)
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
    $commands=[ordered]@{};foreach($key in @('createBornFile','createBornDirectory','createEphemeralFile','createEphemeralDirectory','moveKeptFile','moveBornFile','moveBornDirectory','replaceKeptFile','retireFile','retireDirectory','retireEphemeralFile','retireEphemeralDirectory')){$commands[$key]=[guid]::NewGuid().ToString('D')}
    $marker=[ordered]@{schemaVersion=1;sourceSha=$sourceSha;phase='Setup';routes=$routes;commands=$commands;members=[ordered]@{};originals=@()};Save-DisabledMarker
    Stop-DisabledHost 'configured';Start-DisabledHost 'configured'
    $offlineRoute=@($routes|Where-Object fixtureSide -ceq offline)[0];$activeRoute=@($routes|Where-Object fixtureSide -ceq active)[0]
    $offlineDirectory=Join-Path $sync $offlineRoute.directoryName;$activeDirectory=Join-Path $sync $activeRoute.directoryName
    Invoke-DisabledCli 'initial-refresh' @('--no-start','sync','refresh')|Out-Null
    Get-ChildItem -LiteralPath $offlineDirectory|Out-Null;Get-ChildItem -LiteralPath $activeDirectory|Out-Null
    Wait-DisabledHash (Join-Path $offlineDirectory 'kept.txt') (Get-DisabledHash (Join-Path $fixture 'source/offline/kept.txt')) 'initial-offline-file'
    Wait-DisabledHash (Join-Path $activeDirectory 'progress.txt') (Get-DisabledHash (Join-Path $fixture 'source/active/progress.txt')) 'initial-active-file'
    Read-DisabledWorkers 'initial' 2
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
    Assert-DisabledLocalReceipts $before $progress
    $offlineIds=@($before.journal|Where-Object itemId -cne $marker.members.activeFile.itemId)
    Assert-DisabledOriginals ([pscustomobject]@{journal=$offlineIds}) $progress
    foreach($row in @($before.journal|Where-Object itemId -ceq $marker.members.activeFile.itemId)){
        Assert-DisabledSettled (@($progress.originals|Where-Object operationId -ceq $row.operationId)[0]) $row.kind
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
    foreach($entry in @(@('keptFile','kept-renamed.txt',$false),@('bornFile','born-renamed.txt',$false),@('bornDirectory','moved-dir',$true))){
        $arguments=@('--no-start','namespace','member','inspect',($offlineRoute.directoryName+'/'+$entry[1]));if($entry[2]){$arguments+='--directory'}
        $current=Invoke-DisabledCli ('final-inspect-'+$entry[0].ToLowerInvariant()) $arguments
        if($current.memberId -cne $marker.members[$entry[0]].memberId -or $current.nativeItemId -cne $marker.members[$entry[0]].itemId){throw 'A completed moved member lost its original native or product identity.'}
    }
    Stop-DisabledHost 'final';$marker.phase='Reenabled';Save-DisabledMarker
    $final=Invoke-DisabledProbe audit 'after-reenable'
    Assert-DisabledLocalReceipts $before $final
    foreach($row in $before.journal){Assert-DisabledSettled (@($final.originals|Where-Object operationId -ceq $row.operationId)[0]) $row.kind}
    if(@($final.journal).Count -or @($final.unsettled).Count -or @($final.blocked|Where-Object reason -cne 'UnsupportedMetadataChange').Count){throw 'The completed operation chain retained unresolved original work or an unexpected block.'}
    if(@($final.roots|Where-Object {$_.dispatchFenced -or @($_.pending).Count}).Count){throw 'The completed operation chain retained an unresolved namespace owner.'}
    $sourceFinal=Get-DisabledSourceSnapshot (Join-Path $fixture 'source/offline');Write-DisabledOriginal 'offline-source-final' $sourceFinal
    $metadataBefore=@($baseline|Where-Object relativePath -ceq 'metadata.txt')[0];$metadataAfter=@($sourceFinal|Where-Object relativePath -ceq 'metadata.txt')[0]
    if(($metadataBefore|ConvertTo-Json -Compress) -cne ($metadataAfter|ConvertTo-Json -Compress) -or @($final.blocked|Where-Object reason -ceq 'UnsupportedMetadataChange').Count -lt 1){throw 'Unsupported metadata must retain its real product result without claiming remote acceptance.'}
    if($before.replacement.backupSha256 -cne [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($keptBytes)) -or $final.replacement.backupSha256 -cne $before.replacement.backupSha256){throw 'The complete original replacement backup was lost or changed.'}
    foreach($publishedInput in $inputs){
        if((Get-DisabledHash (Join-Path $fixture ($publishedInput.owner+'/'+$publishedInput.file))) -cne $publishedInput.sha256){throw 'Actual published input bytes changed during the fixture.'}
    }
    Write-DisabledOriginal 'published-inputs-after' ([ordered]@{sourceSha=$sourceSha;inputs=$inputs.ToArray();actualBytesUnchanged=$true;actualManagedModuleLoadClaimed=$false})
    $report=[ordered]@{schemaVersion=1;sourceSha=$sourceSha;runId=$env:GITHUB_RUN_ID;runtime=$Runtime;signedPackageSha256=$installation[0].packageSha256;
        originalCount=@($before.journal).Count;actualHostLifetimes=$lifetimes.ToArray();originalsPreservedAcrossDisabledRestart=$true;independentActiveSourceProgress=$true;
        sourceUnchangedWhileDisabled=$true;allOriginalOutcomesVerified=$true;allPublishedInputBytesUnchanged=$true;metadataRemoteAcceptanceClaimed=$false;actualManagedModuleLoadClaimed=$false;fullS06Accepted=$false}
    Write-DisabledOriginal 'completed-execution' $report
}catch{
    $primaryFailure=$_
    Write-DisabledOriginal 'execution-failure' ([ordered]@{sourceSha=$sourceSha;phase=$phase;type=$_.Exception.GetType().Name;hresult=$_.Exception.HResult.ToString('X8');message=$_.Exception.Message.Replace($fixture,'<fixture>')})
    throw
}finally{
    try{
        Invoke-CliFixtureTeardown -PrimaryFailure $primaryFailure -RecordFailure {param($failure) Write-DisabledOriginal 'cleanup-failure' ([ordered]@{phase=$phase;type=$failure.Exception.GetType().Name;hresult=$failure.Exception.HResult.ToString('X8')})} -Cleanup {
            if($null -ne $cliBinary -and (Test-Path -LiteralPath $cliBinary)){
                $running=(& $cliBinary --json --developer-mode --no-start host status 2>$null|Out-String).Trim()
                if($LASTEXITCODE -eq 0){Stop-DisabledHost 'cleanup'}
            }
            if($null -ne $marker -and (Test-Path -LiteralPath $fixture)){
                if($null -ne $primaryFailure){try{Invoke-DisabledProbe audit 'stopped-failure-observation'|Out-Null}catch{}}
                $resolved=[IO.Path]::GetFullPath($fixture)
                if([IO.Path]::GetDirectoryName($resolved) -ine [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -or
                    [IO.Path]::GetFileName($resolved) -cnotmatch '^MirrorPulse-cli-disabled-[0-9a-f]{32}$' -or (Get-Item -LiteralPath $resolved -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)){throw 'The cleanup target escaped its exact owned fixture.'}
                $cleanup=Invoke-DisabledProbe cleanup 'authenticated-cleanup'
                if(-not $cleanup.exclusiveCatalogOwner -or -not $cleanup.cloudRootUnregistered -or $cleanup.validatedObjects -lt 3 -or $cleanup.validatedObjects -ne $cleanup.restoredObjects){throw 'The authenticated complete fixture cleanup did not finish.'}
                Remove-Item -LiteralPath $resolved -Recurse -Force
                if(Test-Path -LiteralPath $resolved){throw 'The authenticated fixture remained after cleanup.'}
                if($null -ne $report){$report.cleanup=$cleanup;$report.fixtureRemoved=$true}
            }
        }
    }finally{foreach($name in $savedEnvironment.Keys){[Environment]::SetEnvironmentVariable($name,$savedEnvironment[$name],'Process')}}
}
$report|ConvertTo-Json -Depth 30|Set-Content -LiteralPath $evidenceFile -Encoding utf8
'Verified the actual signed default-Host offline-operation chain, independent active progress, original outcomes and authenticated teardown.'
