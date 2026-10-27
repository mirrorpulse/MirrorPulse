using System.Security.Cryptography;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("RootLabelCancellationAdmission")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SealedCancellationAcrossCatalogOwnersRetainsNewAnchorEpochWithoutGrantingOldMoveExecution(bool enabled)
    {
        using var fixture = new Fixture(); RootSubtreeFixture setup;
        MirrorPulseRootLabelCancellation cancellation; Guid rotatedAnchor;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
            await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
            rotatedAnchor = await RotateCancellationAdmissionMemberAsync(catalog, setup.Anchor, setup.Move.PreparedAt.AddSeconds(-1));
        }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            // Reproduce the CI boundary with real retained SQLite epochs: the
            // anchor belongs to a later role while sealed descendants remain old.
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceMemberMoveExecutionAsync(setup.Move.OperationId));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Root.MemberId));
            Assert.AreEqual(setup.Move, await catalog.ValidateUnstartedRootLabelCancellationAsync(setup.Move.OperationId));
            var tree = await catalog.ReadRootLabelCancellationTreeAsync(setup.Move.OperationId);
            Assert.AreEqual(setup.Root.MemberId, tree.RootMemberId); Assert.HasCount(3, tree.Members);
            Assert.AreEqual(rotatedAnchor, tree.Anchor.Context.Protection.ProtectionId);
            Assert.IsTrue(tree.Members.All(snapshot => snapshot.Context.Protection.RoleSid == setup.Move.RoleSid));
            Assert.AreEqual(setup.Move, await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
            Assert.IsNull(await catalog.ReadRootLabelCancellationAsync(setup.Move.OperationId));
            Assert.IsNull(await catalog.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
            Assert.AreEqual(setup.Rename, await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
            cancellation = RootCancellationFromTree(setup, tree);
            Assert.AreEqual(rotatedAnchor, cancellation.AnchorProtectionId);
            _ = await catalog.CancelUnstartedRootLabelAsync(cancellation);
            await AssertRootCancellationSettledAsync(catalog, setup, cancellation);
        }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            _ = await catalog.CancelUnstartedRootLabelAsync(cancellation);
            await AssertRootCancellationSettledAsync(catalog, setup, cancellation);
            Assert.AreEqual(rotatedAnchor, (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Anchor.MemberId))!.Context.Protection.ProtectionId);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateUnstartedRootLabelCancellationAsync(setup.Move.OperationId));
        }
    }

    [TestMethod]
    [TestCategory("RootLabelCancellationAdmission")]
    [DataRow("start")]
    [DataRow("member-epoch")]
    [DataRow("reservation")]
    public async Task CancellationAdmissionAndFinalTransactionBothRefuseChangedOriginalOwnership(string damage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRootSubtreeFixtureAsync(catalog);
        await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        Assert.AreEqual(setup.Move, await catalog.ValidateUnstartedRootLabelCancellationAsync(setup.Move.OperationId));
        var original = await RootCancellationAsync(catalog, setup);
        switch (damage)
        {
            case "start":
                await catalog.RecordNamespaceMemberMoveStartAsync(new(1, setup.Move.OperationId, setup.Move.PreparedAt.AddSeconds(1)));
                break;
            case "member-epoch":
                _ = await RotateCancellationAdmissionMemberAsync(catalog, setup.Root, setup.Move.PreparedAt.AddSeconds(-1));
                break;
            case "reservation":
                await ExecuteSqlAsync(fixture.Paths, $"DELETE FROM namespace_member_move_reservations WHERE operation_id='{setup.Move.OperationId:D}';");
                break;
            default: throw new AssertFailedException("Unknown cancellation admission damage.");
        }
        await RequireCancellationAdmissionRefusalAsync(() => catalog.ValidateUnstartedRootLabelCancellationAsync(setup.Move.OperationId));
        await RequireCancellationAdmissionRefusalAsync(() => catalog.CancelUnstartedRootLabelAsync(original));
        Assert.AreEqual(setup.Rename, await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId));
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var query = connection.CreateCommand(); query.CommandText = "SELECT COUNT(*) FROM root_label_unstarted_cancellations;";
        Assert.AreEqual(0L, await query.ExecuteScalarAsync());
    }

    [TestMethod]
    [TestCategory("RootLabelCancellationAdmission")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CancellationAdmissionRequiresAnExistingNonemptyOriginalOperation(bool empty)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        if (empty) await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.ValidateUnstartedRootLabelCancellationAsync(Guid.Empty));
        else await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.ValidateUnstartedRootLabelCancellationAsync(Guid.NewGuid()));
    }

    [TestMethod]
    [TestCategory("RootLabelCancellationAdmission")]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UnsealedCancellationUsesCompleteScopedMetadataAcrossNewAnchorEpochWithoutWeakeningCurrentLineage(bool enabled)
    {
        using var fixture = new Fixture(); RootSubtreeFixture setup; MirrorPulseRootLabelUnsealedCancellation cancellation;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareRootSubtreeFixtureAsync(catalog, enabled);
            _ = await RotateCancellationAdmissionMemberAsync(catalog, setup.Anchor, setup.Move.PreparedAt.AddSeconds(-1));
        }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Root.MemberId));
            var tree = await catalog.ReadRootLabelCancellationTreeAsync(setup.Move.OperationId);
            var entries = tree.Members.Select(snapshot =>
            {
                var context = snapshot.Context; var member = context.Member; var location = context.Location;
                return new MirrorPulseNamespaceDirectoryMoveEntry(member.MemberId, location.ParentMemberId, location.RelativePath, member.IsDirectory,
                    member.LocalObject, member.OwnerSid, context.Protection.ProtectionId, context.Protection.VerifiedAt, location.CompletedMoveOperationId,
                    snapshot.Identity, location.RelativePath.Count(character => character == '/'))
                { PreviousPositionOperationId = location.PositionOperationId };
            }).ToArray();
            var observation = RootCancellationFromTree(setup, tree);
            cancellation = new(1, setup.Root.MemberId, observation, new(1, setup.Move.OperationId, observation.PublicEvidenceSha256, entries));
            _ = await catalog.CancelUnsealedRootLabelAsync(cancellation);
            Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
            Assert.IsNull(await catalog.ReadNamespaceDirectoryMoveSnapshotAsync(setup.Move.OperationId));
        }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            _ = await catalog.CancelUnsealedRootLabelAsync(cancellation);
            Assert.AreEqual(MirrorPulseRootRenamePhase.Cancelled, (await catalog.ReadManagedRootRenameHistoryAsync(setup.Move.OperationId))!.Intent.Phase);
            Assert.IsFalse((await catalog.ReadNamespaceDispatchFencedRootsAsync()).Contains(setup.Move.RootId));
            Assert.IsNull(await catalog.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
        }
    }

    private static MirrorPulseRootLabelCancellation RootCancellationFromTree(RootSubtreeFixture setup, MirrorPulseRootLabelCancellationTree tree)
    {
        DateTimeOffset reconciled = setup.Move.PreparedAt.AddSeconds(2), observed = reconciled.AddSeconds(1);
        return new(1, setup.Move.OperationId, setup.Move.RootId,
            Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(setup.Rename.Proof!.DirectoryMoveEvidence!))),
            reconciled, observed.AddSeconds(1), false, false, false, 0, setup.Anchor.MemberId, tree.Anchor.Context.Protection.ProtectionId,
            tree.Anchor.Context.Verification with { ObservedAt = observed },
            tree.Members.Select(snapshot => new MirrorPulseRootLabelCancellationMember(snapshot.Context.Member.MemberId,
                snapshot.Context.Protection.ProtectionId, snapshot.Context.Verification with { ObservedAt = observed })).ToArray());
    }

    private static async Task<Guid> RotateCancellationAdmissionMemberAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseNamespaceMemberRegistration member, DateTimeOffset at)
    {
        var original = (await catalog.ReadNamespacePermissionChangeAsync(member.ProtectionId))!;
        var baseline = (await catalog.ReadNamespacePermissionBaselineAsync(original.Intent.EvidenceId))!;
        const string laterRole = "S-1-5-5-123-457";
        var rotation = original.Intent with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
            RoleSid = laterRole,
            ExpectedDacl = original.Intent.TargetDacl,
            TargetDacl = original.Intent.TargetDacl.Replace(original.Intent.RoleSid, laterRole, StringComparison.Ordinal),
            PreparedAt = at
        };
        await ProtectAsync(catalog, baseline, rotation);
        return rotation.OperationId;
    }

    private static async Task RequireCancellationAdmissionRefusalAsync(Func<Task> action)
    {
        try { await action(); Assert.Fail("Changed cancellation ownership was accepted."); }
        catch (Exception failure) when (failure is InvalidOperationException or InvalidDataException or FileNotFoundException) { }
    }
}
