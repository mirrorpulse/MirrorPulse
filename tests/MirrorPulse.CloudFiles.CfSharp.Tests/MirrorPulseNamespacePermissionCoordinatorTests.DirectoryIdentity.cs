using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespacePermissionCoordinatorTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExistingDirectoryIdentityIsRetainedBeforeAccessWriteAndSurvivesReopen(bool interrupted)
    {
        using var fixture = new Fixture();
        var baseline = fixture.Baseline with { IsDirectory = true, RelativePath = "Docs" };
        var intent = fixture.Intent with { RelativePath = "Docs" };
        var identity = new CloudPlaceholderIdentity(Guid.NewGuid(), "existing-directory", "v1");
        var lease = new ProtectedObjectLease(fixture) { Identity = new(identity.ItemId, identity.RemoteId, true, identity) };
        lease.Object.Current = lease.Object.Current with { IsDirectory = true, RelativePath = "Docs" };
        lease.Object.FailAfterWrite = interrupted;
        MirrorPulseNamespacePermissionLocalIdentity original;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await first.PrepareNamespacePermissionChangeAsync(baseline, intent);
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(first);
            lease.BeforeAccessWrite = async () =>
            {
                Assert.AreEqual(identity.ItemId, (await first.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId))!.ItemId);
            };
            if (interrupted)
                await Assert.ThrowsExactlyAsync<IOException>(() => ApplyProtectedFixtureAsync(coordinator, intent.OperationId, lease, null));
            else
                Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified,
                    (await ApplyProtectedFixtureAsync(coordinator, intent.OperationId, lease, null)).Outcome);
            original = (await first.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId))!;
            Assert.AreEqual(2, original.Version); Assert.AreEqual(identity.RemoteId, original.RemoteId);
            Assert.AreEqual(0, lease.Preparations); Assert.AreSame(identity, lease.Identity.PlaceholderIdentity);
        }
        lease.BeforeAccessWrite = null;
        lease.Object.FailAfterWrite = false;
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var recovery = new MirrorPulseNamespacePermissionCoordinator(second);
        Assert.AreEqual(interrupted ? MirrorPulseNamespacePermissionOutcome.Verified : MirrorPulseNamespacePermissionOutcome.AlreadyVerified,
            (await ApplyProtectedFixtureAsync(recovery, intent.OperationId, lease, null)).Outcome);
        Assert.AreEqual(original, await second.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId));
        Assert.AreEqual(1, lease.Object.Writes); Assert.AreEqual(0, lease.Preparations);
        var replacement = new CloudPlaceholderIdentity(Guid.NewGuid(), "replacement-directory", "v1");
        lease.Identity = new(replacement.ItemId, replacement.RemoteId, true, replacement);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.RecoveryRequired,
            (await ApplyProtectedFixtureAsync(recovery, intent.OperationId, lease, null)).Outcome);
        Assert.AreEqual(original, await second.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId));
        Assert.AreEqual(1, lease.Object.Writes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DirectoryIdentityCannotBeInventedAfterHistoricalAccessWork(bool placeholder)
    {
        using var fixture = new Fixture();
        var baseline = fixture.Baseline with { IsDirectory = true, RelativePath = "Docs" };
        var intent = fixture.Intent with { RelativePath = "Docs" };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(baseline, intent);
        await catalog.RecordNamespacePermissionApplicationAsync(intent.OperationId, baseline.LocalObject, DateTimeOffset.UtcNow);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var identity = new CloudPlaceholderIdentity(Guid.NewGuid(), "late-directory", "v1");
        var lease = new ProtectedObjectLease(fixture)
        { Identity = new(identity.ItemId, identity.RemoteId, placeholder, placeholder ? identity : null) };
        lease.Object.Current = lease.Object.Current with { IsDirectory = true, RelativePath = "Docs", Dacl = Target };
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.RecoveryRequired,
            (await ApplyProtectedFixtureAsync(coordinator, intent.OperationId, lease, null)).Outcome);
        Assert.IsNull(await catalog.ReadNamespacePermissionLocalIdentityAsync(baseline.EvidenceId));
        Assert.AreEqual(0, lease.Object.Writes); Assert.AreEqual(0, lease.Preparations);
    }
}
