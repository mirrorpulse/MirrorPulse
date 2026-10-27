using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("RootLabelBornRecoveryWitness")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OriginalLabelOwnedBornRecoveryRetainsItsInitializerAndCommitsOnlyItsAdmittedProtection(bool enabled)
    {
        // Real private SQLite transactions; every post-scope fact below is synthetic.
        // This test proves no native operation, SDK drain or readiness.
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, enabled);
        var before = await catalog.ReadNamespaceInitializationStatusAsync(setup.Initializer);
        var action = setup.Action;
        var legacy = new MirrorPulseNamespaceBornMemberRecoveryAction(1, Guid.NewGuid(), setup.Initializer, null,
            action.Child, action.Parent, action.TargetDacl, action.PreparedAt);
        var refusal = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBornMemberRecoveryAsync(legacy));
        Assert.AreEqual("An unresolved Label change has fenced this managed root.", refusal.Message);
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        Assert.AreEqual(SerializeBornOwnerTest(action), SerializeBornOwnerTest(await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId)));
        var projection = LabelOwnedBornProtection(action, action.PreparedAt.AddSeconds(1));
        var completed = await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId, projection, true, action.PreparedAt.AddSeconds(2));
        Assert.AreEqual(completed, await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
        Assert.AreEqual(projection, await catalog.ReadLatestNamespaceBirthProtectionAsync(action.Child.Member.OriginId));
        Assert.AreEqual(SerializeBornOwnerTest(before), SerializeBornOwnerTest(await catalog.ReadNamespaceInitializationStatusAsync(setup.Initializer)));
        Assert.AreEqual(setup.Tree.Rename, await catalog.ReadManagedRootRenameHistoryAsync(setup.Tree.Move.OperationId));
        Assert.IsNull(await catalog.ReadUnsealedRootLabelCancellationAsync(setup.Tree.Move.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(setup.Tree.Move.OperationId));
        Assert.IsTrue((await catalog.ReadNamespaceDispatchFencedRootsAsync()).Contains(setup.Tree.Move.RootId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("RootLabelBornRecoveryWitness")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task LabelOwnedBornActionMustSettleBeforeCancellationAndRemainsReplayableAfterRestart(bool enabled)
    {
        using var fixture = new Fixture();
        RootSubtreeFixture tree; MirrorPulseNamespaceBornMemberRecoveryAction action;
        MirrorPulseRootLabelUnsealedCancellation cancellation;
        MirrorPulseRootLabelCancellationRecoveryOwner owner; Guid initializer;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareLabelOwnedBornActionAsync(catalog, enabled);
            tree = setup.Tree; action = setup.Action; owner = setup.Owner; initializer = setup.Initializer;
            await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
            var refusal = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadRootLabelCancellationTreeAsync(tree.Move.OperationId));
            Assert.AreEqual("The root has unfinished born member recovery requiring its admitted initializer.", refusal.Message);
            var beforeCancellationProtection = LabelOwnedBornProtection(action, action.PreparedAt.AddSeconds(1));
            await catalog.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId, beforeCancellationProtection, true, action.PreparedAt.AddSeconds(2));
            var inventory = await catalog.ReadRootLabelCancellationTreeAsync(tree.Move.OperationId);
            DateTimeOffset notMoved = action.PreparedAt.AddSeconds(3), observed = notMoved.AddSeconds(1);
            var observation = new MirrorPulseRootLabelCancellation(1, tree.Move.OperationId, tree.Move.RootId,
                Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(tree.Rename.Proof!.DirectoryMoveEvidence!))),
                notMoved, observed.AddSeconds(1), false, false, false, 0, tree.Anchor.MemberId,
                inventory.Anchor.Context.Protection.ProtectionId, inventory.Anchor.Context.Verification with { ObservedAt = observed },
                inventory.Members.Select(snapshot => new MirrorPulseRootLabelCancellationMember(snapshot.Context.Member.MemberId,
                    snapshot.Context.Protection.ProtectionId, snapshot.Context.Verification with { ObservedAt = observed })).ToArray());
            cancellation = new(2, tree.Root.MemberId, observation,
                new(1, tree.Move.OperationId, observation.PublicEvidenceSha256, inventory.Members.Select(PartialRecoveryCancellationEntry).ToArray()))
            { RecoveryOwner = owner };
            await catalog.CancelUnsealedRootLabelAsync(cancellation);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.ValidateNamespaceBornMemberRecoveryExecutionAsync(action.ActionId));
        var projection = LabelOwnedBornProtection(action, action.PreparedAt.AddSeconds(1));
        var completed = await reopened.CompleteNamespaceBornMemberRecoveryAsync(action.ActionId, projection, true, action.PreparedAt.AddSeconds(2));
        Assert.AreEqual(completed, await reopened.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId));
        Assert.AreEqual(SerializeBornOwnerTest(cancellation), SerializeBornOwnerTest(await reopened.CancelUnsealedRootLabelAsync(cancellation)));
        Assert.AreEqual(owner, (await reopened.ReadUnsealedRootLabelCancellationAsync(tree.Move.OperationId))!.RecoveryOwner);
        Assert.IsNull((await reopened.ReadNamespaceInitializationStatusAsync(initializer)).Completion);
        Assert.IsTrue((await reopened.ReadNamespaceDispatchFencedRootsAsync()).Contains(tree.Move.RootId));
        Assert.AreEqual(MirrorPulseRootRenamePhase.Cancelled, (await reopened.ReadManagedRootRenameHistoryAsync(tree.Move.OperationId))!.Intent.Phase);
        Assert.IsNull(await reopened.ReadNamespaceMemberMoveAsync(tree.Move.OperationId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("RootLabelBornRecoveryWitness")]
    [DataRow("fingerprint")]
    [DataRow("initializer")]
    [DataRow("label")]
    public async Task LabelOwnedBornSuccessorCannotSubstituteItsOriginalCorrelation(string damage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareLabelOwnedBornActionAsync(catalog, true);
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(setup.Action);
        var successor = setup.Action with { ActionId = Guid.NewGuid(), PreviousActionId = setup.Action.ActionId, PreparedAt = setup.Action.PreparedAt.AddSeconds(1) };
        var json = JsonSerializer.SerializeToNode(successor, BornOwnerCatalogOptions)!.AsObject();
        var owner = json["RootLabelRecoveryOwner"]!.AsObject();
        if (damage == "label") owner["RootLabelOperationId"] = Guid.NewGuid();
        else if (damage == "initializer") owner["Initialization"]!["InitializationOperationId"] = Guid.NewGuid();
        else owner["Initialization"]!["IntentSha256"] = new string(setup.Owner.IntentSha256[0] == 'A' ? 'B' : 'A', 64);
        var altered = json.Deserialize<MirrorPulseNamespaceBornMemberRecoveryAction>(BornOwnerCatalogOptions)!;
        if (damage == "initializer") await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceBornMemberRecoveryAsync(altered));
        else if (damage == "label") await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.PrepareNamespaceBornMemberRecoveryAsync(altered));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBornMemberRecoveryAsync(altered));
        Assert.HasCount(1, await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(setup.Action.Child.Member.MemberId));
        Assert.AreEqual(SerializeBornOwnerTest(setup.Action), SerializeBornOwnerTest(await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(setup.Action.ActionId)));
        Assert.AreEqual(setup.Owner, await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Tree.Move.OperationId));
    }

    [TestMethod]
    [TestCategory("RootLabelBornRecoveryWitness")]
    public async Task VersionOneBornActionKeepsItsOriginalCanonicalPayloadWithoutALabelOwnerProperty()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var action = await RegisteredRecoveryActionAsync(catalog, setup.Member, setup.Move.PreparedAt.AddSeconds(5));
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        var legacy = new LegacyBornOwnerAction(action.Version, action.ActionId, action.InitializationOperationId, action.PreviousActionId,
            action.Child, action.Parent, action.TargetDacl, action.PreparedAt);
        byte[] expected = JsonSerializer.SerializeToUtf8Bytes(legacy, BornOwnerCatalogOptions);
        await using var raw = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = fixture.Paths.ProductCatalogDatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await raw.OpenAsync(); await using var query = raw.CreateCommand();
        query.CommandText = "SELECT payload FROM namespace_born_member_recovery_actions WHERE action_id=$id;";
        query.Parameters.AddWithValue("$id", action.ActionId.ToString("D"));
        byte[] actual = (byte[])(await query.ExecuteScalarAsync())!;
        CollectionAssert.AreEqual(expected, actual);
        Assert.IsFalse(JsonNode.Parse(actual)!.AsObject().ContainsKey("RootLabelRecoveryOwner"));
    }

    private sealed record LegacyBornOwnerAction(int Version, Guid ActionId, Guid InitializationOperationId, Guid? PreviousActionId,
        MirrorPulseNamespaceMemberRecoveryContext Child, MirrorPulseNamespaceMemberProtectionSnapshot Parent, string TargetDacl, DateTimeOffset PreparedAt);

    private static JsonSerializerOptions BornOwnerCatalogOptions => (JsonSerializerOptions)typeof(MirrorPulseProductCatalog)
        .GetField("TopologyJsonOptions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    private static string SerializeBornOwnerTest<T>(T value) => JsonSerializer.Serialize(value, BornOwnerCatalogOptions);

    private static async Task<(RootSubtreeFixture Tree, Guid Initializer, MirrorPulseRootLabelCancellationRecoveryOwner Owner,
        MirrorPulseNamespaceBornMemberRecoveryAction Action)> PrepareLabelOwnedBornActionAsync(MirrorPulseProductCatalog catalog, bool enabled)
    {
        var tree = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
        Guid initializer = await PreparePartialCancellationInitializerAsync(catalog, tree);
        await RotateCancellationAdmissionMemberAsync(catalog, tree.Anchor, tree.Move.PreparedAt.AddSeconds(-1));
        await RotateCancellationAdmissionMemberAsync(catalog, tree.Root, tree.Move.PreparedAt.AddSeconds(-1));
        var owner = (await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(tree.Move.OperationId))!;
        var child = (await catalog.ReadNamespaceMemberRecoveryLineageAsync(initializer, tree.Members[1].MemberId))[0];
        var parent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(tree.Root.MemberId))!;
        string target = child.BirthProtection!.Verification.Dacl.Replace(child.BirthProtection.RoleSid, parent.Context.Protection.RoleSid, StringComparison.Ordinal);
        var action = new MirrorPulseNamespaceBornMemberRecoveryAction(1, Guid.NewGuid(), initializer, null, child, parent, target, tree.Move.PreparedAt.AddSeconds(5));
        var json = JsonSerializer.SerializeToNode(action, BornOwnerCatalogOptions)!.AsObject();
        json["Version"] = 2;
        json["RootLabelRecoveryOwner"] = JsonSerializer.SerializeToNode(new { RootLabelOperationId = tree.Move.OperationId, Initialization = owner }, BornOwnerCatalogOptions);
        return (tree, initializer, owner, json.Deserialize<MirrorPulseNamespaceBornMemberRecoveryAction>(BornOwnerCatalogOptions)!);
    }

    private static MirrorPulseNamespaceBirthProtection LabelOwnedBornProtection(MirrorPulseNamespaceBornMemberRecoveryAction action, DateTimeOffset observedAt)
    {
        var parent = action.Parent.Context;
        return action.Child.BirthProtection! with
        {
            Version = 3,
            ProtectionId = action.ActionId,
            PreviousProtectionId = action.Child.BirthProtection!.ProtectionId,
            RoleSid = parent.Protection.RoleSid,
            Verification = action.Child.BirthProtection.Verification with { Dacl = action.TargetDacl, ObservedAt = observedAt },
            Placement = new(action.Child.Member.MemberId, action.Child.Location.CurrentPositionOperationId,
                parent.Member.MemberId, parent.Protection.ProtectionId, parent.Location.CurrentPositionOperationId, action.Child.Location.RelativePath)
        };
    }
}
