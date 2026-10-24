# Failure-only observations from the owned synthetic Host. This helper does not
# start, refresh, resume or repair synchronization and does not read databases.
function Get-CliTransferFailureObservations {
    param(
        [Parameter(Mandatory)][string]$CliPath,
        [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$SourceSha,
        [AllowEmptyString()][string]$RunId,
        [Parameter(Mandatory)][string]$Phase,
        [string[]]$RootIds = @()
    )
    $result = [ordered]@{schemaVersion=1;sourceSha=$SourceSha;runId=$RunId;phase=$Phase
        observedAt=[DateTimeOffset]::UtcNow.ToString('O');failureOnly=$true;commandsAreReadOnly=$true
        sync=[ordered]@{observationUnavailable=$true};roots=[ordered]@{observationUnavailable=$true}}
    function Convert-ObservationGuid {
        param($Value)
        $parsed = [guid]::Empty
        if ($null -ne $Value -and $Value -isnot [string] -and $null -ne $Value.PSObject.Properties['value']) { $Value = $Value.value }
        if ([guid]::TryParse([string]$Value, [ref]$parsed) -and $parsed -ne [guid]::Empty) { return $parsed.ToString('D') }
        return $null
    }
    function Read-ObservationCount {
        param($Value)
        if (($Value -is [int] -or $Value -is [long]) -and $Value -ge 0) { return [long]$Value }
        return $null
    }
    try {
        $raw = (& $CliPath --json --developer-mode --no-start --timeout 5 sync status 2>$null | Out-String).Trim()
        $result.sync.exitCode = $LASTEXITCODE
        if ($LASTEXITCODE -eq 0 -and $raw.Length -le 1048576 -and -not [string]::IsNullOrWhiteSpace($raw)) {
            $envelope = $raw | ConvertFrom-Json
            if ($envelope.kind -ceq 'result' -and $null -ne $envelope.data) {
                $status = $envelope.data
                $result.sync.errorPresent = -not [string]::IsNullOrWhiteSpace([string]$status.error)
                $result.sync.observationUnavailable = $result.sync.errorPresent
                $result.sync.pendingUploads = Read-ObservationCount $status.pendingUploads
                $result.sync.pendingRemoteConflicts = Read-ObservationCount $status.pendingRemoteConflicts
                $result.sync.pendingUploadConflicts = Read-ObservationCount $status.pendingUploadConflicts
                $instances = @($status.instances | Where-Object { $null -ne $_ })
                $result.sync.instancesUnavailable = $null -eq $status.instances
                $result.sync.instanceCount = $(if ($result.sync.instancesUnavailable) { $null } else { $instances.Count })
                $result.sync.instancesTruncated = $instances.Count -gt 32
                $result.sync.instances = @($instances | Select-Object -First 32 | ForEach-Object {
                    [ordered]@{instanceId=(Convert-ObservationGuid $_.instanceId)
                        enabled=$(if ($_.enabled -is [bool]) { $_.enabled } else { $null })
                        workerConnected=$(if ($_.workerConnected -is [bool]) { $_.workerConnected } else { $null })}
                })
                $knownReasons = @('RootReconciliationRequired','UnregisteredPath','CrossRootMove','InvalidMove',
                    'UnsupportedDirectoryCreate','UnsupportedMetadataChange','UnsupportedChangeKind','RequestIdentityMismatch',
                    'IncompleteLocalContent','UnsupportedRescanDirectoryDeletion','MissingUploadBinding')
                $blocked = @($status.blockedLocalOperations | Where-Object { $null -ne $_ })
                $result.sync.blockedUnavailable = $null -eq $status.blockedLocalOperations
                $result.sync.blockedCount = $(if ($result.sync.blockedUnavailable) { $null } else { $blocked.Count })
                $result.sync.blockedTruncated = $blocked.Count -gt 64
                $result.sync.blocked = @($blocked | Select-Object -First 64 | ForEach-Object {
                    $reason = 'Unclassified'
                    if ($_.reason -is [string] -and $_.reason -cin $knownReasons) { $reason = $_.reason }
                    elseif (($_.reason -is [int] -or $_.reason -is [long]) -and $_.reason -ge 0 -and $_.reason -lt $knownReasons.Count) { $reason = $knownReasons[[int]$_.reason] }
                    [ordered]@{operationId=(Convert-ObservationGuid $_.operationId)
                        instanceId=(Convert-ObservationGuid $_.instanceId);reason=$reason}
                })
            }
        }
    } catch { $result.sync.failureHResult = $_.Exception.HResult.ToString('X8') }
    try {
        $requested = @($RootIds | ForEach-Object { Convert-ObservationGuid $_ } | Where-Object { $null -ne $_ } | Select-Object -Unique -First 8)
        $result.roots.requestedIds = $requested
        $result.roots.requestedTruncated = $RootIds.Count -gt 8
        if ($requested.Count -gt 0) {
            $raw = (& $CliPath --json --developer-mode --no-start --timeout 5 root list 2>$null | Out-String).Trim()
            $result.roots.exitCode = $LASTEXITCODE
            if ($LASTEXITCODE -eq 0 -and $raw.Length -le 1048576 -and -not [string]::IsNullOrWhiteSpace($raw)) {
                $envelope = $raw | ConvertFrom-Json
                if ($envelope.kind -ceq 'result' -and $null -ne $envelope.data) {
                    $result.roots.observationUnavailable = $false
                    $rootMatches = @($envelope.data | Where-Object { $_.root.rootId -cin $requested })
                    $result.roots.observedCount = $rootMatches.Count
                    $result.roots.requestedNotObserved = @($requested | Where-Object { $_ -cnotin $rootMatches.root.rootId })
                    $result.roots.observedTruncated = $rootMatches.Count -gt 8
                    $result.roots.items = @($rootMatches | Select-Object -First 8 | ForEach-Object {
                        $state = 'Unclassified'
                        if ($_.syncState -cin @('Active','Disabled','Offline','Recovering','Paused','NotReady','Degraded','Failed','ReconciliationRequired')) { $state = $_.syncState }
                        [ordered]@{rootId=(Convert-ObservationGuid $_.root.rootId);instanceId=(Convert-ObservationGuid $_.root.instanceId)
                            syncState=$state;requiresFullRescan=$(if ($_.requiresFullRescan -is [bool]) { $_.requiresFullRescan } else { $null })
                            namespaceReady=$(if ($_.namespaceReady -is [bool]) { $_.namespaceReady } else { $null })
                            namespaceRecoveryOperationId=(Convert-ObservationGuid $_.namespaceRecoveryOperationId)
                            namespaceRecoveryCodePresent=(-not [string]::IsNullOrWhiteSpace([string]$_.namespaceRecoveryCode))}
                    })
                }
            }
        }
    } catch { $result.roots.failureHResult = $_.Exception.HResult.ToString('X8') }
    return $result
}
