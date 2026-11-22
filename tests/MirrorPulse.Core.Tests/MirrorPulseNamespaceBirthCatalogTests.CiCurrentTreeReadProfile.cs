using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("CiCurrentTreeReadProfile")]
    [DataRow(1)]
    [DataRow(5)]
    [DataRow(8)]
    public async Task CurrentTreeReadsRevalidateEveryOriginalMemberAfterCompletedLabels(int labels)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
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
            var rename = await catalog.PrepareManagedRootRenameWithProofAsync(setup.Move.RootId, "CurrentTree" + index,
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
        var child = setup.Members[^1];
        var expected = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(child.MemberId))!;
        Guid parent = expected.Context.Location.ParentMemberId!.Value;
        string name = expected.Context.Location.RelativePath.Split('/')[^1];
        var member = await MeasureCiMovedBornAdmissionAsync(catalog, () => catalog.ReadNamespaceMemberAsync(child.MemberId));
        var binding = await MeasureCiMovedBornAdmissionAsync(catalog, () => catalog.ReadNamespaceMemberByBindingAsync(child.LocalObject));
        var occupied = await MeasureCiMovedBornAdmissionAsync(catalog, () => catalog.ReadNamespaceMemberChildAsync(parent, name));
        var tree = await MeasureCiMovedBornAdmissionAsync(catalog, () => catalog.ReadNamespaceMemberTreeAsync(setup.Root.MemberId));
        var lineage = await MeasureCiMovedBornAdmissionAsync(catalog, () => catalog.ReadNamespaceMemberProtectionLineageAsync(child.MemberId));
        Assert.AreEqual(child, member.Result);
        Assert.AreEqual(child, binding.Result);
        Assert.AreEqual(child, occupied.Result);
        CollectionAssert.AreEquivalent(setup.Members.Select(value => value.MemberId).ToArray(), tree.Result.Select(value => value.MemberId).ToArray());
        Assert.AreEqual(expected.Context, lineage.Result[0]);
        Assert.AreEqual(completion.OperationId, lineage.Result[0].Location.CurrentPositionOperationId);
        Console.WriteLine("CiCurrentTreeReadSql: " + JsonSerializer.Serialize(new
        {
            labels,
            members = setup.Members.Length,
            memberSelects = member.Selects,
            bindingSelects = binding.Selects,
            occupiedSelects = occupied.Selects,
            treeSelects = tree.Selects,
            lineageSelects = lineage.Selects,
            memberTables = member.TableReads,
            treeTables = tree.TableReads,
            lineageTables = lineage.TableReads,
            nativeExecuted = false,
            publicDeadlineEstablished = false
        }));
        // Generous fixed-fixture budgets reject repeated historical walks without
        // treating a retained catalog read as current native authorization.
        Assert.IsLessThanOrEqualTo(100 * labels + 400, member.Selects);
        Assert.IsLessThanOrEqualTo(100 * labels + 410, binding.Selects);
        Assert.IsLessThanOrEqualTo(100 * labels + 410, occupied.Selects);
        Assert.IsLessThanOrEqualTo(120 * labels + 450, tree.Selects);
        Assert.IsLessThanOrEqualTo(120 * labels + 450, lineage.Selects);
        await ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_member_positions SET fingerprint=zeroblob(32);");
        // Immutable registration lookups do not claim a current position.
        Assert.AreEqual(child, await catalog.ReadNamespaceMemberAsync(child.MemberId));
        Assert.AreEqual(child, await catalog.ReadNamespaceMemberByBindingAsync(child.LocalObject));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberChildAsync(parent, name));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberTreeAsync(setup.Root.MemberId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberProtectionLineageAsync(child.MemberId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }
}
