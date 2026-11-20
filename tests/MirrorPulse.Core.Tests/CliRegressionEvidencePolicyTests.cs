using System.Diagnostics;
using System.Text.Json.Nodes;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class CliRegressionEvidencePolicyTests
{
    [TestMethod]
    [DataRow("valid")]
    [DataRow("disabled-admitted")]
    [DataRow("disabled-admission-string")]
    [DataRow("active-closed")]
    [DataRow("active-admission-missing")]
    [DataRow("missing-phase")]
    [DataRow("invalid-anchor")]
    [DataRow("duplicate-phase")]
    [DataRow("changed-root")]
    [DataRow("changed-member")]
    [DataRow("disabled-state")]
    [DataRow("changed-hydration")]
    [DataRow("unacknowledged-write")]
    public async Task LifecycleRequiresDisabledClosedAdmissionAndRetainsIdentityHydrationAndAcknowledgementChecks(string damage)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-cli-regression-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            JsonObject evidence = BuildEvidence();
            var audits = evidence["namespace"]!["audits"]!.AsArray();
            switch (damage)
            {
                case "disabled-admitted": audits[3]!["freshAdmission"] = true; break;
                case "disabled-admission-string": audits[3]!["freshAdmission"] = "false"; break;
                case "active-closed": audits[2]!["freshAdmission"] = false; break;
                case "active-admission-missing": audits[1]!.AsObject().Remove("freshAdmission"); break;
                case "missing-phase": audits.RemoveAt(3); break;
                case "invalid-anchor": audits[3]!["anchorMemberId"] = Guid.Empty.ToString("D"); break;
                case "duplicate-phase": audits[3]!["phase"] = "InitialRoot"; break;
                case "changed-root": audits[3]!["rootId"] = Guid.NewGuid().ToString("D"); break;
                case "changed-member": audits[3]!["memberId"] = Guid.NewGuid().ToString("D"); break;
                case "disabled-state": audits[3]!["state"] = "Active"; break;
                case "changed-hydration": evidence["hydration"]!["reads"]![3]!["binarySha256"] = new string('b', 64); break;
                case "unacknowledged-write": evidence["hydration"]!["audits"]![1]!["acknowledgedQueuedFileMutations"] = 0; break;
            }
            string evidencePath = Path.Combine(directory, "evidence.json");
            await File.WriteAllTextAsync(evidencePath, evidence.ToJsonString());
            string driver = Path.Combine(directory, "verify.ps1");
            await File.WriteAllTextAsync(driver, """
                param([string]$Policy,[string]$Evidence)
                $ErrorActionPreference='Stop'
                . $Policy
                try {
                    Assert-CliRegressionObservations (Get-Content -LiteralPath $Evidence -Raw|ConvertFrom-Json)
                    'accepted';exit 0
                } catch { 'rejected';exit 1 }
                """);
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            string policy = Path.Combine(SftpProtocolFixture.FindRepositoryRoot(), "eng", "cli-regression-evidence-policy.ps1");
            foreach (string argument in new[] { "-NoProfile", "-File", driver, "-Policy", policy, "-Evidence", evidencePath }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(); Task<string> error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(true); throw; }
            Assert.AreEqual(damage == "valid" ? 0 : 1, process.ExitCode, await output + await error);
            StringAssert.Contains(await output, damage == "valid" ? "accepted" : "rejected");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static JsonObject BuildEvidence()
    {
        string anchor = Guid.NewGuid().ToString("D"), root = Guid.NewGuid().ToString("D"), member = Guid.NewGuid().ToString("D");
        var roots = new JsonArray();
        foreach (string phase in new[] { "NoAdapters", "InitialRoot", "RestartedRoot", "DisabledRoot", "ReenabledRoot" })
        {
            var audit = new JsonObject { ["phase"] = phase, ["anchorMemberId"] = anchor, ["freshAdmission"] = phase != "DisabledRoot" };
            if (phase != "NoAdapters")
            {
                audit["rootId"] = root; audit["memberId"] = member; audit["state"] = phase == "DisabledRoot" ? "Disabled" : "Active";
            }
            roots.Add(audit);
        }
        var reads = new JsonArray();
        foreach (string phase in new[] { "Online", "HostStopped", "HostRestarted", "InstanceDisabled" })
            reads.Add(new JsonObject
            {
                ["schemaVersion"] = 1,
                ["phase"] = phase,
                ["wholeFileReads"] = 6,
                ["rangeReads"] = 21,
                ["binaryLength"] = 2097409,
                ["binarySha256"] = new string('a', 64)
            });
        var hydration = new JsonArray();
        foreach (bool written in new[] { false, true })
            hydration.Add(new JsonObject
            {
                ["schemaVersion"] = 1,
                ["phase"] = written ? "AfterLocalWrite" : "BeforeLocalWrite",
                ["routeVerified"] = true,
                ["enumeratedMutations"] = written ? 1 : 0,
                ["readOnlyFileMutations"] = 0,
                ["queuedFileMutations"] = written ? 1 : 0,
                ["acknowledgedQueuedFileMutations"] = written ? 1 : 0
            });
        return new JsonObject
        {
            ["rangeRead"] = true,
            ["offlineQueueRetained"] = true,
            ["uploadJournalDrained"] = true,
            ["conflictPersisted"] = true,
            ["cursorPersisted"] = true,
            ["namespace"] = new JsonObject { ["defaultHostIntegrated"] = true, ["audits"] = roots },
            ["hydration"] = new JsonObject { ["reads"] = reads, ["audits"] = hydration }
        };
    }
}
