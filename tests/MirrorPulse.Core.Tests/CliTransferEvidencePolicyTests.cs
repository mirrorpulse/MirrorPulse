using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed partial class CliTransferEvidencePolicyTests
{
    private static readonly int[] WorkerProcessIds = [101, 102];

    [TestMethod]
    public async Task BothRealExecutionShapesRequireSeparateRetainedReceiptsAndCompletedCleanup()
    {
        var result = await ExerciseAsync(CompleteEvidence());
        Assert.AreEqual(0, result.ExitCode, result.Output + result.Error);
    }

    [TestMethod]
    public async Task RecreatedSourceMayReuseItsPublicIdentityWithoutReusingTheRetiredMember()
    {
        var evidence = CompleteEvidence();
        foreach (var item in evidence["cases"]!.AsArray())
        {
            item!["laterSourceItemId"] = item["sourceItemId"]!.DeepClone();
            item["laterSourcePublicIdentityReused"] = true;
        }
        var result = await ExerciseAsync(evidence);
        Assert.AreEqual(0, result.ExitCode, result.Output + result.Error);
    }

    [TestMethod]
    [DataRow("unsigned")]
    [DataRow("missing-worker")]
    [DataRow("shared-runtime")]
    [DataRow("duplicate-command")]
    [DataRow("missing-scope")]
    [DataRow("wrong-scope")]
    [DataRow("still-fenced")]
    [DataRow("incomplete")]
    [DataRow("missing-phase")]
    [DataRow("phase-order")]
    [DataRow("changed-resume")]
    [DataRow("changed-restart")]
    [DataRow("copy-hash")]
    [DataRow("private-hash")]
    [DataRow("missing-audit")]
    [DataRow("private-unverified")]
    [DataRow("foreign-receipt")]
    [DataRow("empty-claim-ack")]
    [DataRow("unknown-ack-kind")]
    [DataRow("early-ack")]
    [DataRow("source-retained")]
    [DataRow("later-target-overwritten")]
    [DataRow("later-source-overwritten")]
    [DataRow("reused-source-member")]
    [DataRow("no-fresh-admission")]
    [DataRow("cleanup-failed")]
    [DataRow("foreign-fixture-route")]
    [DataRow("same-host-process")]
    public async Task MissingForeignOrContradictoryExecutionCannotBecomeSuccessfulEvidence(string failure)
    {
        var evidence = CompleteEvidence(); var cases = evidence["cases"]!.AsArray();
        var first = cases[0]!.AsObject(); var status = first["status"]!.AsObject();
        var receipt = evidence["audit"]!["transfers"]![0]!.AsObject();
        switch (failure)
        {
            case "unsigned": evidence["signedInstallation"] = false; break;
            case "missing-worker": evidence["workerProcessIds"] = new JsonArray(101); break;
            case "shared-runtime": evidence["privateWorkerRuntime"] = false; break;
            case "duplicate-command": cases[1]!["operationId"] = first["operationId"]!.DeepClone(); break;
            case "missing-scope": cases.RemoveAt(1); break;
            case "wrong-scope": first["scope"] = "CrossInstance"; break;
            case "still-fenced": status["rootsFenced"] = true; break;
            case "incomplete": status["completed"] = false; break;
            case "missing-phase": status["sourceDeleteAcceptedAt"] = null; break;
            case "phase-order": status["copyAcceptedAt"] = "2026-10-21T00:00:00Z"; break;
            case "changed-resume": first["resumedStatus"]!["stage"] = "Prepared"; break;
            case "changed-restart": first["restartedStatus"]!["completed"] = false; break;
            case "copy-hash": first["targetRemoteSha256"] = new string('b', 64); break;
            case "private-hash": receipt["sha256"] = new string('b', 64); break;
            case "missing-audit": evidence["audit"] = null; break;
            case "private-unverified": receipt["privateBytesVerified"] = false; break;
            case "foreign-receipt": receipt["operationId"] = Guid.NewGuid().ToString("D"); break;
            case "empty-claim-ack": status["journalAcknowledged"] = true; break;
            case "unknown-ack-kind": receipt["acknowledgementKind"] = "QueueEmpty"; break;
            case "early-ack": receipt["acknowledgedAt"] = "2026-10-21T00:00:00Z"; break;
            case "source-retained": first["sourceLocalMissing"] = false; break;
            case "later-target-overwritten": first["restartedTargetSha256"] = new string('c', 64); break;
            case "later-source-overwritten": first["resumedSourceSha256"] = new string('c', 64); break;
            case "reused-source-member": first["laterSourceMemberId"] = status["sourceMemberId"]!.DeepClone(); break;
            case "no-fresh-admission": first["freshRootsBeforeReplay"] = false; break;
            case "cleanup-failed": evidence["cleanup"]!["restoredObjects"] = 7; break;
            case "foreign-fixture-route": evidence["roots"]![0]!["rootId"] = Guid.NewGuid().ToString("D"); break;
            case "same-host-process": evidence["hostProcessAfterRestart"] = evidence["hostProcessBeforeRestart"]!.DeepClone(); break;
        }
        if (failure is "still-fenced" or "incomplete" or "missing-phase" or "phase-order" or "empty-claim-ack")
        {
            first["resumedStatus"] = status.DeepClone(); first["restartedStatus"] = status.DeepClone();
        }
        var result = await ExerciseAsync(evidence);
        Assert.AreEqual(1, result.ExitCode, result.Output + result.Error);
        StringAssert.Contains(result.Output, "\"accepted\":false");
    }

    [TestMethod]
    public async Task CompletedExecutionCanBeRetainedBeforeCleanupWithoutClaimingFinalSuccess()
    {
        var evidence = CompleteEvidence(); evidence["cleanup"] = null; evidence["fixtureRemoved"] = false;
        Assert.AreEqual(0, (await ExerciseAsync(evidence, beforeCleanup: true)).ExitCode);
        Assert.AreEqual(1, (await ExerciseAsync(evidence)).ExitCode);
        evidence["cases"]![0]!["sourceRemoteMissing"] = false;
        Assert.AreEqual(1, (await ExerciseAsync(evidence, beforeCleanup: true)).ExitCode);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeTransferHarnessRejectsDisabledEnvironmentBeforePublishingOrCreatingItsFixture(bool unset)
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-transfer-guard-" + Id()); Directory.CreateDirectory(root);
        try
        {
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (unset) start.Environment.Remove("MIRRORPULSE_NATIVE_TEST"); else start.Environment["MIRRORPULSE_NATIVE_TEST"] = "0";
            foreach (string argument in new[] { "-NoProfile", "-File", Path.Combine(SftpProtocolFixture.FindRepositoryRoot(), "eng", "verify-cli-transfers.ps1"),
                "-PackagePath", Path.Combine(root, "not-installed.mpadapter"), "-EvidencePath", Path.Combine(root, "evidence.json") }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            Assert.AreEqual(1, process.ExitCode, await output);
            StringAssert.Contains(await error, "explicitly enabled disposable native test environment");
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static JsonObject CompleteEvidence()
    {
        string left = Id(), right = Id(), target = Id(), firstInstance = Id(), secondInstance = Id();
        var receipts = new JsonArray(); var cases = new JsonArray();
        foreach (bool same in new[] { true, false })
        {
            string operation = Id(), sourceMember = Id(), targetMember = Id(), sourceItem = Id(), targetItem = Id();
            string destination = same ? right : target, scope = same ? "SameInstance" : "CrossInstance";
            int count = same ? 0 : 2; string hash = new('a', 64), later = new('b', 64);
            var status = JsonSerializer.SerializeToNode(new
            {
                operationId = operation,
                sourceRootId = left,
                targetRootId = destination,
                sourceInstanceId = firstInstance,
                targetInstanceId = same ? firstInstance : secondInstance,
                sourceMemberId = sourceMember,
                targetMemberId = targetMember,
                completed = true,
                rootsFenced = false,
                journalAcknowledged = count > 0,
                stage = "Completed",
                copyStartedAt = "2026-10-22T00:00:01Z",
                copyAcceptedAt = "2026-10-22T00:00:02Z",
                sourceDeleteStartedAt = "2026-10-22T00:00:03Z",
                sourceDeleteAcceptedAt = "2026-10-22T00:00:04Z",
                sourceNativeRetiredAt = "2026-10-22T00:00:05Z",
                sourceNameSettledAt = "2026-10-22T00:00:06Z"
            })!;
            cases.Add(JsonSerializer.SerializeToNode(new
            {
                scope,
                operationId = operation,
                sourceItemId = sourceItem,
                targetItemId = targetItem,
                laterSourceMemberId = Id(),
                laterSourceItemId = Id(),
                laterSourcePublicIdentityReused = false,
                status = status.DeepClone(),
                resumedStatus = status.DeepClone(),
                restartedStatus = status.DeepClone(),
                sourceLocalMissing = true,
                sourceRemoteMissing = true,
                freshRootsAfterMove = true,
                freshRootsBeforeReplay = true,
                laterRemoteSourcePreserved = true,
                sourceSha256 = hash,
                targetLocalSha256 = hash,
                targetRemoteSha256 = hash,
                laterTargetSha256 = later,
                restartedTargetSha256 = later,
                laterSourceSha256 = later,
                resumedSourceSha256 = later
            }));
            receipts.Add(JsonSerializer.SerializeToNode(new
            {
                scope,
                operationId = operation,
                sourceRootId = left,
                targetRootId = destination,
                sourceMemberId = sourceMember,
                targetMemberId = targetMember,
                sourceItemId = sourceItem,
                targetItemId = targetItem,
                sha256 = hash,
                privateBytesVerified = true,
                sourceNameEpoch = 2,
                originalJournalCount = count,
                acknowledgementKind = count > 0 ? "OriginalJournal" : "CommandWithoutCapturedNotifications",
                acknowledgedAt = "2026-10-22T00:00:07Z"
            }));
        }
        var evidence = JsonSerializer.SerializeToNode(new
        {
            schemaVersion = 1,
            sourceSha = new string('c', 40),
            runtime = "win-arm64",
            signedInstallation = true,
            packageSha256 = new string('d', 64),
            privateWorkerRuntime = true,
            workerProcessIds = WorkerProcessIds,
            roots = new[] { new { rootId = left, instanceId = firstInstance }, new { rootId = right, instanceId = firstInstance }, new { rootId = target, instanceId = secondInstance } },
            hostProcessBeforeRestart = 201,
            hostProcessAfterRestart = 202,
            fixtureRemoved = true,
            cleanup = new { exclusiveCatalogOwner = true, protectedNamespace = true, validatedObjects = 8, restoredObjects = 8, cloudRootUnregistered = true }
        })!.AsObject();
        evidence["cases"] = cases; evidence["audit"] = new JsonObject { ["schemaVersion"] = 1, ["exclusiveCatalogOwner"] = true, ["transfers"] = receipts };
        return evidence;
    }

    private static string Id() => Guid.NewGuid().ToString("D");

    private static async Task<(int ExitCode, string Output, string Error)> ExerciseAsync(JsonObject evidence, bool beforeCleanup = false,
        bool requireSingleRoot = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-transfer-policy-" + Id()); Directory.CreateDirectory(root);
        try
        {
            string input = Path.Combine(root, "evidence.json"), script = Path.Combine(root, "exercise.ps1");
            await File.WriteAllTextAsync(input, evidence.ToJsonString());
            await File.WriteAllTextAsync(script, """
                param([string]$Repository,[string]$InputPath,[string]$BeforeCleanup,[string]$RequireSingleRoot)
                $ErrorActionPreference='Stop'
                . (Join-Path $Repository 'eng/cli-transfer-evidence-policy.ps1')
                try {
                    Assert-CliTransferObservations -Evidence (Get-Content -Raw -LiteralPath $InputPath | ConvertFrom-Json) -BeforeCleanup:($BeforeCleanup -ceq 'True') -RequireSingleRoot:($RequireSingleRoot -ceq 'True')
                    @{accepted=$true}|ConvertTo-Json -Compress
                } catch { @{accepted=$false;message=$_.Exception.Message}|ConvertTo-Json -Compress; exit 1 }
                """);
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-File", script, SftpProtocolFixture.FindRepositoryRoot(), input, beforeCleanup.ToString(), requireSingleRoot.ToString() }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            return (process.ExitCode, await output, await error);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
