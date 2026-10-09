using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class TestResultGateTests
{
    [TestMethod]
    [DataRow("native", "Passed", false, false, true)]
    [DataRow("native", "NotExecuted", false, false, false)]
    [DataRow("native", "Passed", true, false, false)]
    [DataRow("namespace", "Passed", false, false, true)]
    [DataRow("namespace", "NotExecuted", false, false, false)]
    [DataRow("namespace", "Passed", true, false, false)]
    [DataRow("namespace", "Passed", false, true, false)]
    public async Task DedicatedGateRejectsSkippedOrMissingRequiredResults(string suite, string outcome, bool omitLast, bool repeatFirst, bool succeeds)
    {
        string repository = SftpProtocolFixture.FindRepositoryRoot();
        using JsonDocument catalog = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(repository, "eng", "test-suites.json")));
        string[] names = catalog.RootElement.GetProperty("required").GetProperty(suite)
            .EnumerateArray().Select(item => item.GetString()!).ToArray();
        string root = Path.Combine(Path.GetTempPath(), $"MirrorPulse-trx-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var definitions = new XElement(ns + "TestDefinitions");
            var results = new XElement(ns + "Results");
            IEnumerable<string> selected = names.Take(names.Length - (omitLast ? 1 : 0));
            if (repeatFirst) selected = selected.Append(names[0]);
            foreach (string name in selected)
            {
                string id = Guid.NewGuid().ToString();
                int separator = name.LastIndexOf('.');
                definitions.Add(new XElement(ns + "UnitTest", new XAttribute("id", id),
                    new XAttribute("storage", "MirrorPulse.CloudFiles.CfSharp.Tests.dll"),
                    new XElement(ns + "TestMethod", new XAttribute("className", name[..separator]),
                        new XAttribute("name", name[(separator + 1)..]))));
                results.Add(new XElement(ns + "UnitTestResult", new XAttribute("testId", id),
                    new XAttribute("outcome", outcome)));
            }
            new XDocument(new XElement(ns + "TestRun", definitions, results))
                .Save(Path.Combine(root, "fixture.trx"));
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[] { "-NoProfile", "-File",
                Path.Combine(repository, "eng", "verify-test-results.ps1"),
                "-Suite", suite, "-ResultsDirectory", root })
            {
                start.ArgumentList.Add(argument);
            }
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.AreEqual(succeeds, process.ExitCode == 0, await output + await error);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
