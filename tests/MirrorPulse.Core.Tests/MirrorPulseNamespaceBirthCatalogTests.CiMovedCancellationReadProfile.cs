using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("CiMovedCancellationReadProfile")]
    [DataRow(1)]
    [DataRow(5)]
    [DataRow(12)]
    public async Task CancellationReadsRetainEveryCompletedLabelPositionAcrossTheWholeBornGraph(int labels)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareCancellationInitializationFixtureAsync(catalog, true, true);
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
            string currentName = setup.Move.Target.RelativePath;
            string targetName = "Moved" + (index + 1);
            var rename = await catalog.PrepareManagedRootRenameWithProofAsync(setup.Move.RootId, targetName, currentName,
                setup.Rename.Proof! with { CapturedAt = completion.Root.VerifiedAt.AddSeconds(1) });
            setup = setup with
            {
                Rename = rename,
                Move = setup.Move with
                {
                    OperationId = rename.Intent.OperationId,
                    Source = setup.Move.Source with { RelativePath = currentName },
                    Target = setup.Move.Target with { RelativePath = targetName },
                    PreviousMoveOperationId = completion.OperationId,
                    SourcePositionOperationId = completion.OperationId,
                    PreparedAt = rename.Proof!.CapturedAt.AddSeconds(1)
                }
            };
        }
        var tree = await MeasureRegisteredBirthReadAsync(catalog,
            () => catalog.ReadRootLabelCancellationTreeAsync(setup.Move.OperationId));
        CollectionAssert.AreEquivalent(setup.Members.Select(member => member.MemberId).ToArray(),
            tree.Result.Members.Select(member => member.Context.Member.MemberId).ToArray());
        foreach (var member in tree.Result.Members)
        {
            Assert.AreEqual(completion.OperationId, member.Context.Location.CurrentPositionOperationId);
            Assert.IsTrue(member.Context.Location.RelativePath == setup.Move.Source.RelativePath ||
                member.Context.Location.RelativePath.StartsWith(setup.Move.Source.RelativePath + "/", StringComparison.Ordinal));
        }
        var cancelled = await UnsealedCancellationAsync(catalog, setup);
        await catalog.CancelUnsealedRootLabelAsync(cancelled);
        var receipt = await MeasureRegisteredBirthReadAsync(catalog,
            () => catalog.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId));
        Assert.AreEqual(JsonSerializer.Serialize(cancelled), JsonSerializer.Serialize(receipt.Result));
        var fences = await MeasureRegisteredBirthReadAsync(catalog, () => catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsEmpty(fences.Result);
        Console.WriteLine("CiMovedCancellationSql: " + JsonSerializer.Serialize(new
        {
            labels,
            members = setup.Members.Length,
            treeSelects = tree.Selects,
            receiptSelects = receipt.Selects,
            fenceSelects = fences.Selects,
            nativeExecuted = false,
            publicDeadlineSpeedupEstablished = false
        }));
        // Keep ample room for validation while rejecting repeated historical walks.
        Assert.IsLessThanOrEqualTo(200 * labels + 100, tree.Selects);
        Assert.IsLessThanOrEqualTo(150 * labels + 100, receipt.Selects);
        Assert.IsLessThanOrEqualTo(150 * labels + 120, fences.Selects);
        await ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_member_positions SET fingerprint=zeroblob(32);");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadUnsealedRootLabelCancellationAsync(setup.Move.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }
}
