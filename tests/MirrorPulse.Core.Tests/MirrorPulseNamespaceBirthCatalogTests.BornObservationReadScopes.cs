using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(5)]
    [DataRow(8)]
    public async Task BornObservationProofsAndInitializerFencesRevalidateMovedArtifactsAcrossTransactions(int labels)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareCancellationInitializationFixtureAsync(catalog, false, true);
        MirrorPulseNamespaceDirectoryMoveCompletion completion = null!;
        for (int index = 0; index < labels; index++)
        {
            var root = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Root.MemberId))!;
            var anchor = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Anchor.MemberId))!;
            setup = setup with
            {
                Move = setup.Move with
                {
                    ProtectionId = root.Context.Protection.ProtectionId,
                    RoleSid = root.Context.Protection.RoleSid,
                    Source = setup.Move.Source with { ParentProtectionId = anchor.Context.Protection.ProtectionId },
                    Target = setup.Move.Target with { ParentProtectionId = anchor.Context.Protection.ProtectionId }
                }
            };
            completion = await PrepareRootSubtreeFactsAsync(catalog, setup);
            await catalog.CompleteNamespaceDirectoryMoveAsync(completion);
            if (index + 1 == labels) break;
            var rename = await catalog.PrepareManagedRootRenameWithProofAsync(setup.Move.RootId, "Observed" + index,
                setup.Move.Target.RelativePath, setup.Rename.Proof! with { CapturedAt = completion.Root.VerifiedAt.AddSeconds(1) });
            setup = setup with
            {
                Rename = rename,
                Move = setup.Move with
                {
                    OperationId = rename.Intent.OperationId,
                    Source = setup.Move.Source with { RelativePath = setup.Move.Target.RelativePath },
                    Target = setup.Move.Target with { RelativePath = rename.Intent.TargetName },
                    PreviousMoveOperationId = completion.OperationId,
                    SourcePositionOperationId = completion.OperationId,
                    PreparedAt = rename.Proof!.CapturedAt.AddSeconds(1)
                }
            };
        }
        var measured = await MeasureCiMovedBornAdmissionAsync(catalog, () => catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Move.RootId));
        CollectionAssert.AreEquivalent(setup.Members.Append(setup.Anchor).Select(member => member.MemberId).ToArray(),
            measured.Result.Members.Select(member => member.Context.Member.MemberId).ToArray());
        Assert.IsEmpty(measured.Result.FencedRoots);
        var intent = new MirrorPulseNamespaceInitializationIntent(4, Guid.NewGuid(),
            Array.AsReadOnly(new[] { new MirrorPulseNamespaceInitializationRoot(setup.Move.RootId, Guid.Empty) { BornRootMemberId = setup.Root.MemberId } }),
            completion.Root.VerifiedAt.AddSeconds(1))
        { RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory };
        Assert.AreEqual(JsonSerializer.Serialize(intent), JsonSerializer.Serialize(await catalog.PrepareNamespaceInitializationAsync(intent)));
        Assert.IsTrue((await catalog.ReadNamespaceDispatchFencedRootsAsync()).Contains(setup.Move.RootId));
        var fenced = await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Move.RootId);
        CollectionAssert.AreEquivalent(new[] { setup.Move.RootId }, fenced.FencedRoots.ToArray());
        // The catalog fixture supplies synthetic native audit facts to exercise
        // the actual atomic fence transition, without claiming native acceptance.
        _ = await catalog.CompleteNamespaceCurrentInitializationAsync(intent.OperationId, fenced, intent.PreparedAt.AddSeconds(1));
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
        var later = await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Move.RootId);
        Assert.IsEmpty(later.FencedRoots);
        Console.WriteLine("BornObservationScopeSql: " + JsonSerializer.Serialize(new
        { labels, selects = measured.Selects, measured.TableReads, pendingFenceObserved = true, completedFenceGone = true, nativeExecuted = false }));
        string table = labels switch { 1 => "namespace_birth_plans", 5 => "namespace_birth_starts", _ => "namespace_birth_observations" };
        await ExecuteSqlAsync(fixture.Paths, $"UPDATE {table} SET fingerprint=zeroblob(32) WHERE operation_id='{setup.Members[^1].OriginId:D}';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor.MemberId, setup.Move.RootId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }
}
