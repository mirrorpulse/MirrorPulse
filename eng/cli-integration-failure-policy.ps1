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
    foreach ($relative in @('logs/mirrorpulse.log','logs/cloud-files/mirrorpulse.log','logs/workers/mirrorpulse.log')) {
        $log = Join-Path $DataRoot $relative
        if (Test-Path -LiteralPath $log -PathType Leaf) {
            $name = $relative.Replace('/', '-')
            Get-Content -LiteralPath $log -Tail 32 | Set-Content -LiteralPath (Join-Path $directory ($Phase.ToLowerInvariant()+'-'+$name)) -Encoding utf8
        }
    }
}
