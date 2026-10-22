using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task TransferSourceRetirementSettlesOnlyItsOriginalNameAndKeepsBothFencesAcrossOwners()
    {
        using var fixture = new Fixture(); TransferPublicationFixture target; MirrorPulseNamespaceRetirement deleted;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            target = await PrepareAcceptedTransferSourceRetirementAsync(first);
            var proof = FreshTransferRetirementTarget(target);
            var prepared = await first.PrepareNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, proof,
                target.Protection.ProtectionId, proof.VerifiedAt);
            Assert.AreEqual(target.Copy.Plan.DeleteOperationId, prepared.OperationId);
            Assert.IsNull(prepared.StartedAt); Assert.IsNull(prepared.TransferReceipt);
            var started = await first.StartNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, TransferSourceProjection(target, proof.VerifiedAt.AddSeconds(1)), proof.VerifiedAt.AddSeconds(1));
            Assert.AreEqual(target.Copy.Plan.Source.Identity.ItemId, started.StartedItemId);
            deleted = await first.RecordNamespaceTransferSourceRetiredAsync(target.Copy.Plan.OperationId,
                TransferRetirementReceipt(target, started.StartedAt!.Value.AddSeconds(1)));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.RecordNamespaceTransferSourceRetiredAsync(target.Copy.Plan.OperationId,
                deleted.TransferReceipt! with { VerifiedAt = deleted.TransferReceipt.VerifiedAt.AddSeconds(1) }));
            Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.NativeDeleted, deleted.Phase);
            Assert.AreEqual(target.Copy.Plan.Source.Context.Member,
                await first.ReadNamespaceMemberChildAsync(target.Copy.Plan.SourceParent.Context.Member.MemberId, TransferSourceName(target)));
            Assert.HasCount(2, await first.ReadNamespaceDispatchFencedRootsAsync());
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(deleted, await second.ReadNamespaceRetirementAsync(deleted.OperationId));
        var completed = await second.CompleteNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, deleted.NativeDeletedAt!.Value.AddSeconds(1));
        Assert.AreEqual(MirrorPulseNamespaceRetirementPhase.Completed, completed.Phase);
        Assert.IsNull(await second.ReadNamespaceMemberChildAsync(target.Copy.Plan.SourceParent.Context.Member.MemberId, TransferSourceName(target)));
        Assert.AreEqual(target.Copy.Plan.Source.Context.Member, await second.ReadNamespaceMemberAsync(completed.Intent.MemberId));
        Assert.AreEqual(target.Member, await second.ReadNamespaceMemberChildAsync(target.Copy.Plan.TargetParent.Context.Member.MemberId, "moved.txt"));
        Assert.AreEqual(completed, await second.CompleteNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, DateTimeOffset.MinValue));
        Assert.HasCount(2, await second.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsNotNull(await second.ReadNamespaceTransferAsync(target.Copy.Plan.OperationId));
        Assert.IsNull(await second.ReadMutationAsync(completed.OperationId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("prepare")]
    [DataRow("validate")]
    [DataRow("start")]
    [DataRow("native")]
    [DataRow("complete")]
    public async Task TransferSourceRetirementCannotUseGenericControlledDeleteTransitions(string action)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareAcceptedTransferSourceRetirementAsync(catalog); var proof = FreshTransferRetirementTarget(target);
        var prepared = await catalog.PrepareNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, proof, target.Protection.ProtectionId, proof.VerifiedAt);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            _ = action switch
            {
                "prepare" => await catalog.PrepareNamespaceRetirementAsync(prepared.Intent),
                "validate" => await catalog.ValidateNamespaceRetirementExecutionAsync(prepared.OperationId),
                "start" => await catalog.RecordNamespaceRetirementStartAsync(prepared.OperationId, proof.VerifiedAt, target.Copy.Plan.Source.Identity.ItemId),
                "native" => await catalog.RecordNamespaceRetirementNativeDeletedAsync(prepared.OperationId, proof.VerifiedAt),
                _ => await catalog.CompleteNamespaceRetirementAsync(prepared.OperationId, proof.VerifiedAt),
            };
        });
        Assert.AreEqual(prepared, await catalog.ReadNamespaceRetirementAsync(prepared.OperationId));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TransferSourceRetirementCannotInventMissingOrAmbiguousRemoteDeleteAcceptance(bool started)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareTransferPublicationFixtureAsync(catalog, sameInstance: true);
        await catalog.RecordNamespaceTransferTargetPublicationAsync(target.Publication); var proof = FreshSourceDeleteTarget(target);
        if (started) await catalog.StartNamespaceTransferSourceDeleteAsync(target.Copy.Plan.OperationId, proof, target.Protection.ProtectionId, proof.VerifiedAt);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceTransferSourceRetirementAsync(
            target.Copy.Plan.OperationId, proof, target.Protection.ProtectionId, proof.VerifiedAt));
        Assert.IsNull(await catalog.ReadNamespaceRetirementAsync(target.Copy.Plan.DeleteOperationId));
    }

    [TestMethod]
    [DataRow("old-target")]
    [DataRow("other-target")]
    [DataRow("foreign-epoch")]
    [DataRow("early-time")]
    public async Task TransferSourceRetirementRequiresFreshOriginalTargetAfterAcceptedSourceDeletion(string failure)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareAcceptedTransferSourceRetirementAsync(catalog); var proof = FreshTransferRetirementTarget(target);
        if (failure == "old-target") proof = FreshSourceDeleteTarget(target);
        if (failure == "other-target") proof = proof with { RemoteId = "substituted-target" };
        try
        {
            await catalog.PrepareNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, proof,
                failure == "foreign-epoch" ? Guid.NewGuid() : target.Protection.ProtectionId,
                failure == "early-time" ? proof.VerifiedAt.AddSeconds(-1) : proof.VerifiedAt);
            Assert.Fail("A source retirement borrowed an inexact original target or chronology.");
        }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException) { }
        Assert.IsNull(await catalog.ReadNamespaceRetirementAsync(target.Copy.Plan.DeleteOperationId));
    }

    [TestMethod]
    [DataRow("no-start")]
    [DataRow("wrong-item")]
    [DataRow("wrong-binding")]
    [DataRow("not-missing")]
    [DataRow("not-tombstone")]
    [DataRow("wrong-parent")]
    [DataRow("old-parent")]
    public async Task TransferSourceRetirementCannotSettleANameWithoutTheOwnedNativeAndPublicReceipt(string failure)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareAcceptedTransferSourceRetirementAsync(catalog); var proof = FreshTransferRetirementTarget(target);
        var prepared = await catalog.PrepareNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, proof, target.Protection.ProtectionId, proof.VerifiedAt);
        if (failure != "no-start") await catalog.StartNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, TransferSourceProjection(target, proof.VerifiedAt.AddSeconds(1)), proof.VerifiedAt.AddSeconds(1));
        var receipt = TransferRetirementReceipt(target, proof.VerifiedAt.AddSeconds(2));
        receipt = failure switch
        {
            "wrong-item" => receipt with { OfficialItemId = Guid.NewGuid() },
            "wrong-binding" => receipt with { LocalObject = target.Copy.Plan.TargetParent.Context.Member.LocalObject },
            "not-missing" => receipt with { NativeMissing = false },
            "not-tombstone" => receipt with { OfficialTombstone = false },
            "wrong-parent" => receipt with { Parent = receipt.Parent with { LocalObject = target.Copy.Plan.TargetParent.Context.Member.LocalObject } },
            "old-parent" => receipt with { Parent = receipt.Parent with { ObservedAt = proof.VerifiedAt } },
            _ => receipt,
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.RecordNamespaceTransferSourceRetiredAsync(target.Copy.Plan.OperationId, receipt));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, receipt.VerifiedAt));
        Assert.IsNotNull(await catalog.ReadNamespaceMemberChildAsync(target.Copy.Plan.SourceParent.Context.Member.MemberId, TransferSourceName(target)));
        Assert.IsNull((await catalog.ReadNamespaceRetirementAsync(prepared.OperationId))!.TransferReceipt);
    }

    [TestMethod]
    public async Task TransferSourceRetirementSettlementWriteFailureRetainsNameReceiptAndBothFences()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareAcceptedTransferSourceRetirementAsync(catalog); var proof = FreshTransferRetirementTarget(target);
        await catalog.PrepareNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, proof, target.Protection.ProtectionId, proof.VerifiedAt);
        await catalog.StartNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, TransferSourceProjection(target, proof.VerifiedAt.AddSeconds(1)), proof.VerifiedAt.AddSeconds(1));
        var deleted = await catalog.RecordNamespaceTransferSourceRetiredAsync(target.Copy.Plan.OperationId, TransferRetirementReceipt(target, proof.VerifiedAt.AddSeconds(2)));
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_transfer_settlement BEFORE UPDATE ON namespace_member_retirements BEGIN SELECT RAISE(ABORT,'transfer-settlement-gap'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.CompleteNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, proof.VerifiedAt.AddSeconds(3)));
        Assert.AreEqual(deleted, await catalog.ReadNamespaceRetirementAsync(deleted.OperationId));
        Assert.IsNotNull(await catalog.ReadNamespaceMemberChildAsync(target.Copy.Plan.SourceParent.Context.Member.MemberId, TransferSourceName(target)));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task TransferSourceRetirementNativeReceiptWriteFailureDoesNotRemoveItsOriginalName()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareAcceptedTransferSourceRetirementAsync(catalog); var proof = FreshTransferRetirementTarget(target);
        await catalog.PrepareNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, proof, target.Protection.ProtectionId, proof.VerifiedAt);
        var started = await catalog.StartNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, TransferSourceProjection(target, proof.VerifiedAt.AddSeconds(1)), proof.VerifiedAt.AddSeconds(1));
        var receipt = TransferRetirementReceipt(target, proof.VerifiedAt.AddSeconds(2));
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_transfer_native_receipt BEFORE UPDATE ON namespace_member_retirements BEGIN SELECT RAISE(ABORT,'transfer-native-receipt-gap'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.RecordNamespaceTransferSourceRetiredAsync(target.Copy.Plan.OperationId, receipt));
        Assert.AreEqual(started, await catalog.ReadNamespaceRetirementAsync(started.OperationId));
        Assert.IsNotNull(await catalog.ReadNamespaceMemberChildAsync(target.Copy.Plan.SourceParent.Context.Member.MemberId, TransferSourceName(target)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceTransferSourceRetirementAsync(target.Copy.Plan.OperationId, receipt.VerifiedAt));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_transfer_native_receipt;");
        Assert.IsNotNull((await catalog.RecordNamespaceTransferSourceRetiredAsync(target.Copy.Plan.OperationId, receipt)).TransferReceipt);
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    private static async Task<TransferPublicationFixture> PrepareAcceptedTransferSourceRetirementAsync(MirrorPulseProductCatalog catalog)
    {
        var target = await PrepareTransferPublicationFixtureAsync(catalog, sameInstance: true);
        await catalog.RecordNamespaceTransferTargetPublicationAsync(target.Publication); var proof = FreshSourceDeleteTarget(target);
        await catalog.StartNamespaceTransferSourceDeleteAsync(target.Copy.Plan.OperationId, proof, target.Protection.ProtectionId, proof.VerifiedAt);
        await catalog.RecordNamespaceTransferSourceDeleteAcceptedAsync(target.Copy.Plan.OperationId, null, proof.VerifiedAt.AddSeconds(1));
        return target;
    }

    private static MirrorPulseTransferTargetVerification FreshTransferRetirementTarget(TransferPublicationFixture target) =>
        target.Copy.TargetVerification! with { VerifiedAt = target.Publication.PublishedAt.AddSeconds(4) };

    private static MirrorPulseNamespaceTransferRetirementReceipt TransferRetirementReceipt(TransferPublicationFixture target, DateTimeOffset time) =>
        new(1, target.Copy.Plan.OperationId, target.Copy.Plan.DeleteOperationId, target.Copy.Plan.Source.Identity.ItemId!.Value,
            target.Copy.Plan.Source.Context.Member.LocalObject, true, true,
            new(target.Copy.Plan.SourceParent.Context.Member.LocalObject, target.Copy.Plan.SourceParent.Context.Member.OwnerSid,
                target.Copy.Plan.SourceParent.ExpectedDacl, time), time);

    private static string TransferSourceName(TransferPublicationFixture target) => target.Copy.Plan.Source.Context.Location.RelativePath.Split('/')[^1];
}
