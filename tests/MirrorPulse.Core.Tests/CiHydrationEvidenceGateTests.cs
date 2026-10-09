using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class CiHydrationEvidenceGateTests
{
    [TestMethod]
    [DataRow("valid", true)]
    [DataRow("missing-hydration", false)]
    [DataRow("missing-count", false)]
    [DataRow("string-count", false)]
    [DataRow("partial", false)]
    [DataRow("duplicate-phase", false)]
    [DataRow("content-changed", false)]
    [DataRow("redundant-upload", false)]
    [DataRow("no-positive-control", false)]
    [DataRow("incomplete-local-write", false)]
    [DataRow("unknown-version", false)]
    public async Task CollectorRequiresCompleteReadsAndRetainedIntentCounts(string scenario, bool succeeds)
    {
        string repository = SftpProtocolFixture.FindRepositoryRoot();
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-hydration-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "host"));
        try
        {
            byte[] payload = "synthetic publish payload"u8.ToArray();
            string hash = Convert.ToHexStringLower(SHA256.HashData(payload));
            await File.WriteAllBytesAsync(Path.Combine(root, "mp.exe"), payload);
            await File.WriteAllBytesAsync(Path.Combine(root, "host", "MirrorPulse.Host.exe"), payload);
            await File.WriteAllTextAsync(Path.Combine(root, "publish-manifest.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                runtime = "win-arm64",
                files = new[] { new { path = "mp.exe", sha256 = hash, length = payload.Length },
                    new { path = "host/MirrorPulse.Host.exe", sha256 = hash, length = payload.Length } },
            }));
            string tests = Path.Combine(root, "tests.json");
            await File.WriteAllTextAsync(tests,
                """{"schemaVersion":1,"suite":"signed","selected":1,"executed":1,"skipped":0,"categories":[{"category":"signed","selected":1,"executed":1,"skipped":0}]}""");
            string integration = Path.Combine(root, "integration.json");
            await File.WriteAllTextAsync(integration, JsonSerializer.Serialize(CiHydrationEvidenceFixture.Create(scenario)));
            string evidence = Path.Combine(root, "evidence.json");
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-File", Path.Combine(repository, "eng", "collect-ci-evidence.ps1"),
                "-Job", "official-package-arm64", "-Runtime", "win-arm64", "-OutputPath", evidence,
                "-TestManifests", tests, "-PublishDirectory", root, "-IntegrationPath", integration })
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                Assert.AreEqual(succeeds, process.ExitCode == 0, await output + await error);
                Assert.AreEqual(succeeds, File.Exists(evidence));
                if (succeeds)
                {
                    string text = await File.ReadAllTextAsync(evidence);
                    Assert.DoesNotContain(root, text);
                    using JsonDocument report = JsonDocument.Parse(text);
                    JsonElement hydration = report.RootElement.GetProperty("checks")[0].GetProperty("hydration");
                    Assert.AreEqual(4, hydration.GetProperty("reads").GetArrayLength());
                    Assert.AreEqual(2, hydration.GetProperty("audits").GetArrayLength());
                    Assert.AreEqual(1, hydration.GetProperty("audits")[1].GetProperty("acknowledgedQueuedFileMutations").GetInt32());
                }
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
