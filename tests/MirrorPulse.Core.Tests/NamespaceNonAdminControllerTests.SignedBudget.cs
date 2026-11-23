using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MirrorPulse.Core.Tests;

public sealed partial class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [DataRow("signedAtomicDirectory")]
    [DataRow("signedDirectoryMove")]
    [DataRow("signedDirectoryTransfer")]
    [DataRow("signedDirectoryCopyRecovery")]
    [DataRow("signedPreparedDirectoryCopy")]
    public async Task SignedControllerCoversEveryRequiredExecutionWithoutChangingIndividualDeadlines(string suite)
    {
        string repository = TestRepository.FindRepositoryRoot();
        using var catalog = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(repository, "eng", "test-suites.json")));
        int executions = 0;
        foreach (var method in catalog.RootElement.GetProperty("required").GetProperty(suite).EnumerateArray())
        {
            executions += catalog.RootElement.GetProperty("requiredCases").TryGetProperty(method.GetString()!, out var cases)
                ? cases.GetArrayLength() : 1;
        }
        string controller = Path.Combine(repository, "eng", "namespace-nonadmin-controller.ps1").Replace("'", "''", StringComparison.Ordinal);
        var start = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[] { "-NoProfile", "-Command", $". '{controller}'; Get-SignedNamespaceBudgetMilliseconds -ExecutionCount {executions}" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        Assert.AreEqual(0, process.ExitCode, await error);
        int budget = int.Parse((await output).Trim(), CultureInfo.InvariantCulture);
        Assert.IsGreaterThanOrEqualTo(120_000 + executions * 75_000, budget,
            "The controller must cover every original data row and retain each 75-second fixture deadline.");
        Assert.IsLessThanOrEqualTo(Math.Max(300_000, 180_000 + executions * 75_000), budget,
            "The controller must retain a bounded startup and cleanup allowance.");
    }
}
