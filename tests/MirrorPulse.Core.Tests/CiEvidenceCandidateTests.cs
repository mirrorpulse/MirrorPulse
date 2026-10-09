using System.Diagnostics;
using System.Text.Json;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class CiEvidenceCandidateTests
{
    [TestMethod]
    [DataRow("shared", true)]
    [DataRow("different", false)]
    [DataRow("missing-one", false)]
    [DataRow("missing-all", false)]
    [DataRow("invalid", false)]
    [DataRow("artifact-mismatch", false)]
    [DataRow("duplicate-artifact", false)]
    [DataRow("legacy", true)]
    [DataRow("hydration-missing-hydration", false)]
    [DataRow("hydration-missing-count", false)]
    [DataRow("hydration-string-count", false)]
    [DataRow("hydration-partial", false)]
    [DataRow("hydration-duplicate-phase", false)]
    [DataRow("hydration-content-changed", false)]
    [DataRow("hydration-redundant-upload", false)]
    [DataRow("hydration-no-positive-control", false)]
    [DataRow("hydration-incomplete-local-write", false)]
    public async Task ThreeJobsMustBindTheSameOfficialCandidate(string scenario, bool succeeds)
    {
        string repository = SftpProtocolFixture.FindRepositoryRoot();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-candidate-evidence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = new('a', 40);
        string shared = new('b', 64);
        try
        {
            foreach (string job in new[] { "build-and-test", "official-package-arm64", "official-adapters" })
            {
                bool arm = job == "official-package-arm64";
                string suite = arm ? "signed" : job == "official-adapters" ? "official" : "managed";
                string? hash = scenario is "missing-all" or "legacy" || (scenario == "missing-one" && arm) ? null : shared;
                if (arm && scenario == "different") hash = new('c', 64);
                if (arm && scenario == "invalid") hash = "not-a-digest";
                var artifacts = new List<object>
                {
                    new { path = "cli-host/mp.exe", length = 1, sha256 = shared },
                };
                if (hash is not null)
                {
                    var candidate = new
                    {
                        path = "official-candidate/official-adapter-releases.json",
                        length = 7,
                        sha256 = arm && scenario == "artifact-mismatch" ? new string('d', 64) : hash,
                    };
                    artifacts.Add(candidate);
                    if (arm && scenario == "duplicate-artifact") artifacts.Add(candidate);
                }
                object[] checks = arm
                    ? [CiHydrationEvidenceFixture.Create(scenario.StartsWith("hydration-", StringComparison.Ordinal) ? scenario["hydration-".Length..] : "valid")]
                    : job == "build-and-test"
                        ? [new { name = "unsupported-server", executed = true, cliRejected = true, hostRejected = true, stateUntouched = true, osProductType = 3 }]
                        : [];
                await File.WriteAllTextAsync(Path.Combine(directory, job + ".json"), JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    sourceSha = source,
                    job,
                    runtime = arm ? "win-arm64" : "win-x64",
                    officialAdapterCandidateSha256 = hash,
                    tests = new[] { new { suite, selected = 1, executed = 1, skipped = 0 } },
                    checks,
                    artifacts,
                }));
            }
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in new[] { "-NoProfile", "-File", Path.Combine(repository, "eng", "verify-ci-evidence.ps1"),
                "-EvidenceDirectory", directory, "-ExpectedSourceSha", source })
                start.ArgumentList.Add(argument);
            if (scenario != "legacy") start.ArgumentList.Add("-RequireOfficialCandidate");
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.AreEqual(succeeds, process.ExitCode == 0, await output + await error);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
