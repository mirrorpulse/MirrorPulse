using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class CiEvidenceGateTests
{
    private static readonly string[] OfficialProviders = ["local", "webdav", "smb", "ftp", "sftp"];

    [TestMethod]
    [DataRow("valid", true)]
    [DataRow("changed", false)]
    [DataRow("traversal", false)]
    [DataRow("installed-valid", true)]
    [DataRow("installed-server", false)]
    [DataRow("installed-old-build", false)]
    [DataRow("native-valid", true)]
    [DataRow("native-missing", false)]
    [DataRow("candidate-valid", true)]
    [DataRow("candidate-source-mismatch", false)]
    [DataRow("candidate-invalid-inventory", false)]
    public async Task EvidenceVerifiesArtifactBytesAndRejectsUnsafePaths(string scenario, bool succeeds)
    {
        string repository = SftpProtocolFixture.FindRepositoryRoot();
        string root = Path.Combine(Path.GetTempPath(), $"MirrorPulse-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "host"));
        try
        {
            byte[] bytes = "fixture-payload"u8.ToArray();
            string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            await File.WriteAllBytesAsync(Path.Combine(root, "mp.exe"), bytes);
            await File.WriteAllBytesAsync(Path.Combine(root, "host", "MirrorPulse.Host.exe"), bytes);
            string cliPath = scenario == "traversal" ? "../mp.exe" : "mp.exe";
            var publish = new
            {
                schemaVersion = 1,
                runtime = "win-x64",
                files = new[] { new { path = cliPath, sha256 = hash, length = bytes.Length },
                    new { path = "host/MirrorPulse.Host.exe", sha256 = hash, length = bytes.Length } },
            };
            await File.WriteAllTextAsync(Path.Combine(root, "publish-manifest.json"), JsonSerializer.Serialize(publish));
            if (scenario == "changed") await File.WriteAllTextAsync(Path.Combine(root, "mp.exe"), "different-content");
            string tests = Path.Combine(root, "tests.json");
            await File.WriteAllTextAsync(tests,
                """{"schemaVersion":1,"suite":"managed","selected":3,"executed":3,"skipped":0,"categories":[{"category":"managed","selected":3,"executed":3,"skipped":0}]}""");
            string evidence = Path.Combine(root, "evidence.json");
            string rejected = Path.Combine(root, "rejected.json");
            await File.WriteAllTextAsync(rejected,
                """{"schemaVersion":1,"runtime":"win-x64","osVersion":"10.0.26100.0","osProductType":3,"cliRejected":true,"hostRejected":true,"stateUntouched":true}""");
            string installed = Path.Combine(root, "installed.json");
            if (scenario.StartsWith("installed-", StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(installed, JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    runtime = "win-x64",
                    packageSha256 = hash,
                    installedIdentity = true,
                    cliAlias = true,
                    hostAutoStart = true,
                    uninstalled = true,
                    cloudFilesExtension = true,
                    adapterAssociation = true,
                    minimumVersion = "10.0.26100.0",
                    osVersion = scenario == "installed-old-build" ? "10.0.22631.0" : "10.0.26100.0",
                    osProductType = scenario == "installed-server" ? 3 : 1,
                }));
            }
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in new[] { "-NoProfile", "-File", Path.Combine(repository, "eng", "collect-ci-evidence.ps1"),
                "-Job", "build-and-test", "-Runtime", "win-x64", "-OutputPath", evidence, "-TestManifests", tests, "-PublishDirectory", root,
                "-RejectedPath", rejected })
                start.ArgumentList.Add(argument);
            if (File.Exists(installed))
            {
                start.ArgumentList.Add("-InstalledPath");
                start.ArgumentList.Add(installed);
            }
            if (scenario.StartsWith("candidate-", StringComparison.Ordinal))
            {
                var gitStart = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                foreach (string argument in new[] { "-C", repository, "rev-parse", "HEAD" }) gitStart.ArgumentList.Add(argument);
                using Process git = Process.Start(gitStart)!;
                string source = (await git.StandardOutput.ReadToEndAsync()).Trim();
                await git.WaitForExitAsync();
                Assert.AreEqual(0, git.ExitCode);
                string candidatePath = Path.Combine(root, "official-adapter-releases.json");
                var candidate = new
                {
                    schemaVersion = 1,
                    mirrorPulseSourceSha = scenario == "candidate-source-mismatch" ? new string('a', 40) : source,
                    adapters = OfficialProviders.Select((name, index) => new
                    {
                        adapterId = "com.mirrorpulse.adapter." + name,
                        repository = "MirrorPulse/adapter-" + name,
                        sourceSha = new string('b', 40),
                        release = new
                        {
                            id = index + 1,
                            tag_name = "v1.0.0",
                            draft = false,
                            prerelease = false,
                            published_at = "2026-01-01T00:00:00Z",
                            html_url = "https://github.com/MirrorPulse/adapter-" + name + "/releases/tag/v1.0.0",
                            assets = new[]
                            {
                                new { id = 1, name = "com.mirrorpulse.adapter." + name + "-1.0.0.mpadapter", size = 7,
                                    digest = "sha256:" + new string('c', 64) },
                                new { id = scenario == "candidate-invalid-inventory" ? 1 : 2,
                                    name = "com.mirrorpulse.adapter." + name + "-1.0.0.mpadapter.signature.json", size = 7,
                                    digest = "sha256:" + new string('d', 64) },
                            },
                        },
                    }),
                };
                await File.WriteAllTextAsync(candidatePath, JsonSerializer.Serialize(candidate));
                start.ArgumentList.Add("-AdapterReleaseLockPath");
                start.ArgumentList.Add(candidatePath);
            }
            if (scenario.StartsWith("native-", StringComparison.Ordinal))
            {
                using JsonDocument catalog = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repository, "eng", "test-suites.json")));
                int required = catalog.RootElement.GetProperty("required").GetProperty("native").GetArrayLength();
                int executed = scenario == "native-valid" ? required : required - 1;
                string native = Path.Combine(root, "native.json");
                await File.WriteAllTextAsync(native, JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    suite = "native",
                    selected = executed,
                    executed,
                    skipped = 0,
                    categories = new[] { new { category = "native", selected = executed, executed, skipped = 0 } },
                }));
                static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
                string wrapper = Path.Combine(root, "collect.ps1");
                await File.WriteAllTextAsync(wrapper,
                    $"& {Quote(Path.Combine(repository, "eng", "collect-ci-evidence.ps1"))} -Job build-and-test -Runtime win-x64 " +
                    $"-OutputPath {Quote(evidence)} -TestManifests @({Quote(tests)},{Quote(native)}) -PublishDirectory {Quote(root)} " +
                    $"-RejectedPath {Quote(rejected)} -RequireNative");
                start.ArgumentList.Clear();
                foreach (string argument in new[] { "-NoProfile", "-File", wrapper }) start.ArgumentList.Add(argument);
            }
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.AreEqual(succeeds, process.ExitCode == 0, await output + await error);
            if (succeeds)
            {
                string report = await File.ReadAllTextAsync(evidence);
                Assert.DoesNotContain(root, report);
                using JsonDocument json = JsonDocument.Parse(report);
                Assert.AreEqual(scenario == "candidate-valid" ? 3 : 2, json.RootElement.GetProperty("artifacts").GetArrayLength());
                if (scenario == "candidate-valid")
                    Assert.AreEqual(64, json.RootElement.GetProperty("officialAdapterCandidateSha256").GetString()!.Length);
                Assert.AreEqual(40, json.RootElement.GetProperty("sourceSha").GetString()!.Length);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
