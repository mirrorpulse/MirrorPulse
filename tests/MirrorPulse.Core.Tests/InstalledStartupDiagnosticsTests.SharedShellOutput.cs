namespace MirrorPulse.Core.Tests;

public sealed partial class InstalledStartupDiagnosticsTests
{
    [TestMethod]
    [TestCategory("SharedShellOutput")]
    public async Task ShellProbeLaunchSuppliesItsResultEnvironmentToTheActualObservedChild()
    {
        await RunSharedShellOutputProbeAsync("environment", """
            $executable=Join-Path $Directory 'app with spaces.exe'
            $start=New-InstalledShellProbeStartInfo -ExecutablePath $executable -ResultPath $resultPath
            if ($start.FileName -cne $executable -or $start.UseShellExecute -or -not $start.CreateNoWindow -or
                $start.WindowStyle -ne [Diagnostics.ProcessWindowStyle]::Hidden -or
                $start.Environment['MIRRORPULSE_PACKAGED_SHELL_PROBE_RESULT'] -cne $resultPath -or
                $start.ArgumentList.Count -ne 0 -or $start.Arguments.Length -ne 0) {
                throw 'The observed executable must receive its exact private output environment without shell activation or command interpolation.'
            }
            if (Test-Path -LiteralPath $resultPath) { throw 'Constructing launch information cannot report a probe result.' }
            """);
    }

    [TestMethod]
    [TestCategory("SharedShellOutput")]
    public async Task PrecreatedShellOutputIsIncompleteUntilTheProducerWritesAnActualResult()
    {
        await RunSharedShellOutputProbeAsync("initial", """
            Initialize-InstalledShellProbeOutput -ResultPath $resultPath
            if ((Get-Item -LiteralPath $resultPath).Length -ne 0 -or (Get-Item -LiteralPath ($resultPath+'.progress')).Length -ne 0) {
                throw 'Output preparation must not invent either result or progress.'
            }
            function Start-Sleep { throw 'An expired original deadline cannot wait again.' }
            $observed=Wait-InstalledShellProbeResult -Process $null -ResultPath $resultPath -DeadlineUtc ([DateTime]::UtcNow.AddSeconds(-1))
            if ($observed.resultState -cne 'Invalid' -or $observed.stageState -cne 'Invalid' -or
                $observed.processState -cne 'Unavailable' -or $null -ne $observed.exitCode -or $null -ne $observed.failureHResult) {
                throw 'The two empty real files are not successful completion.'
            }
            """);
    }

    [TestMethod]
    [TestCategory("SharedShellOutput")]
    [DataRow("result")]
    [DataRow("progress")]
    public async Task ShellOutputPreparationNeverOverwritesAnExistingResultOrProgress(string occupied)
    {
        await RunSharedShellOutputProbeAsync(occupied, """
            $occupied=if($Scenario -ceq 'result'){$resultPath}else{$resultPath+'.progress'}
            [IO.File]::WriteAllText($occupied,'PRIVATE_TEST_SENTINEL')
            $rejected=$false
            try { Initialize-InstalledShellProbeOutput -ResultPath $resultPath }
            catch [IO.IOException] { $rejected=$true }
            if (-not $rejected -or [IO.File]::ReadAllText($occupied) -cne 'PRIVATE_TEST_SENTINEL') {
                throw 'A new disposable probe must not replace an existing output.'
            }
            $observed=Get-InstalledShellProbeObservation -Process $null -ResultPath $resultPath
            if ($observed.resultState -ceq 'Success') { throw 'Rejected preparation cannot fabricate success.' }
            """);
    }

