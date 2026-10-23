using System.Text.Json.Nodes;

namespace MirrorPulse.Core.Tests;

public sealed partial class CliTransferEvidencePolicyTests
{
    [TestMethod]
    public async Task CurrentSingleRootEvidenceRequiresBothActualDirectionsAndRejectsTheLegacyTopology()
    {
        var actual = await ExerciseAsync(SingleRootEvidence(), requireSingleRoot: true);
        Assert.AreEqual(0, actual.ExitCode, actual.Output + actual.Error);
        Assert.AreEqual(1, (await ExerciseAsync(CompleteEvidence(), requireSingleRoot: true)).ExitCode);
    }

    [TestMethod]
    [DataRow("missing-direction")]
    [DataRow("duplicate-direction")]
    [DataRow("foreign-direction")]
    [DataRow("receipt-direction")]
    [DataRow("scope")]
    [DataRow("receipt-scope")]
    [DataRow("foreign-instance")]
    [DataRow("same-instance")]
    [DataRow("undeclared-root-key")]
    [DataRow("duplicate-side")]
    [DataRow("reverse-route")]
    [DataRow("audit-schema")]
    [DataRow("missing-root")]
    [DataRow("unverified-private-bytes")]
    [DataRow("later-generation-overwritten")]
    public async Task SingleRootEvidenceCannotSubstituteAnotherDirectionInstanceOrDeclaredRoot(string failure)
    {
        var evidence = SingleRootEvidence(); var first = evidence["cases"]![0]!;
        var receipt = evidence["audit"]!["transfers"]![0]!;
        switch (failure)
        {
            case "missing-direction": first["direction"] = null; break;
            case "duplicate-direction": evidence["cases"]![1]!["direction"] = "Forward"; break;
            case "foreign-direction": first["direction"] = "Other"; break;
            case "receipt-direction": receipt["direction"] = "Reverse"; break;
            case "scope": first["scope"] = "SameInstance"; break;
            case "receipt-scope": receipt["scope"] = "SameInstance"; break;
            case "foreign-instance": first["status"]!["sourceInstanceId"] = Id(); break;
            case "same-instance": evidence["roots"]![1]!["instanceId"] = evidence["roots"]![0]!["instanceId"]!.DeepClone(); break;
            case "undeclared-root-key": evidence["roots"]![1]!["rootKey"] = "right"; break;
            case "duplicate-side": evidence["roots"]![1]!["fixtureSide"] = "left"; break;
            case "reverse-route": evidence["cases"]![1]!["status"]!["sourceRootId"] = evidence["roots"]![0]!["rootId"]!.DeepClone(); break;
            case "audit-schema": evidence["audit"]!["schemaVersion"] = 1; break;
            case "missing-root": evidence["roots"]!.AsArray().RemoveAt(1); break;
            case "unverified-private-bytes": receipt["privateBytesVerified"] = false; break;
            case "later-generation-overwritten": first["resumedSourceSha256"] = new string('c', 64); break;
        }
        var actual = await ExerciseAsync(evidence, requireSingleRoot: true);
        Assert.AreEqual(1, actual.ExitCode, actual.Output + actual.Error);
    }

    private static JsonObject SingleRootEvidence()
    {
        var evidence = CompleteEvidence();
        string left = evidence["roots"]![0]!["rootId"]!.GetValue<string>(), right = evidence["roots"]![2]!["rootId"]!.GetValue<string>();
        string leftInstance = evidence["roots"]![0]!["instanceId"]!.GetValue<string>(), rightInstance = evidence["roots"]![2]!["instanceId"]!.GetValue<string>();
        evidence["schemaVersion"] = 2; evidence["audit"]!["schemaVersion"] = 2;
        evidence["roots"] = new JsonArray(
            new JsonObject { ["rootId"] = left, ["instanceId"] = leftInstance, ["rootKey"] = "local", ["fixtureSide"] = "left" },
            new JsonObject { ["rootId"] = right, ["instanceId"] = rightInstance, ["rootKey"] = "local", ["fixtureSide"] = "right" });
        for (int i = 0; i < 2; i++)
        {
            var item = evidence["cases"]![i]!; var receipt = evidence["audit"]!["transfers"]![i]!; var status = item["status"]!;
            item["scope"] = "CrossInstance"; receipt["scope"] = "CrossInstance";
            item["direction"] = i == 0 ? "Forward" : "Reverse"; receipt["direction"] = i == 0 ? "Forward" : "Reverse";
            status["sourceRootId"] = i == 0 ? left : right; status["targetRootId"] = i == 0 ? right : left;
            status["sourceInstanceId"] = i == 0 ? leftInstance : rightInstance; status["targetInstanceId"] = i == 0 ? rightInstance : leftInstance;
            receipt["sourceRootId"] = status["sourceRootId"]!.DeepClone(); receipt["targetRootId"] = status["targetRootId"]!.DeepClone();
            item["resumedStatus"] = status.DeepClone(); item["restartedStatus"] = status.DeepClone();
        }
        return evidence;
    }
}
