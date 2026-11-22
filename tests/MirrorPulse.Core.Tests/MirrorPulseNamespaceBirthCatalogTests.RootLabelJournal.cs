using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RootLabelJournalOwnsTheExactOriginalAndItsFenceAcrossThreeCatalogOwners(bool enabled)
    {
        using var fixture = new Fixture(); RootSubtreeFixture setup;
        MirrorPulseRootLabelJournalAcknowledgement first;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
            var receipt = await PrepareRootSubtreeFactsAsync(catalog, setup);
            await catalog.CompleteNamespaceDirectoryMoveAsync(receipt);
            var inventory = await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Registration.RootId);
            var root = inventory.Members.Single(member => member.Context.Member.MemberId == setup.Root.MemberId);
            Assert.IsNull(root.Identity.ItemId); // Captured directories do not acquire a fabricated file-identity acceptance.
            DateTimeOffset audit = receipt.Root.VerifiedAt.AddSeconds(3);
            var observation = RootLabelObservation(setup, audit);
            first = await catalog.StartRootLabelJournalAcknowledgementAsync(inventory, root.Context.Protection.RoleSid,
                audit, RootLabelNativeIdentity(setup, audit), observation, audit.AddSeconds(1));
            Assert.AreEqual(observation, first.Observation);
            Assert.IsNull(first.AcknowledgedAt);
            Assert.AreEqual(inventory.Fingerprint, first.Witness.InventoryFingerprint);
            Assert.AreEqual(setup.Root.LocalObject, first.Witness.LocalObject);
            Assert.IsGreaterThan(0L, first.Witness.NameEpoch);
            CollectionAssert.AreEqual(new[] { setup.Registration.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
            CollectionAssert.AreEqual(new[] { new MirrorPulseNamespacePendingOperation(observation.OperationId,
                setup.Registration.RootId, MirrorPulseNamespacePendingOperationKind.RootLabelJournal) },
                (await catalog.ReadNamespacePendingOperationsAsync(setup.Registration.RootId)).ToArray());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareManagedRootRenameAsync(setup.Registration.RootId, "Later"));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.TryRecordWorkerRequestAsync(observation.OperationId,
                setup.Registration.InstanceId, new byte[32]));
            Assert.AreEqual(first, await catalog.StartRootLabelJournalAcknowledgementAsync(inventory, root.Context.Protection.RoleSid,
                audit, RootLabelNativeIdentity(setup, audit), observation, audit.AddSeconds(20)));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteRootLabelJournalAcknowledgementAsync(
                first with { Observation = observation with { Sequence = observation.Sequence + 1 } }, first.StartedAt.AddSeconds(1)));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.CompleteRootLabelJournalAcknowledgementAsync(first, first.StartedAt.AddTicks(-1)));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.StartRootLabelJournalAcknowledgementAsync(
                inventory, root.Context.Protection.RoleSid, audit, RootLabelNativeIdentity(setup, audit), observation with { OperationId = Guid.NewGuid() }, audit.AddSeconds(2)));
            Assert.AreEqual("Renamed", (await catalog.ReadAdapterTopologyAsync()).Roots.Single().DirectoryName);
        }
        MirrorPulseRootLabelJournalAcknowledgement completed;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(first, await catalog.ReadRootLabelJournalAcknowledgementAsync(first.Observation.OperationId));
            CollectionAssert.AreEqual(new[] { first }, (await catalog.ReadPendingRootLabelJournalAcknowledgementsAsync(limit: 1)).ToArray());
            Assert.IsEmpty(await catalog.ReadPendingRootLabelJournalAcknowledgementsAsync(first.Observation.OperationId, limit: 1));
            completed = await catalog.CompleteRootLabelJournalAcknowledgementAsync(first, first.StartedAt.AddSeconds(1));
            Assert.AreEqual(first with { AcknowledgedAt = first.StartedAt.AddSeconds(1) }, completed);
            Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
            Assert.IsEmpty(await catalog.ReadNamespacePendingOperationsAsync(setup.Registration.RootId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(completed, await reopened.ReadRootLabelJournalAcknowledgementAsync(first.Observation.OperationId));
        Assert.AreEqual(completed, await reopened.CompleteRootLabelJournalAcknowledgementAsync(first, first.StartedAt.AddDays(1)));
        Assert.IsEmpty(await reopened.ReadPendingRootLabelJournalAcknowledgementsAsync());
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("unassigned")]
    [DataRow("foreign-item")]
    [DataRow("content")]
    [DataRow("file")]
    [DataRow("nested-target")]
    [DataRow("nested-source")]
    [DataRow("missing-source")]
    [DataRow("zero-sequence")]
    [DataRow("empty-id")]
    [DataRow("future-observation")]
    [DataRow("stale-inventory")]
    [DataRow("foreign-role")]
    [DataRow("unscoped")]
    [DataRow("foreign-native-item")]
    [DataRow("foreign-native-object")]
    [DataRow("foreign-native-name")]
    [DataRow("foreign-native-identity")]
    public async Task RootLabelJournalRefusesUnassignedForeignOrUnprovedOriginalsWithoutOwnership(string fault)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog);
        var receipt = await PrepareRootSubtreeFactsAsync(catalog, setup);
        await catalog.CompleteNamespaceDirectoryMoveAsync(receipt);
        var inventory = await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Registration.RootId);
        string role = inventory.Members.Single(member => member.Context.Member.MemberId == setup.Root.MemberId).Context.Protection.RoleSid;
        DateTimeOffset audit = receipt.Root.VerifiedAt.AddSeconds(3);
        var observation = RootLabelObservation(setup, audit);
        var nativeRoot = RootLabelNativeIdentity(setup, audit);
        switch (fault)
        {
            case "unassigned": observation = observation with { ItemId = null }; break;
            case "foreign-item": observation = observation with { ItemId = Guid.NewGuid() }; break;
            case "content": observation = observation with { Kind = MirrorPulseWorkerChangeKind.ContentUpdate }; break;
            case "file": observation = observation with { IsDirectory = false }; break;
            case "nested-target": observation = observation with { RelativePath = "Renamed/child" }; break;
            case "nested-source": observation = observation with { PreviousRelativePath = "Docs/child" }; break;
            case "missing-source": observation = observation with { PreviousRelativePath = null }; break;
            case "zero-sequence": observation = observation with { Sequence = 0 }; break;
            case "empty-id": observation = observation with { OperationId = Guid.Empty }; break;
            case "future-observation": observation = observation with { ObservedAt = audit.AddDays(1) }; break;
            case "stale-inventory": inventory = inventory with { Fingerprint = new string('A', 64) }; break;
            case "foreign-role": role += "-foreign"; break;
            case "foreign-native-item": nativeRoot = nativeRoot with { ItemId = Guid.NewGuid() }; break;
            case "foreign-native-object": nativeRoot = nativeRoot with { LocalObject = setup.Anchor.LocalObject }; break;
            case "foreign-native-name": nativeRoot = nativeRoot with { DirectoryName = "Other" }; break;
            case "foreign-native-identity": nativeRoot = nativeRoot with { PlaceholderIdentity = "Other" }; break;
            default: inventory = inventory with { ScopeRoot = null }; break;
        }
        if (fault is "foreign-item" or "stale-inventory" or "foreign-role" or "foreign-native-item" or "foreign-native-object" or "foreign-native-name" or "foreign-native-identity")
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.StartRootLabelJournalAcknowledgementAsync(inventory,
                role, audit, nativeRoot, observation, audit.AddSeconds(1)));
        else
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.StartRootLabelJournalAcknowledgementAsync(inventory,
                role, audit, nativeRoot, observation, audit.AddSeconds(1)));
        Assert.IsEmpty(await catalog.ReadPendingRootLabelJournalAcknowledgementsAsync());
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.AreEqual("Renamed", (await catalog.ReadAdapterTopologyAsync()).Roots.Single().DirectoryName);
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    private static MirrorPulseRootLabelJournalObservation RootLabelObservation(RootSubtreeFixture setup, DateTimeOffset observedAt) =>
        new(Guid.NewGuid(), 53, MirrorPulseWorkerChangeKind.Move, setup.Move.ItemId,
            setup.Move.Target.RelativePath, setup.Move.Source.RelativePath, true, observedAt);

    private static MirrorPulseRootLabelJournalNativeIdentity RootLabelNativeIdentity(RootSubtreeFixture setup, DateTimeOffset observedAt) =>
        new(setup.Move.ItemId, setup.Root.LocalObject, setup.Rename.Proof!.PlaceholderIdentity, setup.Move.Target.RelativePath, observedAt);
}
