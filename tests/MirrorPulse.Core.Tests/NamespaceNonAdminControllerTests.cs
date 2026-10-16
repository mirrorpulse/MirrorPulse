using System.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class NamespaceNonAdminControllerTests
{
    [TestMethod]
    [DataRow("allowed", 1)]
    [DataRow("allowed", 52)]
    [DataRow("allowed", 56)]
    [DataRow("allowed", 100)]
    [DataRow("rejected", 0)]
    [DataRow("rejected", 101)]
    [DataRow("rejected", int.MaxValue)]
    public Task ControllerBudgetCoversTheGrowingCatalogAndRejectsAnUnboundedSelection(string expectation, int count) =>
        RunAsync("budget-" + expectation, count);

    [TestMethod]
    [DataRow("copy")]
    [DataRow("wrong-marker")]
    [DataRow("nested-evidence")]
    [DataRow("oversized")]
    public Task FailureCaptureRetainsOnlyBoundedOriginalDiagnosticsBeforeWorkspaceRemoval(string scenario) => RunAsync(scenario);

    private static async Task RunAsync(string scenario, int count = 0)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-controller-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "verify.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Policy,[string]$Directory,[string]$Scenario,[int]$Count)
                $ErrorActionPreference='Stop'
                . $Policy
                if ($Scenario -like 'budget-*') {
                    $rejected=$false
                    try { $budget=Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount $Count }
                    catch { $rejected=$true }
                    if ($Scenario -eq 'budget-rejected') {
                        if (-not $rejected) { throw 'An invalid or unbounded selection was accepted.' }
                    } else {
                        if ($rejected -or $budget -lt 600000 -or $budget -gt 1800000) { throw 'The whole-suite envelope is missing or unbounded.' }
                        if ($Count -ge 52 -and $budget -le 600000) { throw 'The growing suite still has the obsolete fixed ten-minute envelope.' }
                        if ($Count -eq 56 -and $budget -le (Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount 52)) {
                            throw 'New required cases did not receive execution budget.'
                        }
                    }
                    exit 0
                }
                $id=[guid]::NewGuid().ToString('N')
                $workspace=Join-Path $Directory $id
                $evidence=Join-Path $Directory 'evidence'
                New-Item -ItemType Directory -Path (Join-Path $workspace 'work/results') -Force | Out-Null
                [IO.File]::WriteAllText((Join-Path $workspace '.mp-namespace-ci'),$id)
                [IO.File]::WriteAllText((Join-Path $workspace 'work/context.json'),'original-context')
                [IO.File]::WriteAllText((Join-Path $workspace 'work/results/namespace.trx'),'original-trx')
                [IO.File]::WriteAllText((Join-Path $workspace 'stdout.log'),'original-stdout')
                [IO.File]::WriteAllText((Join-Path $workspace 'stderr.log'),'original-stderr')
                [IO.File]::WriteAllText((Join-Path $workspace 'configuration.json'),'synthetic-secret-must-not-be-copied')
                if ($Scenario -eq 'wrong-marker') { [IO.File]::WriteAllText((Join-Path $workspace '.mp-namespace-ci'),'changed') }
                if ($Scenario -eq 'nested-evidence') { $evidence=Join-Path $workspace 'must-not-be-created' }
                if ($Scenario -eq 'oversized') {
                    $large=[IO.File]::OpenWrite((Join-Path $workspace 'work/results/namespace.trx'))
                    try { $large.SetLength(10MB+1) } finally { $large.Dispose() }
                }
                $failed=$false
                try { Copy-OrdinaryUserNamespaceFailureEvidence -Workspace $workspace -Identifier $id -EvidenceDirectory $evidence }
                catch { $failed=$true }
                if ($Scenario -ne 'copy') {
                    if (-not $failed) { throw 'Invalid evidence input was accepted.' }
                    if (Test-Path -LiteralPath (Join-Path $evidence 'namespace.trx')) { throw 'Invalid or unbounded evidence was copied.' }
                    exit 0
                }
                if ($failed) { throw 'Valid marked failure evidence was rejected.' }
                $expected=@{'context.json'='original-context';'namespace.trx'='original-trx';'child-stdout.log'='original-stdout';'child-stderr.log'='original-stderr'}
                if (@(Get-ChildItem -LiteralPath $evidence -File).Count -ne $expected.Count) { throw 'Failure capture copied an unexpected file.' }
                foreach ($name in $expected.Keys) {
                    if ([IO.File]::ReadAllText((Join-Path $evidence $name)) -cne $expected[$name]) { throw 'An original diagnostic changed during capture.' }
                }
                """);
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-File", script, "-Policy",
                Path.Combine(SftpProtocolFixture.FindRepositoryRoot(), "eng", "namespace-nonadmin-controller.ps1"),
                "-Directory", directory, "-Scenario", scenario, "-Count", count.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(); Task<string> error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } throw; }
            Assert.AreEqual(0, process.ExitCode, await output + await error);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
