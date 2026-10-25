using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    private static readonly string[] RootSubtreeFirstHistoricalNames = ["Docs", "Renamed"];

    [TestMethod]
    [TestCategory("RootLabelSubtreeCatalog")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RootLabelAndWholeSubtreeSettleTogetherAndHistoricalReplayCannotRevertTheNextLabel(bool enabled)
    {
        using var fixture = new Fixture();
        RootSubtreeFixture setup;
        MirrorPulseNamespaceDirectoryMoveCompletion receipt;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
            receipt = await PrepareRootSubtreeFactsAsync(catalog, setup);
            Assert.AreEqual(setup.Rename.Intent.SourceName, (await catalog.ReadAdapterTopologyAsync()).Roots.Single().DirectoryName);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RenameManagedRootAsync(setup.Move.RootId, "Renamed"));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceMemberMoveAsync(receipt.Root));
            await catalog.CompleteNamespaceDirectoryMoveAsync(receipt);
            Assert.AreEqual(MirrorPulseRootRenamePhase.Completed, (await catalog.ReadManagedRootRenameAsync(setup.Move.RootId))!.Phase);
            Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
            Assert.IsNull(await catalog.ReadNamespaceMemberChildAsync(setup.Anchor.MemberId, "Docs"));
            Assert.AreEqual(setup.Root, await catalog.ReadNamespaceMemberChildAsync(setup.Anchor.MemberId, "Renamed"));
            var updated = (await catalog.ReadAdapterTopologyAsync()).Roots.Single();
            Assert.AreEqual(new RootRegistration(setup.Registration.AdapterId, setup.Registration.InstanceId, setup.Registration.RootId,
                setup.Registration.UniquenessKey, "Renamed", "Renamed", setup.Registration.CustomEntry, setup.Registration.State,
                setup.Registration.RegisteredAt, setup.Registration.IdentityScope), updated);
            foreach (var member in setup.Members)
            {
                Assert.AreEqual(member, await catalog.ReadNamespaceMemberAsync(member.MemberId));
                Assert.AreEqual("Renamed" + member.RelativePath["Docs".Length..], (await catalog.ReadNamespaceMemberLocationAsync(member.MemberId)).RelativePath);
            }
            var names = await catalog.ReadManagedRootNamesAsync();
            CollectionAssert.AreEquivalent(RootSubtreeFirstHistoricalNames, names.Select(name => name.DirectoryName).ToArray());
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = (await reopened.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId))!;
        Assert.AreEqual(setup.Rename.Proof, original.Proof);
        var next = await reopened.PrepareManagedRootRenameWithProofAsync(setup.Move.RootId, "Later", "Renamed",
            setup.Rename.Proof! with { CapturedAt = receipt.Root.VerifiedAt.AddSeconds(1) });
        var nextMove = setup.Move with
        {
            OperationId = next.Intent.OperationId,
            Source = setup.Move.Source with { RelativePath = "Renamed" },
            Target = setup.Move.Target with { RelativePath = "Later" },
            PreviousMoveOperationId = receipt.OperationId,
            SourcePositionOperationId = receipt.OperationId,
            PreparedAt = next.Proof!.CapturedAt.AddSeconds(1)
        };
        var nextReceipt = await PrepareRootSubtreeFactsAsync(reopened, setup with { Rename = next, Move = nextMove });
        await reopened.CompleteNamespaceDirectoryMoveAsync(nextReceipt);
        var later = await reopened.ReadManagedRootRenameAsync(setup.Move.RootId);
        await reopened.CompleteNamespaceDirectoryMoveAsync(receipt);
        Assert.AreEqual(later, await reopened.ReadManagedRootRenameAsync(setup.Move.RootId));
        Assert.AreEqual("Later", (await reopened.ReadAdapterTopologyAsync()).Roots.Single().DirectoryName);
        Assert.AreEqual("Later", (await reopened.ReadNamespaceMemberLocationAsync(setup.Root.MemberId)).RelativePath);
        Assert.AreEqual(original, await reopened.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        Assert.IsNotNull(await reopened.ReadNamespaceDirectoryMoveCompletionAsync(setup.Move.OperationId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [TestCategory("RootLabelSubtreeCatalog")]
    [DataRow("last-position")]
    [DataRow("topology")]
    [DataRow("label-history")]
    public async Task RootSubtreeSettlementInterruptionRollsBackEveryPositionNameLabelAndFence(string cut)
    {
        using var fixture = new Fixture();
        RootSubtreeFixture setup;
        MirrorPulseNamespaceDirectoryMoveCompletion receipt;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog);
            receipt = await PrepareRootSubtreeFactsAsync(catalog, setup);
            string target = cut switch
            {
                "last-position" => $"BEFORE INSERT ON namespace_member_positions WHEN NEW.member_id='{setup.Members[^1].MemberId:D}'",
                "topology" => "BEFORE UPDATE ON adapter_topology",
                _ => "BEFORE UPDATE ON managed_root_rename_history WHEN json_extract(NEW.payload,'$.Phase')=3"
            };
            await ExecuteSqlAsync(fixture.Paths, $"CREATE TRIGGER interrupt_root_subtree {target} BEGIN SELECT RAISE(ABORT,'original root settlement interruption'); END;");
            await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.CompleteNamespaceDirectoryMoveAsync(receipt));
            Assert.IsNull(await catalog.ReadNamespaceDirectoryMoveCompletionAsync(receipt.OperationId));
            Assert.IsNull(await catalog.ReadNamespaceMemberMoveCompletionAsync(receipt.OperationId));
            Assert.IsNotNull(await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
            Assert.AreEqual(MirrorPulseRootRenamePhase.NativeObserved, (await catalog.ReadManagedRootRenameAsync(setup.Move.RootId))!.Phase);
            Assert.AreEqual(setup.Registration, (await catalog.ReadAdapterTopologyAsync()).Roots.Single());
            Assert.IsEmpty(await catalog.ReadManagedRootNamesAsync());
            Assert.AreEqual(setup.Root, await catalog.ReadNamespaceMemberChildAsync(setup.Anchor.MemberId, "Docs"));
            Assert.IsNull(await catalog.ReadNamespaceMemberChildAsync(setup.Anchor.MemberId, "Renamed"));
            foreach (var member in setup.Members)
            {
                Assert.IsNull(await catalog.ReadNamespaceMemberPositionAsync(member.MemberId, receipt.OperationId));
                Assert.AreEqual(member.RelativePath, (await catalog.ReadNamespaceMemberLocationAsync(member.MemberId)).RelativePath);
            }
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER interrupt_root_subtree;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await reopened.CompleteNamespaceDirectoryMoveAsync(receipt);
        Assert.IsNull(await reopened.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual("Renamed", (await reopened.ReadAdapterTopologyAsync()).Roots.Single().DirectoryName);
        Assert.AreEqual(setup.Rename.Proof, (await reopened.ReadManagedRootRenameHistoryAsync(receipt.OperationId))!.Proof);
    }

    [TestMethod]
    [TestCategory("RootLabelSubtreeCatalog")]
    [DataRow("generic-version")]
    [DataRow("unrelated-operation")]
    [DataRow("proof")]
    [DataRow("item")]
    [DataRow("source")]
    [DataRow("target")]
    [DataRow("nested-path")]
    [DataRow("different-parent")]
    public async Task FirstLevelSubtreeAdmissionRequiresItsExactOriginalLabelProofAndAnchor(string mismatch)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog);
        var move = mismatch switch
        {
            "generic-version" => setup.Move with { Version = 1 },
            "unrelated-operation" => setup.Move with { OperationId = Guid.NewGuid() },
            "proof" => setup.Move with { DirectoryMoveEvidence = "AQ==" },
            "item" => setup.Move with { ItemId = Guid.NewGuid() },
            "source" => setup.Move with { Source = setup.Move.Source with { RelativePath = "Other" } },
            "target" => setup.Move with { Target = setup.Move.Target with { RelativePath = "Other" } },
            "nested-path" => setup.Move with { Target = setup.Move.Target with { RelativePath = "Docs/Other" } },
            _ => setup.Move with { Target = setup.Move.Target with { ParentMemberId = setup.Root.MemberId, ParentLocalObject = setup.Root.LocalObject } }
        };
        if (mismatch is "generic-version" or "nested-path" or "different-parent")
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceMemberMoveAsync(move));
        else if (mismatch == "unrelated-operation")
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.PrepareNamespaceMemberMoveAsync(move));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceMemberMoveAsync(move));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(move.OperationId));
        Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual(setup.Rename, await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Move, await catalog.PrepareNamespaceMemberMoveAsync(setup.Move));
    }

    [TestMethod]
    [TestCategory("RootLabelSubtreeCatalog")]
    public async Task SealedRootLabelCannotCancelOrSettleThroughTheLegacyMappingOnlyLifecycle()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog);
        await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.TransitionManagedRootRenameAsync(setup.Move.OperationId,
            MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled));
        Assert.AreEqual(setup.Move, await catalog.ValidateNamespaceMemberMoveExecutionAsync(setup.Move.OperationId));
        var receipt = await PrepareRootSubtreeFactsAsync(catalog, setup, observeLabel: false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceDirectoryMoveAsync(receipt));
        Assert.IsNotNull(await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.IsNull(await catalog.ReadNamespaceDirectoryMoveCompletionAsync(receipt.OperationId));
        Assert.AreEqual(setup.Registration, (await catalog.ReadAdapterTopologyAsync()).Roots.Single());
        await ObserveRootSubtreeAsync(catalog, setup);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.TransitionManagedRootRenameAsync(setup.Move.OperationId,
            MirrorPulseRootRenamePhase.NativeObserved, MirrorPulseRootRenamePhase.LocalProjected));
        await catalog.CompleteNamespaceDirectoryMoveAsync(receipt);
    }

    private sealed record RootSubtreeFixture(RootRegistration Registration, MirrorPulseRootRenameHistory Rename,
        MirrorPulseNamespaceMemberRegistration Anchor, MirrorPulseNamespaceMemberRegistration Root,
        MirrorPulseNamespaceMemberRegistration[] Members, MirrorPulseNamespaceMemberMoveIntent Move);

    private static async Task<RootSubtreeFixture> PrepareRootSubtreeFixtureAsync(MirrorPulseProductCatalog catalog, bool enabled = true)
    {
        var manifest = new AdapterManifest(1, AdapterId.Parse("example.root-subtree"), "Example", "1.0.0", new(1, 1),
            new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
            new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0", [new("docs", "Docs", "Docs", false)]);
        var installation = new InstalledAdapter(manifest, InstallId.New(), "C:\\synthetic\\installed",
            new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        await catalog.AddInstallationAsync(installation);
        await catalog.CreateInstanceAsync(installation.InstallId, "Example", new Dictionary<string, string>(), [],
            "C:\\synthetic\\files", "C:\\synthetic\\transfers", true);
        if (!enabled)
            await catalog.SetInstanceEnabledAsync((await catalog.ReadAdapterTopologyAsync()).Instances.Single().InstanceId, false);
        var registration = (await catalog.ReadAdapterTopologyAsync()).Roots.Single();
        var original = Baseline() with { RootId = registration.RootId };
        var proof = Protection(original); await ProtectAsync(catalog, original, proof);
        var root = await RegisterOriginalParentMembersAsync(catalog, original, proof);
        var anchor = (await catalog.ReadNamespaceMemberAsync(root.ParentMemberId!.Value))!;
        var childBorn = await AddBornAsync(catalog, Birth(original, proof) with { IsDirectory = true });
        var child = BornMember(childBorn, root); await catalog.RegisterNamespaceMemberAsync(child);
        var leafBorn = await AddBornAsync(catalog, NestedBirth(childBorn, "leaf.txt", false));
        var leaf = BornMember(leafBorn, child); await catalog.RegisterNamespaceMemberAsync(leaf);
        var capturedAt = leaf.RegisteredAt.AddSeconds(1);
        // Synthetic catalog evidence only. This test asserts neither public SDK decoding nor native authorization.
        var rename = await catalog.PrepareManagedRootRenameWithProofAsync(registration.RootId, "Renamed", "Docs",
            new(Guid.NewGuid(), root.LocalObject, "AQID", capturedAt, "AA=="));
        var move = new MirrorPulseNamespaceMemberMoveIntent(3, rename.Intent.OperationId, root.MemberId, root.ProtectionId,
            registration.RootId, root.LocalObject, rename.Proof!.ItemId, "local:managed-root", root.OwnerSid, proof.RoleSid,
            new(anchor.MemberId, anchor.LocalObject, anchor.ProtectionId, "Docs"),
            new(anchor.MemberId, anchor.LocalObject, anchor.ProtectionId, "Renamed"), capturedAt.AddSeconds(1))
        { DirectoryMoveEvidence = rename.Proof.DirectoryMoveEvidence };
        return new(registration, rename, anchor, root, [root, child, leaf], move);
    }

    private static Task<MirrorPulseRootRenameIntent> ObserveRootSubtreeAsync(MirrorPulseProductCatalog catalog, RootSubtreeFixture setup) =>
        catalog.ObserveManagedRootRenameAsync(setup.Move.OperationId,
            new(setup.Root.LocalObject, setup.Rename.Proof!.PlaceholderIdentity, setup.Move.Target.RelativePath, setup.Move.PreparedAt.AddSeconds(3)));

    private static async Task<MirrorPulseNamespaceDirectoryMoveCompletion> PrepareRootSubtreeFactsAsync(
        MirrorPulseProductCatalog catalog, RootSubtreeFixture setup, bool observeLabel = true)
    {
        await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        await catalog.RecordNamespaceMemberMoveStartAsync(new(1, setup.Move.OperationId, setup.Move.PreparedAt.AddSeconds(1)));
        var native = new MirrorPulseNamespaceMemberMoveObservation(2, setup.Move.OperationId, setup.Move.ItemId, setup.Move.RemoteId,
            "native-revision", setup.Move.Target.RelativePath, true, true, 1,
            new(setup.Root.LocalObject, Owner, Protected, setup.Move.PreparedAt.AddSeconds(2)),
            new(setup.Anchor.LocalObject, Owner, Protected, setup.Move.PreparedAt.AddSeconds(3)));
        await catalog.RecordNamespaceMemberMoveObservationAsync(native);
        if (observeLabel) await ObserveRootSubtreeAsync(catalog, setup);
        var projection = MoveProjection(native) with { Version = 2 };
        await catalog.RecordNamespaceMemberMoveProjectionAsync(projection);
        var rootCompletion = MoveCompletion(native, projection) with { Version = 2 };
        var snapshot = (await catalog.ReadNamespaceDirectoryMoveSnapshotAsync(setup.Move.OperationId))!;
        var entries = snapshot.Entries.Select(entry =>
        {
            if (entry.Depth == 0) return new MirrorPulseNamespaceDirectoryMoveMemberCompletion(entry.MemberId,
                setup.Anchor.MemberId, native, projection, rootCompletion);
            var parent = setup.Members.Single(member => member.MemberId == entry.ParentMemberId);
            var childNative = new MirrorPulseNamespaceMemberMoveObservation(1, setup.Move.OperationId, entry.Identity.ItemId!.Value,
                entry.Identity.RemoteId!, "native-revision", setup.Move.Target.RelativePath + entry.RelativePath[setup.Move.Source.RelativePath.Length..], entry.IsDirectory, true, 1,
                new(entry.LocalObject, Owner, Protected, native.Child.ObservedAt),
                new(parent.LocalObject, Owner, Protected, native.Parent.ObservedAt));
            var childProjection = MoveProjection(childNative);
            return new(entry.MemberId, parent.MemberId, childNative, childProjection, MoveCompletion(childNative, childProjection));
        }).ToArray();
        return new(1, setup.Move.OperationId, snapshot.PublicEvidenceSha256,
            new(MirrorPulseNamespaceDirectoryMovePublicOutcome.Projected, true, true, false, entries.Length, projection.ObservedAt), rootCompletion, entries);
    }
}
