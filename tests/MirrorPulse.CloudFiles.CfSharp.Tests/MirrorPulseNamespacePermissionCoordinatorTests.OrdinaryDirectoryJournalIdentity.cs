using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespacePermissionCoordinatorTests
{
    [TestMethod]
    [DataRow("item-only")]
    [DataRow("remote-only")]
    [DataRow("empty-item")]
    [DataRow("new-placeholder")]
    [DataRow("known-native-identity-lost")]
    public async Task OrdinaryDirectoryJournalMetadataCannotIntroduceOrReplaceANativeIdentity(string invalid)
    {
        using var fixture = new Fixture();
        var baseline = fixture.Baseline with { IsDirectory = true, RelativePath = "Docs" };
        var intent = fixture.Intent with { RelativePath = "Docs" };
        var lease = new ProtectedObjectLease(fixture);
        lease.Object.Current = lease.Object.Current with { IsDirectory = true, RelativePath = "Docs" };
        if (invalid == "known-native-identity-lost")
        {
            var known = new CloudPlaceholderIdentity(Guid.NewGuid(), "registered-directory");
            lease.Identity = new(known.ItemId, known.RemoteId, true, known);
        }
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(baseline, intent);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified,
            (await ApplyProtectedFixtureAsync(coordinator, intent.OperationId, lease, null)).Outcome);
        var anchor = await RegisterIdentityRecoveryParentAsync(catalog, fixture, null, string.Empty,
            baseline.LocalObject.SyncRootFileId);
        var member = new MirrorPulseNamespaceMemberRegistration(1, Guid.NewGuid(), anchor.MemberId,
            MirrorPulseNamespaceMemberOrigin.CapturedOriginal, baseline.EvidenceId, intent.OperationId, baseline.RootId,
            baseline.RelativePath, baseline.LocalObject, true, baseline.OwnerSid, DateTimeOffset.UtcNow);
        await catalog.RegisterNamespaceMemberAsync(member);
        var original = await catalog.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId);
        var registered = await catalog.ReadNamespaceMemberIdentityAsync(member.MemberId);
        int writes = lease.Object.Writes;
        var tracking = new CloudPlaceholderIdentity(Guid.NewGuid(), "local:tracked-directory");
        lease.Identity = invalid switch
        {
            "item-only" => new(tracking.ItemId, null, false, null),
            "remote-only" => new(null, tracking.RemoteId, false, null),
            "empty-item" => new(Guid.Empty, tracking.RemoteId, false, null),
            "new-placeholder" => new(tracking.ItemId, tracking.RemoteId, true, tracking),
            _ => new(tracking.ItemId, tracking.RemoteId, false, null)
        };
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.RecoveryRequired,
            (await ApplyProtectedFixtureAsync(coordinator, intent.OperationId, lease, null)).Outcome);
        Assert.AreEqual(writes, lease.Object.Writes); Assert.AreEqual(0, lease.Preparations);
        Assert.AreEqual(original, await catalog.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId));
        Assert.AreEqual(registered, await catalog.ReadNamespaceMemberIdentityAsync(member.MemberId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RegisteredOrdinaryDirectoryDoesNotAdoptOfficialJournalIdentityDuringAccessRecovery(bool reopen)
    {
        using var fixture = new Fixture();
        var baseline = fixture.Baseline with { IsDirectory = true, RelativePath = "Docs" };
        var intent = fixture.Intent with { RelativePath = "Docs" };
        var lease = new ProtectedObjectLease(fixture);
        lease.Object.Current = lease.Object.Current with { IsDirectory = true, RelativePath = "Docs" };
        MirrorPulseNamespaceMemberRegistration member;
        await using (var initial = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await initial.PrepareNamespacePermissionChangeAsync(baseline, intent);
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(initial);
            Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified,
                (await ApplyProtectedFixtureAsync(coordinator, intent.OperationId, lease, null)).Outcome);
            var anchor = await RegisterIdentityRecoveryParentAsync(initial, fixture, null, string.Empty,
                baseline.LocalObject.SyncRootFileId);
            member = new(1, Guid.NewGuid(), anchor.MemberId, MirrorPulseNamespaceMemberOrigin.CapturedOriginal,
                baseline.EvidenceId, intent.OperationId, baseline.RootId, baseline.RelativePath, baseline.LocalObject,
                true, baseline.OwnerSid, DateTimeOffset.UtcNow);
            await initial.RegisterNamespaceMemberAsync(member);
            if (!reopen) await AssertJournalMetadataDoesNotBecomeNativeIdentityAsync(initial, coordinator);
        }
        if (reopen)
        {
            await using var recovered = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(recovered);
            await AssertJournalMetadataDoesNotBecomeNativeIdentityAsync(recovered, coordinator);
        }

        async Task AssertJournalMetadataDoesNotBecomeNativeIdentityAsync(MirrorPulseProductCatalog catalog,
            MirrorPulseNamespacePermissionCoordinator coordinator)
        {
            var registered = await catalog.ReadNamespaceMemberIdentityAsync(member.MemberId);
            Assert.IsNotNull(registered); Assert.IsNull(registered.ItemId); Assert.IsNull(registered.RemoteId);
            Assert.IsNull(await catalog.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId));
            int writes = lease.Object.Writes;
            // The pinned SDK's journal assigns a durable local tracking pair to a
            // discovered ordinary directory. Its native object is still ordinary.
            // This fake lease asserts no native success; the real SQLite member,
            // original protection and reopened owner are the boundary under test.
            Guid localTrackingId = Guid.NewGuid();
            lease.Identity = new(localTrackingId, "local:" + localTrackingId.ToString("N"), false, null);
            Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.AlreadyVerified,
                (await ApplyProtectedFixtureAsync(coordinator, intent.OperationId, lease, null)).Outcome);
            Assert.AreEqual(writes, lease.Object.Writes);
            Assert.AreEqual(0, lease.Preparations, "Access recovery cannot convert a directory or manufacture a native identity.");
            Assert.IsFalse(lease.Identity.IsPlaceholder);
            Assert.AreEqual(registered, await catalog.ReadNamespaceMemberIdentityAsync(member.MemberId));
            Assert.IsNull(await catalog.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId),
                "A journal tracking pair cannot replace the original absence of a native placeholder identity.");
        }
    }
}
