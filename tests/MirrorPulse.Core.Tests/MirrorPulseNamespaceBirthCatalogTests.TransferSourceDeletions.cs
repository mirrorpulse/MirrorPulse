using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task TransferSourceDeleteRetainsItsOriginalClaimAndCorrelatedAcceptanceAcrossCatalogOwners()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferCopy accepted;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var target = await PrepareTransferPublicationFixtureAsync(first, sameInstance: true);
            var published = await first.RecordNamespaceTransferTargetPublicationAsync(target.Publication);
            var proof = FreshSourceDeleteTarget(target);
            var started = await first.StartNamespaceTransferSourceDeleteAsync(published.Plan.OperationId, proof,
                target.Protection.ProtectionId, proof.VerifiedAt.AddSeconds(1));
            Assert.IsNotNull(started.SourceDeletion); Assert.IsNull(started.SourceDeletion.AcceptedAt);
            Assert.AreEqual(started.Plan.DeleteOperationId, started.SourceDeletion.DeleteOperationId);
            Assert.AreEqual(proof, started.SourceDeletion.TargetAtStart);
            accepted = await first.RecordNamespaceTransferSourceDeleteAcceptedAsync(started.Plan.OperationId, null, proof.VerifiedAt.AddSeconds(2));
            Assert.IsNotNull(accepted.SourceDeletion!.AcceptedAt);
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(accepted, await second.ReadNamespaceTransferAsync(accepted.Plan.OperationId));
        Assert.AreEqual(accepted, await second.RecordNamespaceTransferSourceDeleteAcceptedAsync(accepted.Plan.OperationId,
            null, accepted.SourceDeletion!.AcceptedAt!.Value));
        Assert.HasCount(2, await second.ReadNamespaceDispatchFencedRootsAsync());
        Assert.AreEqual(accepted.Plan.Source.Context.Member, await second.ReadNamespaceMemberAsync(accepted.Plan.Source.Context.Member.MemberId));
        Assert.IsNull(await second.ReadNamespaceRetirementAsync(accepted.Plan.DeleteOperationId));
        Assert.IsNull(await second.ReadMutationAsync(accepted.Plan.DeleteOperationId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("unpublished")]
    [DataRow("historical-target")]
    [DataRow("local-only-source")]
    [DataRow("legacy-source")]
    [DataRow("foreign-epoch")]
    [DataRow("foreign-target")]
    [DataRow("early-start")]
    public async Task TransferSourceDeleteCannotBorrowMissingOrStaleConditionalOwnership(string change)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareTransferPublicationFixtureAsync(catalog, sameInstance: change != "legacy-source",
            localOnlySource: change == "local-only-source");
        if (change != "unpublished") await catalog.RecordNamespaceTransferTargetPublicationAsync(target.Publication);
        var proof = change == "historical-target" ? target.Copy.TargetVerification! : FreshSourceDeleteTarget(target);
        if (change == "foreign-target") proof = proof with { RemoteId = "unrelated-target" };
        try
        {
            await catalog.StartNamespaceTransferSourceDeleteAsync(target.Copy.Plan.OperationId, proof,
                change == "foreign-epoch" ? Guid.NewGuid() : target.Protection.ProtectionId,
                change == "early-start" ? target.Copy.Plan.PreparedAt : proof.VerifiedAt.AddSeconds(1));
            Assert.Fail("Source deletion borrowed missing or substituted original ownership.");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
        Assert.IsNull((await catalog.ReadNamespaceTransferAsync(target.Copy.Plan.OperationId))!.SourceDeletion);
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task TransferSourceDeleteUnknownStartCannotGrantAnotherDispatchOrAcceptWithoutAClaim()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareTransferPublicationFixtureAsync(catalog, sameInstance: true);
        await catalog.RecordNamespaceTransferTargetPublicationAsync(target.Publication);
        var proof = FreshSourceDeleteTarget(target); Guid id = target.Copy.Plan.OperationId;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceTransferSourceDeleteAcceptedAsync(id, null, proof.VerifiedAt));
        var started = await catalog.StartNamespaceTransferSourceDeleteAsync(id, proof, target.Protection.ProtectionId, proof.VerifiedAt);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.StartNamespaceTransferSourceDeleteAsync(id,
            proof, target.Protection.ProtectionId, proof.VerifiedAt.AddSeconds(1)));
        Assert.AreEqual(started, await catalog.ReadNamespaceTransferAsync(id));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("time")]
    [DataRow("revision")]
    [DataRow("new-dispatch")]
    public async Task TransferSourceDeleteFirstAcceptanceCannotBeRewritten(string change)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareTransferPublicationFixtureAsync(catalog, sameInstance: true);
        await catalog.RecordNamespaceTransferTargetPublicationAsync(target.Publication); var proof = FreshSourceDeleteTarget(target);
        Guid id = target.Copy.Plan.OperationId; var time = proof.VerifiedAt.AddSeconds(1);
        await catalog.StartNamespaceTransferSourceDeleteAsync(id, proof, target.Protection.ProtectionId, proof.VerifiedAt);
        var accepted = await catalog.RecordNamespaceTransferSourceDeleteAcceptedAsync(id, "original-deletion-reply", time);
        if (change == "new-dispatch")
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.StartNamespaceTransferSourceDeleteAsync(id, proof, target.Protection.ProtectionId, time));
        else
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceTransferSourceDeleteAcceptedAsync(id,
                change == "revision" ? "substituted-reply" : "original-deletion-reply", change == "time" ? time.AddSeconds(1) : time));
        Assert.AreEqual(accepted, await catalog.ReadNamespaceTransferAsync(id));
    }

    [TestMethod]
    public async Task TransferSourceDeleteWriteFailureDoesNotInventItsFirstDispatchClaim()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareTransferPublicationFixtureAsync(catalog, sameInstance: true);
        await catalog.RecordNamespaceTransferTargetPublicationAsync(target.Publication); var proof = FreshSourceDeleteTarget(target);
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_source_start BEFORE UPDATE ON namespace_transfers BEGIN SELECT RAISE(ABORT,'source-claim-gap'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.StartNamespaceTransferSourceDeleteAsync(target.Copy.Plan.OperationId,
            proof, target.Protection.ProtectionId, proof.VerifiedAt));
        Assert.IsNull((await catalog.ReadNamespaceTransferAsync(target.Copy.Plan.OperationId))!.SourceDeletion);
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_source_start;");
        Assert.IsNotNull((await catalog.StartNamespaceTransferSourceDeleteAsync(target.Copy.Plan.OperationId,
            proof, target.Protection.ProtectionId, proof.VerifiedAt)).SourceDeletion);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("changed-bytes")]
    [DataRow("replaced-id")]
    public async Task TransferFinalReadbackObservesCurrentBytesAndOpaqueIdentityInsteadOfHistoricalVerification(string failure)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareTransferPublicationFixtureAsync(catalog, sameInstance: true);
        var published = await catalog.RecordNamespaceTransferTargetPublicationAsync(target.Publication);
        var remote = new TransferTarget(TransferBytes) { Plan = published.Plan, Failure = failure };
        var verifier = new MirrorPulseTransferTargetVerifier(catalog, remote, remote, remote,
            new TransferClock(target.Publication.PublishedAt.AddSeconds(1)));
        Assert.AreEqual(published, await verifier.VerifyAsync(published.Plan.OperationId));
        Assert.AreEqual(0, remote.Reads);
        if (failure.Length == 0)
        {
            var fresh = await verifier.ReadCurrentAsync(published);
            Assert.IsTrue(fresh.VerifiedAt > published.TargetVerification!.VerifiedAt);
            Assert.AreEqual(published.TargetVerification.RemoteId, fresh.RemoteId);
        }
        else await Assert.ThrowsExactlyAsync<IOException>(() => verifier.ReadCurrentAsync(published));
        Assert.IsGreaterThan(0, remote.Reads);
        Assert.AreEqual(published, await catalog.ReadNamespaceTransferAsync(published.Plan.OperationId));
        Assert.IsNull((await catalog.ReadNamespaceTransferAsync(published.Plan.OperationId))!.SourceDeletion);
    }

    [TestMethod]
    public async Task TransferSourceDeleteUsesLatestTargetEpochWithoutChangingTheOriginalPublication()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareTransferPublicationFixtureAsync(catalog, sameInstance: true);
        await catalog.RecordNamespaceTransferTargetPublicationAsync(target.Publication);
        var latest = target.Protection with
        {
            ProtectionId = Guid.NewGuid(),
            PreviousProtectionId = target.Protection.ProtectionId,
            Verification = target.Protection.Verification with { ObservedAt = target.Protection.Verification.ObservedAt.AddMilliseconds(500) }
        };
        await catalog.RecordNamespaceBirthProtectionAsync(latest); var proof = FreshSourceDeleteTarget(target);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.StartNamespaceTransferSourceDeleteAsync(target.Copy.Plan.OperationId,
            proof, target.Protection.ProtectionId, proof.VerifiedAt));
        var started = await catalog.StartNamespaceTransferSourceDeleteAsync(target.Copy.Plan.OperationId, proof, latest.ProtectionId, proof.VerifiedAt);
        Assert.AreEqual(latest.ProtectionId, started.SourceDeletion!.TargetProtectionId);
        Assert.AreEqual(target.Publication, started.TargetPublication);
        Assert.AreEqual(target.Member, await catalog.ReadNamespaceMemberAsync(target.Member.MemberId));
    }

    [TestMethod]
    public async Task TransferSourceDeleteReadersRejectMissingOriginalDeleteEpochAfterReopen()
    {
        using var fixture = new Fixture(); Guid id, epoch;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var target = await PrepareTransferPublicationFixtureAsync(first, sameInstance: true); id = target.Copy.Plan.OperationId;
            await first.RecordNamespaceTransferTargetPublicationAsync(target.Publication);
            var latest = target.Protection with
            {
                ProtectionId = Guid.NewGuid(),
                PreviousProtectionId = target.Protection.ProtectionId,
                Verification = target.Protection.Verification with { ObservedAt = target.Protection.Verification.ObservedAt.AddMilliseconds(500) }
            };
            await first.RecordNamespaceBirthProtectionAsync(latest); epoch = latest.ProtectionId; var proof = FreshSourceDeleteTarget(target);
            await first.StartNamespaceTransferSourceDeleteAsync(id, proof, epoch, proof.VerifiedAt);
            await first.RecordNamespaceTransferSourceDeleteAcceptedAsync(id, null, proof.VerifiedAt.AddSeconds(1));
        }
        await ExecuteSqlAsync(fixture.Paths, $"DELETE FROM namespace_birth_protections WHERE protection_id='{epoch:D}';");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceTransferAsync(id));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task TransferSourceDeleteAcceptanceWriteFailureRetainsItsClaimUntilTheOriginalReplyCanBeSaved()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareTransferPublicationFixtureAsync(catalog, sameInstance: true);
        await catalog.RecordNamespaceTransferTargetPublicationAsync(target.Publication); var proof = FreshSourceDeleteTarget(target);
        var started = await catalog.StartNamespaceTransferSourceDeleteAsync(target.Copy.Plan.OperationId, proof, target.Protection.ProtectionId, proof.VerifiedAt);
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_source_receipt BEFORE UPDATE ON namespace_transfers BEGIN SELECT RAISE(ABORT,'source-receipt-gap'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.RecordNamespaceTransferSourceDeleteAcceptedAsync(started.Plan.OperationId,
            null, proof.VerifiedAt.AddSeconds(1)));
        Assert.AreEqual(started, await catalog.ReadNamespaceTransferAsync(started.Plan.OperationId));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_source_receipt;");
        Assert.IsNotNull((await catalog.RecordNamespaceTransferSourceDeleteAcceptedAsync(started.Plan.OperationId,
            null, proof.VerifiedAt.AddSeconds(1))).SourceDeletion!.AcceptedAt);
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task TransferSourceDeleteAcceptedHistorySurvivesDisabledScopesWithoutGrantingAnotherDelete()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareTransferPublicationFixtureAsync(catalog, sameInstance: true);
        await catalog.RecordNamespaceTransferTargetPublicationAsync(target.Publication); var proof = FreshSourceDeleteTarget(target);
        Guid id = target.Copy.Plan.OperationId;
        await catalog.StartNamespaceTransferSourceDeleteAsync(id, proof, target.Protection.ProtectionId, proof.VerifiedAt);
        var accepted = await catalog.RecordNamespaceTransferSourceDeleteAcceptedAsync(id, null, proof.VerifiedAt.AddSeconds(1));
        var topology = await catalog.ReadAdapterTopologyAsync();
        await catalog.SaveAdapterTopologyAsync(topology with
        {
            Instances = topology.Instances.Select(instance =>
            new MirrorPulse.Core.Contracts.AdapterInstance(instance.AdapterId, instance.InstallId, instance.InstanceId, instance.DisplayName,
                instance.Configuration, instance.CredentialReferences, instance.FileCacheDirectory, instance.TransferCacheDirectory,
                false, instance.LifecycleState, instance.WorkerSessionId, instance.CreatedAt)).ToArray()
        });
        Assert.AreEqual(accepted, await catalog.ValidateNamespaceTransferSourceDeleteAsync(id));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.StartNamespaceTransferSourceDeleteAsync(id, proof,
            target.Protection.ProtectionId, proof.VerifiedAt));
    }

    private static MirrorPulseTransferTargetVerification FreshSourceDeleteTarget(TransferPublicationFixture target) =>
        target.Copy.TargetVerification! with { VerifiedAt = target.Publication.PublishedAt.AddSeconds(1) };
}
