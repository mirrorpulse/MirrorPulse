using System.Diagnostics;
using System.Text.Json;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class CliIntegrationFailurePolicyTests
{
    [TestMethod]
    [DataRow("success")]
    [DataRow("primary")]
    [DataRow("cleanup")]
    [DataRow("both")]
    [DataRow("observer")]
    [DataRow("writer")]
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
                if ($Scenario -eq 'writer') {
                    $data=Join-Path $FixtureRoot 'data'
                    $evidence=Join-Path $FixtureRoot 'evidence/cli-regression.json'
                    New-Item -ItemType Directory -Path (Join-Path $data 'logs') -Force | Out-Null
                    'safe structured diagnostic' | Set-Content -LiteralPath (Join-Path $data 'logs/mirrorpulse.log')
                    try { throw [IO.IOException]::new("Original failure at $FixtureRoot/synthetic.txt") } catch { $original=$_ }
                    try { throw [ArgumentException]::new('Independent cleanup failure.') } catch { $secondary=$_ }
                    Write-CliFixtureFailure -Failure $original -Phase Execution -EvidencePath $evidence -FixtureRoot $FixtureRoot -DataRoot $data
                    Write-CliFixtureFailure -Failure $secondary -Phase Cleanup -EvidencePath $evidence -FixtureRoot $FixtureRoot -DataRoot $data
                    $originals=Join-Path $FixtureRoot 'evidence/cli-regression.originals'
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
