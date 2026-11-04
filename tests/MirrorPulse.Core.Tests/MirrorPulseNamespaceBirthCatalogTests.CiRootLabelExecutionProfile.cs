using System.Diagnostics;
using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("CiRootLabelExecutionProfile")]
    [DataRow(1)]
    [DataRow(5)]
    [DataRow(8)]
    public async Task RootLabelExecutionRevalidatesWholeHistoryAfterRepeatedCompletedLabels(int labels)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareCancellationInitializationFixtureAsync(catalog, false, true);
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
            var prior = await PrepareRootSubtreeFactsAsync(catalog, setup);
            await catalog.CompleteNamespaceDirectoryMoveAsync(prior);
            var rename = await catalog.PrepareManagedRootRenameWithProofAsync(setup.Move.RootId, "Execution" + index,
                setup.Move.Target.RelativePath, setup.Rename.Proof! with { CapturedAt = prior.Root.VerifiedAt.AddSeconds(1) });
            setup = setup with
            {
                Rename = rename,
                Move = setup.Move with
                {
                    OperationId = rename.Intent.OperationId,
                    Source = setup.Move.Source with { RelativePath = setup.Move.Target.RelativePath },
                    Target = setup.Move.Target with { RelativePath = rename.Intent.TargetName },
                    PreviousMoveOperationId = prior.OperationId,
                    SourcePositionOperationId = prior.OperationId,
                    PreparedAt = rename.Proof!.CapturedAt.AddSeconds(1)
                }
            };
        }
        var watch = Stopwatch.StartNew();
        await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        long prepareMilliseconds = watch.ElapsedMilliseconds;
        watch.Restart();
        var admission = await MeasureCiMovedBornAdmissionAsync(catalog,
            () => catalog.ValidateNamespaceMemberMoveExecutionAsync(setup.Move.OperationId));
        long admissionMilliseconds = watch.ElapsedMilliseconds;
        Assert.AreEqual(setup.Move, admission.Result);
        var snapshot = await MeasureCiMovedBornAdmissionAsync(catalog,
            () => catalog.ReadNamespaceDirectoryMoveSnapshotAsync(setup.Move.OperationId));
        Assert.IsNotNull(snapshot.Result);
        CollectionAssert.AreEquivalent(setup.Members.Select(member => member.MemberId).ToArray(),
            snapshot.Result.Entries.Select(entry => entry.MemberId).ToArray());
        var completion = await PrepareRootSubtreeFactsAsync(catalog, setup);
        watch.Restart();
        await catalog.CompleteNamespaceDirectoryMoveAsync(completion);
        long settlementMilliseconds = watch.ElapsedMilliseconds;
        var receipt = await MeasureCiMovedBornAdmissionAsync(catalog,
            () => catalog.ReadNamespaceDirectoryMoveCompletionAsync(setup.Move.OperationId));
        Assert.AreEqual(JsonSerializer.Serialize(completion), JsonSerializer.Serialize(receipt.Result));
        Console.WriteLine("CiRootLabelExecutionSql: " + JsonSerializer.Serialize(new
        {
            labels,
            members = setup.Members.Length,
            prepareMilliseconds,
            admissionMilliseconds,
            settlementMilliseconds,
            admissionSelects = admission.Selects,
            snapshotSelects = snapshot.Selects,
            receiptSelects = receipt.Selects,
            admissionTables = admission.TableReads,
            snapshotTables = snapshot.TableReads,
            receiptTables = receipt.TableReads,
            nativeExecuted = false,
            publicDeadlineEstablished = false
        }));
        Assert.IsLessThanOrEqualTo(100 * labels + 300, admission.Selects);
        Assert.IsLessThanOrEqualTo(50 * labels + 150, snapshot.Selects);
        Assert.IsLessThanOrEqualTo(50 * labels + 170, receipt.Selects);
        await ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_member_positions SET fingerprint=zeroblob(32);");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDirectoryMoveSnapshotAsync(setup.Move.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDirectoryMoveCompletionAsync(setup.Move.OperationId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }
}
