# Precreate the verifier-owned files before launching the packaged process. MSIX
# may redirect newly created AppData files; opening an existing real file retains
# its actual cross-process location. Empty files are never probe completion.
function New-InstalledShellProbeStartInfo {
    param([Parameter(Mandatory)][string]$ExecutablePath,[Parameter(Mandatory)][string]$ResultPath)
    $start=[Diagnostics.ProcessStartInfo]::new($ExecutablePath)
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $start.Environment['MIRRORPULSE_PACKAGED_SHELL_PROBE_RESULT']=$ResultPath
    return $start
}

function Initialize-InstalledShellProbeOutput {
    param([Parameter(Mandatory)][string]$ResultPath)
    ([IO.File]::Open($ResultPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)).Dispose()
    ([IO.File]::Open($ResultPath+'.progress',[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)).Dispose()
}

function Wait-InstalledShellProbeResult {
    param(
        [AllowNull()][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory)][string]$ResultPath,
        [Parameter(Mandatory)][DateTime]$DeadlineUtc
    )
    while ($true) {
        $observation=Get-InstalledShellProbeObservation -Process $Process -ResultPath $ResultPath
        if ($observation.resultState -cin @('Success','Failure') -or $observation.processState -ceq 'Exited' -or
            [DateTime]::UtcNow -ge $DeadlineUtc) { return $observation }
        Start-Sleep -Milliseconds 500
    }
}

function Get-InstalledShellProbeObservation {
    param(
        [AllowNull()][System.Diagnostics.Process]$Process,
        [Parameter(Mandatory)][string]$ResultPath
    )
    $processState='Unavailable';$exitCode=$null
    if ($null -ne $Process) {
        try {
            $Process.Refresh()
            if ($Process.HasExited) { $processState='Exited';$exitCode=$Process.ExitCode }
            else { $processState='Running' }
        } catch { $processState='Unavailable';$exitCode=$null }
    }
    $resultState='Missing';$failureHResult=$null
    try {
        $file=Get-Item -LiteralPath $ResultPath -Force -ErrorAction Stop
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $resultState='Linked' }
        elseif ($file.PSIsContainer) { $resultState='Invalid' }
        elseif ($file.Length -gt 4096) { $resultState='TooLarge' }
        else {
            $stream=[IO.File]::Open($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,
                [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            try {
                $bytes=[byte[]]::new(4097);$count=$stream.Read($bytes,0,$bytes.Length)
                if ($count -gt 4096) { $resultState='TooLarge' }
                else {
                    $value=[Text.Encoding]::UTF8.GetString($bytes,0,$count).Trim()
                    if ($value -ceq 'success') { $resultState='Success' }
                    elseif ($value -cmatch '\Afailure: 0x([0-9A-Fa-f]{8}) ') {
                        $resultState='Failure';$failureHResult=$Matches[1].ToUpperInvariant()
                    } else { $resultState='Invalid' }
                }
            } finally { $stream.Dispose() }
        }
    } catch [System.Management.Automation.ItemNotFoundException] { $resultState='Missing' }
    catch { $resultState='Unreadable' }
    # No process path, arbitrary probe text, exception, user label or credential.
    $progress=Get-InstalledShellProbeStage -ResultPath $ResultPath
    [pscustomobject]@{processState=$processState;exitCode=$exitCode;resultState=$resultState;failureHResult=$failureHResult;
        stageState=$progress.state;stage=$progress.stage}
}

function Get-InstalledShellProbeStage {
    param([Parameter(Mandatory)][string]$ResultPath)
    $state='Missing';$stage=$null
    try {
        $file=Get-Item -LiteralPath ($ResultPath+'.progress') -Force -ErrorAction Stop
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $state='Linked' }
        elseif ($file.PSIsContainer) { $state='Invalid' }
        elseif ($file.Length -gt 128) { $state='TooLarge' }
        else {
            $stream=[IO.File]::Open($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,
                [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            try {
                $bytes=[byte[]]::new(129);$count=$stream.Read($bytes,0,$bytes.Length)
                if ($count -gt 128) { $state='TooLarge' }
                else {
                    $value=[Text.Encoding]::UTF8.GetString($bytes,0,$count)
                    $known=@('AppConstructing','AppInitialized','AppLaunched','ProbeSelected','ProbeEntered',
                        'PackageValidated','RootCreated','CustomRegistrationStarted','CustomRegistrationCompleted',
                        'CustomReadbackStarted','CustomReadbackVerified','UnifiedRegistrationStarted',
                        'UnifiedRegistrationCompleted','UnifiedReadbackStarted','UnifiedReadbackVerified',
                        'WritingSuccess','SuccessWritten','FailureHandling','MinimalFolderStarted','MinimalFolderReady',
                        'MinimalRegistrationStarted','MinimalRegistrationCompleted','MinimalReadbackStarted',
                        'MinimalReadbackCompleted','MinimalUnregisterStarted','MinimalUnregisterCompleted',
                        'WritingFailure','FailureWritten','UnregisterStarted','UnregisterCompleted',
                        'DirectoryCleanupStarted','DirectoryCleanupCompleted','Exiting')
                    if ($value.StartsWith('1:',[StringComparison]::Ordinal) -and $value.Substring(2) -cin $known) {
                        $state='Observed';$stage=$value.Substring(2)
                    } else { $state='Invalid' }
                }
            } finally { $stream.Dispose() }
        }
    } catch [System.Management.Automation.ItemNotFoundException] { $state='Missing' }
    catch { $state='Unreadable' }
    [pscustomobject]@{state=$state;stage=$stage}
}
