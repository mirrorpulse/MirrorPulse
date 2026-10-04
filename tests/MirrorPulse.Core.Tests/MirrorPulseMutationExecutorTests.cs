using System.Security.Cryptography;
using CfSharp;
using CfSharp.Storage.Sqlite;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseMutationExecutorTests
{
    internal static MirrorPulseMutationIntent Intent() => new(Guid.NewGuid(), InstanceId.New(), "local",
        MirrorPulseWorkerChangeKind.ContentUpdate, "file.txt", null, false, "before", 4, new string('A', 64),
        MirrorPulseMutationOrigin.Journal);

    [TestMethod]
    public async Task LostWorkerReplySurvivesRestartWithoutAnotherMutation()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        MirrorPulseMutationIntent intent = Intent();
        int mutations = 0;
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await Assert.ThrowsExactlyAsync<IOException>(() => new MirrorPulseMutationExecutor(catalog).ExecuteAsync(intent,
                    _ => { mutations++; throw new IOException("Reply lost after remote commit."); },
                    (_, _) => throw new AssertFailedException("An unknown result cannot be acknowledged."), default).AsTask());
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            MirrorPulseMutationRecord record = (await reopened.ReadMutationAsync(intent.OperationId))!;
            Assert.AreEqual(intent, record.Intent);
            Assert.AreEqual(MirrorPulseMutationState.Ambiguous, record.State);
            await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() => new MirrorPulseMutationExecutor(reopened)
                .ExecuteAsync(intent, _ => { mutations++; return ValueTask.FromResult<string?>("after"); },
                    (_, _) => ValueTask.CompletedTask, default).AsTask());
            Assert.AreEqual(1, mutations);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.PrepareMutationAsync(intent with { RelativePath = "other.txt" }));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task FailedOfficialAcknowledgementRetainsConfirmedResult()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            MirrorPulseMutationIntent intent = Intent();
            await Assert.ThrowsExactlyAsync<IOException>(() => new MirrorPulseMutationExecutor(catalog).ExecuteAsync(intent,
                _ => ValueTask.FromResult<string?>("accepted"), (_, _) => throw new IOException("Ack failed."), default).AsTask());
            MirrorPulseMutationRecord record = (await catalog.ReadMutationAsync(intent.OperationId))!;
            Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, record.State);
            Assert.AreEqual("accepted", record.AcceptedRevision);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false, "file")]
    [DataRow(false, "File")]
    [DataRow(true, "file")]
    [DataRow(true, "File")]
    public async Task ReadbackAfterRestartConvergesMatchingBytesOrRetainsConflict(bool changed, string itemKind)
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        byte[] accepted = [1, 2, 3, 4];
        MirrorPulseMutationIntent intent = Intent() with { ContentSha256 = Convert.ToHexString(SHA256.HashData(accepted)) };
        int mutations = 0;
        int acknowledgements = 0;
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
                await Assert.ThrowsExactlyAsync<IOException>(() => new MirrorPulseMutationExecutor(catalog).ExecuteAsync(intent,
                    _ => { mutations++; File.WriteAllBytes(Path.Combine(root, "remote"), accepted); throw new IOException("Lost reply."); },
                    (_, _) => throw new AssertFailedException(), default).AsTask());
            if (changed) File.WriteAllBytes(Path.Combine(root, "remote"), [4, 3, 2, 1]);
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            var transport = new ReadbackFixture(Path.Combine(root, "remote")) { ItemKind = itemKind };
            var readback = new MirrorPulseMutationReadback(transport, transport, transport);
            var executor = new MirrorPulseMutationExecutor(reopened);
            MirrorPulseMutationRecord record = (await reopened.ReadMutationAsync(intent.OperationId))!;
            ValueTask Acknowledge(string? revision, CancellationToken _)
            {
                Assert.AreEqual("different-revision", revision);
                acknowledgements++;
                return ValueTask.CompletedTask;
            }
            if (changed)
                await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => executor.ReconcileAsync(record,
                    readback.VerifyAsync, Acknowledge, default).AsTask());
            else await executor.ReconcileAsync(record, readback.VerifyAsync, Acknowledge, default);
            Assert.AreEqual(changed ? MirrorPulseMutationState.Conflict : MirrorPulseMutationState.Acknowledged,
                (await reopened.ReadMutationAsync(intent.OperationId))!.State);
            Assert.AreEqual(changed ? 0 : 1, acknowledgements);
            Assert.AreEqual(1, mutations);
            CollectionAssert.AreEqual(changed ? new byte[] { 4, 3, 2, 1 } : accepted, File.ReadAllBytes(Path.Combine(root, "remote")));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task UnknownMoveCannotBeAcknowledgedAndChangedReadbackCannotProveUpload()
    {
        var fixture = new ReadbackFixture(null);
        MirrorPulseMutationIntent intent = Intent() with { Kind = MirrorPulseWorkerChangeKind.Move, PreviousRelativePath = "before.txt" };
        var record = new MirrorPulseMutationRecord(intent, MirrorPulseMutationState.Ambiguous, null, DateTimeOffset.UtcNow);
        Assert.AreEqual(MirrorPulseMutationProofKind.Conflict, (await new MirrorPulseMutationReadback(fixture).VerifyAsync(record, default)).Kind);
        fixture.RevisionChanges = true;
        record = record with { Intent = Intent() with { ContentLength = 0, ContentSha256 = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())) } };
        Assert.AreEqual(MirrorPulseMutationProofKind.Unknown,
            (await new MirrorPulseMutationReadback(fixture, fixture, fixture).VerifyAsync(record, default)).Kind);
    }

    [TestMethod]
    public async Task OfficialAckSurvivesFailedProjectionAndCatalogRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        MirrorPulseMutationIntent intent = Intent() with
        {
            UploadBinding = new(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.ConvertRegularFile),
        };
        int mutations = 0;
        try
        {
            Directory.CreateDirectory(paths.SyncRootPath);
            Directory.CreateDirectory(Path.GetDirectoryName(paths.CfSharpStateDatabasePath)!);
            await using ICloudStateStore official = await new SqliteCloudStateStoreFactory(paths.CfSharpStateDatabasePath)
                .OpenAsync(new CloudStateStoreContext(paths.SyncRootPath));
            await using (ICloudStateTransaction transaction = await official.BeginTransactionAsync())
            {
                await transaction.Operations.EnqueueAsync(new(intent.OperationId, CloudStateOperationKind.ContentUpdate,
                    null, Array.Empty<byte>(), DateTimeOffset.UtcNow));
                await transaction.CommitAsync();
            }
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await Assert.ThrowsExactlyAsync<IOException>(() => new MirrorPulseMutationExecutor(catalog).ExecuteAsync(intent,
                    _ => { mutations++; return ValueTask.FromResult<string?>("accepted"); }, async (_, token) =>
                    {
                        await catalog.SaveContentAcceptanceProofAsync(new(intent.OperationId, intent.UploadBinding!, Guid.NewGuid(), "remote",
                            "accepted", intent.ContentLength!.Value, intent.ContentSha256!), token);
                        await catalog.SaveContentConfirmationReceiptAsync(intent.OperationId, new(MirrorPulseContentConfirmationOutcome.Confirmed,
                            MirrorPulseContentConfirmationStage.Complete, MirrorPulseContentConfirmationStage.Complete,
                            true, true, true, true, 4, 1, 0, 0, null, null, true, null, DateTimeOffset.UtcNow), token);
                        await using ICloudStateTransaction transaction = await official.BeginTransactionAsync(token);
                        await transaction.Operations.RemoveAsync(intent.OperationId, token);
                        await transaction.CommitAsync(token);
                        throw new IOException("Product projection failed after authoritative ack.");
                    }, default).AsTask());
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            var recovery = new MirrorPulseMutationProjectionRecovery(reopened);
            async ValueTask<bool> Pending(Guid operationId, CancellationToken token)
            {
                await using ICloudStateTransaction transaction = await official.BeginTransactionAsync(token);
                bool exists = await transaction.Operations.GetAsync(operationId, token) is not null;
                await transaction.RollbackAsync(token);
                return exists;
            }
            await Assert.ThrowsExactlyAsync<IOException>(() => recovery.RepairAsync(Pending,
                (_, _) => throw new IOException("Projection remains unavailable."), default).AsTask());
            Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, (await reopened.ReadMutationAsync(intent.OperationId))!.State);
            IReadOnlyList<Guid> repaired = await recovery.RepairAsync(Pending, async (record, token) =>
                await reopened.SaveInstanceRuntimeStateAsync(new(record.Intent.InstanceId, "Connected", false, DateTimeOffset.UtcNow), token), default);
            CollectionAssert.AreEqual(new[] { intent.OperationId }, repaired.ToArray());
            Assert.IsEmpty(await recovery.RepairAsync(Pending, (_, _) => throw new AssertFailedException(), default));
            Assert.AreEqual(1, mutations);
            Assert.AreEqual(MirrorPulseMutationState.Acknowledged, (await reopened.ReadMutationAsync(intent.OperationId))!.State);
            Assert.AreEqual("Connected", (await reopened.ReadInstanceRuntimeStateAsync(intent.InstanceId))!.Phase);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task RetainedAcceptanceReplaysLocalConfirmationWithoutChangingRemoteProofOrRepeatingUpload()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        MirrorPulseMutationIntent intent = Intent() with
        {
            UploadBinding = new(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.ConvertRegularFile),
        };
        var proof = new MirrorPulseContentAcceptanceProof(intent.OperationId, intent.UploadBinding!, Guid.NewGuid(), "remote",
            "accepted", intent.ContentLength!.Value, intent.ContentSha256!);
        int mutations = 0;
        int confirmations = 0;
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await Assert.ThrowsExactlyAsync<IOException>(() => new MirrorPulseMutationExecutor(catalog).ExecuteAsync(intent,
                    _ => { mutations++; return ValueTask.FromResult<string?>("accepted"); }, async (_, token) =>
                    {
                        await catalog.SaveContentAcceptanceProofAsync(proof, token);
                        confirmations++;
                        throw new IOException("Native confirmation committed but projection failed.");
                    }, default).AsTask());
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            MirrorPulseMutationRecord record = (await reopened.ReadMutationAsync(intent.OperationId))!;
            await new MirrorPulseMutationExecutor(reopened).ReconcileAsync(record,
                (_, _) => throw new AssertFailedException("Later remote observations must not replace the accepted proof."),
                async (revision, token) =>
                {
                    Assert.AreEqual(proof.AcceptedRevision, revision);
                    Assert.AreEqual(proof, await reopened.ReadContentAcceptanceProofAsync(intent.OperationId, token));
                    confirmations++;
                }, default);
            Assert.AreEqual(1, mutations);
            Assert.AreEqual(2, confirmations);
            Assert.AreEqual(proof, await reopened.ReadContentAcceptanceProofAsync(intent.OperationId));
            Assert.AreEqual(MirrorPulseMutationState.Acknowledged, (await reopened.ReadMutationAsync(intent.OperationId))!.State);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingJournalRecordCannotInventHistoricalBindingOrNativeConfirmation(bool hasBinding)
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        MirrorPulseMutationIntent intent = Intent() with
        {
            UploadBinding = hasBinding ? new(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.ConvertRegularFile) : null,
        };
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            await Assert.ThrowsExactlyAsync<IOException>(() => new MirrorPulseMutationExecutor(catalog).ExecuteAsync(intent,
                _ => ValueTask.FromResult<string?>("accepted"), (_, _) => throw new IOException("Unconfirmed local outcome."), default).AsTask());
            Assert.IsEmpty(await new MirrorPulseMutationProjectionRecovery(catalog).RepairAsync((_, _) => ValueTask.FromResult(false),
                (_, _) => throw new AssertFailedException("Missing official journal state is not a native content proof."), default));
            Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, (await catalog.ReadMutationAsync(intent.OperationId))!.State);
            if (!hasBinding)
                Assert.AreEqual(MirrorPulseLocalOperationBlockReason.MissingUploadBinding, (await catalog.ReadBlockedLocalOperationsAsync()).Single().Reason);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class ReadbackFixture(string? path) : IMirrorPulseWorkerStatTransport,
        IMirrorPulseWorkerRangeTransport, IMirrorPulseWorkerDirectoryPageSource
    {
        private int _stats;
        public bool RevisionChanges { get; set; }
        public string ItemKind { get; set; } = "file";
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(RevisionChanges && ++_stats > 1 ? "concurrent-change" : "different-revision");
        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken cancellationToken)
        {
            byte[] bytes = File.ReadAllBytes(path!);
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes.AsSpan((int)request.Offset, (int)request.Length).ToArray()));
        }
        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage(
                [new("remote-id", "different-revision", ItemKind, "file.txt", path is null ? 0 : new FileInfo(path).Length, null, null, false)],
                ReadOnlyMemory<byte>.Empty, true));
    }
}
