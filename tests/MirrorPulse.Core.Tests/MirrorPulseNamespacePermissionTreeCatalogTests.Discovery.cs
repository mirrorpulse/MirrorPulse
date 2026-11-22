using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespacePermissionTreeCatalogTests
{
    [TestMethod]
    public async Task RestartDiscoveryPagesAllCapturePhasesWithoutMakingThemExecutable()
    {
        using var fixture = new CatalogFixture();
        var (open, openMembers) = Tree();
        var (sealedDefinition, sealedMembers) = Tree();
        var (cancelled, cancelledMembers) = Tree();
        RootId root = open.Anchor.Intent.RootId!.Value;
        sealedDefinition = InRoot(sealedDefinition, root);
        cancelled = InRoot(cancelled, root);
        sealedMembers = InRoot(sealedMembers, root);
        cancelledMembers = InRoot(cancelledMembers, root);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.CreateNamespacePermissionTreeAsync(open);
            await catalog.AppendNamespacePermissionTreeMembersAsync(open.ManifestId, openMembers[..1]);
            await catalog.CreateNamespacePermissionTreeAsync(sealedDefinition);
            await catalog.AppendNamespacePermissionTreeMembersAsync(sealedDefinition.ManifestId, sealedMembers);
            await catalog.SealNamespacePermissionTreeAsync(sealedDefinition.ManifestId, SealTime(sealedDefinition));
            await catalog.CreateNamespacePermissionTreeAsync(cancelled);
            await catalog.AppendNamespacePermissionTreeMembersAsync(cancelled.ManifestId, cancelledMembers[..2]);
            await catalog.CancelNamespacePermissionTreeCaptureAsync(cancelled.ManifestId, SealTime(cancelled));
            var (other, _) = Tree();
            await catalog.CreateNamespacePermissionTreeAsync(other);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var found = new List<MirrorPulseNamespacePermissionTree>();
        Guid? after = null;
        while (true)
        {
            var page = await reopened.ReadNamespacePermissionTreesAsync(root, after, limit: 1);
            if (page.Count == 0) break;
            Assert.HasCount(1, page); found.Add(page[0]); after = page[0].Definition.ManifestId;
        }
        CollectionAssert.AreEqual(new[] { open.ManifestId, sealedDefinition.ManifestId, cancelled.ManifestId }
            .OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray(), found.Select(tree => tree.Definition.ManifestId).ToArray());
        Assert.AreEqual(1, found.Single(tree => tree.Definition.ManifestId == open.ManifestId).CapturedMembers);
        Assert.AreEqual(MirrorPulseNamespacePermissionTreePhase.Sealed, found.Single(tree => tree.Definition.ManifestId == sealedDefinition.ManifestId).Phase);
        Assert.AreEqual(MirrorPulseNamespacePermissionTreePhase.Cancelled, found.Single(tree => tree.Definition.ManifestId == cancelled.ManifestId).Phase);
        Assert.IsEmpty(await reopened.ReadNamespacePermissionChangesAsync());
        Assert.IsEmpty(await reopened.ReadNamespacePermissionTreesAsync(RootId.New()));
    }

    [TestMethod]
    [DataRow("root")]
    [DataRow("cursor")]
    [DataRow("limit")]
    [DataRow("cancel")]
    public async Task DiscoveryRejectsInvalidBoundsBeforeReadingAnyCapture(string scenario)
    {
        using var fixture = new CatalogFixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        if (scenario == "cancel")
            await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.ReadNamespacePermissionTreesAsync(RootId.New(), cancellationToken: new(true)));
        else
            await Assert.ThrowsAsync<ArgumentException>(() => catalog.ReadNamespacePermissionTreesAsync(
                scenario == "root" ? new(Guid.Empty) : RootId.New(), scenario == "cursor" ? Guid.Empty : null,
                limit: scenario == "limit" ? 257 : 1));
    }

    [TestMethod]
    public async Task DiscoveryFailsClosedWhenTheIndexedManifestHeaderIsChanged()
    {
        using var fixture = new CatalogFixture();
        var (definition, _) = Tree();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await catalog.CreateNamespacePermissionTreeAsync(definition);
        RootId otherRoot = RootId.New();
        await ExecuteSqlAsync(fixture.Paths, $"UPDATE namespace_permission_trees SET root_id='{otherRoot.Value:D}';");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespacePermissionTreesAsync(otherRoot));
    }

    private static MirrorPulseNamespacePermissionTreeDefinition InRoot(MirrorPulseNamespacePermissionTreeDefinition definition, RootId root) =>
        definition with { Anchor = definition.Anchor with { Baseline = definition.Anchor.Baseline with { RootId = root }, Intent = definition.Anchor.Intent with { RootId = root } } };

    private static MirrorPulseNamespacePermissionTreeMember[] InRoot(MirrorPulseNamespacePermissionTreeMember[] members, RootId root) =>
        members.Select(member => member with
        {
            Preparation = member.Preparation with
            { Baseline = member.Preparation.Baseline with { RootId = root }, Intent = member.Preparation.Intent with { RootId = root } }
        }).ToArray();
}
