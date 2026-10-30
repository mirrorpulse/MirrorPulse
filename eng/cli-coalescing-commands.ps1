# Test-only public CLI and authenticated stopped-oracle boundaries for the signed fixture.
function Assert-CliCoalescingEnvironment {
    param([Parameter(Mandatory)][string]$FixtureRoot)
    $root=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($FixtureRoot))
    $parent=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath())
    $name=[IO.Path]::GetFileName($root);$id=[guid]::Empty;$prefix='MirrorPulse-cli-integration-'
    if($env:MIRRORPULSE_NATIVE_TEST -cne '1' -or [IO.Path]::GetDirectoryName($root) -ine $parent -or
        -not $name.StartsWith($prefix,[StringComparison]::Ordinal) -or -not [guid]::TryParseExact($name.Substring($prefix.Length),'N',[ref]$id) -or
        -not (Test-Path -LiteralPath $root -PathType Container) -or (Get-Item -LiteralPath $root -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -or
        [IO.Path]::GetFullPath($env:MIRRORPULSE_DATA_ROOT) -ine (Join-Path $root 'data') -or
        [IO.Path]::GetFullPath($env:MIRRORPULSE_SYNC_ROOT) -ine (Join-Path $root 'sync')){
        throw 'The coalescing fixture requires its explicitly enabled exact disposable native environment.'
    }
    $root
}

function Invoke-CliCoalescingKeepBoth {
    param([Parameter(Mandatory)][string]$CliPath,[Parameter(Mandatory)][string]$FixtureRoot,[Parameter(Mandatory)][guid]$ConflictId)
    $root=Assert-CliCoalescingEnvironment $FixtureRoot
    if($ConflictId -eq [guid]::Empty -or [IO.Path]::GetFullPath($CliPath) -ine (Join-Path $root 'cli/mp.exe')){
        throw 'The choice requires its original isolated CLI and selected conflict.'
    }
    $raw=(& $CliPath --json --developer-mode --no-start conflict resolve --conflict-id $ConflictId.ToString('D') --action KeepBoth 2>&1|Out-String).Trim()
    $code=$LASTEXITCODE
    if($code -notin @(0,11) -or [string]::IsNullOrEmpty($raw) -or $raw.Length -gt 1048576){throw 'The real isolated KeepBoth command did not return an accepted result.'}
    $result=$raw|ConvertFrom-Json;$id=[guid]::Empty
    $command=$result.data.conflictCommand
    if($result.kind -cne 'result' -or $null -eq $command -or $command.conflictId -cne $ConflictId.ToString('D') -or
        -not [guid]::TryParse([string]$command.commandId,[ref]$id) -or $id -eq [guid]::Empty -or
        ($code -eq 0 -and $command.state -cne 'resolved') -or ($code -eq 11 -and $command.state -cne 'pending')){
        throw 'KeepBoth must return the exact command identity and state matching exit0 or exit11.'
    }
    $result
}

function Invoke-CliStoppedCoalescingOracle {
    param([Parameter(Mandatory)][string]$ProbeAssembly,[Parameter(Mandatory)][string]$FixtureRoot,[Parameter(Mandatory)][bool]$Completed)
    $root=Assert-CliCoalescingEnvironment $FixtureRoot
    if([IO.Path]::GetFullPath($ProbeAssembly) -ine (Join-Path $root 'probe/MirrorPulse.CfSharp.CrashProbe.dll')){
        throw 'The stopped oracle must be the current fixture publication.'
    }
    $start=[Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $mode=if($Completed){'--audit-cli-coalescing-completed'}else{'--audit-cli-coalescing-pending'}
    foreach($argument in @($ProbeAssembly,$mode,$root)){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start)
    try{
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(30000)){throw 'The real stopped coalescing oracle exceeded its30s observation window.'}
        $errorText=$stderr.GetAwaiter().GetResult();$text=$stdout.GetAwaiter().GetResult()
        if($process.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($text) -or $text.Length -gt 1048576){
            throw 'The independent stopped oracle refused the original fixture evidence.'
        }
        $result=$text|ConvertFrom-Json
        if($result.schemaVersion -ne 1 -or $result.exclusiveCatalogOwner -ne $true -or $result.rawEffect -cne 'UpdateFile' -or
            $result.originalCount -lt 2 -or $result.originalCount -gt 64 -or @($result.originalOperationIds).Count -ne $result.originalCount){
            throw 'The oracle did not return its bounded authenticated original group.'
        }
        $result
    }finally{
        if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()}
        $process.Dispose()
    }
}

function Invoke-CliCoalescingEditFailureObservation {
    param([Parameter(Mandatory)][string]$ProbeAssembly,[Parameter(Mandatory)][string]$FixtureRoot,[Parameter(Mandatory)][string]$EvidencePath)
    $root=Assert-CliCoalescingEnvironment $FixtureRoot
    if([IO.Path]::GetFullPath($ProbeAssembly) -ine (Join-Path $root 'probe/MirrorPulse.CfSharp.CrashProbe.dll')){
        throw 'The edit observer must be the current isolated fixture publication.'
    }
    $start=[Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in @($ProbeAssembly,'--observe-cli-coalescing-edit',$root)){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start)
    try{
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(30000)){throw 'The stopped edit observation exceeded its30s observation window.'}
        $text=$stdout.GetAwaiter().GetResult();$errorText=$stderr.GetAwaiter().GetResult()
        if($process.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($text) -or $text.Length -gt 1048576){throw 'The stopped edit observer refused the original fixture.'}
        $result=$text|ConvertFrom-Json
        if($result.schemaVersion -ne 1 -or $result.exclusiveCatalogOwner -ne $true -or $result.nativeInspected -ne $true -or
            $result.originalSdkAcknowledgementExecuted -ne $false -or $result.mutationExecuted -ne $false -or @($result.journal).Count -gt 2048){
            throw 'The stopped edit observer crossed its bounded read-only boundary.'
        }
        $directory=Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath))) 'coalescing.originals'
        New-Item -ItemType Directory -Path $directory -Force|Out-Null
        $result|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $directory 'failed-edit-state.json') -Encoding utf8
    }finally{
        if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()}
        $process.Dispose()
    }
}
