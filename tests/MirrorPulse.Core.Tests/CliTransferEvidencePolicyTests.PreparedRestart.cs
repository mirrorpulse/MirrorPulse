using System.Text.Json;
using System.Text.Json.Nodes;

namespace MirrorPulse.Core.Tests;

public sealed partial class CliTransferEvidencePolicyTests
{
    [TestMethod]
    [DataRow(null)]
    [DataRow("missing")]
    [DataRow("old-host")]
    [DataRow("old-worker")]
    [DataRow("wrong-package")]
    [DataRow("wrong-operation")]
    [DataRow("copy-started")]
    [DataRow("lost-fence")]
    [DataRow("target-already-written")]
    public async Task SignedFirstCopyRequiresTheOriginalUnstartedTransferOnBothSidesOfTheRestart(string? failure)
    {
        var evidence = SingleRootEvidence();
        var forward = evidence["cases"]![0]!;
        AddPreparedRestart(evidence, Id());
        var restart = forward["preparedRestart"]!;
        switch (failure)
        {
            case "missing": forward["preparedRestart"] = null; break;
            case "old-host": restart["resumedBy"] = restart["preparedBy"]!.DeepClone(); break;
            case "old-worker": restart["resumeWorkers"]![0]!["processId"] = evidence["workerProcessIds"]![0]!.DeepClone(); break;
            case "wrong-package": restart["resumeWorkers"]![1]!["adapterId"] = "com.mirrorpulse.adapter.webdav"; break;
            case "wrong-operation": forward["status"]!["sourceRelativePath"] = "foreign.bin"; UpdateCompletedStatus(forward); break;
            case "copy-started": restart["afterRestart"]!["status"]!["copyStartedAt"] = forward["status"]!["copyStartedAt"]!.DeepClone(); break;
            case "lost-fence": restart["afterRestart"]!["targetPending"] = new JsonArray(); break;
            case "target-already-written": restart["beforeRestart"]!["targetRemoteMissing"] = false; break;
        }
        var result = await ExerciseAsync(evidence, requireSingleRoot: true, liveDictionaries: true, requirePreparedRestart: true);
        Assert.AreEqual(failure is null ? 0 : 1, result.ExitCode, result.Output + result.Error);
    }

    private static void AddPreparedRestart(JsonObject evidence, string local, string? webdav = null)
    {
        var forward = evidence["cases"]![0]!;
        var completed = forward["status"]!.AsObject();
        evidence["localInstallId"] = local;
        completed["stage"] = "Completed";
        completed["sourceRelativePath"] = "Transfer Left/forward.bin";
        completed["targetRelativePath"] = "Transfer Right/forward-moved.bin";
        completed["preparedAt"] = completed["copyStartedAt"]!.DeepClone();
        UpdateCompletedStatus(forward);

        var prepared = completed.DeepClone().AsObject();
        prepared["stage"] = "Prepared";
        prepared["rootsFenced"] = true;
        prepared["completed"] = false;
        prepared["journalAcknowledged"] = false;
        foreach (string field in new[] { "targetMemberId", "copyStartedAt", "copyAcceptedAt", "sourceDeleteStartedAt",
            "sourceDeleteAcceptedAt", "sourceNativeRetiredAt", "sourceNameSettledAt" })
            prepared.Remove(field);

        var observation = new JsonObject
        {
            ["status"] = prepared.DeepClone(),
            ["sourcePending"] = new JsonArray(prepared.DeepClone()),
            ["targetPending"] = new JsonArray(prepared.DeepClone()),
            ["sourceLocalSha256"] = forward["sourceSha256"]!.DeepClone(),
            ["sourceRemoteSha256"] = forward["sourceSha256"]!.DeepClone(),
            ["targetLocalMissing"] = true,
            ["targetRemoteMissing"] = true
        };
        forward["preparedRestart"] = new JsonObject
        {
            ["preparedBy"] = evidence["hostProcessBeforeRestart"]!.DeepClone(),
            ["resumedBy"] = 203,
            ["command"] = prepared,
            ["beforeRestart"] = observation,
            ["afterRestart"] = observation.DeepClone(),
            ["resumeWorkers"] = JsonSerializer.SerializeToNode(new[]
            {
                new { processId = 103, installId = local, adapterId = "com.mirrorpulse.adapter.local" },
                new { processId = 104, installId = webdav ?? local, adapterId = webdav is null ? "com.mirrorpulse.adapter.local" : "com.mirrorpulse.adapter.webdav" }
            })
        };
    }

    private static void UpdateCompletedStatus(JsonNode transferCase)
    {
        transferCase["resumedStatus"] = transferCase["status"]!.DeepClone();
        transferCase["restartedStatus"] = transferCase["status"]!.DeepClone();
    }
}
