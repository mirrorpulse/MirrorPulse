using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Caching;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DirectoryTransferCopiesParentsAndPrivateBytesWithoutRepeatingAnUncertainEffect(bool interruptReceipt)
    {
        using var fixture = new Fixture();
        MirrorPulseDirectoryTransferAdmission plan;
        var inputs = new MirrorPulseLocalTransferContentStore(fixture.Paths);
        var worker = new DirectoryTransferCopyWorker();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = await PrepareDirectoryTransferFixtureAsync(catalog, TransferBytes);
            worker.Plan = plan;
            string sourcePath = Path.Combine(fixture.Paths.DataRootPath, "directory-transfer-input.bin");
            await File.WriteAllBytesAsync(sourcePath, TransferBytes);
            await using (var file = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                await inputs.StageRetainedAsync(plan.Members[1].PrivateContentId!.Value, file);
            await catalog.PrepareNamespaceDirectoryTransferAsync(plan);
            if (interruptReceipt)
            {
                await ExecuteSqlAsync(fixture.Paths, $"""
                    CREATE TRIGGER interrupt_directory_copy_receipt
                    BEFORE UPDATE OF accepted_revision ON namespace_directory_transfer_copy_steps
                    WHEN NEW.effect_id='{plan.Members[1].CopyOperationId:D}'
                    BEGIN SELECT RAISE(ABORT,'original file acceptance interrupted'); END;
                    """);
            }
            var executor = new MirrorPulseDirectoryTransferCopyExecutor(catalog, inputs, worker, worker,
                new TransferClock(plan.PreparedAt.AddSeconds(1)));
            if (interruptReceipt)
                await Assert.ThrowsExactlyAsync<SqliteException>(() => executor.CopyAsync(plan.Command.CommandId, default));
            else
                Assert.IsTrue((await executor.CopyAsync(plan.Command.CommandId, default)).Steps.All(step => step.AcceptedAt is not null));
            Assert.AreEqual(1, worker.Creates);
            Assert.AreEqual(1, worker.Uploads);
            CollectionAssert.AreEqual(TransferBytes, worker.Bytes);
            Assert.IsNull(await catalog.ReadMutationAsync(plan.Members[1].DeleteOperationId));
        }

        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retry = new MirrorPulseDirectoryTransferCopyExecutor(reopened, inputs, worker, worker,
            new TransferClock(plan.PreparedAt.AddSeconds(2)));
        if (interruptReceipt)
            await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() => retry.CopyAsync(plan.Command.CommandId, default));
        else
            Assert.IsTrue((await retry.CopyAsync(plan.Command.CommandId, default)).Steps.All(step => step.AcceptedAt is not null));
        Assert.AreEqual(1, worker.Creates);
        Assert.AreEqual(1, worker.Uploads);
        Assert.HasCount(2, await reopened.ReadNamespaceDispatchFencedRootsAsync());
    }

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
            var modified = plan with { Members = [plan.Members[0] with { SourceRevision = "changed-original" }, plan.Members[1]] };
            await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() =>
                catalog.StartNamespaceDirectoryTransferCopyAsync(modified, 0, started, default));
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
            reopened.StartNamespaceDirectoryTransferCopyAsync(copy.Admission, 1, accepted.AddTicks(1), default));
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
        foreach (var parent in new[] { plan.SourceParent, plan.TargetParent })
        {
            var member = parent.Context.Member;
            var intent = new MirrorPulseNamespaceBirthIntent(1, Guid.NewGuid(), member.RootId!.Value,
                member.OriginId, parent.PermissionOperationId, member.LocalObject, member.RelativePath + "/unrelated.txt",
                false, MirrorPulseNamespaceBirthOrigin.ControlledCreation, parent.Context.Protection.RoleSid, plan.PreparedAt.AddSeconds(1));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => third.PrepareNamespaceBirthAsync(intent));
            Assert.IsNull(await third.ReadNamespaceBirthAsync(intent.OperationId));
        }
        Assert.AreEqual(plan.Command, (await third.PrepareNamespaceDirectoryTransferAsync(plan)).Command);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            third.PrepareNamespaceDirectoryTransferAsync(plan with
            {
                Members = [plan.Members[0], plan.Members[1] with { SourceRevision = "later-generation" }]
            }));
    }

    private static async Task<MirrorPulseDirectoryTransferAdmission> PrepareDirectoryTransferFixtureAsync(
        MirrorPulseProductCatalog catalog, byte[]? bytes = null)
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
                directory ? null : Guid.NewGuid(), directory ? null : bytes is null
                    ? new(setup.Plan.Length, setup.Plan.Sha256)
                    : new(bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes))));
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

    private sealed class DirectoryTransferCopyWorker : IMirrorPulseWorkerUploadTransport, IMirrorPulseWorkerMutationTransport,
        IMirrorPulseWorkerStatTransport, IMirrorPulseWorkerDirectoryPageSource, IMirrorPulseWorkerRangeTransport
    {
        internal MirrorPulseDirectoryTransferAdmission Plan = null!;
        internal int Creates;
        internal int Uploads;
        internal byte[] Bytes = [];
        internal bool ExtraTargetChild;
        internal bool ChangedTargetDirectory;
        internal bool DuplicateTargetIdentity;

        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken token) =>
            ValueTask.FromResult<string?>(request.NormalizedPath == "moved.txt" ? ChangedTargetDirectory ? "later-directory" : "directory-after-children" : "file-upload-receipt");

        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken token)
        {
            Assert.AreEqual("file-upload-receipt", request.ExpectedRevision);
            Assert.AreEqual("moved.txt/payload.bin", request.NormalizedPath);
            return ValueTask.FromResult<Stream>(new MemoryStream(Bytes.AsSpan((int)request.Offset, (int)request.Length).ToArray(), writable: false));
        }

        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request, CancellationToken token)
        {
            Assert.IsTrue(request.ContinuationCursor.IsEmpty);
            var directory = new MirrorPulseWorkerDirectoryEntry("directory-id", ChangedTargetDirectory ? "later-directory" : "directory-after-children",
                "Directory", "moved.txt", null, null, null, false);
            var file = new MirrorPulseWorkerDirectoryEntry(DuplicateTargetIdentity ? "directory-id" : "file-id",
                "file-upload-receipt", "File", "moved.txt/payload.bin", Bytes.LongLength, null, null, false);
            MirrorPulseWorkerDirectoryEntry[] entries = request.NormalizedPath == "" ? [directory] : ExtraTargetChild
                ? [file, file with { RemoteId = "another-file-id", RelativePath = "moved.txt/another.bin" }]
                : [file];
            return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage(entries, ReadOnlyMemory<byte>.Empty, true));
        }

        public ValueTask<string> CreateDirectoryAsync(MirrorPulseWorkerCreateDirectoryRequest request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(0, Uploads);
            Assert.AreEqual(0, Creates++);
            Assert.AreEqual(Plan.TargetRoute.InstanceId, request.InstanceId);
            Assert.AreEqual(Plan.TargetRoute.RootKey, request.RootKey);
            Assert.AreEqual("moved.txt", request.NormalizedPath);
            Assert.AreEqual(Plan.Members[0].CopyOperationId, request.OperationId);
            Assert.IsTrue(request.MustBeAbsent);
            return ValueTask.FromResult("directory-create-receipt");
        }

        public async ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken cancellationToken)
        {
            Assert.AreEqual(1, Creates);
            Assert.AreEqual(0, Uploads++);
            Assert.AreEqual(Plan.TargetRoute.InstanceId, request.InstanceId);
            Assert.AreEqual(Plan.TargetRoute.RootKey, request.RootKey);
            Assert.AreEqual("moved.txt/payload.bin", request.NormalizedPath);
            Assert.AreEqual(Plan.Members[1].CopyOperationId, request.OperationId);
            Assert.IsNull(request.ExpectedRevision);
            Assert.IsTrue(request.DestinationMustBeAbsent);
            Assert.IsTrue(request.RequireConditionalTargetSupport);
            Assert.IsFalse(request.Content.CanWrite);
            using var received = new MemoryStream();
            await request.Content.CopyToAsync(received, cancellationToken);
            Bytes = received.ToArray();
            Assert.AreEqual(request.ExpectedContentSha256, Convert.ToHexString(SHA256.HashData(Bytes)));
            return "file-upload-receipt";
        }

        public ValueTask<string?> DeleteAsync(MirrorPulseWorkerDeleteRequest request, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Copy dispatch reached source deletion.");

        public ValueTask<string> MoveAsync(MirrorPulseWorkerMoveRequest request, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Copy dispatch reached remote Move.");
    }
}
