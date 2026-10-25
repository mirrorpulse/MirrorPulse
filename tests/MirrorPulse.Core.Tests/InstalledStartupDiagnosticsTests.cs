using System.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class InstalledStartupDiagnosticsTests
{
    [TestMethod]
    [DataRow("valid")]
    [DataRow("mixed")]
    [DataRow("bounded")]
    [DataRow("linked")]
    [DataRow("anchor-stages")]
    [DataRow("failure-site")]
    [DataRow("private-site")]
    public async Task InstalledFailureEvidenceRetainsOnlyOriginalBoundedCloudStartupBoundaries(string scenario)
    {
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-installed-diagnostics-tests"));
        string directory = Path.Combine(prefix, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "verify.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Policy,[string]$Directory,[string]$Scenario)
                $ErrorActionPreference='Stop'
                . $Policy
                $root=Join-Path $Directory 'data'; $logs=Join-Path $root 'logs'; $cloud=Join-Path $logs 'cloud-files'
                New-Item -ItemType Directory -Path $logs -Force|Out-Null
                $since=[DateTimeOffset]::UtcNow.AddMinutes(-1)
                function New-Record([DateTimeOffset]$At,[string]$Stage='NamespaceAnchorBegin',[string]$Sequence='5',[string]$Elapsed='123') {
                    [ordered]@{OccurredAt=$At.ToString('O');Level='Information';Category='CloudFiles.Startup';Code='CloudFilesStartupProgress';
                        Message='PRIVATE_TEST_SENTINEL';Fields=@{startupStage=$Stage;startupSequence=$Sequence;startupElapsedMs=$Elapsed;path=$root}}
                }
                if ($Scenario -eq 'linked') {
                    $target=Join-Path $Directory 'foreign-logs';New-Item -ItemType Directory -Path $target|Out-Null
                    New-Item -ItemType Junction -Path $cloud -Target $target|Out-Null
                } else {New-Item -ItemType Directory -Path $cloud|Out-Null}
                $log=Join-Path $cloud 'mirrorpulse.log'
                if ($Scenario -eq 'bounded') {
                    1..130|ForEach-Object {(New-Record $since.AddMilliseconds($_) 'NamespaceRootBegin' ([string]$_))|ConvertTo-Json -Depth 4 -Compress}|
                        Set-Content -LiteralPath $log
                } elseif ($Scenario -eq 'anchor-stages') {
                    @(
                        ((New-Record $since.AddSeconds(1) 'NamespaceAnchorEntryGuardBegin' '6')|ConvertTo-Json -Depth 4 -Compress)
                        ((New-Record $since.AddSeconds(2) 'NamespaceAnchorInitializationBegin' '8')|ConvertTo-Json -Depth 4 -Compress)
                        ((New-Record $since.AddSeconds(3) 'NamespaceEmptyInventoryRecoveryBegin' '10')|ConvertTo-Json -Depth 4 -Compress)
                        ((New-Record $since.AddSeconds(4) 'PRIVATE_TEST_SENTINEL' '11')|ConvertTo-Json -Depth 4 -Compress)
                    )|Set-Content -LiteralPath $log
                } elseif ($Scenario -eq 'mixed') {
                    @(
                        'not-json'
                        ((New-Record $since.AddSeconds(-1))|ConvertTo-Json -Depth 4 -Compress)
                        ((New-Record $since.AddSeconds(1) 'PRIVATE_TEST_SENTINEL')|ConvertTo-Json -Depth 4 -Compress)
                        ((New-Record $since.AddSeconds(2) 'NamespaceAnchorBegin' '-1')|ConvertTo-Json -Depth 4 -Compress)
                        ((New-Record $since.AddSeconds(3) 'NamespaceAnchorBegin' '5' 'PRIVATE_TEST_SENTINEL')|ConvertTo-Json -Depth 4 -Compress)
                        ((New-Record $since.AddSeconds(4))|ConvertTo-Json -Depth 4 -Compress)
                    )|Set-Content -LiteralPath $log
                } else {(New-Record $since.AddSeconds(1))|ConvertTo-Json -Depth 4 -Compress|Set-Content -LiteralPath $log}
                # Preserve the original failing Host fact independently of progress volume.
                $originalIoFailure=$Scenario -in @('anchor-stages','failure-site','private-site')
                $failureCode=if($originalIoFailure){'HostStartupFailed'}else{'NamespaceAnchorOwnerMismatch'}
                $failureCategory=if($originalIoFailure){'IO'}else{'InvalidData'}
                $failureHResult=if($originalIoFailure){'80070002'}else{'80131501'}
                $failureSite=if($Scenario -eq 'failure-site'){'AnchorProtectedPermission'}else{'PRIVATE_TEST_SENTINEL'}
                $failureKind=if($Scenario -eq 'failure-site'){'FileNotFound'}else{'PRIVATE_TEST_SENTINEL'}
                [ordered]@{OccurredAt=$since.AddSeconds(5).ToString('O');Level='Error';Category='host';Code=$failureCode;
                    Fields=@{failureCategory=$failureCategory;hresult=$failureHResult;failureSite=$failureSite;failureKind=$failureKind}}|ConvertTo-Json -Depth 4 -Compress|
                    Set-Content -LiteralPath (Join-Path $logs 'mirrorpulse.log')
                try {
                $result=Get-InstalledHostStartupDiagnostics -DataRoots @($root) -Since $since
                $json=$result|ConvertTo-Json -Depth 6 -Compress
                if ($json.Contains('PRIVATE_TEST_SENTINEL') -or $json.Contains($root)) {throw 'Private fields or paths escaped.'}
                if ($result.records.Count -ne 1 -or $result.records[0].code -cne $failureCode -or $result.records[0].hresult -cne $failureHResult) {throw 'The original Host failure was lost.'}
                if($Scenario -eq 'failure-site') {
                    if($result.records[0].failureSite -cne 'AnchorProtectedPermission' -or $result.records[0].failureKind -cne 'FileNotFound') {
                        throw 'The original closed startup failure boundary was lost.'
                    }
                } elseif($null -ne $result.records[0].failureSite -or $null -ne $result.records[0].failureKind) {
                    throw 'An arbitrary startup failure site or type was retained.'
                }
                if ($Scenario -eq 'linked') {
                    if ($result.startupProgress.Count -ne 0 -or @($result.sources|Where-Object {$_.variant -ceq 'cloud-files/mirrorpulse.log' -and $_.state -ceq 'Linked'}).Count -ne 1) {
                        throw 'A linked progress directory was read.'
                    }
                } elseif ($Scenario -eq 'bounded') {
                    if ($result.startupProgress.Count -ne 64 -or $result.startupProgress[0].sequence -ne 67 -or $result.startupProgress[-1].sequence -ne 130) {
                        throw 'The latest original bounded progress sequence was not retained.'
                    }
                } elseif ($Scenario -eq 'anchor-stages') {
                    $expected=@('NamespaceAnchorEntryGuardBegin','NamespaceAnchorInitializationBegin','NamespaceEmptyInventoryRecoveryBegin')
                    if ($result.startupProgress.Count -ne 3 -or @(Compare-Object $expected @($result.startupProgress|ForEach-Object stage)).Count) {
                        throw 'The original anchor initialization sub-boundaries were lost or untyped progress was adopted.'
                    }
                } elseif ($result.startupProgress.Count -ne 1 -or $result.startupProgress[0].stage -cne 'NamespaceAnchorBegin' -or
                    $result.startupProgress[0].sequence -ne 5 -or $result.startupProgress[0].elapsedMs -ne 123) {throw 'Untyped or stale startup progress was adopted.'}
                $json
                } finally {if ($Scenario -eq 'linked') {Remove-Item -LiteralPath $cloud -Force}}
                """);
            await RunProbeAsync(script, directory, scenario);
        }
        finally { Cleanup(prefix, directory); }
    }

    [TestMethod]
    [DataRow("valid")]
    [DataRow("mixed")]
    [DataRow("oversized")]
    [DataRow("bounded")]
    public async Task InstalledFailureEvidenceRetainsOnlyRecentBoundedTypedHostFacts(string scenario)
    {
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-installed-diagnostics-tests"));
        string directory = Path.Combine(prefix, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "verify.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Policy,[string]$Directory,[string]$Scenario)
                $ErrorActionPreference='Stop'
                . $Policy
                $root=Join-Path $Directory 'data'; $logs=Join-Path $root 'logs'
                New-Item -ItemType Directory -Path $logs -Force|Out-Null
                $log=Join-Path $logs 'mirrorpulse.log'; $since=[DateTimeOffset]::UtcNow.AddMinutes(-1)
                function New-Record([DateTimeOffset]$At,[string]$Code='HostStartupFailed',[string]$Category='Authorization',[string]$HResult='80070005') {
                    [ordered]@{OccurredAt=$At.ToString('O');Level='Error';Category='host';Code=$Code;
                        Message='PRIVATE_TEST_SENTINEL';Fields=@{failureCategory=$Category;hresult=$HResult;credential='PRIVATE_TEST_SENTINEL';path=$root}}
                }
                if ($Scenario -eq 'oversized') {
                    [IO.File]::WriteAllBytes($log,[byte[]]::new(4MB+1))
                } elseif ($Scenario -eq 'bounded') {
                    1..130|ForEach-Object {(New-Record $since.AddMilliseconds($_))|ConvertTo-Json -Depth 4 -Compress}|Set-Content -LiteralPath $log
                } elseif ($Scenario -eq 'mixed') {
                    @(
                        'not-json'
                        ((New-Record $since.AddSeconds(-1))|ConvertTo-Json -Depth 4 -Compress)
                        ((New-Record $since.AddSeconds(1) 'PRIVATE_TEST_SENTINEL')|ConvertTo-Json -Depth 4 -Compress)
                        ((New-Record $since.AddSeconds(2) 'HostStartupFailed' 'PRIVATE_TEST_SENTINEL' 'PRIVATE_TEST_SENTINEL')|ConvertTo-Json -Depth 4 -Compress)
                    )|Set-Content -LiteralPath $log
                } else {
                    (New-Record $since.AddSeconds(1))|ConvertTo-Json -Depth 4 -Compress|Set-Content -LiteralPath $log
                }
                $result=Get-InstalledHostStartupDiagnostics -DataRoots @($root) -Since $since
                $json=$result|ConvertTo-Json -Depth 6 -Compress
                if ($json.Contains('PRIVATE_TEST_SENTINEL') -or $json.Contains($root)) {throw 'Private fields or paths escaped the typed projection.'}
                if ($Scenario -eq 'oversized') {
                    if ($result.records.Count -ne 0 -or $result.sources[0].state -cne 'Oversized') {throw 'Oversized input was read or accepted.'}
                } elseif ($Scenario -eq 'bounded') {
                    if ($result.records.Count -ne 64) {throw 'The bounded tail did not retain exactly its latest64 facts.'}
                    $rejected=$false
                    try {Get-InstalledHostStartupDiagnostics -DataRoots @($root,$root,$root,$root,$root) -Since $since|Out-Null} catch {$rejected=$true}
                    if (-not $rejected) {throw 'An unbounded root inventory was accepted.'}
                } else {
                    if ($result.records.Count -ne 1 -or $result.records[0].code -cne 'HostStartupFailed') {throw 'The current Host diagnostic was not retained once.'}
                    if ($Scenario -eq 'mixed') {
                        if ($null -ne $result.records[0].hresult -or $null -ne $result.records[0].failureCategory) {throw 'Untyped fields were adopted.'}
                    } elseif ($result.records[0].hresult -cne '80070005' -or $result.records[0].failureCategory -cne 'Authorization') {
                        throw 'The actual typed native failure facts changed.'
                    }
                }
                $json
                """);
            await RunProbeAsync(script, directory, scenario);
        }
        finally { Cleanup(prefix, directory); }
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("success")]
    [DataRow("failure")]
    [DataRow("private")]
    [DataRow("oversized")]
    [DataRow("directory")]
    [DataRow("linked")]
    [DataRow("exited")]
    [DataRow("running")]
    public async Task ShellFailureObservationRetainsOnlyBoundedResultAndActualProcessState(string scenario)
    {
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-installed-diagnostics-tests"));
        string directory = Path.Combine(prefix, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "verify.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Policy,[string]$Directory,[string]$Scenario)
                $ErrorActionPreference='Stop'
                . (Join-Path (Split-Path -Parent $Policy) 'installed-shell-observations.ps1')
                $resultPath=Join-Path $Directory 'result';$child=$null;$linkCreated=$false
                try {
                    switch ($Scenario) {
                        'success' { [IO.File]::WriteAllText($resultPath,'success') }
                        'failure' { [IO.File]::WriteAllText($resultPath,'failure: 0x80070005 PRIVATE_TEST_SENTINEL') }
                        'private' { [IO.File]::WriteAllText($resultPath,'PRIVATE_TEST_SENTINEL') }
                        'oversized' { [IO.File]::WriteAllBytes($resultPath,[byte[]]::new(4097)) }
                        'directory' { New-Item -ItemType Directory $resultPath|Out-Null }
                        'linked' {
                            $target=Join-Path $Directory 'target';New-Item -ItemType Directory $target|Out-Null
                            New-Item -ItemType Junction -Path $resultPath -Target $target|Out-Null
                            $linkCreated=$true
                        }
                        'exited' {
                            $child=Start-Process (Get-Command pwsh).Source -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile','-NonInteractive','-Command','exit 23')
                            if (-not $child.WaitForExit(5000)) {throw 'The synthetic process did not exit.'}
                        }
                        'running' {
                            $child=Start-Process (Get-Command pwsh).Source -WindowStyle Hidden -PassThru -ArgumentList @('-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30')
                        }
                    }
                    $result=Get-InstalledShellProbeObservation -Process $child -ResultPath $resultPath
                    $json=$result|ConvertTo-Json -Compress
                    if ($json.Contains('PRIVATE_TEST_SENTINEL') -or $json.Contains($Directory)) {throw 'Private text or paths escaped the closed projection.'}
                    $expected=@{missing='Missing';success='Success';failure='Failure';private='Invalid';oversized='TooLarge';directory='Invalid';linked='Linked';exited='Missing';running='Missing'}
                    if ($result.resultState -cne $expected[$Scenario]) {throw ('Wrong actual result observation: '+$json)}
                    if ($Scenario -eq 'exited') {
                        if ($result.processState -cne 'Exited' -or $result.exitCode -ne 23) {throw ('The actual exit fact was lost: '+$json)}
                    } elseif ($Scenario -eq 'running') {
                        if ($result.processState -cne 'Running' -or $null -ne $result.exitCode) {throw 'A running probe acquired a fabricated exit code.'}
                    } elseif ($result.processState -cne 'Unavailable' -or $null -ne $result.exitCode) {throw 'A missing process acquired a fabricated state.'}
                    if ($Scenario -eq 'failure') {
                        if ($result.failureHResult -cne '80070005') {throw 'The closed native failure code was lost.'}
                    } elseif ($null -ne $result.failureHResult) {throw 'An unobserved native failure code was fabricated.'}
                    $json
                } finally {
                    if ($null -ne $child) {try {if (-not $child.HasExited) {$child.Kill();$child.WaitForExit()}} finally {$child.Dispose()}}
                    if ($linkCreated) {Remove-Item -LiteralPath $resultPath -Force}
                }
                """);
            await RunProbeAsync(script, directory, scenario);
        }
        finally { Cleanup(prefix, directory); }
    }

    private static async Task RunProbeAsync(string script, string directory, string scenario)
    {
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string value in new[] { "-NoProfile", "-File", script, "-Policy", Policy(), "-Directory", directory, "-Scenario", scenario }) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new IOException("The installed diagnostic policy probe did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(stop.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        Assert.AreEqual(0, process.ExitCode, await output + await error);
    }

    private static void Cleanup(string prefix, string directory)
    {
        string target = Path.GetFullPath(directory);
        if (!target.StartsWith(prefix + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || new DirectoryInfo(target).LinkTarget is not null)
            throw new InvalidOperationException("Installed diagnostic cleanup requires its own ordinary temporary directory.");
        Directory.Delete(target, recursive: true);
    }

    private static string Policy()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string policy = Path.Combine(directory.FullName, "eng", "installed-startup-diagnostics.ps1");
            if (File.Exists(policy)) return policy;
        }
        throw new FileNotFoundException("The installed startup diagnostic policy is missing from this checkout.");
    }
}
