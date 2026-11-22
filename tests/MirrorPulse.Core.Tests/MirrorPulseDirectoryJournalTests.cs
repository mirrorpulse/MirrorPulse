using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseDirectoryJournalTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DirectoryAcceptanceReplaysAfterCatalogRestartWithoutRepeatingCreation(bool changed)
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-directory-journal-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var remote = new DirectoryReadback(Path.Combine(root, "remote"));
        MirrorPulseMutationIntent intent = Intent();
        int mutations = 0;
        int acknowledgements = 0;
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
                await Assert.ThrowsExactlyAsync<IOException>(() => new MirrorPulseMutationExecutor(catalog).ExecuteAsync(intent,
                    _ => { mutations++; Directory.CreateDirectory(remote.Path); return ValueTask.FromResult<string?>(remote.Revision); },
                    (_, _) => throw new IOException("Projection interrupted after remote acceptance."), default).AsTask());
            if (changed) remote.Revision = "another-directory-version";
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            MirrorPulseMutationRecord record = (await reopened.ReadMutationAsync(intent.OperationId))!;
            Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, record.State);
            var readback = new MirrorPulseMutationReadback(remote, directories: remote);
            ValueTask Acknowledge(string? revision, CancellationToken _)
            {
                Assert.AreEqual("accepted-directory", revision);
                acknowledgements++;
                return ValueTask.CompletedTask;
            }
            if (changed)
                await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => new MirrorPulseMutationExecutor(reopened)
                    .ReconcileAsync(record, readback.VerifyAsync, Acknowledge, default).AsTask());
            else
                await new MirrorPulseMutationExecutor(reopened).ReconcileAsync(record, readback.VerifyAsync, Acknowledge, default);
            Assert.IsTrue(Directory.Exists(remote.Path));
            Assert.AreEqual(1, mutations);
            Assert.AreEqual(changed ? 0 : 1, acknowledgements);
            Assert.AreEqual(changed ? MirrorPulseMutationState.Conflict : MirrorPulseMutationState.Acknowledged,
                (await reopened.ReadMutationAsync(intent.OperationId))!.State);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ExistingDirectoryCannotProveAnUnknownCreationOrAFileReplacement()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-directory-journal-tests", Guid.NewGuid().ToString("N"));
        var remote = new DirectoryReadback(root);
        Directory.CreateDirectory(root);
        try
        {
            var record = new MirrorPulseMutationRecord(Intent(), MirrorPulseMutationState.Ambiguous, null, DateTimeOffset.UtcNow);
            var readback = new MirrorPulseMutationReadback(remote, directories: remote);
            Assert.AreEqual(MirrorPulseMutationProofKind.Unknown, (await readback.VerifyAsync(record, default)).Kind);
            remote.ItemKind = "File";
            record = record with { State = MirrorPulseMutationState.RemoteAccepted, AcceptedRevision = remote.Revision };
            Assert.AreEqual(MirrorPulseMutationProofKind.Unknown, (await readback.VerifyAsync(record, default)).Kind);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task UnsupportedMutationRetainsPreparedIntentForTheSameOperationAfterCapabilityRecovery()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-directory-journal-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        MirrorPulseMutationIntent intent = Intent();
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await Assert.ThrowsExactlyAsync<NotSupportedException>(() => new MirrorPulseMutationExecutor(catalog).ExecuteAsync(intent,
                    _ => throw new NotSupportedException("CreateDirectoryUnsupported"),
                    (_, _) => throw new AssertFailedException("Unsupported operations cannot be acknowledged."), default).AsTask());
                Assert.AreEqual(MirrorPulseMutationState.Prepared, (await catalog.ReadMutationAsync(intent.OperationId))!.State);
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            int acknowledgements = 0;
            await new MirrorPulseMutationExecutor(reopened).ExecuteAsync(intent, _ =>
            {
                Directory.CreateDirectory(Path.Combine(root, "remote", "folder"));
                return ValueTask.FromResult<string?>("created");
            }, (_, _) => { acknowledgements++; return ValueTask.CompletedTask; }, default);
            Assert.AreEqual(1, acknowledgements);
            Assert.AreEqual(MirrorPulseMutationState.Acknowledged, (await reopened.ReadMutationAsync(intent.OperationId))!.State);
        }
        finally { Directory.Delete(root, true); }
    }

    private static MirrorPulseMutationIntent Intent() => new(Guid.NewGuid(), InstanceId.New(), "docs",
        MirrorPulseWorkerChangeKind.Create, "folder", null, true, null, null, null, MirrorPulseMutationOrigin.Journal);

    private sealed class DirectoryReadback(string path) : IMirrorPulseWorkerStatTransport, IMirrorPulseWorkerDirectoryPageSource
    {
        public string Path { get; } = path;
        public string Revision { get; set; } = "accepted-directory";
        public string ItemKind { get; set; } = "Directory";
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Directory.Exists(Path) ? Revision : null);
        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage(
                [new("directory-id", Revision, ItemKind, "folder", null, null, null, false)], ReadOnlyMemory<byte>.Empty, true));
    }
}
