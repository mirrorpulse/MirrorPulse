using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false, 0, "Prepared")]
    [DataRow(false, 1, "CopyMayHaveBegun")]
    [DataRow(false, 2, "CopyAccepted")]
    [DataRow(true, 0, "Prepared")]
    [DataRow(true, 1, "CopyMayHaveBegun")]
    [DataRow(true, 2, "CopyAccepted")]
    public async Task TransferStatusRetainsBothScopesAndUnknownCopyAfterCatalogReopen(bool sameInstance, int cut, string stage)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferPlan plan;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = (await PrepareTransferFixtureAsync(catalog, sameInstance)).Plan;
            await catalog.PrepareNamespaceTransferAsync(plan);
            if (cut > 0) await catalog.StartNamespaceTransferCopyAsync(plan.OperationId, plan.PreparedAt.AddSeconds(1));
            if (cut > 1) await catalog.RecordNamespaceTransferCopyAcceptedAsync(plan.OperationId, "accepted-v1", plan.PreparedAt.AddSeconds(2));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = (await reopened.ReadNamespaceTransferStatusAsync(plan.OperationId))!;
        Assert.AreEqual(stage, actual.Stage); Assert.AreEqual(plan.SourceRoute.RootId, actual.SourceRootId);
        Assert.AreEqual(plan.TargetRoute.RootId, actual.TargetRootId); Assert.AreEqual(plan.SourceRoute.InstanceId, actual.SourceInstanceId);
        Assert.AreEqual(plan.TargetRoute.InstanceId, actual.TargetInstanceId); Assert.AreEqual(plan.Source.Context.Member.MemberId, actual.SourceMemberId);
        Assert.AreEqual(plan.Source.Context.Location.RelativePath, actual.SourceRelativePath); Assert.AreEqual(plan.TargetRelativePath, actual.TargetRelativePath);
        Assert.AreEqual(plan.PreparedAt, actual.PreparedAt); Assert.AreEqual(cut > 0, actual.CopyStartedAt is not null);
        Assert.AreEqual(cut > 1, actual.CopyAcceptedAt is not null); Assert.IsNull(actual.TargetMemberId);
        Assert.IsNull(actual.SourceDeleteStartedAt); Assert.IsNull(actual.SourceDeleteAcceptedAt);
        await RequirePendingTransferStatusAsync(reopened, actual);
        Assert.AreEqual(plan, (await reopened.ReadNamespaceTransferAsync(plan.OperationId))!.Plan);
        Assert.IsNull(await reopened.ReadMutationAsync(plan.CopyOperationId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow(0, "TargetBirthPrepared")]
    [DataRow(1, "TargetPublished")]
    [DataRow(2, "SourceDeleteMayHaveBegun")]
    [DataRow(3, "SourceDeleteAccepted")]
    [DataRow(4, "SourceRetirementPrepared")]
    [DataRow(5, "SourceRetirementMayHaveBegun")]
    [DataRow(6, "SourceNativeRetired")]
    [DataRow(7, "SourceNameSettled")]
    public async Task TransferStatusDoesNotTurnSourceNameSettlementIntoJournalAcknowledgementOrCompletion(int cut, string stage)
    {
        using var fixture = new Fixture(); TransferPublicationFixture target;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            target = await PrepareTransferPublicationFixtureAsync(catalog, sameInstance: true);
            Guid id = target.Copy.Plan.OperationId;
            var deletionProof = FreshSourceDeleteTarget(target); var retirementProof = FreshTransferRetirementTarget(target);
            if (cut >= 1) await catalog.RecordNamespaceTransferTargetPublicationAsync(target.Publication);
            if (cut >= 2) await catalog.StartNamespaceTransferSourceDeleteAsync(id, deletionProof, target.Protection.ProtectionId, deletionProof.VerifiedAt);
            if (cut >= 3) await catalog.RecordNamespaceTransferSourceDeleteAcceptedAsync(id, null, deletionProof.VerifiedAt.AddSeconds(1));
            if (cut >= 4) await catalog.PrepareNamespaceTransferSourceRetirementAsync(id, retirementProof, target.Protection.ProtectionId, retirementProof.VerifiedAt);
            if (cut >= 5) await catalog.StartNamespaceTransferSourceRetirementAsync(id, retirementProof.VerifiedAt.AddSeconds(1));
            if (cut >= 6) await catalog.RecordNamespaceTransferSourceRetiredAsync(id, TransferRetirementReceipt(target, retirementProof.VerifiedAt.AddSeconds(2)));
            if (cut >= 7) await catalog.CompleteNamespaceTransferSourceRetirementAsync(id, retirementProof.VerifiedAt.AddSeconds(3));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = (await reopened.ReadNamespaceTransferStatusAsync(target.Copy.Plan.OperationId))!;
        Assert.AreEqual(stage, actual.Stage); Assert.AreEqual(cut >= 1 ? target.Member.MemberId : (Guid?)null, actual.TargetMemberId);
        Assert.AreEqual(cut >= 2, actual.SourceDeleteStartedAt is not null); Assert.AreEqual(cut >= 3, actual.SourceDeleteAcceptedAt is not null);
        Assert.AreEqual(cut >= 6, actual.SourceNativeRetiredAt is not null); Assert.AreEqual(cut >= 7, actual.SourceNameSettledAt is not null);
        await RequirePendingTransferStatusAsync(reopened, actual);
        Assert.IsNull(await reopened.ReadMutationAsync(target.Copy.Plan.DeleteOperationId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("fence")]
    [DataRow("effect")]
    [DataRow("retirement")]
    public async Task TransferStatusRejectsCorruptedProgressAndFencesInsteadOfReportingACompletedMove(string damage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareAcceptedTransferSourceRetirementAsync(catalog); var proof = FreshTransferRetirementTarget(target);
        await catalog.PrepareNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, proof, target.Protection.ProtectionId, proof.VerifiedAt);
        await ExecuteSqlAsync(fixture.Paths, damage switch
        {
            "fingerprint" => "UPDATE namespace_transfers SET fingerprint=zeroblob(32);",
            "fence" => "DELETE FROM namespace_transfer_roots;",
            "effect" => "DELETE FROM namespace_transfer_effects;",
            _ => "UPDATE namespace_member_retirements SET fingerprint=zeroblob(32);"
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceTransferStatusAsync(target.Copy.Plan.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadPendingNamespaceTransferStatusesAsync(target.Copy.Plan.TargetRoute.RootId));
    }

    [TestMethod]
    public async Task TransferStatusQueriesRejectEmptyIdsAndCancellationWithoutCreatingAnOperation()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.ReadNamespaceTransferStatusAsync(Guid.Empty));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.ReadPendingNamespaceTransferStatusesAsync(new RootId(Guid.Empty)));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.ReadNamespaceTransferStatusAsync(Guid.NewGuid(), cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.ReadPendingNamespaceTransferStatusesAsync(RootId.New(), cancellation.Token));
        Assert.IsNull(await catalog.ReadNamespaceTransferStatusAsync(Guid.NewGuid()));
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    private static async Task RequirePendingTransferStatusAsync(MirrorPulseProductCatalog catalog, MirrorPulseNamespaceTransferStatus actual)
    {
        Assert.IsTrue(actual.RootsFenced); Assert.IsFalse(actual.JournalAcknowledged); Assert.IsFalse(actual.Completed);
        foreach (var root in new[] { actual.SourceRootId, actual.TargetRootId })
            Assert.AreEqual((await catalog.ReadPendingNamespaceTransferStatusesAsync(root)).Single(), actual);
        Assert.IsEmpty(await catalog.ReadPendingNamespaceTransferStatusesAsync(RootId.New()));
        CollectionAssert.AreEquivalent(new[] { actual.SourceRootId, actual.TargetRootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
    }
}
