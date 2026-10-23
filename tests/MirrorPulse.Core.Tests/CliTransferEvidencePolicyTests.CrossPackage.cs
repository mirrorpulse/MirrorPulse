using System.Text.Json;
using System.Text.Json.Nodes;

namespace MirrorPulse.Core.Tests;

public sealed partial class CliTransferEvidencePolicyTests
{
    [TestMethod]
    public async Task CrossPackagePolicyRequiresBothActualPackagesAndDirectionsWithoutLosingOriginalReceipts()
    {
        var result = await ExerciseAsync(CrossPackageEvidence(), requireSingleRoot: true);
        Assert.AreEqual(0, result.ExitCode, result.Output + result.Error);
    }

    [TestMethod]
    [DataRow("unsigned-webdav")]
    [DataRow("same-package")]
    [DataRow("same-worker-owner")]
    [DataRow("foreign-worker")]
    [DataRow("wrong-key")]
    [DataRow("missing-http")]
    [DataRow("missing-copy")]
    [DataRow("missing-delete")]
    [DataRow("running-http")]
    [DataRow("later-source-overwritten")]
    [DataRow("private-bytes")]
    public async Task SyntheticOrIncompleteCrossPackageEvidenceCannotBecomeAcceptance(string failure)
    {
        var evidence = CrossPackageEvidence();
        switch (failure)
        {
            case "unsigned-webdav": evidence["packages"]![1]!["isSigned"] = false; break;
            case "same-package": evidence["packages"]![1]!["adapterId"] = "com.mirrorpulse.adapter.local"; break;
            case "same-worker-owner": evidence["workerBindings"]![1]!["installId"] = evidence["packages"]![0]!["installId"]!.DeepClone(); break;
            case "foreign-worker": evidence["workerBindings"]![1]!["processId"] = 999; break;
            case "wrong-key": evidence["roots"]![1]!["rootKey"] = "local"; break;
            case "missing-http": evidence["webDavServer"] = null; break;
            case "missing-copy": evidence["webDavServer"]!["puts"] = 1; break;
            case "missing-delete": evidence["webDavServer"]!["deletes"] = 0; break;
            case "running-http": evidence["webDavServer"]!["stopped"] = false; break;
            case "later-source-overwritten": evidence["cases"]![0]!["resumedSourceSha256"] = new string('c', 64); break;
            case "private-bytes": evidence["audit"]!["transfers"]![1]!["privateBytesVerified"] = false; break;
        }
        var result = await ExerciseAsync(evidence, requireSingleRoot: true);
        Assert.AreEqual(1, result.ExitCode, result.Output + result.Error);
    }

    private static JsonObject CrossPackageEvidence()
    {
        var evidence = SingleRootEvidence(); evidence["schemaVersion"] = 3; evidence["audit"]!["schemaVersion"] = 3;
        evidence["roots"]![1]!["rootKey"] = "webdav";
        string local = Id(), webdav = Id();
        evidence["packages"] = JsonSerializer.SerializeToNode(new[]
        {
            new { installId = local, adapterId = "com.mirrorpulse.adapter.local", packageSha256 = evidence["packageSha256"]!.GetValue<string>(), isSigned = true },
            new { installId = webdav, adapterId = "com.mirrorpulse.adapter.webdav", packageSha256 = new string('b', 64), isSigned = true }
        });
        evidence["workerBindings"] = JsonSerializer.SerializeToNode(new[]
        {
            new { processId = 101, installId = local, adapterId = "com.mirrorpulse.adapter.local" },
            new { processId = 102, installId = webdav, adapterId = "com.mirrorpulse.adapter.webdav" }
        });
        evidence["webDavServer"] = JsonSerializer.SerializeToNode(new { schemaVersion = 1, stopped = true, fileBacked = true, reads = 2, puts = 2, deletes = 1 });
        return evidence;
    }
}
