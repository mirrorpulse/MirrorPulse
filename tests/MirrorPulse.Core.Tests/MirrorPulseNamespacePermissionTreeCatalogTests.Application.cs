using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespacePermissionTreeCatalogTests
{
    [TestMethod]
    [DataRow(MirrorPulseNamespacePermissionPhase.Prepared)]
    [DataRow(MirrorPulseNamespacePermissionPhase.Applied)]
    [DataRow(MirrorPulseNamespacePermissionPhase.Verified)]
    public async Task OpenCaptureFencesEarlierApplicationAcrossRestartWithoutRewritingItsHistory(
        MirrorPulseNamespacePermissionPhase phase)
    {
        using var fixture = new CatalogFixture();
        var (definition, _) = Tree();
        var preparation = Child(definition.Anchor, 1, "Docs/earlier.txt").Preparation;
        MirrorPulseNamespacePermissionChange retained;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            retained = await catalog.PrepareNamespacePermissionChangeAsync(preparation.Baseline, preparation.Intent);
            if (phase != MirrorPulseNamespacePermissionPhase.Prepared)
                retained = await catalog.RecordNamespacePermissionApplicationAsync(preparation.Intent.OperationId,
                    preparation.Baseline.LocalObject, preparation.Intent.PreparedAt.AddSeconds(1));
            if (phase == MirrorPulseNamespacePermissionPhase.Verified)
                retained = await catalog.VerifyNamespacePermissionChangeAsync(preparation.Intent.OperationId,
                    new(preparation.Baseline.LocalObject, Owner, Target, preparation.Intent.PreparedAt.AddSeconds(2)));
            await catalog.CreateNamespacePermissionTreeAsync(definition);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            reopened.ReadNamespacePermissionChangeForApplicationAsync(preparation.Intent.OperationId));
        Assert.AreEqual(retained, await reopened.ReadNamespacePermissionChangeAsync(preparation.Intent.OperationId));
        Assert.AreEqual(preparation.Baseline, await reopened.ReadNamespacePermissionBaselineAsync(preparation.Baseline.EvidenceId));
        await reopened.CancelNamespacePermissionTreeCaptureAsync(definition.ManifestId, SealTime(definition));
        Assert.AreEqual(retained, await reopened.ReadNamespacePermissionChangeForApplicationAsync(preparation.Intent.OperationId));
    }

    [TestMethod]
    [DataRow("root", true)]
    [DataRow("parent", true)]
    [DataRow("other-root", false)]
    [DataRow("other-sync-root", false)]
    public async Task ApplicationFenceUsesStableScopeAndLeavesIndependentRootsAvailable(string scope, bool fenced)
    {
        using var fixture = new CatalogFixture();
        var (definition, _) = Tree();
        var preparation = Child(definition.Anchor, 1, "Docs/earlier.txt").Preparation;
        var baseline = preparation.Baseline;
        if (scope == "parent") baseline = baseline with
        {
            RootId = null,
            IsDirectory = true,
            RelativePath = string.Empty,
            LocalObject = baseline.LocalObject with { LocalFileId = baseline.LocalObject.SyncRootFileId },
        };
        else if (scope == "other-root") baseline = baseline with { RootId = RootId.New(), RelativePath = "Other/earlier.txt" };
        else if (scope == "other-sync-root") baseline = baseline with
        {
            LocalObject = baseline.LocalObject with { SyncRootFileId = Guid.NewGuid() },
        };
        var intent = preparation.Intent with
        {
            RootId = baseline.RootId,
            RelativePath = baseline.RelativePath,
            LocalObject = baseline.LocalObject,
        };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retained = await catalog.PrepareNamespacePermissionChangeAsync(baseline, intent);
        await catalog.CreateNamespacePermissionTreeAsync(definition);
        if (fenced)
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespacePermissionChangeForApplicationAsync(intent.OperationId));
        else
            Assert.AreEqual(retained, await catalog.ReadNamespacePermissionChangeForApplicationAsync(intent.OperationId));
        Assert.AreEqual(retained, await catalog.ReadNamespacePermissionChangeAsync(intent.OperationId));
    }

    [TestMethod]
    public async Task SealedOriginalApplicationIsAdmittedButUnknownIntentIsNotCreated()
    {
        using var fixture = new CatalogFixture();
        var (definition, members) = Tree();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.CreateNamespacePermissionTreeAsync(definition);
        await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members);
        await catalog.SealNamespacePermissionTreeAsync(definition.ManifestId, SealTime(definition));
        var retained = await catalog.PrepareNamespacePermissionChangeAsync(definition.Anchor.Baseline, definition.Anchor.Intent);
        Assert.AreEqual(retained, await catalog.ReadNamespacePermissionChangeForApplicationAsync(retained.Intent.OperationId));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.ReadNamespacePermissionChangeForApplicationAsync(Guid.NewGuid()));
        Assert.HasCount(1, await catalog.ReadNamespacePermissionChangesAsync());
    }
}
