using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class CompiledTestInventoryTests
{
    private static readonly string[] Assemblies = ["MirrorPulse.Core.Tests", "MirrorPulse.CloudFiles.CfSharp.Tests", "MirrorPulse.Cli.Tests"];
    private static readonly string[] Outcomes = ["Passed", "Failed", "NotExecuted"];

    [TestMethod]
    [DataRow("complete", true)]
    [DataRow("encodedControl", true)]
    [DataRow("literalEscape", true)]
    [DataRow("changedEncodedControl", false)]
    [DataRow("encodedSupplementary", true)]
    [DataRow("changedEncodedSupplementary", false)]
    [DataRow("omittedResult", false)]
    [DataRow("extraResult", false)]
    [DataRow("duplicateResult", false)]
    [DataRow("changedDisplay", false)]
    [DataRow("changedMethod", false)]
    [DataRow("changedId", false)]
    [DataRow("foreignResult", false)]
    [DataRow("changedRawResult", false)]
    [DataRow("forgedRawIndex", false)]
    [DataRow("changedBinary", false)]
    [DataRow("changedRetainedBinary", false)]
    [DataRow("changedSource", false)]
    [DataRow("changedTrace", false)]
    [DataRow("duplicateDiscovery", false)]
    [DataRow("abortedDiscovery", false)]
    [DataRow("partialDiscovery", false)]
    [DataRow("foreignDiscovery", false)]
    [DataRow("missingCompletion", false)]
    [DataRow("changedTotal", false)]
    public async Task ReconciliationRequiresEveryOriginalCompiledCaseAndPreservesNegativeOutcomes(string boundary, bool succeeds)
    {
        string repository = SftpProtocolFixture.FindRepositoryRoot();
        string root = Path.Combine(Path.GetTempPath(), $"MirrorPulse-compiled-inventory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            foreach (string name in new[] { "collect-managed-test-originals.ps1", "collect-compiled-test-inventory.ps1", "compiled-test-inventory-policy.ps1", "test-suites.json" })
            {
                string destination = Path.Combine(root, "eng", name); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(repository, "eng", name), destination);
            }
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "bin/\nartifacts/\n");
            const string framework = "net10.0-windows10.0.26100.0";
            foreach (string name in Assemblies)
            {
                string project = Path.Combine(root, "tests", name); string output = Path.Combine(project, "bin", "Release", framework);
                Directory.CreateDirectory(output);
                File.Copy(Path.Combine(repository, "tests", name, name + ".csproj"), Path.Combine(project, name + ".csproj"));
                File.Copy(Path.Combine(repository, "tests", name, "bin", "Release", framework, name + ".dll"), Path.Combine(output, name + ".dll"));
            }
            await RequireAsync(root, "git", "init", "--quiet"); await RequireAsync(root, "git", "add", ".");
            await RequireAsync(root, "git", "-c", "user.name=Compiled inventory fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "Fixture source");
            string collector = Path.Combine(root, "eng", "collect-managed-test-originals.ps1");
            string verifier = Path.Combine(root, "eng", "collect-compiled-test-inventory.ps1");
            await RequireAsync(root, "pwsh", "-NoProfile", "-File", collector, "-Phase", "Prepare");
            string originals = Path.Combine(root, "artifacts", "test-results", "managed-originals");
            string results = Path.Combine(root, "artifacts", "test-results", "managed"); Directory.CreateDirectory(results);
            string discovery = Path.Combine(originals, "discovery"); Directory.CreateDirectory(discovery);
            JsonObject prepared = (JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(originals, "prepared.json"))) as JsonObject)!;
            var inventories = new JsonArray();
            foreach (JsonNode? assemblyNode in prepared["assemblies"]!.AsArray())
            {
                JsonObject assembly = assemblyNode!.AsObject(); string relative = assembly["path"]!.GetValue<string>();
                string path = Path.GetFullPath(relative, root); string name = Path.GetFileNameWithoutExtension(path);
                bool selected = name == Assemblies[0];
                var cases = new JsonArray(); var discovered = new JsonArray();
                var definitions = new XElement(Ns + "TestDefinitions"); var executions = new XElement(Ns + "Results");
                for (int i = 0; i < 3; i++)
                {
                    string id = Guid.NewGuid().ToString("D"); string display = $"SameMethod ({i})";
                    if (selected && i == 0 && boundary is "encodedControl" or "changedEncodedControl") display = "SameMethod (\u0001)";
                    if (selected && i == 0 && boundary == "literalEscape") display = @"SameMethod (\u0001)";
                    if (selected && i == 0 && boundary is "encodedSupplementary" or "changedEncodedSupplementary") display = "SameMethod (\U0001F600)";
                    string xmlDisplay = display.Replace("\u0001", boundary == "changedEncodedControl" ? @"\u0002" : @"\u0001", StringComparison.Ordinal);
                    if (selected && i == 0 && boundary is "encodedSupplementary" or "changedEncodedSupplementary")
                        xmlDisplay = boundary == "changedEncodedSupplementary" ? @"SameMethod (\ud83d\ude01)" : @"SameMethod (\ud83d\ude00)";
                    cases.Add(new JsonObject { ["testId"] = id, ["fullyQualifiedName"] = "Fixture.RepeatedCase.SameMethod", ["displayName"] = display, ["source"] = path });
                    discovered.Add(new JsonObject { ["Id"] = id, ["FullyQualifiedName"] = "Fixture.RepeatedCase.SameMethod", ["DisplayName"] = display, ["Source"] = path });
                    definitions.Add(new XElement(Ns + "UnitTest", new XAttribute("id", id), new XAttribute("storage", path),
                        new XElement(Ns + "TestMethod", new XAttribute("codeBase", path), new XAttribute("className", "Fixture.RepeatedCase"), new XAttribute("name", "SameMethod"))));
                    executions.Add(new XElement(Ns + "UnitTestResult", new XAttribute("testId", id), new XAttribute("executionId", Guid.NewGuid()),
                        new XAttribute("testName", xmlDisplay), new XAttribute("outcome", Outcomes[i])));
                }
                var payload = new JsonObject
                {
                    ["TotalTests"] = 3,
                    ["IsAborted"] = false,
                    ["LastDiscoveredTests"] = new JsonArray(discovered[2]!.DeepClone()),
                    ["FullyDiscoveredSources"] = new JsonArray(path),
                    ["PartiallyDiscoveredSources"] = new JsonArray(),
                    ["NotDiscoveredSources"] = new JsonArray(),
                    ["SkippedDiscoverySources"] = new JsonArray()
                };
                if (selected)
                {
                    if (boundary == "duplicateDiscovery") discovered[1]!["Id"] = discovered[0]!["Id"]!.DeepClone();
                    if (boundary == "abortedDiscovery") payload["IsAborted"] = true;
                    if (boundary == "partialDiscovery") payload["PartiallyDiscoveredSources"]!.AsArray().Add(path);
                    if (boundary == "foreignDiscovery") discovered[0]!["Source"] = typeof(object).Assembly.Location;
                    if (boundary == "changedTotal") payload["TotalTests"] = 4;
                }
                string traceRelative = name + "/discovery.trace.log"; string trace = Path.Combine(discovery, traceRelative);
                Directory.CreateDirectory(Path.GetDirectoryName(trace)!);
                string text = Message("TestDiscovery.TestFound", new JsonArray(discovered[0]!.DeepClone(), discovered[1]!.DeepClone()));
                if (!selected || boundary != "missingCompletion") text += Message("TestDiscovery.Completed", payload);
                await File.WriteAllTextAsync(trace, text);
                inventories.Add(new JsonObject
                {
                    ["assembly"] = relative,
                    ["assemblySha256"] = assembly["sha256"]!.DeepClone(),
                    ["trace"] = traceRelative,
                    ["cases"] = cases,
                    ["originalFiles"] = new JsonArray(new JsonObject { ["path"] = traceRelative, ["sha256"] = Hash(trace) })
                });
                DateTimeOffset now = DateTimeOffset.UtcNow;
                new XDocument(new XElement(Ns + "TestRun", new XElement(Ns + "Times", new XAttribute("start", now.ToString("O")),
                    new XAttribute("finish", now.ToString("O"))), definitions, executions)).Save(Path.Combine(results, name + ".trx"));
            }
            string inventoryPath = Path.Combine(discovery, "prepared.json");
            await File.WriteAllTextAsync(inventoryPath, new JsonObject
            {
                ["schemaVersion"] = 1,
                ["sourceSha"] = prepared["sourceSha"]!.DeepClone(),
                ["preparedSha256"] = Hash(Path.Combine(originals, "prepared.json")),
                ["assemblies"] = inventories
            }.ToJsonString());
            await RequireAsync(root, "pwsh", "-NoProfile", "-File", collector, "-Phase", "Collect");
            string collectedPath = Path.Combine(originals, "collected.json"); string originalCollected = await File.ReadAllTextAsync(collectedPath);
            JsonObject collected = JsonNode.Parse(originalCollected)!.AsObject(); JsonArray rows = collected["results"]!.AsArray();
            if (boundary == "omittedResult") rows.RemoveAt(0);
            if (boundary == "extraResult") { JsonNode added = rows[0]!.DeepClone(); added["testId"] = Guid.NewGuid().ToString("D"); rows.Add(added); }
            if (boundary == "duplicateResult") rows[1] = rows[0]!.DeepClone();
            if (boundary == "changedDisplay") rows[0]!["name"] = "Other display";
            if (boundary == "changedMethod") rows[0]!["method"] = "OtherMethod";
            if (boundary == "changedId") rows[0]!["testId"] = Guid.NewGuid().ToString("D");
            if (boundary == "foreignResult") rows[0]!["assembly"] = "tests/Other.dll";
            string binary = Path.Combine(root, prepared["assemblies"]![0]!["path"]!.GetValue<string>());
            if (boundary == "changedBinary") await File.AppendAllTextAsync(binary, "changed");
            if (boundary == "changedRetainedBinary") await File.AppendAllTextAsync(Path.Combine(originals, "assemblies", Assemblies[0] + ".dll"), "changed");
            if (boundary == "changedSource") await File.AppendAllTextAsync(verifier, "\n# Changed after preparation\n");
            if (boundary == "changedTrace") await File.AppendAllTextAsync(Path.Combine(discovery, Assemblies[0], "discovery.trace.log"), "changed");
            if (boundary is "changedRawResult" or "forgedRawIndex")
            {
                JsonNode raw = collected["originals"]![0]!; string rawPath = Path.Combine(originals, raw["archivePath"]!.GetValue<string>());
                XDocument xml = XDocument.Load(rawPath); xml.Root!.Element(Ns + "Results")!.Elements().First().Remove(); xml.Save(rawPath);
                if (boundary == "forgedRawIndex") raw["sha256"] = Hash(rawPath);
            }
            await File.WriteAllTextAsync(collectedPath, collected.ToJsonString());
            string before = await File.ReadAllTextAsync(collectedPath);
            var reconciled = await InvokeAsync(root, "pwsh", "-NoProfile", "-File", verifier, "-Phase", "Reconcile");
            Assert.AreEqual(succeeds, reconciled.ExitCode == 0, reconciled.Output);
            string receipt = Path.Combine(discovery, "reconciled.json"); Assert.AreEqual(succeeds, File.Exists(receipt));
            Assert.AreEqual(before, await File.ReadAllTextAsync(collectedPath), "Reconciliation cannot rewrite original outcomes or receipts.");
            Assert.IsTrue(File.Exists(inventoryPath));
            if (succeeds)
            {
                using JsonDocument result = JsonDocument.Parse(await File.ReadAllTextAsync(receipt));
                Assert.IsTrue(result.RootElement.GetProperty("allDiscoveredCaseIdsNamesAndMultiplicitiesRetained").GetBoolean());
                Assert.IsFalse(result.RootElement.GetProperty("gateSuccessEstablishedByReconciliation").GetBoolean());
                Assert.AreEqual(9, result.RootElement.GetProperty("assemblies").EnumerateArray().Sum(row => row.GetProperty("originalResults").GetInt32()));
                CollectionAssert.AreEqual(Outcomes, rows.Take(3).Select(row => row!["outcome"]!.GetValue<string>()).ToArray());
            }
        }
        finally
        {
            string full = Path.GetFullPath(root);
            Assert.IsTrue(full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(Path.GetFileName(full).StartsWith("MirrorPulse-compiled-inventory-", StringComparison.Ordinal));
            Assert.AreEqual((FileAttributes)0, File.GetAttributes(full) & FileAttributes.ReparsePoint);
            foreach (string path in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(full, recursive: true);
        }
    }

    private static XNamespace Ns => "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Message(string type, JsonNode payload) => "TpTrace fixture: TestRequestSender.OnDiscoveryMessageReceived: Received message: " +
        new JsonObject { ["MessageType"] = type, ["Payload"] = payload }.ToJsonString() + "\n";

    private static async Task RequireAsync(string root, string command, params string[] arguments)
    {
        var result = await InvokeAsync(root, command, arguments); Assert.AreEqual(0, result.ExitCode, result.Output);
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
        start.Environment.Remove("GITHUB_ACTIONS"); start.Environment.Remove("GITHUB_SHA");
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!; Task<string> stdout = process.StandardOutput.ReadToEndAsync(); Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(stop.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        return (process.ExitCode, await stdout + await stderr);
    }
}
