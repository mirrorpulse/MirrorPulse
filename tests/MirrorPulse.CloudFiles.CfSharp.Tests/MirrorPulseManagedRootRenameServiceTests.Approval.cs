using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CfSharp;
using Microsoft.Data.Sqlite;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseManagedRootRenameServiceTests
{
    [TestMethod]
    [DataRow("file")]
    [DataRow("outside-source")]
    [DataRow("outside-target")]
    [DataRow("empty-identity")]
    [DataRow("oversized-identity")]
    [DataRow("unknown-source")]
    [DataRow("wrong-target")]
    [DataRow("descendant-source")]
    [DataRow("descendant-target")]
    [DataRow("sync-root")]
    [DataRow("missing-proof")]
    [DataRow("malformed-public-proof")]
    [DataRow("cancelled")]
    [DataRow("history-mismatch")]
    public async Task RejectedRenameApprovalLeavesAllOriginalIntentsAndEvidenceUntouched(string scenario)
    {
        await using var fixture = await Fixture.CreateAsync();
        var root = fixture.Roots[0];
        var intent = fixture.Intents[0];
        var router = new MirrorPulseRootRouter(fixture.Paths.SyncRootPath, [root]);
        byte[] identity = MirrorPulsePlaceholderIdentity.Create(root.InstanceId, $"mirrorpulse-root:{root.RootId}").ToCfSharp().Encode();
        if (scenario == "malformed-public-proof")
            await fixture.Catalog.SaveManagedRootRenameProofAsync(intent.OperationId, new(CloudPlaceholderIdentity.Decode(identity).ItemId,
                new(1, Guid.NewGuid(), Guid.NewGuid()), Convert.ToBase64String(identity), DateTimeOffset.UtcNow, Convert.ToBase64String([4, 5, 6])));
        if (scenario == "cancelled")
            await fixture.Catalog.TransitionManagedRootRenameAsync(intent.OperationId, MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled);
        if (scenario == "history-mismatch")
        {
            var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Catalog)!;
            await using SqliteCommand update = connection.CreateCommand();
            update.CommandText = "SELECT payload FROM managed_root_rename_history WHERE operation_id=$operation;";
            update.Parameters.AddWithValue("$operation", intent.OperationId.ToString("D"));
            JsonNode payload = JsonNode.Parse((string)(await update.ExecuteScalarAsync())!)!;
            payload["TargetName"] = "Another original target";
            update.CommandText = "UPDATE managed_root_rename_history SET payload=$payload WHERE operation_id=$operation;";
            update.Parameters.AddWithValue("$payload", payload.ToJsonString());
            Assert.AreEqual(1, await update.ExecuteNonQueryAsync());
        }
        string current = JsonSerializer.Serialize(await fixture.Catalog.ReadManagedRootRenamesAsync());
        string histories = JsonSerializer.Serialize(await fixture.Catalog.ReadManagedRootRenameHistoryAsync());
        string topology = JsonSerializer.Serialize(await fixture.Catalog.ReadAdapterTopologyAsync());
        var approval = new MirrorPulseManagedRootRenameApproval(router, fixture.Catalog);
        string source = scenario switch
        {
            "unknown-source" => "Unregistered",
            "descendant-source" => root.DirectoryName + "/child",
            "sync-root" => fixture.Paths.SyncRootPath,
            _ => root.DirectoryName
        };
        string target = scenario switch
        {
            "wrong-target" => "Wrong target",
            "descendant-target" => intent.TargetName + "/child",
            _ => intent.TargetName
        };
        ReadOnlyMemory<byte> callbackIdentity = scenario switch
        {
            "empty-identity" => ReadOnlyMemory<byte>.Empty,
            "oversized-identity" => new byte[SyncRootRegistrationOptions.MaxFileIdentityLength + 1],
            _ => identity
        };
        var result = await approval.EvaluateAsync(source, callbackIdentity,
            target, scenario != "file", scenario != "outside-source", scenario != "outside-target", CancellationToken.None);
        Assert.AreEqual(CloudProviderPolicyDecision.Deny, result);
        Assert.AreEqual(current, JsonSerializer.Serialize(await fixture.Catalog.ReadManagedRootRenamesAsync()));
        Assert.AreEqual(histories, JsonSerializer.Serialize(await fixture.Catalog.ReadManagedRootRenameHistoryAsync()));
        Assert.AreEqual(topology, JsonSerializer.Serialize(await fixture.Catalog.ReadAdapterTopologyAsync()));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task RenameApprovalCancellationRemainsCancellationWithoutReadingOrChangingOriginals()
    {
        await using var fixture = await Fixture.CreateAsync();
        string original = JsonSerializer.Serialize(await fixture.Catalog.ReadManagedRootRenameHistoryAsync());
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var approval = new MirrorPulseManagedRootRenameApproval(new(fixture.Paths.SyncRootPath, [fixture.Roots[0]]), fixture.Catalog);
        await Assert.ThrowsAsync<OperationCanceledException>(() => approval.EvaluateAsync("", ReadOnlyMemory<byte>.Empty, "", false,
            false, false, stop.Token).AsTask());
        Assert.AreEqual(original, JsonSerializer.Serialize(await fixture.Catalog.ReadManagedRootRenameHistoryAsync()));
    }
}