    [TestMethod]
    [TestCategory("SharedShellOutput")]
    [DataRow("empty")]
    [DataRow("partial")]
    [DataRow("success")]
    [DataRow("failure")]
    [DataRow("oversized")]
    public async Task ShellWaitUsesBoundedTerminalContentAndTheExistingDeadline(string content)
    {
        await RunSharedShellOutputProbeAsync(content, """
            Initialize-InstalledShellProbeOutput -ResultPath $resultPath
            switch ($Scenario) {
                'partial' {[IO.File]::WriteAllText($resultPath,'succ')}
                'success' {[IO.File]::WriteAllText($resultPath,'success')}
                'failure' {[IO.File]::WriteAllText($resultPath,'failure: 0x80070005 PRIVATE_TEST_SENTINEL')}
                'oversized' {[IO.File]::WriteAllBytes($resultPath,[byte[]]::new(4097))}
            }
            function Start-Sleep { throw 'An expired original deadline cannot wait again.' }
            $observed=Wait-InstalledShellProbeResult -Process $null -ResultPath $resultPath -DeadlineUtc ([DateTime]::UtcNow.AddSeconds(-1))
            $expected=switch($Scenario){'success'{'Success'};'failure'{'Failure'};'oversized'{'TooLarge'};default{'Invalid'}}
            if ($observed.resultState -cne $expected -or $observed.stageState -cne 'Invalid' -or
                $observed.processState -cne 'Unavailable' -or $null -ne $observed.exitCode -or
                ($Scenario -ceq 'failure' -and $observed.failureHResult -cne '80070005') -or
                ($Scenario -cne 'failure' -and $null -ne $observed.failureHResult)) {
                throw 'Only the independently parsed actual result may terminate successfully.'
            }
            if (($observed|ConvertTo-Json -Compress).Contains('PRIVATE_TEST_SENTINEL')) { throw 'The observer cannot expose arbitrary probe text.' }
            """);
    }

    [TestMethod]
    [TestCategory("SharedShellOutput")]
    public async Task ShellWaitDoesNotReturnAtEmptyFileExistenceBeforeTheChildWritesItsActualResult()
    {
        await RunSharedShellOutputProbeAsync("child", """
            Initialize-InstalledShellProbeOutput -ResultPath $resultPath
            $childScript=Join-Path $Directory 'child.ps1'
            [IO.File]::WriteAllText($childScript,@'
            $ResultPath=$env:MIRRORPULSE_PACKAGED_SHELL_PROBE_RESULT
            if ([string]::IsNullOrWhiteSpace($ResultPath)) { throw 'The observed child did not receive its probe output environment.' }
            Start-Sleep -Milliseconds 100
            [IO.File]::WriteAllText($ResultPath+'.progress','1:ProbeEntered')
            [IO.File]::WriteAllText($ResultPath,'success')
            '@)
            $start=New-InstalledShellProbeStartInfo -ExecutablePath 'pwsh' -ResultPath $resultPath
            foreach($arg in @('-NoProfile','-File',$childScript)){$start.ArgumentList.Add($arg)}
            $child=[Diagnostics.Process]::Start($start)
            try {
                $observed=Wait-InstalledShellProbeResult -Process $child -ResultPath $resultPath -DeadlineUtc ([DateTime]::UtcNow.AddSeconds(20))
                if ($observed.resultState -cne 'Success' -or $observed.stageState -cne 'Observed' -or $observed.stage -cne 'ProbeEntered') {
                    throw 'A real child result must replace the precreated incomplete files before successful completion.'
                }
            } finally {
                if (-not $child.HasExited) { $child.WaitForExit(1000)|Out-Null }
                if (-not $child.HasExited) { $child.Kill($true);$child.WaitForExit() }
                $child.Dispose()
            }
            """);
    }

    private static async Task RunSharedShellOutputProbeAsync(string scenario, string body)
    {
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-installed-diagnostics-tests"));
        string directory = Path.Combine(prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "verify.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Policy,[string]$Directory,[string]$Scenario)
                $ErrorActionPreference='Stop'
                . (Join-Path (Split-Path -Parent $Policy) 'installed-shell-observations.ps1')
                $resultPath=Join-Path $Directory 'result'

                """ + body);
            await RunProbeAsync(script, directory, scenario);
        }
        finally { Cleanup(prefix, directory); }
    }
}
