using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("RootLabelCancellationCatalog")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UnstartedRootCancellationSettlesBothOwnersAndPreservesOriginalProofAcrossThreeCatalogOwners(bool enabled)
    {
        using var fixture = new Fixture(); RootSubtreeFixture setup;
        MirrorPulseRootLabelCancellation cancellation;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
            await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
            cancellation = await RootCancellationAsync(catalog, setup);
            Assert.AreEqual(cancellation, await catalog.CancelUnstartedRootLabelAsync(cancellation));
            await AssertRootCancellationSettledAsync(catalog, setup, cancellation);
        }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(JsonSerializer.Serialize(cancellation), JsonSerializer.Serialize(await catalog.ReadRootLabelCancellationAsync(setup.Move.OperationId)));
            await AssertRootCancellationSettledAsync(catalog, setup, cancellation);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceMemberMoveStartAsync(new(1, setup.Move.OperationId, cancellation.VerifiedAt.AddSeconds(1))));
            // Metadata-only fixture: this is a new synthetic preparation, never SDK/native authority.
            var next = await catalog.PrepareManagedRootRenameWithProofAsync(setup.Registration.RootId, "Renamed", "Docs",
                setup.Rename.Proof! with { CapturedAt = cancellation.VerifiedAt.AddSeconds(1) });
            Assert.AreNotEqual(setup.Move.OperationId, next.Intent.OperationId);
            await catalog.PrepareNamespaceMemberMoveAsync(setup.Move with { OperationId = next.Intent.OperationId, PreparedAt = cancellation.VerifiedAt.AddSeconds(2) });
            Assert.AreEqual(next.Intent.OperationId, (await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId))!.OperationId);
        }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var current = (await catalog.ReadManagedRootRenameAsync(setup.Move.RootId))!;
            Assert.AreEqual(MirrorPulseRootRenamePhase.Prepared, current.Phase);
            Assert.AreNotEqual(cancellation.OperationId, current.OperationId);
            _ = await catalog.CancelUnstartedRootLabelAsync(cancellation);
            Assert.AreEqual(current, await catalog.ReadManagedRootRenameAsync(setup.Move.RootId));
            Assert.AreEqual(current.OperationId, (await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId))!.OperationId);
        }
    }

    [TestMethod]
    [TestCategory("RootLabelCancellationCatalog")]
    [DataRow("missing-member")]
    [DataRow("duplicate-member")]
    [DataRow("foreign-member")]
    [DataRow("binding")]
    [DataRow("descriptor")]
    [DataRow("anchor")]
    [DataRow("protection")]
    [DataRow("public-proof")]
    [DataRow("native-observed")]
    [DataRow("projected")]
    [DataRow("rescan")]
    [DataRow("updated")]
    [DataRow("stale-native")]
    [DataRow("early-terminal")]
    public async Task DamagedCancellationCannotReleaseEitherOriginalOwner(string damage)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog);
        await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        var cancellation = await RootCancellationAsync(catalog, setup);
        var members = cancellation.Members.ToArray();
        cancellation = damage switch
        {
            "missing-member" => cancellation with { Members = members[..^1] },
            "duplicate-member" => cancellation with { Members = [members[0], members[0], .. members.Skip(2)] },
            "foreign-member" => cancellation with { Members = [members[0] with { MemberId = Guid.NewGuid() }, .. members.Skip(1)] },
            "binding" => cancellation with { Members = [members[0] with { Native = members[0].Native with { LocalObject = setup.Anchor.LocalObject } }, .. members.Skip(1)] },
            "descriptor" => cancellation with { Members = [members[0] with { Native = members[0].Native with { Dacl = members[0].Native.Dacl + "(A;;FR;;;WD)" } }, .. members.Skip(1)] },
            "anchor" => cancellation with { Anchor = cancellation.Anchor with { LocalObject = setup.Root.LocalObject } },
            "protection" => cancellation with { AnchorProtectionId = Guid.NewGuid() },
            "public-proof" => cancellation with { PublicEvidenceSha256 = new string('A', 64) },
            "native-observed" => cancellation with { NativeMoveObserved = true },
            "projected" => cancellation with { DurableProjectionCommitted = true },
            "rescan" => cancellation with { RequiresFullRescan = true },
            "updated" => cancellation with { DurableStateEntriesUpdated = 1 },
            "stale-native" => cancellation with { Anchor = cancellation.Anchor with { ObservedAt = cancellation.PublicNotMovedAt.AddTicks(-1) } },
            "early-terminal" => cancellation with { VerifiedAt = cancellation.PublicNotMovedAt.AddTicks(-1) },
            _ => throw new AssertFailedException("Unknown damage.")
        };
        try { await catalog.CancelUnstartedRootLabelAsync(cancellation); Assert.Fail("Damaged proof was accepted."); }
        catch (Exception failure) when (failure is ArgumentException or InvalidOperationException or InvalidDataException or FileNotFoundException) { }
        Assert.AreEqual(setup.Rename.Intent, (await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId))!.Intent);
        Assert.AreEqual(setup.Move, await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.IsNull(await catalog.ReadRootLabelCancellationAsync(setup.Move.OperationId));
        CollectionAssert.Contains((await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), setup.Move.RootId);
    }

    [TestMethod]
    [TestCategory("RootLabelCancellationCatalog")]
    public async Task BeforeNativeStartAlreadyWrittenCannotBeCancelledFromAClaimedNotMovedResult()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog);
        await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        var cancellation = await RootCancellationAsync(catalog, setup);
        var start = new MirrorPulseNamespaceMemberMoveStart(1, setup.Move.OperationId, setup.Move.PreparedAt.AddSeconds(1));
        await catalog.RecordNamespaceMemberMoveStartAsync(start);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CancelUnstartedRootLabelAsync(cancellation));
        Assert.AreEqual(start, await catalog.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Move, await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.IsNull(await catalog.ReadRootLabelCancellationAsync(setup.Move.OperationId));
    }

    [TestMethod]
    [TestCategory("RootLabelCancellationCatalog")]
    public async Task FinalLabelWriteFailureRollsBackCancellationAndEveryReleasedNamespaceIndex()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog);
        await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        var cancellation = await RootCancellationAsync(catalog, setup);
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_root_cancel BEFORE UPDATE ON managed_root_rename_history BEGIN SELECT RAISE(ABORT,'injected cancellation write failure'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.CancelUnstartedRootLabelAsync(cancellation));
        Assert.AreEqual(setup.Move, await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual(setup.Rename.Intent, (await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId))!.Intent);
        Assert.IsNull(await catalog.ReadRootLabelCancellationAsync(setup.Move.OperationId));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_root_cancel;");
        _ = await catalog.CancelUnstartedRootLabelAsync(cancellation);
        await AssertRootCancellationSettledAsync(catalog, setup, cancellation);
    }

    private static async Task<MirrorPulseRootLabelCancellation> RootCancellationAsync(MirrorPulseProductCatalog catalog, RootSubtreeFixture setup)
    {
        var snapshot = (await catalog.ReadNamespaceDirectoryMoveSnapshotAsync(setup.Move.OperationId))!;
        var anchor = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Anchor.MemberId))!;
        DateTimeOffset reconciled = setup.Move.PreparedAt.AddSeconds(2), observed = reconciled.AddSeconds(1);
        var members = new List<MirrorPulseRootLabelCancellationMember>();
        foreach (var entry in snapshot.Entries)
        {
            var protection = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(entry.MemberId))!;
            members.Add(new(entry.MemberId, protection.Context.Protection.ProtectionId, protection.Context.Verification with { ObservedAt = observed }));
        }
        return new(1, setup.Move.OperationId, setup.Move.RootId, snapshot.PublicEvidenceSha256, reconciled, observed.AddSeconds(1),
            false, false, false, 0, setup.Anchor.MemberId, anchor.Context.Protection.ProtectionId,
            anchor.Context.Verification with { ObservedAt = observed }, members);
    }

    private static async Task AssertRootCancellationSettledAsync(MirrorPulseProductCatalog catalog, RootSubtreeFixture setup,
        MirrorPulseRootLabelCancellation cancellation)
    {
        var history = (await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId))!;
        Assert.AreEqual(MirrorPulseRootRenamePhase.Cancelled, history.Intent.Phase);
        Assert.AreEqual(setup.Move.OperationId, history.Intent.CancellationOperationId);
        Assert.AreEqual(cancellation.VerifiedAt, history.Intent.UpdatedAt); Assert.AreEqual(setup.Rename.Proof, history.Proof);
        Assert.IsNull(history.Observation); Assert.IsNull(await catalog.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.IsFalse((await catalog.ReadNamespaceDispatchFencedRootsAsync()).Contains(setup.Move.RootId));
        Assert.IsNull(await catalog.ReadNamespaceDirectoryMoveCompletionAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadPendingRootLabelJournalAcknowledgementAsync(setup.Move.RootId));
        Assert.AreEqual("Docs", (await catalog.ReadAdapterTopologyAsync()).Roots.Single().DirectoryName);
        foreach (var member in setup.Members) Assert.AreEqual(member, await catalog.ReadNamespaceMemberAsync(member.MemberId));
    }
}
