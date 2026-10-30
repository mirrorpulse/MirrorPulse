function Assert-CliCoalescingEditSnapshot {
    param([Parameter(Mandatory)][object]$Observation)
    $target=[guid]::Empty
    if($Observation.schemaVersion -ne 1 -or $Observation.exclusiveCatalogOwner -ne $true -or
        $Observation.journalInventoryComplete -ne $true -or $Observation.targetItemIsTombstone -ne $false -or
        $Observation.originalSdkAcknowledgementExecuted -ne $false -or $Observation.mutationExecuted -ne $false -or
        -not [guid]::TryParse([string]$Observation.targetItemId,[ref]$target) -or $target -eq [guid]::Empty -or
        ($Observation.throughSequence -isnot [long] -and $Observation.throughSequence -isnot [int]) -or
        ($Observation.lastScannedSequence -isnot [long] -and $Observation.lastScannedSequence -isnot [int]) -or
        $Observation.throughSequence -lt 0 -or $Observation.lastScannedSequence -ne $Observation.throughSequence -or
        @($Observation.journal).Count -gt 2048){throw 'The edit witness requires a complete original journal from its exclusive stopped owner.'}
    $ids=[Collections.Generic.HashSet[guid]]::new();$previous=0L
    foreach($row in @($Observation.journal)){
        $id=[guid]::Empty;$item=[guid]::Empty;$created=[DateTimeOffset]::MinValue
        if(-not [guid]::TryParse([string]$row.operationId,[ref]$id) -or $id -eq [guid]::Empty -or -not $ids.Add($id) -or
            ($row.sequence -isnot [long] -and $row.sequence -isnot [int]) -or
            ($row.attemptCount -isnot [long] -and $row.attemptCount -isnot [int]) -or
            $row.sequence -le $previous -or $row.sequence -gt $Observation.throughSequence -or $row.attemptCount -lt 0 -or
            $row.payloadSha256 -cnotmatch '^[A-F0-9]{64}$' -or -not [DateTimeOffset]::TryParse([string]$row.createdAt,[ref]$created)){
            throw 'The edit witness lost an original operation ID, sequence, timestamp or payload fingerprint.'
        }
        $matches=[guid]::TryParse([string]$row.itemId,[ref]$item) -and $item -eq $target
        if($row.associatedWithTarget -isnot [bool] -or $row.associatedWithTarget -ne $matches){throw 'The edit witness changed its exact target association.'}
        $previous=[long]$row.sequence
    }
}

function Get-CliCoalescingEditWitness {
    param(
        [Parameter(Mandatory)][object]$Before,
        [Parameter(Mandatory)][object]$After,
        [Parameter(Mandatory)][DateTimeOffset]$WrittenAt,
        [Parameter(Mandatory)][string]$LocalPath,
        [Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$ExpectedContent,
        [guid[]]$RequiredOriginalIds=@()
    )
    Assert-CliCoalescingEditSnapshot $Before
    Assert-CliCoalescingEditSnapshot $After
    if($Before.targetItemId -cne $After.targetItemId -or $After.throughSequence -lt $Before.throughSequence){
        throw 'The edit witness cannot adopt another target or roll back its original journal sequence.'
    }
    $beforeRows=@{};foreach($row in @($Before.journal)){$beforeRows[[string]$row.operationId]=$row}
    $afterRows=@{};foreach($row in @($After.journal)){$afterRows[[string]$row.operationId]=$row}
    foreach($key in $beforeRows.Keys){
        $original=$beforeRows[$key];$retained=$afterRows[$key]
        if($original.associatedWithTarget -eq $true -and $null -ne $retained -and
            ($retained.associatedWithTarget -ne $true -or $retained.sequence -ne $original.sequence -or
            $retained.itemId -cne $original.itemId -or $retained.kind -cne $original.kind -or
            $retained.payloadSha256 -cne $original.payloadSha256 -or $retained.createdAt -cne $original.createdAt)){
            throw 'An existing target original changed its retained sequence, identity or payload.'
        }
    }
    foreach($id in $RequiredOriginalIds){
        $key=$id.ToString('D');$original=$beforeRows[$key];$retained=$afterRows[$key]
        if($null -eq $original -or $null -eq $retained -or $original.associatedWithTarget -ne $true -or
            $retained.associatedWithTarget -ne $true -or $retained.sequence -ne $original.sequence -or
            $retained.itemId -cne $original.itemId -or $retained.kind -cne $original.kind -or
            $retained.payloadSha256 -cne $original.payloadSha256 -or $retained.createdAt -cne $original.createdAt){
            throw 'A preceding actual edit lost or changed an original journal operation before acknowledgement.'
        }
    }
    $added=@($After.journal|Where-Object {
        $_.associatedWithTarget -eq $true -and $_.kind -ceq 'ContentUpdate' -and $_.attemptCount -eq 0 -and
        $_.sequence -gt $Before.throughSequence -and -not $beforeRows.ContainsKey([string]$_.operationId) -and
        [DateTimeOffset]::Parse([string]$_.createdAt) -ge $WrittenAt
    })
    if($added.Count -eq 0){throw 'The actual edit has no fresh target content-update original after its retained baseline.'}
    $expected=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($ExpectedContent))
    if(-not (Test-Path -LiteralPath $LocalPath -PathType Leaf) -or (Get-Item -LiteralPath $LocalPath).Length -ne $ExpectedContent.Length -or
        (Get-FileHash -LiteralPath $LocalPath).Hash -cne $expected){throw 'The edit witness lost the exact complete bytes of its actual local edit.'}
    [ordered]@{schemaVersion=1;targetItemId=$After.targetItemId;baselineThroughSequence=$Before.throughSequence;
        observedThroughSequence=$After.throughSequence;writtenAt=$WrittenAt.ToString('o');localLength=$ExpectedContent.Length;localSha256=$expected;
        originalOperationIds=@($added|ForEach-Object {$_.operationId});retainedPrecedingOriginalIds=@($RequiredOriginalIds|ForEach-Object {$_.ToString('D')});
        globalPendingCountUsedAsProof=$false;originalSdkAcknowledgementExecuted=$false;mutationExecuted=$false}
}
