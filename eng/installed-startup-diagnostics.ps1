function Get-InstalledHostStartupDiagnostics {
    param(
        [Parameter(Mandatory)][ValidateCount(1,4)][string[]]$DataRoots,
        [Parameter(Mandatory)][DateTimeOffset]$Since
    )
    $records = [Collections.Generic.List[object]]::new()
    $sources = [Collections.Generic.List[object]]::new()
    $codes = @('HostStartupFailed','ShellRootPolicyMismatch','CloudRootMetadataMismatch','CloudRootIdentityMismatch',
        'ShellRootOwnershipMismatch','ShellRootPathMismatch','ShellRootRegistrationFailed','UnclassifiedDiagnostic')
    $categories = @('IO','InvalidData','Authorization','Cancelled','Internal','MutationAmbiguous','WorkerOffline','WorkerDisconnected','WorkerRejected')
    for ($index=0; $index -lt $DataRoots.Count; $index++) {
        foreach ($variant in @('mirrorpulse.log','mirrorpulse.log.1')) {
            $state='Missing'
            try {
                $root=[IO.DirectoryInfo]::new([IO.Path]::GetFullPath($DataRoots[$index]))
                $logs=[IO.DirectoryInfo]::new((Join-Path $root.FullName 'logs'))
                $file=[IO.FileInfo]::new((Join-Path $logs.FullName $variant))
                if ($null -ne $root.LinkTarget -or $null -ne $logs.LinkTarget -or $null -ne $file.LinkTarget) { $state='Linked' }
                elseif ($file.Exists) {
                    if ($file.Length -gt 4MB) { $state='Oversized' }
                    else {
                        $state='Read'
                        foreach ($line in @(Get-Content -LiteralPath $file.FullName -Tail 64 -ErrorAction Stop)) {
                            if ($records.Count -ge 64) { break }
                            try { $entry=$line|ConvertFrom-Json -Depth 8 -ErrorAction Stop } catch { continue }
                            $at=[DateTimeOffset]::MinValue
                            # Recent PowerShell versions may already parse ISO JSON dates. Preserve
                            # their fractional precision instead of casting through a display string.
                            if ($entry.OccurredAt -is [DateTimeOffset]) { $at=$entry.OccurredAt; $validTime=$true }
                            elseif ($entry.OccurredAt -is [DateTime]) { $at=[DateTimeOffset]::new($entry.OccurredAt); $validTime=$true }
                            else { $validTime=[DateTimeOffset]::TryParse([string]$entry.OccurredAt,[Globalization.CultureInfo]::InvariantCulture,
                                [Globalization.DateTimeStyles]::None,[ref]$at) }
                            if ($entry.Category -cne 'host' -or $entry.Level -cne 'Error' -or $entry.Code -cnotin $codes -or
                                -not $validTime -or $at -lt $Since) { continue }
                            $category=if ([string]$entry.Fields.failureCategory -cin $categories) { [string]$entry.Fields.failureCategory } else { $null }
                            $hresult=if ([string]$entry.Fields.hresult -cmatch '\A[0-9A-Fa-f]{8}\z') { ([string]$entry.Fields.hresult).ToUpperInvariant() } else { $null }
                            # Never copy arbitrary messages, fields, paths, configuration or credential values.
                            $records.Add([ordered]@{sourceIndex=$index;occurredAt=$at.ToUniversalTime().ToString('O');code=[string]$entry.Code;
                                failureCategory=$category;hresult=$hresult})
                        }
                    }
                }
            } catch { $state='Unreadable' }
            $sources.Add([ordered]@{sourceIndex=$index;variant=$variant;state=$state})
        }
    }
    [pscustomobject]@{records=$records.ToArray();sources=$sources.ToArray()}
}

function Get-InstalledHostExitObservation {
    param([Parameter(Mandatory)][string]$ExpectedExecutable)
    $observations=[Collections.Generic.List[object]]::new()
    foreach ($process in @(Get-Process -Name 'MirrorPulse.Host' -ErrorAction SilentlyContinue|Select-Object -First 2)) {
        try {
            if (-not [string]::Equals($process.Path,$ExpectedExecutable,[StringComparison]::OrdinalIgnoreCase)) { continue }
            $exited=$process.WaitForExit(2000)
            $observations.Add([ordered]@{processId=$process.Id;exited=$exited;exitCode=if($exited){$process.ExitCode}else{$null}})
        } catch { $observations.Add([ordered]@{observationUnavailable=$true}) }
        finally { $process.Dispose() }
    }
    $observations.ToArray()
}
