using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task DirectoryTransferRetainsOnlyTheOriginalOrderedCopyReceipts()
    {
        using var fixture = new Fixture();
        MirrorPulseDirectoryTransferAdmission plan;
        DateTimeOffset started;
        DateTimeOffset accepted;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = await PrepareDirectoryTransferFixtureAsync(catalog);
            await catalog.PrepareNamespaceDirectoryTransferAsync(plan);
            started = plan.Originals.CapturedAt.AddTicks(1);
            accepted = started.AddTicks(1);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                catalog.RecordNamespaceDirectoryTransferCopyAcceptedAsync(plan.Command.CommandId, 0,
                    plan.Members[0].CopyOperationId, "unproved", accepted, default));
            await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() =>
                catalog.StartNamespaceDirectoryTransferCopyAsync(plan, 1, started, default));
            await catalog.StartNamespaceDirectoryTransferCopyAsync(plan, 0, started, default);
            await catalog.RecordNamespaceDirectoryTransferCopyAcceptedAsync(plan.Command.CommandId, 0,
                plan.Members[0].CopyOperationId, "target-directory-receipt", accepted, default);
            await catalog.StartNamespaceDirectoryTransferCopyAsync(plan, 1, accepted, default);
        }

        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await reopened.ReadNamespaceDirectoryTransferCopyAsync(plan.Command.CommandId, default);
        Assert.AreEqual(new MirrorPulseDirectoryTransferCopyStep(plan.Members[0].CopyOperationId, started,
            "target-directory-receipt", accepted), copy.Steps[0]);
        Assert.AreEqual(new MirrorPulseDirectoryTransferCopyStep(plan.Members[1].CopyOperationId, accepted, null, null), copy.Steps[1]);
        await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() =>
            reopened.StartNamespaceDirectoryTransferCopyAsync(plan, 1, accepted.AddTicks(1), default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            reopened.RecordNamespaceDirectoryTransferCopyAcceptedAsync(plan.Command.CommandId, 0,
                plan.Members[0].CopyOperationId, "later-revision", accepted, default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            reopened.RecordNamespaceDirectoryTransferCopyAcceptedAsync(plan.Command.CommandId, 1,
                plan.Members[0].CopyOperationId, "different-member", accepted, default));
        CollectionAssert.AreEquivalent(new[] { plan.SourceRoute.RootId, plan.TargetRoute.RootId },
            (await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
    }

    [TestMethod]
    public async Task DirectoryTransferSealsEveryOriginalAndBothRootsInTheSameCommitAcrossOwners()
    {
        using var fixture = new Fixture();
        MirrorPulseDirectoryTransferAdmission plan;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = await PrepareDirectoryTransferFixtureAsync(first);
            await ExecuteSqlAsync(fixture.Paths, """
                CREATE TRIGGER interrupt_directory_transfer
                BEFORE INSERT ON namespace_directory_transfer_ids WHEN NEW.journal_original=1
                BEGIN SELECT RAISE(ABORT,'fixture interruption'); END;
                """);
            await Assert.ThrowsExactlyAsync<SqliteException>(() => first.PrepareNamespaceDirectoryTransferAsync(plan));
            Assert.IsNull(await first.ReadNamespaceDirectoryTransferAsync(plan.Command.CommandId));
            Assert.IsEmpty(await first.ReadNamespaceDispatchFencedRootsAsync());
        }

        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER interrupt_directory_transfer;");
        await using (var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                second.PrepareNamespaceDirectoryTransferAsync(plan with { Members = plan.Members.Reverse().ToArray() }));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                second.PrepareNamespaceDirectoryTransferAsync(plan with { Members = [plan.Members[0]], Originals = plan.Originals with { Members = [] } }));
            Assert.IsEmpty(await second.ReadNamespaceDispatchFencedRootsAsync());
            _ = await second.PrepareNamespaceDirectoryTransferAsync(plan);
        }

        await using var third = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retained = await third.ReadNamespaceDirectoryTransferAsync(plan.Command.CommandId);
        Assert.IsNotNull(retained);
        CollectionAssert.AreEqual(plan.Members.ToArray(), retained.Members.ToArray());
        Assert.AreEqual(plan.Originals, retained.Originals);
        CollectionAssert.AreEquivalent(new[] { plan.SourceRoute.RootId, plan.TargetRoute.RootId },
            (await third.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        var pending = await third.ReadNamespacePendingOperationsAsync(plan.TargetRoute.RootId);
        Assert.AreEqual(plan.Command.CommandId, pending.Single(x => x.Kind == MirrorPulseNamespacePendingOperationKind.Transfer).OperationId);
        Assert.AreEqual(plan.Command, (await third.PrepareNamespaceDirectoryTransferAsync(plan)).Command);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            third.PrepareNamespaceDirectoryTransferAsync(plan with
            {
                Members = [plan.Members[0], plan.Members[1] with { SourceRevision = "later-generation" }]
            }));
    }

    private static async Task<MirrorPulseDirectoryTransferAdmission> PrepareDirectoryTransferFixtureAsync(MirrorPulseProductCatalog catalog)
    {
        var setup = await PrepareTransferFixtureAsync(catalog, directory: true);
        var child = await AddBornAsync(catalog, NestedBirth(setup.Source.Born, "payload.bin", false));
        await catalog.RegisterNamespaceMemberAsync(BornMember(child, setup.Source.Member));
        var source = await catalog.ReadNamespaceProtectedSubtreeAsync(setup.Source.Member.MemberId);
        string originalPath = source[0].Protection.Context.Location.RelativePath;
        var copied = source.Select(entry =>
        {
            bool directory = entry.Protection.Context.Member.IsDirectory;
            return new MirrorPulseDirectoryTransferMember(entry,
                setup.Plan.TargetRelativePath + entry.Protection.Context.Location.RelativePath[originalPath.Length..],
                directory ? "source-directory-v1" : "source-file-v1", Guid.NewGuid(), Guid.NewGuid(),
                directory ? null : Guid.NewGuid(), directory ? null : new(setup.Plan.Length, setup.Plan.Sha256));
        }).ToArray();
        var prepared = source.Select(entry => entry.Protection.Context.Verification.ObservedAt)
            .Append(setup.Plan.PreparedAt).Max().AddSeconds(1);
        var command = new MirrorPulseNamespaceTransferCommandOrigin(1, Guid.NewGuid(), setup.Source.Member.MemberId,
            setup.Plan.TargetParent.Context.Member.MemberId, "moved.txt", prepared);
        var journal = new MirrorPulseNamespaceTransferJournalClaim(1, command.CommandId, 1, prepared,
            [new(Guid.NewGuid(), 1, MirrorPulseWorkerChangeKind.ContentUpdate, source[1].Protection.Identity.ItemId,
                source[1].Protection.Context.Location.RelativePath, null, false, prepared)]);
        return new(1, command, setup.Plan.SourceRoute, setup.Plan.TargetRoute, setup.Plan.SourceParent, setup.Plan.TargetParent,
            copied, journal, prepared);
    }
}
