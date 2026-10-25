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
    [pscustomobject]@{processState=$processState;exitCode=$exitCode;resultState=$resultState;failureHResult=$failureHResult}
}
