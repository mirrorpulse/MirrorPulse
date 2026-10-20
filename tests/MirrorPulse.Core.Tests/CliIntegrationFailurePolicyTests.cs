using System.Diagnostics;
using System.Text.Json;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class CliIntegrationFailurePolicyTests
{
    private static readonly string[] AnchorArguments = ["--json", "--no-start", "namespace", "member", "inspect", "", "--directory"];

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompletedExecutionObservationsSurviveCleanupFailureWithoutClaimingFinalSuccess(bool failCleanup)
    {
        string repository = SftpProtocolFixture.FindRepositoryRoot();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-completed-execution-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "exercise.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Repository,[string]$Fixture,[string]$FailCleanup)
                $ErrorActionPreference='Stop'
                . (Join-Path $Repository 'eng/cli-integration-failure-policy.ps1')
                $evidence=Join-Path $Fixture 'evidence/cli-regression.json'
                $data=Join-Path $Fixture 'data'
                $sentinel=Join-Path $Fixture 'retained.txt'
                'original fixture bytes' | Set-Content -LiteralPath $sentinel
                Write-CliFixtureExecutionObservations -EvidencePath $evidence -HydrationReads @([ordered]@{phase='Online';wholeFileReads=6;rangeReads=21}) `
                    -HydrationAudits @([ordered]@{phase='AfterLocalWrite';queuedFileMutations=1;acknowledgedQueuedFileMutations=1}) `
                    -NamespaceAudits @([ordered]@{phase='ReenabledRoot';freshAdmission=$true})
                $original=Join-Path $Fixture 'evidence/cli-regression.originals/completed-execution.json'
                $before=(Get-FileHash -LiteralPath $original).Hash
                $caught=$null
                try {
                    Invoke-CliFixtureTeardown -PrimaryFailure $null -Cleanup {
                        if ($FailCleanup -ceq 'True') { throw [IO.IOException]::new('Independent cleanup failure.') }
                    } -RecordFailure {
                        param($failure)
                        Write-CliFixtureFailure -Failure $failure -Phase Cleanup -EvidencePath $evidence -FixtureRoot $Fixture -DataRoot $data
                    }
                    [ordered]@{executionCompleted=$true;teardownCompleted=$true} | ConvertTo-Json | Set-Content -LiteralPath $evidence
                } catch { $caught=$_.Exception.Message }
                [ordered]@{caught=$caught;finalExists=(Test-Path -LiteralPath $evidence);unchanged=($before -ceq (Get-FileHash -LiteralPath $original).Hash)
                    sentinel=(Get-Content -Raw -LiteralPath $sentinel).Trim();completed=(Get-Content -Raw -LiteralPath $original | ConvertFrom-Json)
                    cleanupExists=(Test-Path -LiteralPath (Join-Path $Fixture 'evidence/cli-regression.originals/cleanup.json'))} | ConvertTo-Json -Compress -Depth 8
                """);
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-File", script, repository, directory, failCleanup.ToString() }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            Assert.AreEqual(0, process.ExitCode, await error);
            using var json = JsonDocument.Parse((await output).Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Trim());
            var result = json.RootElement;
            Assert.AreEqual(failCleanup ? "Independent cleanup failure." : null, result.GetProperty("caught").GetString());
            Assert.AreEqual(!failCleanup, result.GetProperty("finalExists").GetBoolean()); Assert.AreEqual(failCleanup, result.GetProperty("cleanupExists").GetBoolean());
            Assert.IsTrue(result.GetProperty("unchanged").GetBoolean()); Assert.AreEqual("original fixture bytes", result.GetProperty("sentinel").GetString());
            var completed = result.GetProperty("completed");
            Assert.IsTrue(completed.GetProperty("executionCompleted").GetBoolean()); Assert.IsFalse(completed.GetProperty("teardownCompleted").GetBoolean());
            Assert.AreEqual(6, completed.GetProperty("hydration").GetProperty("reads")[0].GetProperty("wholeFileReads").GetInt32());
            Assert.AreEqual(1, completed.GetProperty("hydration").GetProperty("audits")[0].GetProperty("acknowledgedQueuedFileMutations").GetInt32());
            Assert.IsTrue(completed.GetProperty("namespace").GetProperty("audits")[0].GetProperty("freshAdmission").GetBoolean());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DataRow("success")]
    [DataRow("primary")]
    [DataRow("cleanup")]
    [DataRow("both")]
    [DataRow("observer")]
    [DataRow("writer")]
    [DataRow("empty-argument")]
    [DataRow("lifecycle")]
    [DataRow("lifecycle-observer-failure")]
    public async Task TeardownPreservesTheOriginalFailureAndRecordsItsIndependentCleanupFailure(string scenario)
    {
        string repository = SftpProtocolFixture.FindRepositoryRoot();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-failure-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "exercise.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Repository, [string]$Scenario, [string]$FixtureRoot)
                $ErrorActionPreference='Stop'
                . (Join-Path $Repository 'eng/cli-integration-failure-policy.ps1')
                if ($Scenario -eq 'empty-argument') {
                    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $Repository 'eng/verify-cli-integration.ps1'),[ref]$null,[ref]$null)
                    $function=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-MirrorPulseCli'},$true)
                    . ([scriptblock]::Create($function.Extent.Text))
                    $consumer=Join-Path $FixtureRoot 'argument-consumer.ps1'
                    '[ordered]@{kind="arguments";data=@($args)} | ConvertTo-Json -Compress' | Set-Content -LiteralPath $consumer
                    $script:cliExecutable=(Get-Process -Id $PID).Path
                    $result=Invoke-MirrorPulseCli @('-NoProfile','-File',$consumer,'--json','--no-start','namespace','member','inspect','','--directory')
                    $result | ConvertTo-Json -Compress
                    return
                }
                if ($Scenario -in @('writer','lifecycle','lifecycle-observer-failure')) {
                    $data=Join-Path $FixtureRoot 'data'
                    $evidence=Join-Path $FixtureRoot 'evidence/cli-regression.json'
                    New-Item -ItemType Directory -Path (Join-Path $data 'logs') -Force | Out-Null
                    'safe structured diagnostic' | Set-Content -LiteralPath (Join-Path $data 'logs/mirrorpulse.log')
                    try { throw [IO.IOException]::new("Original failure at $FixtureRoot/synthetic.txt") } catch { $original=$_ }
                    try { throw [ArgumentException]::new('Independent cleanup failure.') } catch { $secondary=$_ }
                    Write-CliFixtureFailure -Failure $original -Phase Execution -EvidencePath $evidence -FixtureRoot $FixtureRoot -DataRoot $data
                    Write-CliFixtureFailure -Failure $secondary -Phase Cleanup -EvidencePath $evidence -FixtureRoot $FixtureRoot -DataRoot $data
                    $originals=Join-Path $FixtureRoot 'evidence/cli-regression.originals'
                    if ($Scenario -ne 'writer') {
                        Write-CliFixtureStartupObservations -EvidencePath $evidence -HydrationReads @([ordered]@{phase='Online';binarySha256='verified-fixture'}) -ReadLifecycle {
                            if ($Scenario -eq 'lifecycle-observer-failure') { throw [IO.IOException]::new('private-password-needle') }
                            [ordered]@{kind='result';data=[ordered]@{state='Starting';startupPhase='CloudFiles';processId=42;privateField='private-password-needle'}}
                        }
                        [ordered]@{execution=(Get-Content -Raw -LiteralPath (Join-Path $originals 'execution.json') | ConvertFrom-Json)
                            lifecycle=(Get-Content -Raw -LiteralPath (Join-Path $originals 'lifecycle.json') | ConvertFrom-Json)
                            partial=(Get-Content -Raw -LiteralPath (Join-Path $originals 'partial-observations.json') | ConvertFrom-Json)} | ConvertTo-Json -Compress -Depth 8
                        return
                    }
                    [ordered]@{
                        execution=(Get-Content -Raw -LiteralPath (Join-Path $originals 'execution.json') | ConvertFrom-Json)
                        cleanup=(Get-Content -Raw -LiteralPath (Join-Path $originals 'cleanup.json') | ConvertFrom-Json)
                        log=(Get-Content -Raw -LiteralPath (Join-Path $originals 'execution-logs-mirrorpulse.log'))
                    } | ConvertTo-Json -Compress -Depth 5
                    return
                }
                $events=[Collections.Generic.List[string]]::new()
                $primary=$null; $caught=$null
                try {
                    try {
                        $events.Add('execute')
                        if ($Scenario -in @('primary','both','observer')) { throw [IO.IOException]::new('Original execution failure.') }
                    } catch { $primary=$_; throw }
                    finally {
                        Invoke-CliFixtureTeardown -PrimaryFailure $primary -Cleanup {
                            $events.Add('cleanup')
                            if ($Scenario -in @('cleanup','both','observer')) { throw [InvalidOperationException]::new('Independent cleanup failure.') }
                        } -RecordFailure {
                            param($failure)
                            $events.Add($failure.Exception.Message)
                            if ($Scenario -eq 'observer') { throw [IO.IOException]::new('Observer failure.') }
                        }
                    }
                } catch { $caught=$_.Exception.Message }
                [ordered]@{caught=$caught;events=$events.ToArray()} | ConvertTo-Json -Compress
                """);
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-File", script, repository, scenario, directory }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            Assert.AreEqual(0, process.ExitCode, await error);
            string jsonLine = (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Trim();
            using var json = JsonDocument.Parse(jsonLine);
            if (scenario == "empty-argument")
            {
                Assert.AreEqual("arguments", json.RootElement.GetProperty("kind").GetString());
                var arguments = json.RootElement.GetProperty("data").EnumerateArray().Select(value => value.GetString()).ToArray();
                CollectionAssert.AreEqual(AnchorArguments, arguments);
                return;
            }
            if (scenario == "writer")
            {
                Assert.AreEqual("Execution", json.RootElement.GetProperty("execution").GetProperty("phase").GetString());
                Assert.AreEqual("Cleanup", json.RootElement.GetProperty("cleanup").GetProperty("phase").GetString());
                Assert.IsFalse(jsonLine.Contains(directory.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
                Assert.IsFalse(json.RootElement.GetProperty("execution").GetProperty("message").GetString()!.Contains(directory, StringComparison.OrdinalIgnoreCase));
                foreach (string field in new[] { "message", "errorId", "scriptStack" })
                    Assert.IsFalse(json.RootElement.GetProperty("execution").GetProperty(field).GetString()!.Contains(directory, StringComparison.OrdinalIgnoreCase));
                StringAssert.Contains(json.RootElement.GetProperty("execution").GetProperty("message").GetString()!, "<fixture>");
                StringAssert.Contains(json.RootElement.GetProperty("log").GetString()!, "safe structured diagnostic");
                return;
            }
            if (scenario is "lifecycle" or "lifecycle-observer-failure")
            {
                StringAssert.Contains(json.RootElement.GetProperty("execution").GetProperty("message").GetString()!, "Original failure");
                Assert.IsFalse(jsonLine.Contains("private-password-needle", StringComparison.Ordinal));
                var partial = json.RootElement.GetProperty("partial");
                Assert.IsTrue(partial.GetProperty("partial").GetBoolean());
                Assert.AreEqual(1, partial.GetProperty("hydration").GetProperty("reads").GetArrayLength());
                Assert.AreEqual(0, partial.GetProperty("namespace").GetProperty("audits").GetArrayLength());
                var lifecycle = json.RootElement.GetProperty("lifecycle");
                if (scenario == "lifecycle")
                {
                    Assert.AreEqual("Starting", lifecycle.GetProperty("state").GetString());
                    Assert.AreEqual("CloudFiles", lifecycle.GetProperty("startupPhase").GetString());
                    Assert.AreEqual(42, lifecycle.GetProperty("processId").GetInt32());
                }
                else Assert.IsTrue(lifecycle.GetProperty("observationUnavailable").GetBoolean());
                return;
            }
            string? actual = json.RootElement.GetProperty("caught").GetString();
            Assert.AreEqual(scenario is "primary" or "both" or "observer" ? "Original execution failure." :
                scenario == "cleanup" ? "Independent cleanup failure." : null, actual);
            var events = json.RootElement.GetProperty("events").EnumerateArray().Select(entry => entry.GetString()).ToArray();
            Assert.AreEqual("execute", events[0]); Assert.AreEqual("cleanup", events[1]);
            Assert.HasCount(scenario is "cleanup" or "both" or "observer" ? 3 : 2, events);
            if (events.Length == 3) Assert.AreEqual("Independent cleanup failure.", events[2]);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
