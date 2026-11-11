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

function Invoke-CliCoalescingChoice {
    param([Parameter(Mandatory)][string]$CliPath,[Parameter(Mandatory)][string]$FixtureRoot,[Parameter(Mandatory)][guid]$ConflictId,
        [ValidateSet('KeepBoth','KeepRemote', IgnoreCase = $false)][string]$Action = 'KeepBoth')
    $root=Assert-CliCoalescingEnvironment $FixtureRoot
    if($ConflictId -eq [guid]::Empty -or [IO.Path]::GetFullPath($CliPath) -ine (Join-Path $root 'cli/mp.exe')){
        throw 'The choice requires its original isolated CLI and selected conflict.'
    }
    $raw=(& $CliPath --json --developer-mode --no-start conflict resolve --conflict-id $ConflictId.ToString('D') --action $Action 2>&1|Out-String).Trim()
    $code=$LASTEXITCODE
    if([string]::IsNullOrEmpty($raw) -or $raw.Length -gt 1048576){throw "The real isolated $Action command returned no bounded result (exit $code)."}
    if($code -notin @(0,11)){
        $errorCode='unavailable'
        try{
            $failure=$raw|ConvertFrom-Json -ErrorAction Stop
            if($failure.kind -ceq 'error' -and $failure.code -cmatch '^[A-Za-z][A-Za-z0-9._-]{0,127}$'){
                $errorCode=$failure.code
            }
        }catch{}
        throw "The real isolated $Action command was refused (exit $code, code $errorCode)."
    }
    $result=$raw|ConvertFrom-Json;$id=[guid]::Empty
    $command=$result.data.conflictCommand
    if($result.kind -cne 'result' -or $null -eq $command -or $command.conflictId -cne $ConflictId.ToString('D') -or
        -not [guid]::TryParse([string]$command.commandId,[ref]$id) -or $id -eq [guid]::Empty -or
        ($code -eq 0 -and $command.state -cne 'resolved') -or ($code -eq 11 -and $command.state -cne 'pending')){
        throw "$Action must return the exact command identity and state matching exit0 or exit11."
    }
    $result
}

function Invoke-CliStoppedCoalescingOracle {
    param([Parameter(Mandatory)][string]$ProbeAssembly,[Parameter(Mandatory)][string]$FixtureRoot,[Parameter(Mandatory)][bool]$Completed,
        [ValidateSet('KeepBoth','KeepRemote', IgnoreCase = $false)][string]$Action = 'KeepBoth', [switch]$ControlledCreate, [switch]$MovedFile)
    $root=Assert-CliCoalescingEnvironment $FixtureRoot
    if([IO.Path]::GetFullPath($ProbeAssembly) -ine (Join-Path $root 'probe/MirrorPulse.CfSharp.CrashProbe.dll')){
        throw 'The stopped oracle must be the current fixture publication.'
    }
    $start=[Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $mode=if(-not $Completed){'--audit-cli-coalescing-pending'}elseif($Action -ceq 'KeepRemote'){'--audit-cli-coalescing-keep-remote'}else{'--audit-cli-coalescing-completed'}
    if($ControlledCreate){
        if($Action -cne 'KeepRemote'){throw 'Controlled-Create oracle requires KeepRemote.'}
        $mode=if($Completed){'--audit-cli-create-keep-remote'}else{'--audit-cli-create-pending'}
    }
    if($MovedFile){
        if($Action -cne 'KeepRemote' -or $ControlledCreate){throw 'The moved-file oracle requires its independent KeepRemote fixture.'}
        $mode=if($Completed){'--audit-cli-move-keep-remote'}else{'--audit-cli-move-pending'}
    }
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
        $expectedEffect=if($MovedFile){'MoveAndUpdateFile'}elseif($ControlledCreate){'CreateFile'}else{'UpdateFile'}
        if($result.schemaVersion -ne 1 -or $result.exclusiveCatalogOwner -ne $true -or $result.rawEffect -cne $expectedEffect -or
            $result.originalCount -lt 2 -or $result.originalCount -gt 64 -or @($result.originalOperationIds).Count -ne $result.originalCount){
            throw 'The oracle did not return its bounded authenticated original group.'
        }
        $result
    }finally{
        if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()}
        $process.Dispose()
    }
}

function Read-CliStoppedCoalescingEdit {
    param([Parameter(Mandatory)][string]$ProbeAssembly,[Parameter(Mandatory)][string]$FixtureRoot,[switch]$MovedFile)
    $root=Assert-CliCoalescingEnvironment $FixtureRoot
    if([IO.Path]::GetFullPath($ProbeAssembly) -ine (Join-Path $root 'probe/MirrorPulse.CfSharp.CrashProbe.dll')){
        throw 'The edit observer must be the current isolated fixture publication.'
    }
    $start=[Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $mode=if($MovedFile){'--observe-cli-move-coalescing-edit'}else{'--observe-cli-coalescing-edit'}
    foreach($argument in @($ProbeAssembly,$mode,$root)){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start)
    try{
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        if(-not $process.WaitForExit(30000)){throw 'The stopped edit observation exceeded its30s observation window.'}
        $text=$stdout.GetAwaiter().GetResult();$errorText=$stderr.GetAwaiter().GetResult()
        if($process.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($text) -or $text.Length -gt 1048576){
            $detail='unavailable'
            # Preserve only the child probe's bounded public failure fields. Never relay raw stderr.
            if($errorText -cmatch '\AFixture consumer failure: hresult=([0-9A-F]{8}); type=([A-Za-z][A-Za-z0-9]{0,63}); stage=([A-Za-z][A-Za-z0-9]{0,63}); argument=([A-Za-z][A-Za-z0-9_]{0,63})\.\s*\z'){
                $detail="hresult=$($Matches[1]); type=$($Matches[2]); stage=$($Matches[3]); argument=$($Matches[4])"
            }
            throw "The stopped edit observer refused the original fixture (exit $($process.ExitCode); $detail)."
        }
        $result=$text|ConvertFrom-Json
        if($result.schemaVersion -ne 1 -or $result.exclusiveCatalogOwner -ne $true -or $result.nativeInspected -ne $true -or
            $result.originalSdkAcknowledgementExecuted -ne $false -or $result.mutationExecuted -ne $false -or @($result.journal).Count -gt 2048){
            throw 'The stopped edit observer crossed its bounded read-only boundary.'
        }
        return $result
    }finally{
        if(-not $process.HasExited){$process.Kill($true);$process.WaitForExit()}
        $process.Dispose()
    }
}

function Invoke-CliCoalescingEditFailureObservation {
    param([Parameter(Mandatory)][string]$ProbeAssembly,[Parameter(Mandatory)][string]$FixtureRoot,[Parameter(Mandatory)][string]$EvidencePath,[switch]$MovedFile)
    $result=Read-CliStoppedCoalescingEdit -ProbeAssembly $ProbeAssembly -FixtureRoot $FixtureRoot -MovedFile:$MovedFile
    $directory=Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath))) 'coalescing.originals'
    New-Item -ItemType Directory -Path $directory -Force|Out-Null
    $result|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $directory 'failed-edit-state.json') -Encoding utf8
}
