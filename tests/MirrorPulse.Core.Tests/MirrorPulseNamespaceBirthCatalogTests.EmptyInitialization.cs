using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory)]
    [DataRow(MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory)]
    public async Task EmptyInitializationIsDiscoverableAcrossRestartAndCompletesOnlyItsActualAnchor(MirrorPulseNamespaceInitializationInventoryKind kind)
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceInitializationIntent intent;
        MirrorPulseNamespaceMemberRegistration anchor;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var baseline = Baseline();
            baseline = baseline with
            {
                RootId = null,
                RelativePath = string.Empty,
                LocalObject = baseline.LocalObject with { LocalFileId = baseline.LocalObject.SyncRootFileId }
            };
            var protection = Protection(baseline); await ProtectAsync(catalog, baseline, protection);
            anchor = await catalog.RegisterNamespaceMemberAsync(OriginalMember(baseline, protection, null));
            intent = new(3, Guid.NewGuid(), [], anchor.RegisteredAt.AddSeconds(1)) { RequestedInventoryKind = kind };
            await catalog.PrepareNamespaceInitializationAsync(intent);
        }
        MirrorPulseNamespaceInitializationCompletion completion;
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var pending = (await reopened.ReadPendingNamespaceInitializationsAsync()).Single();
            Assert.AreEqual(intent.OperationId, pending.Intent.OperationId); Assert.AreEqual(3, pending.Intent.Version);
            Assert.IsEmpty(pending.Intent.Roots); Assert.IsNull(pending.Completion);
            var inventory = await reopened.ReadNamespaceCurrentTreeSnapshotAsync(anchor.MemberId);
            completion = kind == MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory
                ? await reopened.CompleteNamespaceInitializationAsync(intent.OperationId, inventory.Members, intent.PreparedAt.AddSeconds(1))
                : await reopened.CompleteNamespaceCurrentInitializationAsync(intent.OperationId, inventory, intent.PreparedAt.AddSeconds(1));
            Assert.AreEqual(1, completion.MemberCount); Assert.AreEqual(anchor.MemberId, completion.AnchorMemberId);
            Assert.AreEqual(kind, completion.InventoryKind); Assert.IsEmpty(await reopened.ReadPendingNamespaceInitializationsAsync());
        }
        await using var final = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(completion, await final.ReadNamespaceInitializationCompletionAsync(intent.OperationId));
        Assert.AreEqual(intent.OperationId, (await final.PrepareNamespaceInitializationAsync(intent)).OperationId);
        Assert.IsEmpty(await final.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("version-one")]
    [DataRow("version-two")]
    [DataRow("nonempty-version-three")]
    [DataRow("unclassified-version-three")]
    public async Task EmptyInitializationRequiresItsExplicitVersionAndRoute(string invalid)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var intent = new MirrorPulseNamespaceInitializationIntent(3, Guid.NewGuid(), [], DateTimeOffset.UtcNow)
        { RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory };
        intent = invalid switch
        {
            "version-one" => intent with { Version = 1 },
            "version-two" => intent with { Version = 2 },
            "nonempty-version-three" => intent with { Roots = [new(RootId.New(), Guid.NewGuid())] },
            "unclassified-version-three" => intent with { RequestedInventoryKind = null },
            _ => throw new ArgumentException("Unknown invalid admission.", nameof(invalid)),
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceInitializationAsync(intent));
        Assert.IsNull(await catalog.ReadNamespaceInitializationAsync(intent.OperationId));
    }

    [TestMethod]
    [DataRow(MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory)]
    [DataRow(MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory)]
    public async Task EmptyInitializationCannotHideARegisteredAdapterRoot(MirrorPulseNamespaceInitializationInventoryKind kind)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var baseline = Baseline();
        baseline = baseline with
        {
            RootId = null,
            RelativePath = string.Empty,
            LocalObject = baseline.LocalObject with { LocalFileId = baseline.LocalObject.SyncRootFileId }
        };
        var protection = Protection(baseline); await ProtectAsync(catalog, baseline, protection);
        var anchor = await catalog.RegisterNamespaceMemberAsync(OriginalMember(baseline, protection, null));
        var intent = new MirrorPulseNamespaceInitializationIntent(3, Guid.NewGuid(), [], anchor.RegisteredAt.AddSeconds(1))
        { RequestedInventoryKind = kind };
        await catalog.PrepareNamespaceInitializationAsync(intent);
        var birth = new MirrorPulseNamespaceBirthIntent(1, Guid.NewGuid(), RootId.New(), baseline.EvidenceId,
            protection.OperationId, baseline.LocalObject, "First", true, MirrorPulseNamespaceBirthOrigin.RemotePopulation,
            Role, intent.PreparedAt.AddSeconds(1));
        var born = await AddBornAsync(catalog, birth);
        await catalog.RegisterNamespaceMemberAsync(BornMember(born, anchor));
        var inventory = await catalog.ReadNamespaceCurrentTreeSnapshotAsync(anchor.MemberId);
        if (kind == MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory)
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.CompleteNamespaceInitializationAsync(intent.OperationId,
                inventory.Members.Where(member => member.Context.Member.MemberId == anchor.MemberId).ToArray(), birth.PreparedAt.AddMinutes(1)));
        else
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.CompleteNamespaceCurrentInitializationAsync(intent.OperationId,
                inventory, birth.PreparedAt.AddMinutes(1)));
        Assert.IsNull(await catalog.ReadNamespaceInitializationCompletionAsync(intent.OperationId));
        Assert.HasCount(1, await catalog.ReadPendingNamespaceInitializationsAsync());
    }
}
