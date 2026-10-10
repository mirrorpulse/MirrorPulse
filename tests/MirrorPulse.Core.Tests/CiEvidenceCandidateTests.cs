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
    [DataRow("namespace-valid", true)]
    [DataRow("namespace-missing", false)]
    [DataRow("namespace-partial", false)]
    [DataRow("nonadmin-valid", true)]
    [DataRow("nonadmin-missing", false)]
    [DataRow("nonadmin-administrator", false)]
    [DataRow("nonadmin-string-boolean", false)]
    [DataRow("nonadmin-partial", false)]
    [DataRow("nonadmin-wrong-source", false)]
    [DataRow("nonadmin-wrong-architecture", false)]
    [DataRow("nonadmin-context-mismatch", false)]
    [DataRow("nonadmin-missing-trx", false)]
    [DataRow("nonadmin-not-cleaned", false)]
    [DataRow("nonadmin-no-payload-digest", false)]
    [DataRow("nonadmin-duplicate", false)]
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
                if (arm && scenario.StartsWith("nonadmin-", StringComparison.Ordinal) && scenario != "nonadmin-missing")
                {
                    var observations = await CreateOrdinaryUserObservationsAsync(repository, scenario, source, shared);
                    var check = new { name = "namespace-nonadmin", executed = true, observations };
                    checks = [.. checks, check];
                    if (scenario == "nonadmin-duplicate") checks = [.. checks, check];
                    artifacts.Add(new
                    {
                        path = "namespace-nonadmin/context.json",
                        length = 10,
                        sha256 = scenario == "nonadmin-context-mismatch" ? new string('c', 64) : shared
                    });
                    if (scenario != "nonadmin-missing-trx")
                        artifacts.Add(new { path = "namespace-nonadmin/namespace.trx", length = 10, sha256 = shared });
                }
                var tests = new List<object> { new { suite, selected = 1, executed = 1, skipped = 0 } };
                if (arm && scenario.StartsWith("namespace-", StringComparison.Ordinal) && scenario != "namespace-missing")
                {
                    using JsonDocument catalog = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repository, "eng", "test-suites.json")));
                    string[] required = catalog.RootElement.GetProperty("required").GetProperty("namespace").EnumerateArray().Select(item => item.GetString()!).ToArray();
                    string[] native = catalog.RootElement.GetProperty("required").GetProperty("native").EnumerateArray().Select(item => item.GetString()!).ToArray();
                    int nativeCount = required.Count(native.Contains) - (scenario == "namespace-partial" ? 1 : 0);
                    int managedCount = required.Count(name => !native.Contains(name));
                    tests.Add(new
                    {
                        suite = "namespace",
                        selected = nativeCount + managedCount,
                        executed = nativeCount + managedCount,
                        skipped = 0,
                        categories = new[] { new { category = "native", selected = nativeCount, executed = nativeCount, skipped = 0 },
                            new { category = "managed", selected = managedCount, executed = managedCount, skipped = 0 } },
                    });
                }
                await File.WriteAllTextAsync(Path.Combine(directory, job + ".json"), JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    sourceSha = source,
                    job,
                    runtime = arm ? "win-arm64" : "win-x64",
                    officialAdapterCandidateSha256 = hash,
                    tests,
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
            if (scenario.StartsWith("namespace-", StringComparison.Ordinal)) start.ArgumentList.Add("-RequireNamespace");
            if (scenario.StartsWith("nonadmin-", StringComparison.Ordinal)) start.ArgumentList.Add("-RequireNonAdminNamespace");
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.AreEqual(succeeds, process.ExitCode == 0, await output + await error);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task<Dictionary<string, object>> CreateOrdinaryUserObservationsAsync(string repository,
        string scenario, string source, string hash)
    {
        using JsonDocument catalog = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repository, "eng", "test-suites.json")));
        string[] required = catalog.RootElement.GetProperty("required").GetProperty("namespace").EnumerateArray().Select(item => item.GetString()!).ToArray();
        string[] native = catalog.RootElement.GetProperty("required").GetProperty("native").EnumerateArray().Select(item => item.GetString()!).ToArray();
        int nativeCount = required.Count(native.Contains) - (scenario == "nonadmin-partial" ? 1 : 0);
        int managedCount = required.Count(name => !native.Contains(name));
        return new()
        {
            ["schemaVersion"] = 1,
            ["sourceSha"] = scenario == "nonadmin-wrong-source" ? new string('c', 40) : source,
            ["architecture"] = scenario == "nonadmin-wrong-architecture" ? "X64" : "Arm64",
            ["nonAdministrator"] = scenario == "nonadmin-string-boolean" ? "true" : scenario != "nonadmin-administrator",
            ["freshUser"] = true,
            ["expectedUserMatched"] = true,
            ["profileLoaded"] = true,
            ["accountRemoved"] = scenario != "nonadmin-not-cleaned",
            ["profileRemoved"] = true,
            ["workspaceRemoved"] = true,
            ["testAssemblySha256"] = scenario == "nonadmin-no-payload-digest" ? "missing" : hash,
            ["contextSha256"] = hash,
            ["trxSha256"] = hash,
            ["tests"] = new
            {
                suite = "namespace",
                selected = nativeCount + managedCount,
                executed = nativeCount + managedCount,
                skipped = 0,
                categories = new[] { new { category = "native", selected = nativeCount, executed = nativeCount, skipped = 0 },
                    new { category = "managed", selected = managedCount, executed = managedCount, skipped = 0 } },
            },
        };
    }
}
