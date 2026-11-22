function Wait-CliRootLabelHostStart {
    param([Parameter(Mandatory)][scriptblock]$InvokeCli, [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)][int]$PreviousProcessId, [AllowEmptyString()][string]$OriginalsDirectory)

    function Save-LabelStartOriginal([string]$Suffix, [object]$Original) {
        if ($OriginalsDirectory) {
            $Original | ConvertTo-Json -Depth 12 |
                Set-Content -LiteralPath (Join-Path $OriginalsDirectory ($Phase + '-' + $Suffix + '.json')) -Encoding utf8
        }
    }
    $start = & $InvokeCli @('--json', '--no-start', 'host', 'start') @(0, 3)
    Save-LabelStartOriginal 'start' $start
    $timedOut = $start.kind -ceq 'error' -and $start.exitCode -eq 3 -and $start.code -ceq 'mp.control.requestTimeout'
    if (-not $timedOut -and ($start.kind -cne 'result' -or $start.data.processId -le 0)) {
        throw 'The single Host start command did not return a process or a request timeout.'
    }
    $currentProcessId = if ($timedOut) { $null } else { $start.data.processId }
    $budget = [Diagnostics.Stopwatch]::StartNew()
    do {
        $observation = & $InvokeCli @('--json', '--no-start', 'host', 'status')
        $hostStatus = $observation.data
        if ($observation.kind -cne 'result' -or $hostStatus.processId -le 0 -or $hostStatus.processId -eq $PreviousProcessId -or
            ($null -ne $currentProcessId -and $hostStatus.processId -ne $currentProcessId)) {
            throw 'The original new Host did not retain one process identity throughout startup.'
        }
        if ($null -eq $currentProcessId) {
            $currentProcessId = $hostStatus.processId
            Save-LabelStartOriginal 'starting' $observation
        }
        if ($hostStatus.state -cin @('Running', 'Degraded') -and $null -eq $hostStatus.startupPhase) {
            Save-LabelStartOriginal 'after' $observation
            return $hostStatus
        }
        if ($hostStatus.state -cne 'Starting' -or [string]::IsNullOrWhiteSpace($hostStatus.startupPhase)) {
            throw 'The original new Host is no longer initializing or ready.'
        }
        Start-Sleep -Milliseconds 250
    } while ($budget.Elapsed -lt [TimeSpan]::FromSeconds(60))
    throw 'The original new Host did not complete initialization within the readiness budget.'
}

function Assert-CliRootLabelHostStartOriginal {
    param([Parameter(Mandatory)][string]$OriginalsDirectory, [Parameter(Mandatory)][string]$Phase,
        [Parameter(Mandatory)][object]$After)

    function Read-LabelStartOriginal([string]$Suffix) {
        $file = Get-Item -LiteralPath (Join-Path $OriginalsDirectory ($Phase + '-' + $Suffix + '.json')) -ErrorAction Stop
        if ($file.Length -gt 4MB -or $file.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
            throw 'Require bounded ordinary Host startup originals.'
        }
        Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    }
    $start = Read-LabelStartOriginal 'start'
    if ($start.kind -ceq 'result' -and $null -ne $start.data) { return }
    if ($start.kind -cne 'error' -or $start.exitCode -ne 3 -or $start.code -cne 'mp.control.requestTimeout') {
        throw 'Only the original startup request timeout can be resolved by observing the same Host.'
    }
    $starting = Read-LabelStartOriginal 'starting'
    if ($starting.kind -cne 'result' -or $starting.data.processId -le 0 -or $starting.data.processId -ne $After.processId -or
        $After.state -cnotin @('Running', 'Degraded') -or $null -ne $After.startupPhase) {
        throw 'Require the original new Host to remain the same process through verified readiness.'
    }
    $observed = $starting.data
    if (-not ($observed.state -ceq 'Starting' -and -not [string]::IsNullOrWhiteSpace($observed.startupPhase)) -and
        -not ($observed.state -cin @('Running', 'Degraded') -and $null -eq $observed.startupPhase)) {
        throw 'The original observation after the timeout must show a starting or ready Host.'
    }
}
