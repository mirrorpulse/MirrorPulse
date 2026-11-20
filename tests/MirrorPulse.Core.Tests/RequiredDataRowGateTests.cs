using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace MirrorPulse.Core.Tests;

[TestClass, DoNotParallelize]
public sealed class RequiredDataRowGateTests
{
    [TestMethod]
    [DataRow("valid")]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("renamed")]
    [DataRow("skipped")]
    [DataRow("failed")]
    [DataRow("wrong-method")]
    [DataRow("duplicate-test-id")]
    [DataRow("duplicate-execution")]
    [DataRow("foreign")]
    [DataRow("reader-count-only")]
    [DataRow("reader-method-count")]
    [DataRow("reader-case")]
    public async Task RequiredOriginalDataRowsCannotBeReplacedByCountsOrOtherPassingCases(string scenario)
    {
        string repository = SftpProtocolFixture.FindRepositoryRoot();
        using var catalog = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repository, "eng", "test-suites.json")));
        var root = catalog.RootElement;
        var rows = new List<GateCase>();
        foreach (var item in root.GetProperty("required").GetProperty("namespace").EnumerateArray())
        {
            string method = item.GetString()!;
            string[] cases = root.GetProperty("requiredCases").TryGetProperty(method, out var explicitCases)
                ? explicitCases.EnumerateArray().Select(value => value.GetString()!).ToArray() : [method[(method.LastIndexOf('.') + 1)..]];
            rows.AddRange(cases.Select(display => new GateCase(method, display, "Passed", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"))));
        }
        int target = rows.FindIndex(row => row.Display == "NativeKeepRemoteResidentByteCopyRetainsPlaceholderAndOriginalJournal (0)");
        Assert.IsGreaterThan(0, target);
        var selected = rows[target];
        if (scenario == "missing") rows.RemoveAt(target);
        else if (scenario == "duplicate") rows[target] = selected with { Display = rows[target - 1].Display };
        else if (scenario == "renamed") rows[target] = selected with { Display = "a different passing case" };
        else if (scenario == "skipped") rows[target] = selected with { Outcome = "NotExecuted" };
        else if (scenario == "failed") rows[target] = selected with { Outcome = "Failed" };
        else if (scenario == "wrong-method") rows[target] = selected with { Method = "Fixture.Unrelated" };
        else if (scenario == "duplicate-test-id") rows[target] = selected with { TestId = rows[target - 1].TestId };
        else if (scenario == "duplicate-execution") rows[target] = selected with { ExecutionId = rows[target - 1].ExecutionId };
        else if (scenario == "foreign") rows.Add(selected with { Method = "Fixture.Unrelated", TestId = Guid.NewGuid().ToString("D") });
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-row-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var definitions = new XElement(ns + "TestDefinitions");
            var results = new XElement(ns + "Results");
            foreach (var row in rows)
            {
                int separator = row.Method.LastIndexOf('.');
                definitions.Add(new XElement(ns + "UnitTest", new XAttribute("id", row.TestId),
                    new XAttribute("storage", "MirrorPulse.CloudFiles.CfSharp.Tests.dll"),
                    new XElement(ns + "TestMethod", new XAttribute("className", row.Method[..separator]), new XAttribute("name", row.Method[(separator + 1)..]))));
                results.Add(new XElement(ns + "UnitTestResult", new XAttribute("testId", row.TestId), new XAttribute("executionId", row.ExecutionId),
                    new XAttribute("testName", row.Display), new XAttribute("outcome", row.Outcome)));
            }
            new XDocument(new XElement(ns + "TestRun", definitions, results)).Save(Path.Combine(directory, "fixture.trx"));
            string evidence = Path.Combine(directory, "evidence.json");
            var verification = await RunAsync(Path.Combine(repository, "eng", "verify-test-results.ps1"),
                "-Suite", "namespace", "-ResultsDirectory", directory, "-OutputPath", evidence);
            bool validRaw = scenario == "valid" || scenario.StartsWith("reader-", StringComparison.Ordinal);
            Assert.AreEqual(validRaw, verification.Exit == 0, verification.Output);
            if (!validRaw) return;
            var report = JsonNode.Parse(await File.ReadAllTextAsync(evidence))!;
            if (scenario == "reader-count-only") report.AsObject().Remove("results");
            else if (scenario == "reader-method-count")
            {
                int methods = root.GetProperty("required").GetProperty("namespace").GetArrayLength();
                report["selected"] = methods; report["executed"] = methods;
            }
            else if (scenario == "reader-case")
                report["results"]!.AsArray().First(row => row!["case"]!.GetValue<string>() == selected.Display)!["case"] = "a substituted case";
            await File.WriteAllTextAsync(evidence, report.ToJsonString());
            string script = Path.Combine(directory, "reader.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Repository,[string]$Evidence)
                $ErrorActionPreference='Stop'
                . (Join-Path $Repository 'eng/namespace-evidence-policy.ps1')
                $catalog=Get-Content (Join-Path $Repository 'eng/test-suites.json') -Raw|ConvertFrom-Json
                Assert-NamespaceTestExecution (Get-Content -LiteralPath $Evidence -Raw|ConvertFrom-Json) $catalog
                """
            );
            var readback = await RunAsync(script, "-Repository", repository, "-Evidence", evidence);
            Assert.AreEqual(scenario == "valid", readback.Exit == 0, readback.Output);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed record GateCase(string Method, string Display, string Outcome, string TestId, string ExecutionId);

    private static async Task<(int Exit, string Output)> RunAsync(string script, params string[] arguments)
    {
        var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-NoProfile", "-File", script }.Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } throw; }
        return (process.ExitCode, await output + await error);
    }
}
