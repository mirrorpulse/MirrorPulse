function Invoke-CliFixtureTeardown {
    param(
        [AllowNull()][System.Management.Automation.ErrorRecord]$PrimaryFailure,
        [Parameter(Mandatory)][scriptblock]$Cleanup,
        [scriptblock]$RecordFailure
    )
    try { & $Cleanup }
    catch {
        $cleanupFailure = $_
        if ($null -ne $RecordFailure) {
            try { & $RecordFailure $cleanupFailure }
            catch { Write-Host "Fixture cleanup failure recording also failed: $($_.Exception.GetType().Name)." }
        }
        if ($null -eq $PrimaryFailure) { throw }
        # The caller rethrows its original ErrorRecord before entering finally.
        # Let that error continue after recording the independent teardown failure.
        Write-Host "Secondary fixture cleanup failure: type=$($cleanupFailure.Exception.GetType().Name); hresult=$($cleanupFailure.Exception.HResult.ToString('X8'))."
    }
}

function Write-CliFixtureFailure {
    param(
        [Parameter(Mandatory)][System.Management.Automation.ErrorRecord]$Failure,
        [Parameter(Mandatory)][ValidateSet('Execution','Cleanup')][string]$Phase,
        [AllowNull()][string]$EvidencePath,
        [Parameter(Mandatory)][string]$FixtureRoot,
        [Parameter(Mandatory)][string]$DataRoot
    )
    $message = $Failure.Exception.Message.Replace($FixtureRoot, '<fixture>')
    Write-Host "$Phase CLI fixture failure: $message"
    if ([string]::IsNullOrEmpty($EvidencePath)) { return }
    $directory = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath))) 'cli-regression.originals'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $record = [ordered]@{
        schemaVersion=1;phase=$Phase;sourceSha=$env:GITHUB_SHA;runId=$env:GITHUB_RUN_ID
        type=$Failure.Exception.GetType().Name;hresult=$Failure.Exception.HResult.ToString('X8')
        errorId=([string]$Failure.FullyQualifiedErrorId).Replace($FixtureRoot, '<fixture>');message=$message
        scriptStack=([string]$Failure.ScriptStackTrace).Replace($FixtureRoot, '<fixture>')
    }
    $record | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $directory ($Phase.ToLowerInvariant()+'.json')) -Encoding utf8
    foreach ($relative in @('logs/mirrorpulse.log','logs/cloud-files/mirrorpulse.log','logs/workers/mirrorpulse.log','logs/root-labels/mirrorpulse.log')) {
        $log = Join-Path $DataRoot $relative
        if (Test-Path -LiteralPath $log -PathType Leaf) {
            $name = $relative.Replace('/', '-')
            Get-Content -LiteralPath $log -Tail 32 | Set-Content -LiteralPath (Join-Path $directory ($Phase.ToLowerInvariant()+'-'+$name)) -Encoding utf8
        }
    }
}

function Write-CliFixtureExecutionObservations {
    param(
        [AllowNull()][string]$EvidencePath,
        [AllowEmptyCollection()][object[]]$HydrationReads = @(),
        [AllowEmptyCollection()][object[]]$HydrationAudits = @(),
        [AllowEmptyCollection()][object[]]$NamespaceAudits = @()
    )
    if ([string]::IsNullOrEmpty($EvidencePath)) { return }
    $directory = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath))) 'cli-regression.originals'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    # Snapshot completed assertions before teardown can remove their fixture or
    # fail independently. The final successful report still requires cleanup.
    [ordered]@{
        schemaVersion=1;sourceSha=$env:GITHUB_SHA;runId=$env:GITHUB_RUN_ID
        executionCompleted=$true;teardownCompleted=$false
        hydration=[ordered]@{reads=@($HydrationReads);audits=@($HydrationAudits)}
        namespace=[ordered]@{audits=@($NamespaceAudits)}
    } | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $directory 'completed-execution.json') -Encoding utf8
}

function Write-CliFixtureStartupObservations {
    param(
        [Parameter(Mandatory)][scriptblock]$ReadLifecycle,
        [AllowNull()][string]$EvidencePath,
        [AllowEmptyCollection()][object[]]$HydrationReads = @(),
        [AllowEmptyCollection()][object[]]$HydrationAudits = @(),
        [AllowEmptyCollection()][object[]]$NamespaceAudits = @()
    )
    if ([string]::IsNullOrEmpty($EvidencePath)) { return }
    $directory = Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath))) 'cli-regression.originals'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    # Preserve only completed fixture observations. A failure never invents the remaining phases.
    [ordered]@{
        schemaVersion=1;sourceSha=$env:GITHUB_SHA;runId=$env:GITHUB_RUN_ID;partial=$true
        hydration=[ordered]@{reads=@($HydrationReads);audits=@($HydrationAudits)}
        namespace=[ordered]@{audits=@($NamespaceAudits)}
    } | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $directory 'partial-observations.json') -Encoding utf8
    try {
        $response = & $ReadLifecycle
        if ($response.kind -cne 'result' -or $response.data.state -cnotmatch '\A(?:Created|Starting|Running|Degraded|Stopping|Stopped|Failed)\z' -or
            ($response.data.processId -isnot [int] -and $response.data.processId -isnot [long]) -or $response.data.processId -le 0 -or
            ($null -ne $response.data.startupPhase -and $response.data.startupPhase -cnotmatch '\A(?:CloudFiles|Adapters|RemotePolling)\z')) {
            throw [IO.InvalidDataException]::new('Invalid lifecycle observation.')
        }
        $record = [ordered]@{schemaVersion=1;sourceSha=$env:GITHUB_SHA;runId=$env:GITHUB_RUN_ID
            state=$response.data.state;startupPhase=$response.data.startupPhase;processId=$response.data.processId}
        Write-Host "Actual Host lifecycle at fixture failure: state=$($record.state); startupPhase=$($record.startupPhase)."
    } catch {
        # No exception text, arbitrary response body, environment or credential values.
        $record = [ordered]@{schemaVersion=1;sourceSha=$env:GITHUB_SHA;runId=$env:GITHUB_RUN_ID
            observationUnavailable=$true;type=$_.Exception.GetType().Name;hresult=$_.Exception.HResult.ToString('X8')}
    }
    $record | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $directory 'lifecycle.json') -Encoding utf8
}
