using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class ManagedTestOriginalsTests
{
    private static readonly string[] OriginalOutcomes = ["Passed", "Failed", "NotExecuted"];

    [TestMethod]
    [DataRow("originals", true)]
    [DataRow("missingSummary", true)]
    [DataRow("changedBinary", false)]
    [DataRow("changedRetainedBinary", false)]
    [DataRow("changedPolicy", false)]
    [DataRow("changedSource", false)]
    [DataRow("foreignAssembly", false)]
    [DataRow("duplicateDefinition", false)]
    [DataRow("missingDefinition", false)]
    [DataRow("doctype", false)]
    [DataRow("staleResults", false)]
    [DataRow("staleTrx", false)]
    public async Task CollectorBindsActualAssembliesAndPreservesOriginalNegativeOutcomes(string boundary, bool succeeds)
    {
        string repository = SftpProtocolFixture.FindRepositoryRoot();
        string root = Path.Combine(Path.GetTempPath(), $"MirrorPulse-managed-originals-{Guid.NewGuid():N}");
        string script = Path.Combine(root, "eng", "collect-managed-test-originals.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        try
        {
            File.Copy(Path.Combine(repository, "eng", "collect-managed-test-originals.ps1"), script);
            File.Copy(Path.Combine(repository, "eng", "test-suites.json"), Path.Combine(root, "eng", "test-suites.json"));
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "bin/\nartifacts/\n");
            string framework = "net10.0-windows10.0.26100.0";
            foreach (string name in new[] { "MirrorPulse.Core.Tests", "MirrorPulse.CloudFiles.CfSharp.Tests", "MirrorPulse.Cli.Tests" })
            {
                string project = Path.Combine(root, "tests", name);
                string output = Path.Combine(project, "bin", "Release", framework);
                Directory.CreateDirectory(output);
                File.Copy(Path.Combine(repository, "tests", name, name + ".csproj"), Path.Combine(project, name + ".csproj"));
                File.Copy(Path.Combine(repository, "tests", name, "bin", "Release", framework, name + ".dll"), Path.Combine(output, name + ".dll"));
            }
            await RunAsync(root, "git", "init", "--quiet");
            await RunAsync(root, "git", "add", ".");
            await RunAsync(root, "git", "-c", "user.name=Managed originals fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "Fixture source");
            if (boundary == "staleResults")
            {
                string oldResults = Path.Combine(root, "artifacts", "test-results", "managed");
                Directory.CreateDirectory(oldResults);
                await File.WriteAllTextAsync(Path.Combine(oldResults, "old.trx"), "<TestRun />");
                var refused = await InvokeAsync(root, "pwsh", "-NoProfile", "-File", script, "-Phase", "Prepare");
                Assert.AreNotEqual(0, refused.ExitCode, refused.Output);
                Assert.IsFalse(Directory.Exists(Path.Combine(root, "artifacts", "test-results", "managed-originals")));
                return;
            }
            await RunAsync(root, "pwsh", "-NoProfile", "-File", script, "-Phase", "Prepare");

            string results = Path.Combine(root, "artifacts", "test-results", "managed");
            string originals = Path.Combine(root, "artifacts", "test-results", "managed-originals");
            Directory.CreateDirectory(results);
            string assembly = Path.Combine(root, "tests", "MirrorPulse.Core.Tests", "bin", "Release", framework, "MirrorPulse.Core.Tests.dll");
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            string id = Guid.NewGuid().ToString();
            string codeBase = boundary == "foreignAssembly" ? typeof(object).Assembly.Location : assembly;
            var definition = new XElement(ns + "UnitTest", new XAttribute("id", id), new XAttribute("storage", codeBase),
                new XElement(ns + "TestMethod", new XAttribute("codeBase", codeBase),
                    new XAttribute("className", "Fixture.RepeatedCase"), new XAttribute("name", "SameMethod")));
            var definitions = new XElement(ns + "TestDefinitions", definition);
            if (boundary == "duplicateDefinition") definitions.Add(new XElement(definition));
            if (boundary == "missingDefinition") definition.Remove();
            var cases = new XElement(ns + "Results", OriginalOutcomes.Select(outcome =>
                new XElement(ns + "UnitTestResult", new XAttribute("testId", id), new XAttribute("executionId", Guid.NewGuid()),
                    new XAttribute("testName", "Same display name"), new XAttribute("outcome", outcome))));
            string trx = Path.Combine(results, "original.trx");
            DateTimeOffset started = boundary == "staleTrx" ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow;
            var times = new XElement(ns + "Times", new XAttribute("start", started.ToString("O")), new XAttribute("finish", DateTimeOffset.UtcNow.ToString("O")));
            new XDocument(new XElement(ns + "TestRun", times, definitions, cases)).Save(trx);
            if (boundary != "missingSummary") await File.WriteAllTextAsync(Path.Combine(results, "test-evidence.json"), "{\"originalFixtureSummary\":true}");
            if (boundary == "changedBinary") await File.AppendAllTextAsync(assembly, "changed");
            if (boundary == "changedRetainedBinary") await File.AppendAllTextAsync(Path.Combine(originals, "assemblies", "MirrorPulse.Core.Tests.dll"), "changed");
            if (boundary == "changedPolicy") await File.AppendAllTextAsync(Path.Combine(root, "eng", "test-suites.json"), " ");
            if (boundary == "changedSource") await File.AppendAllTextAsync(script, "\n# Changed after preparation\n");
            if (boundary == "doctype") await File.WriteAllTextAsync(trx, "<!DOCTYPE TestRun [<!ENTITY x SYSTEM 'file:///unread-fixture'>]><TestRun>&x;</TestRun>");

            var collected = await InvokeAsync(root, "pwsh", "-NoProfile", "-File", script, "-Phase", "Collect");
            Assert.AreEqual(succeeds, collected.ExitCode == 0, collected.Output);
            Assert.IsTrue(File.Exists(Path.Combine(originals, "prepared.json")), "A failed collector must retain the original preparation.");
            string receipt = Path.Combine(originals, "collected.json");
            Assert.AreEqual(succeeds, File.Exists(receipt));
            if (succeeds)
            {
                using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(receipt));
                JsonElement value = document.RootElement;
                Assert.AreEqual(3, value.GetProperty("resultCount").GetInt32());
                Assert.IsFalse(value.GetProperty("gateSuccessEstablishedByCollection").GetBoolean());
                CollectionAssert.AreEqual(OriginalOutcomes,
                    value.GetProperty("results").EnumerateArray().Select(row => row.GetProperty("outcome").GetString()).ToArray());
                Assert.IsTrue(value.GetProperty("results").EnumerateArray().All(row => row.GetProperty("name").GetString() == "Same display name"));
                Assert.AreEqual(boundary == "missingSummary", value.GetProperty("originalSummary").ValueKind == JsonValueKind.Null);
                CollectionAssert.AreEqual(await File.ReadAllBytesAsync(trx), await File.ReadAllBytesAsync(Path.Combine(originals, "results", "original.trx")));
            }
        }
        finally
        {
            string fullPath = Path.GetFullPath(root);
            Assert.IsTrue(fullPath.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(Path.GetFileName(fullPath).StartsWith("MirrorPulse-managed-originals-", StringComparison.Ordinal));
            Assert.AreEqual((FileAttributes)0, File.GetAttributes(fullPath) & FileAttributes.ReparsePoint);
            // Git stores fixture objects as read-only on Windows.
            foreach (string path in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private static async Task RunAsync(string root, string command, params string[] arguments)
    {
        var result = await InvokeAsync(root, command, arguments);
        Assert.AreEqual(0, result.ExitCode, result.Output);
    }

    private static async Task<(int ExitCode, string Output)> InvokeAsync(string root, string command, params string[] arguments)
    {
        var start = new ProcessStartInfo(command)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment.Remove("GITHUB_ACTIONS");
        start.Environment.Remove("GITHUB_SHA");
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        return (process.ExitCode, await output + await error);
    }
}
