using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("UnsealedRootCancellationCatalog")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UnsealedCancellationClosesOnlyItsLabelAcrossThreeOwnersWithoutInventingAMove(bool enabled)
    {
        using var fixture = new Fixture(); RootSubtreeFixture setup; MirrorPulseRootLabelUnsealedCancellation cancellation;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
            cancellation = await UnsealedCancellationAsync(catalog, setup);
            _ = await catalog.CancelUnsealedRootLabelAsync(cancellation);
            await AssertUnsealedCancellationAsync(catalog, setup, cancellation);
        }
        MirrorPulseRootRenameHistory later;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await AssertUnsealedCancellationAsync(catalog, setup, cancellation);
            later = await catalog.PrepareManagedRootRenameWithProofAsync(setup.Move.RootId, "Later", "Docs",
                setup.Rename.Proof! with { CapturedAt = cancellation.Observation.VerifiedAt.AddSeconds(1) });
            await catalog.PrepareNamespaceMemberMoveAsync(setup.Move with
            {
                OperationId = later.Intent.OperationId,
                Target = setup.Move.Target with { RelativePath = "Later" },
                PreparedAt = cancellation.Observation.VerifiedAt.AddSeconds(2)
            });
        }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(JsonSerializer.Serialize(cancellation), JsonSerializer.Serialize(await catalog.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId)));
            _ = await catalog.CancelUnsealedRootLabelAsync(cancellation);
            Assert.AreEqual(later, await catalog.ReadManagedRootRenameHistoryAsync(later.Intent.OperationId));
            Assert.AreEqual(later.Intent, await catalog.ReadManagedRootRenameAsync(setup.Move.RootId));
            Assert.AreEqual(later.Intent.OperationId, (await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId))!.OperationId);
            Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        }
    }

    [TestMethod]
    [TestCategory("UnsealedRootCancellationCatalog")]
    [DataRow("missing-member")]
    [DataRow("duplicate-member")]
    [DataRow("foreign-member")]
    [DataRow("binding")]
    [DataRow("descriptor")]
    [DataRow("protection")]
    [DataRow("parent")]
    [DataRow("depth")]
    [DataRow("identity")]
    [DataRow("source")]
    [DataRow("proof")]
    [DataRow("native-observed")]
    [DataRow("projection")]
    [DataRow("rescan")]
    [DataRow("updated")]
    [DataRow("stale-observation")]
    [DataRow("inventory-owner")]
    [DataRow("root-member")]
    public async Task DamagedUnsealedCancellationCannotCloseTheOriginalOrCreateMoveOwnership(string damage)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog); var original = await UnsealedCancellationAsync(catalog, setup);
        var entries = original.Inventory.Entries.ToArray(); var native = original.Observation.Members.ToArray();
        switch (damage)
        {
            case "missing-member": entries = entries[..^1]; native = native[..^1]; break;
            case "duplicate-member": entries[1] = entries[0]; break;
            case "foreign-member": entries[1] = entries[1] with { MemberId = Guid.NewGuid() }; break;
            case "binding": native[1] = native[1] with { Native = native[1].Native with { LocalObject = native[0].Native.LocalObject } }; break;
            case "descriptor": native[1] = native[1] with { Native = native[1].Native with { Dacl = "D:P(A;;FA;;;S-1-5-18)" } }; break;
            case "protection": native[1] = native[1] with { ProtectionId = Guid.NewGuid() }; break;
            case "parent": entries[1] = entries[1] with { ParentMemberId = original.Observation.AnchorMemberId }; break;
            case "depth": entries[1] = entries[1] with { Depth = 7 }; break;
            case "identity": entries[1] = entries[1] with { Identity = entries[1].Identity with { RemoteId = "foreign" } }; break;
            case "source": entries[0] = entries[0] with { RelativePath = "Foreign" }; break;
            case "stale-observation": native[1] = native[1] with { Native = native[1].Native with { ObservedAt = original.Observation.PublicNotMovedAt.AddTicks(-1) } }; break;
        }
        var observation = original.Observation with { Members = native };
        observation = damage switch
        {
            "proof" => observation with { PublicEvidenceSha256 = new string('F', 64) },
            "native-observed" => observation with { NativeMoveObserved = true },
            "projection" => observation with { DurableProjectionCommitted = true },
            "rescan" => observation with { RequiresFullRescan = true },
            "updated" => observation with { DurableStateEntriesUpdated = 1 },
            _ => observation
        };
        var candidate = original with
        {
            Observation = observation,
            RootMemberId = damage == "root-member" ? Guid.NewGuid() : original.RootMemberId,
            Inventory = original.Inventory with { Entries = entries, OperationId = damage == "inventory-owner" ? Guid.NewGuid() : original.Inventory.OperationId }
        };
        Exception? failure = null;
        try { _ = await catalog.CancelUnsealedRootLabelAsync(candidate); } catch (Exception rejected) { failure = rejected; }
        Assert.IsTrue(failure is ArgumentException or InvalidOperationException or InvalidDataException, failure?.ToString());
        Assert.AreEqual(setup.Rename, await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        CollectionAssert.Contains((await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), setup.Move.RootId);
    }

    [TestMethod]
    [TestCategory("UnsealedRootCancellationCatalog")]
    public async Task UnsealedCancellationRefusesAnActuallySealedOriginalInsteadOfReplacingItsOwners()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog); var cancellation = await UnsealedCancellationAsync(catalog, setup);
        await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CancelUnsealedRootLabelAsync(cancellation));
        Assert.AreEqual(setup.Move, await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual(setup.Rename, await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId));
    }

    [TestMethod]
    [TestCategory("UnsealedRootCancellationCatalog")]
    public async Task UnsealedCancellationFinalLabelWriteFailureRollsBackItsTerminalEvidenceAndOriginalOwner()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog); var cancellation = await UnsealedCancellationAsync(catalog, setup);
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_unsealed_cancel BEFORE UPDATE ON managed_root_rename_history BEGIN SELECT RAISE(ABORT,'unsealed-cancel-gap'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.CancelUnsealedRootLabelAsync(cancellation));
        Assert.AreEqual(setup.Rename, await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId));
        CollectionAssert.Contains((await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), setup.Move.RootId);
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_unsealed_cancel;");
        _ = await catalog.CancelUnsealedRootLabelAsync(cancellation);
        await AssertUnsealedCancellationAsync(catalog, setup, cancellation);
    }

    private static async Task<MirrorPulseRootLabelUnsealedCancellation> UnsealedCancellationAsync(MirrorPulseProductCatalog catalog, RootSubtreeFixture setup)
    {
        DateTimeOffset reconciled = setup.Move.PreparedAt.AddSeconds(2), observed = reconciled.AddSeconds(1);
        var entries = new List<MirrorPulseNamespaceDirectoryMoveEntry>(); var native = new List<MirrorPulseRootLabelCancellationMember>();
        foreach (var entry in await catalog.ReadNamespaceMemberTreeAsync(setup.Root.MemberId))
        {
            var snapshot = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(entry.MemberId))!;
            var member = snapshot.Context.Member; var location = snapshot.Context.Location; var protection = snapshot.Context.Protection;
            entries.Add(new(member.MemberId, location.ParentMemberId, location.RelativePath, member.IsDirectory, member.LocalObject,
                member.OwnerSid, protection.ProtectionId, protection.VerifiedAt, location.CompletedMoveOperationId, snapshot.Identity, entry.Depth)
            { PreviousPositionOperationId = location.PositionOperationId });
            native.Add(new(member.MemberId, protection.ProtectionId, snapshot.Context.Verification with { ObservedAt = observed }));
        }
        var anchor = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Anchor.MemberId))!;
        string hash = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(setup.Rename.Proof!.DirectoryMoveEvidence!)));
        var observation = new MirrorPulseRootLabelCancellation(1, setup.Move.OperationId, setup.Move.RootId, hash, reconciled,
            observed.AddSeconds(1), false, false, false, 0, setup.Anchor.MemberId, anchor.Context.Protection.ProtectionId,
            anchor.Context.Verification with { ObservedAt = observed }, native.AsReadOnly());
        return new(1, setup.Root.MemberId, observation, new(1, setup.Move.OperationId, hash, entries.AsReadOnly()));
    }

    private static async Task AssertUnsealedCancellationAsync(MirrorPulseProductCatalog catalog, RootSubtreeFixture setup,
        MirrorPulseRootLabelUnsealedCancellation cancellation)
    {
        var history = (await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId))!;
        Assert.AreEqual(MirrorPulseRootRenamePhase.Cancelled, history.Intent.Phase);
        Assert.AreEqual(setup.Move.OperationId, history.Intent.UnsealedCancellationOperationId); Assert.IsNull(history.Intent.CancellationOperationId);
        Assert.AreEqual(cancellation.Observation.VerifiedAt, history.Intent.UpdatedAt); Assert.AreEqual(setup.Rename.Proof, history.Proof); Assert.IsNull(history.Observation);
        Assert.AreEqual(JsonSerializer.Serialize(cancellation), JsonSerializer.Serialize(await catalog.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId)));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceDirectoryMoveSnapshotAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
        Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.IsFalse((await catalog.ReadNamespaceDispatchFencedRootsAsync()).Contains(setup.Move.RootId));
        foreach (var member in setup.Members) Assert.AreEqual(member, await catalog.ReadNamespaceMemberAsync(member.MemberId));
        Assert.AreEqual("Docs", (await catalog.ReadAdapterTopologyAsync()).Roots.Single().DirectoryName);
    }
}
