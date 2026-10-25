# One bounded public query after a synthetic transfer failure; never retain paths,
# version strings, messages or credentials, and never resolve or repair conflicts.
function Get-CliTransferConflictObservations {
    param([Parameter(Mandatory)][string]$CliPath)
    $result=[ordered]@{observationUnavailable=$true;exitCode=$null;count=$null;truncated=$null;moreAvailable=$null;items=@()}
    try {
        $raw=(& $CliPath --json --developer-mode --no-start --timeout 5 conflict list 2>$null|Out-String).Trim()
        $result.exitCode=$LASTEXITCODE
        if ($LASTEXITCODE -ne 0 -or $raw.Length -gt 1048576 -or [string]::IsNullOrWhiteSpace($raw)) { return [pscustomobject]$result }
        $envelope=$raw|ConvertFrom-Json
        if ($envelope.kind -cne 'result' -or $null -eq $envelope.data -or $null -eq $envelope.data.PSObject.Properties['items']) { return [pscustomobject]$result }
        $items=@($envelope.data.items|Where-Object {$null -ne $_})
        $result.count=$items.Count;$result.truncated=$items.Count -gt 64
        $result.moreAvailable=-not [string]::IsNullOrEmpty([string]$envelope.data.nextCursor)
        $result.items=@($items|Select-Object -First 64|ForEach-Object {
            $id=[guid]::Empty;$instance=[guid]::Empty
            $conflictId=if([guid]::TryParse([string]$_.conflictId,[ref]$id) -and $id -ne [guid]::Empty){$id.ToString('D')}else{$null}
            $instanceId=if([guid]::TryParse([string]$_.instanceId,[ref]$instance) -and $instance -ne [guid]::Empty){$instance.ToString('D')}else{$null}
            $reason=if($_.reason -is [string] -and $_.reason -cin @('Content','Metadata','Move','Delete','PathCollision','StaleRemoteRevision','MissingItem','Unknown')){$_.reason}else{'Unclassified'}
            $source=if($_.source -is [string] -and $_.source -cin @('Upload','Protocol','CfSharpRemote')){$_.source}else{'Unclassified'}
            $status=if($_.status -is [string] -and $_.status -cin @('Pending','Resolved','Dismissed')){$_.status}else{'Unclassified'}
            $local=$_.localRevision;$remote=$_.remoteRevision;$relation='Unavailable'
            if(($null -eq $local -or ($local -is [string] -and $local.Length -le 8192)) -and
                ($null -eq $remote -or ($remote -is [string] -and $remote.Length -le 8192))) {
                if([string]::IsNullOrEmpty($local)){$relation=if([string]::IsNullOrEmpty($remote)){'BothMissing'}else{'LocalMissing'}}
                elseif([string]::IsNullOrEmpty($remote)){$relation='RemoteMissing'}
                elseif([string]::Equals($local,$remote,[StringComparison]::Ordinal)){$relation='Equal'}else{$relation='Different'}
            }
            [ordered]@{conflictId=$conflictId;instanceId=$instanceId;reason=$reason;source=$source;status=$status;revisionRelation=$relation}
        })
        $result.observationUnavailable=$false
    } catch { }
    [pscustomobject]$result
}
