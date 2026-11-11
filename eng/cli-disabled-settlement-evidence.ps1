# These facts verify an already returned public ACK. They never authorize an
# operation, native mutation, receipt, source acceptance or another ACK.
function Assert-CliDisabledEvidenceGuid([object]$Value) {
    $parsed=[Guid]::Empty
    if(-not [Guid]::TryParse([string]$Value,[ref]$parsed) -or $parsed -eq [Guid]::Empty){throw 'Require a complete original evidence identifier.'}
}

function Read-CliDisabledAcknowledgementEvents {
    param([Parameter(Mandatory)][string]$DataRoot)
    $path=Join-Path $DataRoot 'logs/mirrorpulse.log'
    if(-not(Test-Path -LiteralPath $path -PathType Leaf)){return @()}
    $info=Get-Item -LiteralPath $path
    if($info.Length -gt 4MB -or ($info.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Bound the original owned acknowledgement log.'}
    $events=@(Get-Content -LiteralPath $path|ForEach-Object {$_|ConvertFrom-Json}|Where-Object {
        $_.Category -ceq 'CloudFiles.Upload' -and $_.Code -cin @('JournalAcceptedContentObservation','JournalAcceptedContentAcknowledged')
    })
    if($events.Count -gt 256){throw 'Bound the actual acknowledgement evidence.'}
    return $events
}

function Assert-CliDisabledCurrentIdentity {
    param([Parameter(Mandatory)]$Before,[Parameter(Mandatory)]$After,[Parameter(Mandatory)]$Inspection,[Parameter(Mandatory)][string]$Key)
    $original=@($Before.members|Where-Object key -CEQ $Key)
    $retained=@($After.members|Where-Object key -CEQ $Key)
    $current=@($After.identities|Where-Object key -CEQ $Key)
    foreach($value in @($Inspection.memberId,$Inspection.rootId,$Inspection.nativeItemId)){Assert-CliDisabledEvidenceGuid $value}
    if($original.Count -ne 1 -or $retained.Count -ne 1 -or $current.Count -ne 1 -or
       $Inspection.memberId -cne $original[0].memberId -or $retained[0].memberId -cne $original[0].memberId -or
       $Inspection.rootId -cne $original[0].rootId -or $Inspection.isDirectory -ne $original[0].isDirectory -or
       $retained[0].rootId -cne $original[0].rootId -or $retained[0].originalItemId -cne $original[0].originalItemId -or
       $original[0].originalBindingSha256 -cnotmatch '^[A-F0-9]{64}$' -or $retained[0].originalBindingSha256 -cne $original[0].originalBindingSha256 -or
       $current[0].memberId -cne $original[0].memberId -or $current[0].originalItemId -cne $original[0].originalItemId -or
       -not $Inspection.nativeItemId -or $current[0].itemId -cne $Inspection.nativeItemId){throw 'The actual native member, original binding/birth identity or confirmed current public identity changed.'}
    if($current[0].itemId -cne $original[0].originalItemId -and -not $current[0].acceptanceOperationId){throw 'A changed public identity requires its actual confirmed acceptance history.'}
    if($current[0].acceptanceOperationId){Assert-CliDisabledEvidenceGuid $current[0].acceptanceOperationId}
}

function Assert-CliDisabledAcceptedObservation {
    param([Parameter(Mandatory)]$Before,[Parameter(Mandatory)]$After,[Parameter(Mandatory)]$Original,
        [Parameter(Mandatory)]$Outcome,[AllowEmptyCollection()][object[]]$Events)
    if($Original.kind -cnotin @('Create','ContentUpdate') -or $Outcome.operationId -cne $Original.operationId -or $Outcome.pending -or
       @($After.journal|Where-Object operationId -CEQ $Original.operationId).Count){throw 'Only the exact absent original content header can use actual public-ACK evidence.'}
    $member=@($Before.members|Where-Object originalItemId -CEQ $Original.itemId)
    if($member.Count -ne 1 -or $member[0].isDirectory){throw 'An accepted observation requires its original file member.'}
    $beforeIdentity=@($Before.identities|Where-Object memberId -CEQ $member[0].memberId)
    $afterIdentity=@($After.identities|Where-Object memberId -CEQ $member[0].memberId)
    $root=@($Before.roots|Where-Object rootId -CEQ $member[0].rootId)
    if($beforeIdentity.Count -ne 1 -or $afterIdentity.Count -ne 1 -or $root.Count -ne 1 -or
       $beforeIdentity[0].nativeBindingSha256 -cne $afterIdentity[0].nativeBindingSha256){throw 'The original observation lost its owning root or physical binding.'}
    $acks=@($Events|Where-Object {$_.Code -ceq 'JournalAcceptedContentAcknowledged' -and $_.Fields.operationId -ceq $Original.operationId})
    if($acks.Count -ne 1){throw 'Require the actual successful public-ACK return, not a missing row or before-event.'}
    $ack=$acks[0];$fields=$ack.Fields
    foreach($value in @($fields.correlationId,$fields.operationId,$fields.rootId,$fields.instanceId)){Assert-CliDisabledEvidenceGuid $value}
    $observations=@($Events|Where-Object {$_.Code -ceq 'JournalAcceptedContentObservation' -and $_.Fields.correlationId -ceq $fields.correlationId})
    if($observations.Count -ne 1 -or -not $fields.correlationId -or
       [DateTimeOffset]$observations[0].OccurredAt -gt [DateTimeOffset]$ack.OccurredAt){throw 'Require the exact ordered original acknowledgement pair.'}
    $observed=$observations[0].Fields
    if(@(Compare-Object @($observed.PSObject.Properties.Name) @($fields.PSObject.Properties.Name) -CaseSensitive).Count){throw 'Acknowledgement fields changed across the actual return.'}
    foreach($field in $fields.PSObject.Properties.Name){if($observed.$field -cne $fields.$field){throw 'Acknowledgement facts differ from their original observation.'}}
    foreach($digest in @('journalPayloadSha256','nativeIdentitySha256','nativeBindingSha256','acknowledgedRevisionSha256')){
        if($fields.$digest -cnotmatch '^[A-F0-9]{64}$'){throw 'Require complete actual acknowledgement digests.'}
    }
    if($fields.operationId -cne $Original.operationId -or $fields.kind -cne $Original.kind -or [long]$fields.journalSequence -ne $Original.sequence -or
       $fields.journalPayloadSha256 -cne $Original.payloadSha256 -or $fields.rootId -cne $member[0].rootId -or
       $fields.instanceId -cne $root[0].instanceId -or $fields.nativeBindingSha256 -cne $afterIdentity[0].nativeBindingSha256){throw 'The successful ACK does not identify the exact original notification, root and native object.'}
    $snapshots=@($Before.remoteSnapshots|Where-Object rootId -CEQ $member[0].rootId)
    $baseline=@($snapshots|ForEach-Object {$_.entries}|Where-Object publicItemId -CEQ $Original.itemId)
    $revisions=@($afterIdentity[0].acceptedRevisionSha256)
    if($snapshots.Count -eq 1 -and $baseline.Count -eq 1){$revisions+=@($baseline[0].remoteRevisionSha256)}
    if($fields.acknowledgedRevisionSha256 -cnotin $revisions){throw 'The ACK revision has neither an original owned baseline nor a confirmed current acceptance.'}
}
