using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespacePermissionCoordinatorTests
{
    [TestMethod]
    public async Task ReopenedCaptureRejectsEarlierPreparedApplicationBeforeLeaseInspection()
    {
        using var fixture = new Fixture();
        MirrorPulseNamespacePermissionTreeDefinition definition = Capture(fixture);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
            await catalog.CreateNamespacePermissionTreeAsync(definition);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(reopened);
        var lease = new ObjectLease(fixture.Baseline, fixture.Intent);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ApplyAsync(fixture.Intent.OperationId, lease));
        Assert.AreEqual(0, lease.Reads);
        Assert.AreEqual(0, lease.Writes);
        Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, (await reopened.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId))!.Phase);
        await reopened.CancelNamespacePermissionTreeCaptureAsync(definition.ManifestId, DateTimeOffset.UtcNow);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, (await coordinator.ApplyAsync(fixture.Intent.OperationId, lease)).Outcome);
        Assert.AreEqual(1, lease.Writes);
        Assert.IsFalse(lease.Disposed);
    }

    private static MirrorPulseNamespacePermissionTreeDefinition Capture(Fixture fixture)
    {
        var baseline = fixture.Baseline with
        {
            EvidenceId = Guid.NewGuid(),
            RelativePath = "Docs",
            IsDirectory = true,
            LocalObject = fixture.Baseline.LocalObject with { LocalFileId = Guid.NewGuid() },
        };
        var intent = fixture.Intent with
        {
            OperationId = Guid.NewGuid(),
            EvidenceId = baseline.EvidenceId,
            LocalObject = baseline.LocalObject,
            RelativePath = baseline.RelativePath,
        };
        return new(Guid.NewGuid(), new(baseline, intent), 1, baseline.CapturedAt);
    }
}
