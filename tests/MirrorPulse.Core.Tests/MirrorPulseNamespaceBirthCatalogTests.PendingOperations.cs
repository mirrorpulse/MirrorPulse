using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(MirrorPulseNamespaceBirthOrigin.ControlledCreation, false)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.ControlledCreation, true)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.Import, false)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.Import, true)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.RemotePopulation, false)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.RemotePopulation, true)]
    public async Task PendingBirthDiscoveryPreservesOriginalRetryIdentityUntilMembershipCompletes(MirrorPulseNamespaceBirthOrigin origin, bool observed)
    {
        using var fixture = new Fixture(); var original = Baseline(); var proof = Protection(original);
        var birth = Birth(original, proof) with { Origin = origin }; BornObject? born = null;
        var expected = new MirrorPulseNamespacePendingOperation(birth.OperationId, birth.RootId, MirrorPulseNamespacePendingOperationKind.Birth);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, original, proof); await first.PrepareNamespaceBirthAsync(birth);
            if (observed) born = await AddBornAsync(first, birth);
            CollectionAssert.AreEqual(new[] { expected }, (await first.ReadNamespacePendingOperationsAsync(birth.RootId)).ToArray());
            Assert.IsEmpty(await first.ReadNamespacePendingOperationsAsync(RootId.New()));
            Assert.IsEmpty(await first.ReadPendingNamespaceInitializationsAsync());
        }
        await using var current = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        CollectionAssert.AreEqual(new[] { expected }, (await current.ReadNamespacePendingOperationsAsync(birth.RootId)).ToArray());
        var parent = await RegisterOriginalParentMembersAsync(current, original, proof); born ??= await AddBornAsync(current, birth);
        await current.RegisterNamespaceMemberAsync(BornMember(born, parent));
        Assert.IsEmpty(await current.ReadNamespacePendingOperationsAsync(birth.RootId));
        Assert.AreEqual(birth, await current.ReadNamespaceBirthAsync(birth.OperationId));
        Assert.IsEmpty(await current.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow(MirrorPulseNamespacePendingOperationKind.Move)]
    [DataRow(MirrorPulseNamespacePendingOperationKind.Retirement)]
    [DataRow(MirrorPulseNamespacePendingOperationKind.RetirementBatch)]
    [DataRow(MirrorPulseNamespacePendingOperationKind.Replacement)]
    public async Task PendingLocalOperationDiscoveryKeepsTheOriginalKindAndFenceAcrossOwners(MirrorPulseNamespacePendingOperationKind kind)
    {
        using var fixture = new Fixture(); RootId root; Guid operation;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(first, directory: kind == MirrorPulseNamespacePendingOperationKind.RetirementBatch);
            root = setup.Member.RootId!.Value; operation = Guid.NewGuid();
            switch (kind)
            {
                case MirrorPulseNamespacePendingOperationKind.Move:
                    operation = setup.Move.OperationId; await first.PrepareNamespaceMemberMoveAsync(setup.Move); break;
                case MirrorPulseNamespacePendingOperationKind.Retirement:
                    await first.PrepareNamespaceRetirementAsync(Retire(setup, operation)); break;
                case MirrorPulseNamespacePendingOperationKind.RetirementBatch:
                    await first.PrepareNamespaceRetirementBatchAsync(operation, setup.Member.MemberId); break;
                default:
                    var replacement = Replacement((await first.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
                    operation = replacement.OperationId; await first.PrepareNamespaceReplacementAsync(replacement); break;
            }
            CollectionAssert.AreEqual(new[] { new MirrorPulseNamespacePendingOperation(operation, root, kind) },
                (await first.ReadNamespacePendingOperationsAsync(root)).ToArray());
            Assert.IsEmpty(await first.ReadPendingNamespaceInitializationsAsync());
        }
        await using var current = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        CollectionAssert.AreEqual(new[] { new MirrorPulseNamespacePendingOperation(operation, root, kind) },
            (await current.ReadNamespacePendingOperationsAsync(root)).ToArray());
        CollectionAssert.AreEqual(new[] { root }, (await current.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        Assert.IsEmpty(await current.ReadNamespacePendingOperationsAsync(RootId.New()));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => current.ReadNamespacePendingOperationsAsync(root, cancelled.Token));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => current.ReadNamespacePendingOperationsAsync(default));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }
}
