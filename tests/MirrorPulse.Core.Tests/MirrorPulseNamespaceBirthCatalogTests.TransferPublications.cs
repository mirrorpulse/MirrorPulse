using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TransferPublicationRetainsSeparateTargetMembershipAndBothFencesAcrossOwners(bool sameInstance)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferCopy published; TransferPublicationFixture target;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            target = await PrepareTransferPublicationFixtureAsync(first, sameInstance);
            published = await first.RecordNamespaceTransferTargetPublicationAsync(target.Publication);
            Assert.AreEqual(target.Publication, published.TargetPublication);
            Assert.AreEqual(target.Member, await first.ReadNamespaceMemberChildAsync(published.Plan.TargetParent.Context.Member.MemberId, "moved.txt"));
            Assert.IsNull(await first.ReadNamespaceBirthContentCompletionAsync(target.Member.OriginId));
            Assert.IsNull(await first.ReadNamespaceBirthConversionAsync(target.Member.OriginId));
            Assert.HasCount(2, await first.ReadNamespaceDispatchFencedRootsAsync());
            Assert.HasCount(1, await first.ReadNamespacePendingOperationsAsync(published.Plan.TargetRoute.RootId));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(published, await second.ReadNamespaceTransferAsync(published.Plan.OperationId));
        Assert.AreEqual(published, await second.RecordNamespaceTransferTargetPublicationAsync(target.Publication));
        Assert.AreEqual(published.Plan.Source.Context.Member, await second.ReadNamespaceMemberAsync(published.Plan.Source.Context.Member.MemberId));
        Assert.IsNull(await second.ReadMutationAsync(published.Plan.DeleteOperationId));
        Assert.HasCount(2, await second.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("unstarted")]
    [DataRow("unobserved")]
    [DataRow("unprotected")]
    [DataRow("unregistered")]
    public async Task TransferPublicationCannotReplaceMissingNativeLifecycleFacts(string cut)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareTransferPublicationFixtureAsync(catalog, stopAt: cut);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceTransferTargetPublicationAsync(target.Publication));
        Assert.IsNull((await catalog.ReadNamespaceTransferAsync(target.Copy.Plan.OperationId))!.TargetPublication);
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsNull(await catalog.ReadMutationAsync(target.Copy.Plan.DeleteOperationId));
    }

    [TestMethod]
    [DataRow("item")]
    [DataRow("member")]
    [DataRow("binding")]
    [DataRow("epoch")]
    [DataRow("time")]
    public async Task TransferPublicationRejectsSubstitutedTargetFactsWithoutReleasingEitherRoot(string change)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareTransferPublicationFixtureAsync(catalog);
        var altered = change switch
        {
            "item" => target.Publication with { ItemId = target.Copy.Plan.Source.Identity.ItemId!.Value },
            "member" => target.Publication with { MemberId = target.Copy.Plan.Source.Context.Member.MemberId },
            "binding" => target.Publication with { LocalObject = target.Copy.Plan.Source.Context.Member.LocalObject },
            "epoch" => target.Publication with { ProtectionId = target.Copy.Plan.TargetParent.Context.Protection.ProtectionId },
            _ => target.Publication with { PublishedAt = target.Copy.Plan.PreparedAt }
        };
        try { await catalog.RecordNamespaceTransferTargetPublicationAsync(altered); Assert.Fail("A substituted publication was accepted."); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
        Assert.IsNull((await catalog.ReadNamespaceTransferAsync(target.Copy.Plan.OperationId))!.TargetPublication);
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.AreEqual(target.Publication, (await catalog.RecordNamespaceTransferTargetPublicationAsync(target.Publication)).TargetPublication);
    }

    [TestMethod]
    public async Task TransferPublicationFailureRetainsFirstMemberAndCanCommitOnlyTheOriginalReceiptAfterReopen()
    {
        using var fixture = new Fixture(); TransferPublicationFixture target;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            target = await PrepareTransferPublicationFixtureAsync(first);
            await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_transfer_publication BEFORE UPDATE ON namespace_transfers BEGIN SELECT RAISE(ABORT,'synthetic-publication-gap'); END;");
            await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => first.RecordNamespaceTransferTargetPublicationAsync(target.Publication));
            Assert.AreEqual(target.Member, await first.ReadNamespaceMemberAsync(target.Member.MemberId));
            Assert.IsNull((await first.ReadNamespaceTransferAsync(target.Copy.Plan.OperationId))!.TargetPublication);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_transfer_publication;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(target.Publication, (await second.RecordNamespaceTransferTargetPublicationAsync(target.Publication)).TargetPublication);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.RecordNamespaceTransferTargetPublicationAsync(target.Publication with { PublishedAt = target.Publication.PublishedAt.AddSeconds(1) }));
        Assert.HasCount(2, await second.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("disabled-source")]
    [DataRow("disabled-target")]
    [DataRow("configuration")]
    public async Task TransferPublicationContinuationStillRejectsChangedBackendAfterItsOwnBirthAdmission(string change)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareVerifiedTransferBirthAsync(catalog); await catalog.PrepareNamespaceTransferTargetBirthAsync(TransferBirth(copy));
        var topology = await catalog.ReadAdapterTopologyAsync();
        var changed = change == "disabled-source" ? copy.Plan.SourceRoute.InstanceId : copy.Plan.TargetRoute.InstanceId;
        await catalog.SaveAdapterTopologyAsync(topology with
        {
            Instances = topology.Instances.Select(instance => instance.InstanceId != changed ? instance : new AdapterInstance(
                instance.AdapterId, instance.InstallId, instance.InstanceId, instance.DisplayName,
                change == "configuration" ? new Dictionary<string, string> { ["endpoint"] = "another-backend" } : instance.Configuration,
                instance.CredentialReferences, instance.FileCacheDirectory, instance.TransferCacheDirectory, change == "configuration",
                instance.LifecycleState, instance.WorkerSessionId, instance.CreatedAt)).ToArray()
        });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceTransferTargetPublicationAsync(copy.Plan.OperationId));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task TransferTargetCannotBecomeAnOrdinaryImportOrBorrowAnotherMemberId()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareTransferPublicationFixtureAsync(catalog, stopAt: "unregistered");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RegisterNamespaceMemberAsync(target.Member with { MemberId = Guid.NewGuid() }));
        var conversion = Conversion(target.Copy.TargetBirth!.Birth, Start(target.Copy.TargetBirth.Plan));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthConversionAsync(conversion));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareMutationAsync(new(target.Member.OriginId,
            target.Copy.Plan.TargetRoute.InstanceId, target.Copy.Plan.TargetRoute.RootKey, MirrorPulseWorkerChangeKind.Create,
            "moved.txt", null, false, null, target.Copy.Plan.Length, target.Copy.Plan.Sha256, MirrorPulseMutationOrigin.Journal)));
        Assert.AreEqual(target.Member, await catalog.RegisterNamespaceMemberAsync(target.Member));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task TransferPublicationHistoryRemainsReadableAfterBothBackendsAreDisabled()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareTransferPublicationFixtureAsync(catalog);
        var published = await catalog.RecordNamespaceTransferTargetPublicationAsync(target.Publication);
        var topology = await catalog.ReadAdapterTopologyAsync();
        await catalog.SaveAdapterTopologyAsync(topology with
        {
            Instances = topology.Instances.Select(instance => new AdapterInstance(instance.AdapterId, instance.InstallId, instance.InstanceId,
                instance.DisplayName, instance.Configuration, instance.CredentialReferences, instance.FileCacheDirectory,
                instance.TransferCacheDirectory, false, instance.LifecycleState, instance.WorkerSessionId, instance.CreatedAt)).ToArray()
        });
        Assert.AreEqual(published, await catalog.ValidateNamespaceTransferTargetPublicationAsync(target.Copy.Plan.OperationId));
        Assert.AreEqual(published, await catalog.RecordNamespaceTransferTargetPublicationAsync(target.Publication));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsNull(await catalog.ReadMutationAsync(target.Copy.Plan.DeleteOperationId));
    }

    [TestMethod]
    public async Task TransferFirstPublicationRequiresTheLatestTargetEpochWithoutRewritingFirstMembership()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareTransferPublicationFixtureAsync(catalog);
        var latest = target.Protection with
        {
            ProtectionId = Guid.NewGuid(),
            PreviousProtectionId = target.Protection.ProtectionId,
            Verification = target.Protection.Verification with { ObservedAt = target.Protection.Verification.ObservedAt.AddMilliseconds(500) }
        };
        await catalog.RecordNamespaceBirthProtectionAsync(latest);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceTransferTargetPublicationAsync(target.Publication));
        var current = target.Publication with { ProtectionId = latest.ProtectionId };
        Assert.AreEqual(current, (await catalog.RecordNamespaceTransferTargetPublicationAsync(current)).TargetPublication);
        Assert.AreEqual(target.Member, await catalog.ReadNamespaceMemberAsync(target.Member.MemberId));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task TransferTargetFirstObservationRequiresActualAcceptedPlaceholderState()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var target = await PrepareTransferPublicationFixtureAsync(catalog, stopAt: "unobserved");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceBirthObservationAsync(target.Observation with { IsInSync = false }));
        Assert.IsNull(await catalog.ReadNamespaceBirthObservationAsync(target.Member.OriginId));
        Assert.AreEqual(target.Observation, await catalog.RecordNamespaceBirthObservationAsync(target.Observation));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("member")]
    [DataRow("observation")]
    [DataRow("protection")]
    [DataRow("publication-binding")]
    public async Task TransferPublicationReadersRejectMissingOrRehashedNativeReferences(string damage)
    {
        using var fixture = new Fixture(); TransferPublicationFixture target;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        { target = await PrepareTransferPublicationFixtureAsync(first); await first.RecordNamespaceTransferTargetPublicationAsync(target.Publication); }
        if (damage == "publication-binding")
            await ReplacePayloadValueAsync(fixture.Paths, "namespace_transfers", "operation_id", target.Copy.Plan.OperationId,
                target.Publication.LocalObject.LocalFileId.ToString("D"), Guid.NewGuid().ToString("D"));
        else
            await ExecuteSqlAsync(fixture.Paths, "PRAGMA foreign_keys=OFF; " + (damage switch
            {
                "member" => $"DELETE FROM namespace_members WHERE member_id='{target.Member.MemberId:D}';",
                "observation" => $"DELETE FROM namespace_birth_observations WHERE operation_id='{target.Member.OriginId:D}';",
                _ => $"DELETE FROM namespace_birth_protections WHERE protection_id='{target.Member.ProtectionId:D}';"
            }));
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceTransferAsync(target.Copy.Plan.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceDispatchFencedRootsAsync());
    }

    private sealed record TransferPublicationFixture(MirrorPulseNamespaceTransferCopy Copy, MirrorPulseNamespaceBirthObservation Observation,
        MirrorPulseNamespaceBirthProtection Protection, MirrorPulseNamespaceMemberRegistration Member, MirrorPulseNamespaceTransferTargetPublication Publication);

    private static async Task<TransferPublicationFixture> PrepareTransferPublicationFixtureAsync(MirrorPulseProductCatalog catalog,
        bool sameInstance = false, string? stopAt = null)
    {
        var verified = await PrepareVerifiedTransferBirthAsync(catalog, sameInstance); var target = TransferBirth(verified);
        var copy = await catalog.PrepareNamespaceTransferTargetBirthAsync(target);
        Assert.AreEqual(copy, await catalog.ValidateNamespaceTransferTargetPublicationAsync(copy.Plan.OperationId));
        var start = Start(target.Plan); var observation = Observation(target.Birth, target.Plan, start) with { IsInSync = true };
        var protection = new MirrorPulseNamespaceBirthProtection(1, Guid.NewGuid(), target.Birth.OperationId, null,
            target.Birth.ParentPermissionOperationId, target.Birth.RoleSid,
            new(observation.LocalObject, observation.OwnerSid, observation.BirthDacl, observation.ObservedAt.AddSeconds(1)), true, 1);
        var member = new MirrorPulseNamespaceMemberRegistration(1, target.Birth.OperationId, copy.Plan.TargetParent.Context.Member.MemberId,
            MirrorPulseNamespaceMemberOrigin.ProtectedBirth, target.Birth.OperationId, protection.ProtectionId, target.Birth.RootId,
            target.Birth.RelativePath, observation.LocalObject, false, observation.OwnerSid, protection.Verification.ObservedAt.AddSeconds(1));
        var publication = new MirrorPulseNamespaceTransferTargetPublication(1, copy.Plan.OperationId, target.Birth.OperationId,
            member.MemberId, target.Plan.ItemId, member.LocalObject, protection.ProtectionId, member.RegisteredAt.AddSeconds(1));
        // Native facts are fixture premises. These tests exercise real catalog ownership and transaction boundaries;
        // only the clean Windows native suite can establish production filesystem/SDK publication behavior.
        if (stopAt != "unstarted") await catalog.RecordNamespaceBirthStartAsync(start);
        if (stopAt is not ("unstarted" or "unobserved")) await catalog.RecordNamespaceBirthObservationAsync(observation);
        if (stopAt is not ("unstarted" or "unobserved" or "unprotected")) await catalog.RecordNamespaceBirthProtectionAsync(protection);
        if (stopAt is null) await catalog.RegisterNamespaceMemberAsync(member);
        return new(copy, observation, protection, member, publication);
    }
}
