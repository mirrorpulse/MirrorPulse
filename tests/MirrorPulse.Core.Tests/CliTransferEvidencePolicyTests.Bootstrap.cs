using System.Diagnostics;
using System.Text.Json;

namespace MirrorPulse.Core.Tests;

public sealed partial class CliTransferEvidencePolicyTests
{
    [TestMethod]
    [DataRow("prepare")]
    [DataRow("verify")]
    [DataRow("missing-report")]
    [DataRow("wrong-source")]
    [DataRow("wrong-runtime")]
    [DataRow("wrong-run")]
    [DataRow("wrong-shape")]
    [DataRow("corrupt-report")]
    [DataRow("observer-failure")]
    [DataRow("previous-failure")]
    [DataRow("stale-report")]
    [DataRow("success")]
    public async Task CrossPackageBootstrapPreservesSourceBoundFailuresAndRequiresTheActualCompleteReport(string scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-cross-bootstrap-" + Id()); Directory.CreateDirectory(root);
        try
        {
            var evidence = CrossPackageEvidence(); evidence["sourceSha"] = new string('1', 40); evidence["runId"] = "12345";
            string input = Path.Combine(root, "input.json"), script = Path.Combine(root, "exercise.ps1");
            await File.WriteAllTextAsync(input, evidence.ToJsonString());
            await File.WriteAllTextAsync(script, """
                param([string]$Repository,[string]$Fixture,[string]$Scenario)
                $ErrorActionPreference='Stop'
                . (Join-Path $Repository 'eng/cli-cross-package-verification-policy.ps1')
                $evidence=Join-Path $Fixture 'evidence/cli-cross-package.json'
                $failure=Join-Path $Fixture 'evidence/cli-cross-package.originals/verification-failure.json'
                New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($failure)) | Out-Null
                if ($Scenario -ceq 'observer-failure') { New-Item -ItemType Directory -Path $failure | Out-Null }
                if ($Scenario -ceq 'previous-failure') { 'original immutable failure' | Set-Content -LiteralPath $failure }
                if ($Scenario -ceq 'stale-report') { Copy-Item -LiteralPath (Join-Path $Fixture 'input.json') -Destination $evidence }
                $events=[Collections.Generic.List[string]]::new()
                $caught=$null; $accepted=$false
                try {
                    Invoke-CliCrossPackageVerification -SourceSha ('1'*40) -RunId '12345' -Runtime win-arm64 -EvidencePath $evidence -Prepare {
                        $events.Add('prepare')
                        if ($Scenario -cin @('prepare','observer-failure','previous-failure')) { throw [IO.IOException]::new('PRIVATE_TOKEN PRIVATE_PATH') }
                        [pscustomobject]@{local='synthetic-local';webdav='synthetic-webdav'}
                    } -Verify {
                        param($packages)
                        $events.Add('verify')
                        if ($packages.local -cne 'synthetic-local' -or $packages.webdav -cne 'synthetic-webdav') { throw 'Lost original preparation output.' }
                        if ($Scenario -ceq 'verify') { throw [IO.IOException]::new('PRIVATE_TOKEN PRIVATE_PATH') }
                        if ($Scenario -ceq 'missing-report') { return }
                        $report=Get-Content -Raw -LiteralPath (Join-Path $Fixture 'input.json') | ConvertFrom-Json
                        if ($Scenario -ceq 'wrong-source') { $report.sourceSha='2'*40 }
                        if ($Scenario -ceq 'wrong-runtime') { $report.runtime='win-x64' }
                        if ($Scenario -ceq 'wrong-run') { $report.runId='12346' }
                        if ($Scenario -ceq 'wrong-shape') { $report.schemaVersion=2 }
                        if ($Scenario -ceq 'corrupt-report') { $report.audit.transfers[0].privateBytesVerified=$false }
                        $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $evidence
                    }
                    $accepted=$true
                } catch { $caught=$_.Exception.Message }
                $record=if(Test-Path -LiteralPath $failure -PathType Leaf){Get-Content -Raw -LiteralPath $failure}else{$null}
                [ordered]@{accepted=$accepted;caught=$caught;events=$events.ToArray();failure=$record;
                    reportPresent=(Test-Path -LiteralPath $evidence -PathType Leaf)} | ConvertTo-Json -Compress -Depth 6
                """);
            var start = new ProcessStartInfo("pwsh")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-File", script, SftpProtocolFixture.FindRepositoryRoot(), root, scenario }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            Assert.AreEqual(0, process.ExitCode, await error);
            using var json = JsonDocument.Parse((await output).Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Trim());
            var observed = json.RootElement; bool success = scenario == "success";
            Assert.AreEqual(success, observed.GetProperty("accepted").GetBoolean(), observed.ToString());
            var events = observed.GetProperty("events").EnumerateArray().Select(value => value.GetString()!).ToArray();
            string[] expectedEvents = scenario == "stale-report" ? [] :
                scenario is "prepare" or "observer-failure" or "previous-failure" ? ["prepare"] : ["prepare", "verify"];
            CollectionAssert.AreEqual(expectedEvents, events);
            if (scenario is "prepare" or "verify" or "observer-failure" or "previous-failure")
                Assert.AreEqual("PRIVATE_TOKEN PRIVATE_PATH", observed.GetProperty("caught").GetString());
            if (success || scenario == "observer-failure") Assert.AreEqual(JsonValueKind.Null, observed.GetProperty("failure").ValueKind);
            else if (scenario == "previous-failure") Assert.AreEqual("original immutable failure", observed.GetProperty("failure").GetString()!.Trim());
            else
            {
                string record = observed.GetProperty("failure").GetString()!;
                Assert.IsFalse(record.Contains("PRIVATE", StringComparison.Ordinal));
                using var failure = JsonDocument.Parse(record); var fields = failure.RootElement;
                Assert.AreEqual(new string('1', 40), fields.GetProperty("sourceSha").GetString());
                Assert.AreEqual("12345", fields.GetProperty("runId").GetString());
                Assert.AreEqual("win-arm64", fields.GetProperty("runtime").GetString()); Assert.IsFalse(fields.GetProperty("accepted").GetBoolean());
                string expectedPhase = scenario is "prepare" or "stale-report" ? "PrepareSignedPackages" :
                    scenario == "verify" ? "VerifyActualTransfers" : "VerifyOriginalReport";
                Assert.AreEqual(expectedPhase, fields.GetProperty("phase").GetString());
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CrossPackageBootstrapRequiresExplicitNativeEnvironmentBeforePackagePreparation(bool unset)
    {
        var start = new ProcessStartInfo("pwsh")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (unset) start.Environment.Remove("MIRRORPULSE_NATIVE_TEST"); else start.Environment["MIRRORPULSE_NATIVE_TEST"] = "0";
        foreach (string argument in new[] { "-NoProfile", "-File", Path.Combine(SftpProtocolFixture.FindRepositoryRoot(), "eng", "verify-cli-cross-package.ps1"),
            "-ReleaseLockPath", "not-read.json", "-LocalPackageDirectory", "not-read-local", "-WebDavPackageDirectory", "not-read-webdav", "-EvidencePath", "not-created.json" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        Assert.AreNotEqual(0, process.ExitCode);
        StringAssert.Contains(await output + await error, "explicitly enabled disposable native test environment");
    }
}
