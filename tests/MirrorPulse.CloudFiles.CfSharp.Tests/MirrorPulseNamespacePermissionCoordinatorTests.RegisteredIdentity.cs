using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespacePermissionCoordinatorTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RegisteredOriginalUsesConfirmedCurrentIdentityWithoutReplacingItsFirstIdentity(bool ordinary)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var first = new CloudPlaceholderIdentity(Guid.NewGuid(), "local-first");
        var lease = new ProtectedObjectLease(fixture);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified,
            (await ApplyProtectedFixtureAsync(coordinator, fixture.Intent.OperationId, lease, first)).Outcome);
        var retained = await catalog.ReadNamespacePermissionLocalIdentityAsync(fixture.Baseline.EvidenceId);
        var parent = await RegisterIdentityRecoveryParentAsync(catalog, fixture, null, string.Empty,
            fixture.Baseline.LocalObject.SyncRootFileId);
        parent = await RegisterIdentityRecoveryParentAsync(catalog, fixture, parent, "Docs", Guid.NewGuid());
        var member = new MirrorPulseNamespaceMemberRegistration(1, Guid.NewGuid(), parent.MemberId,
            MirrorPulseNamespaceMemberOrigin.CapturedOriginal, fixture.Baseline.EvidenceId, fixture.Intent.OperationId,
            fixture.Baseline.RootId, fixture.Baseline.RelativePath, fixture.Baseline.LocalObject, false, Owner, DateTimeOffset.UtcNow);
        await catalog.RegisterNamespaceMemberAsync(member);
        var manifest = new AdapterManifest(1, AdapterId.Parse("example.permission-identity"), "Example", "1.0.0", new(1, 1),
            new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
            new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0", [new("docs", "Docs", "Docs", false)]);
        var installation = new InstalledAdapter(manifest, InstallId.New(), Path.GetTempPath(), new(new string('A', 64)),
            AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        var instance = new AdapterInstance(manifest.AdapterId, installation.InstallId, InstanceId.New(), "Storage",
            new Dictionary<string, string>(), [], Path.GetTempPath(), Path.GetTempPath(), true, AdapterLifecycleState.Configured, null, DateTimeOffset.UtcNow);
        var root = new RootRegistration(manifest.AdapterId, instance.InstanceId, member.RootId!.Value, "docs", "Docs", "Docs", false,
            RootRegistrationState.Active, DateTimeOffset.UtcNow, RootIdentityScope.InstanceRoot);
        await catalog.SaveAdapterTopologyAsync(new([installation], [instance], [root]));
        var binding = new MirrorPulseUploadBinding(member.LocalObject, MirrorPulseContentPreparation.ReplacePlaceholderIdentity, "AQID");
        var intent = new MirrorPulseMutationIntent(Guid.NewGuid(), instance.InstanceId, "docs", MirrorPulseWorkerChangeKind.ContentUpdate,
            "edited.txt", null, false, null, 4, new string('B', 64), MirrorPulseMutationOrigin.Journal, binding);
        await catalog.PrepareMutationAsync(intent);
        await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
        await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted, "v2");
        var accepted = new CloudPlaceholderIdentity(Guid.NewGuid(), "remote-accepted", "v2");
        await catalog.SaveContentAcceptanceProofAsync(new(intent.OperationId, binding, accepted.ItemId, accepted.RemoteId, "v2", 4, intent.ContentSha256!));
        await catalog.SaveContentConfirmationReceiptAsync(intent.OperationId, new(MirrorPulseContentConfirmationOutcome.Confirmed,
            MirrorPulseContentConfirmationStage.Complete, MirrorPulseContentConfirmationStage.Complete, true, true, true, true,
            4, 1, 0, 0, null, null, true, "BAUG", DateTimeOffset.UtcNow));
        // Synthetic platform observations assert no native success. The SQLite correlation
        // and the fake protected boundary are the behavior exercised by this managed test.
        await catalog.RecordNamespaceIdentityAcceptanceAsync(member.MemberId, intent.OperationId, null, first.ItemId, first.RemoteId);
        lease.Identity = ordinary ? new(accepted.ItemId, accepted.RemoteId, false, null) : new(accepted.ItemId, accepted.RemoteId, true, accepted);
        int preparations = lease.Preparations, writes = lease.Object.Writes;
        var result = await ApplyProtectedFixtureAsync(coordinator, fixture.Intent.OperationId, lease, null);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.AlreadyVerified, result.Outcome);
        Assert.AreEqual(preparations + (ordinary ? 1 : 0), lease.Preparations);
        Assert.AreEqual(writes, lease.Object.Writes);
        Assert.AreEqual(retained, await catalog.ReadNamespacePermissionLocalIdentityAsync(fixture.Baseline.EvidenceId));
        Assert.AreEqual(accepted.ItemId, (await catalog.ReadNamespaceMemberIdentityAsync(member.MemberId)).ItemId);
        Assert.AreEqual(first.ItemId, (await catalog.ReadNamespaceMemberIdentityAsync(member.MemberId)).OriginalItemId);
        if (ordinary) { Assert.AreEqual(accepted.ItemId, lease.ConvertedIdentity!.ItemId); Assert.IsNull(lease.ConvertedIdentity.RemoteRevision); }
        lease.Identity = new(first.ItemId, first.RemoteId, true, first);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.RecoveryRequired,
            (await ApplyProtectedFixtureAsync(coordinator, fixture.Intent.OperationId, lease, null)).Outcome);
        Assert.AreEqual(writes, lease.Object.Writes);
    }

    private static async Task<MirrorPulseNamespaceMemberRegistration> RegisterIdentityRecoveryParentAsync(MirrorPulseProductCatalog catalog,
        Fixture fixture, MirrorPulseNamespaceMemberRegistration? parent, string path, Guid localId)
    {
        var baseline = fixture.Baseline with
        {
            EvidenceId = Guid.NewGuid(),
            RootId = parent is null ? null : fixture.Baseline.RootId,
            RelativePath = path,
            IsDirectory = true,
            LocalObject = fixture.Baseline.LocalObject with { LocalFileId = localId }
        };
        var intent = fixture.Intent with
        {
            OperationId = Guid.NewGuid(),
            EvidenceId = baseline.EvidenceId,
            RootId = baseline.RootId,
            RelativePath = path,
            LocalObject = baseline.LocalObject,
            PreparedAt = DateTimeOffset.UtcNow
        };
        await catalog.PrepareNamespacePermissionChangeAsync(baseline, intent);
        await catalog.RecordNamespacePermissionApplicationAsync(intent.OperationId, baseline.LocalObject, DateTimeOffset.UtcNow);
        await catalog.VerifyNamespacePermissionChangeAsync(intent.OperationId, new(baseline.LocalObject, Owner, Target, DateTimeOffset.UtcNow));
        var member = new MirrorPulseNamespaceMemberRegistration(1, Guid.NewGuid(), parent?.MemberId,
            MirrorPulseNamespaceMemberOrigin.CapturedOriginal, baseline.EvidenceId, intent.OperationId, baseline.RootId, path,
            baseline.LocalObject, true, Owner, DateTimeOffset.UtcNow);
        return await catalog.RegisterNamespaceMemberAsync(member);
    }
}
