#if HOST_ATOMIC_DIRECTORY_CANDIDATE
using System.Runtime.Versioning;
using CfSharp;
using Microsoft.Data.Sqlite;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class MirrorPulseAtomicDirectoryJournalTests
{
    [TestMethod]
    [DataRow("before-return")]
    [DataRow("catalog-commit")]
    public async Task WholeBatchAcknowledgementRestartsWithExactIdsAndPreservesLaterJournalEntry(string cut)
    {
        using var fixture = new Fixture();
        AdapterDirectoryAdmissionRequest request;
        IReadOnlyList<MirrorPulseWorkerChangeCommand> originals;
        Guid successor = Guid.NewGuid();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        await using (var store = await fixture.OpenJournalAsync())
        {
            (request, originals) = await PrepareAsync(catalog, store, successor);
            if (cut == "catalog-commit") await fixture.ProductSqlAsync($"""
                CREATE TRIGGER interrupt_atomic_ack BEFORE UPDATE ON mutation_intents
                WHEN NEW.operation_id='{originals[1].OperationId:D}' AND NEW.state={(int)MirrorPulseMutationState.Acknowledged}
                BEGIN SELECT RAISE(ABORT,'interrupted atomic ACK'); END;
                """);
            var ack = new MirrorPulseAtomicDirectoryJournalAcknowledgement(catalog, store, async (ids, token) =>
            {
                CollectionAssert.AreEqual(originals.Select(value => value.OperationId).ToArray(), ids.ToArray());
                await RemoveJournalFixtureRowsAsync(store, ids, token);
                if (cut == "before-return") throw new IOException("Injected lost journal return.");
            });
            if (cut == "before-return")
                await Assert.ThrowsExactlyAsync<IOException>(() => ack.AcknowledgeAsync(request.AdmissionId, default).AsTask());
            else
                await Assert.ThrowsExactlyAsync<SqliteException>(() => ack.AcknowledgeAsync(request.AdmissionId, default).AsTask());
            foreach (var original in originals)
                Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, (await catalog.ReadMutationAsync(original.OperationId))!.State);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var official = await fixture.OpenJournalAsync();
        if (cut == "catalog-commit") await fixture.ProductSqlAsync("DROP TRIGGER interrupt_atomic_ack;");
        Assert.HasCount(1, await reopened.ReadPendingAtomicDirectoryJournalAsync(null, 4, default));
        int calls = 0;
        var recovery = new MirrorPulseAtomicDirectoryJournalAcknowledgement(reopened, official, async (ids, token) =>
        {
            calls++;
            CollectionAssert.AreEqual(originals.Select(value => value.OperationId).ToArray(), ids.ToArray());
            await RemoveJournalFixtureRowsAsync(official, ids, token);
        });
        await recovery.AcknowledgeAsync(request.AdmissionId, default);
        await recovery.AcknowledgeAsync(request.AdmissionId, default);
        Assert.AreEqual(1, calls);
        Assert.IsEmpty(await reopened.ReadPendingAtomicDirectoryJournalAsync(null, 4, default));
        Assert.IsNotNull((await reopened.ReadAtomicDirectoryBatchAsync(request.AdmissionId, default))!.Receipt);
        await using var tx = await official.BeginTransactionAsync();
        foreach (var original in originals)
        {
            Assert.IsNull(await tx.Operations.GetAsync(original.OperationId));
            Assert.AreEqual(MirrorPulseMutationState.Acknowledged, (await reopened.ReadMutationAsync(original.OperationId))!.State);
        }
        var later = await tx.Operations.GetAsync(successor);
        Assert.IsNotNull(later);
        CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, later.Payload.ToArray());
        await tx.RollbackAsync();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WholeBatchAcknowledgementRejectsPartialOrChangedOfficialRows(bool changed)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var store = await fixture.OpenJournalAsync();
        var (request, originals) = await PrepareAsync(catalog, store, Guid.NewGuid());
        await using (var tx = await store.BeginTransactionAsync())
        {
            var original = (await tx.Operations.GetAsync(originals[0].OperationId))!;
            if (changed)
            {
                Guid replacement = Guid.NewGuid();
                await tx.Items.UpsertAsync(new CloudItemState(replacement, "replacement", "Docs/replacement.txt", CloudItemKind.File,
                    null, 0, false, DateTimeOffset.UtcNow));
                await tx.Operations.UpdateAsync(new CloudOperationJournalEntry(original.OperationId, original.Kind,
                    replacement, original.Payload.Span, original.CreatedAt, original.AttemptCount, original.RetryAfter, original.Sequence));
            }
            else
                await tx.Operations.RemoveAsync(original.OperationId);
            await tx.CommitAsync();
        }
        var ack = new MirrorPulseAtomicDirectoryJournalAcknowledgement(catalog, store,
            (_, _) => throw new AssertFailedException("Invalid journal evidence reached acknowledgement."));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ack.AcknowledgeAsync(request.AdmissionId, default).AsTask());
        foreach (var original in originals)
            Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, (await catalog.ReadMutationAsync(original.OperationId))!.State);
    }

    [TestMethod]
    public async Task MissingOriginalRowsWithoutAnAcknowledgementStartCannotCompleteTheBatch()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var store = await fixture.OpenJournalAsync();
        var (request, originals) = await PrepareAsync(catalog, store, Guid.NewGuid());
        await RemoveJournalFixtureRowsAsync(store, originals.Select(value => value.OperationId).ToArray(), default);
        var ack = new MirrorPulseAtomicDirectoryJournalAcknowledgement(catalog, store,
            (_, _) => throw new AssertFailedException("Missing originals cannot authorize a first acknowledgement."));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ack.AcknowledgeAsync(request.AdmissionId, default).AsTask());
        var pending = await catalog.ReadAcceptedAtomicDirectoryJournalAsync(request.AdmissionId, default);
        Assert.IsNull(pending.ReceiptFingerprint);
        Assert.IsFalse(pending.Completed);
    }

    [TestMethod]
    public void AtomicJournalSelectionOrdersOriginalsAndRefusesUnownedOrIncompleteSubtrees()
    {
        var instance = InstanceId.New();
        MirrorPulseWorkerChangeCommand Delete(string path, long sequence, bool directory = false) =>
            new(Guid.NewGuid(), sequence, instance, "docs", MirrorPulseWorkerChangeKind.Delete, path, null, null,
                directory, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var root = Delete("tree", 1, true);
        var child = Delete("tree/nested", 2, true);
        var leaf = Delete("tree/nested/leaf.txt", 3);
        var peer = Delete("treehouse/peer.txt", 4);
        var selected = MirrorPulseJournalUploadPump.SelectAtomicDirectoryOriginals(root, [root, child, leaf, peer], out bool isBatch);
        Assert.IsTrue(isBatch);
        CollectionAssert.AreEqual(new[] { leaf, child, root }, selected!.ToArray());
        Assert.IsNull(MirrorPulseJournalUploadPump.SelectAtomicDirectoryOriginals(root, [root, leaf], out isBatch));
        Assert.IsTrue(isBatch);
        var replacement = leaf with
        {
            OperationId = Guid.NewGuid(),
            Sequence = 5,
            ItemId = Guid.NewGuid(),
            Kind = MirrorPulseWorkerChangeKind.Create
        };
        Assert.IsNull(MirrorPulseJournalUploadPump.SelectAtomicDirectoryOriginals(root, [root, child, leaf, replacement], out isBatch));
        Assert.IsTrue(isBatch);
        var escaping = replacement with
        {
            RelativePath = "escaped",
            PreviousRelativePath = leaf.RelativePath,
            PreviousRootKey = "docs",
            Kind = MirrorPulseWorkerChangeKind.Move
        };
        Assert.IsNull(MirrorPulseJournalUploadPump.SelectAtomicDirectoryOriginals(root, [root, child, leaf, escaping], out isBatch));
        Assert.IsTrue(isBatch);
    }

    private static async Task RemoveJournalFixtureRowsAsync(ICloudStateStore store, IReadOnlyList<Guid> ids, CancellationToken token)
    {
        await using var tx = await store.BeginTransactionAsync(token);
        foreach (Guid id in ids) await tx.Operations.RemoveAsync(id, token);
        await tx.CommitAsync(token);
    }

    private static async Task<(AdapterDirectoryAdmissionRequest Request, IReadOnlyList<MirrorPulseWorkerChangeCommand> Originals)> PrepareAsync(
        MirrorPulseProductCatalog catalog, ICloudStateStore store, Guid successor)
    {
        var instance = InstanceId.New();
        Guid admissionId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var originals = new List<MirrorPulseWorkerChangeCommand>();
        await using (var tx = await store.BeginTransactionAsync())
        {
            foreach (string path in new[] { "tree/leaf.txt", "tree" })
            {
                Guid id = path == "tree" ? admissionId : Guid.NewGuid();
                Guid item = Guid.NewGuid();
                await tx.Items.UpsertAsync(new CloudItemState(item, "original:" + path, "Docs/" + path,
                    path == "tree" ? CloudItemKind.Directory : CloudItemKind.File, "rev:" + path,
                    path == "tree" ? null : 3, true, now));
                await tx.Operations.EnqueueAsync(new CloudOperationJournalEntry(id, CloudStateOperationKind.Delete, item, [1, 2, 3], now));
                var row = (await tx.Operations.GetAsync(id))!;
                originals.Add(new(id, row.Sequence, instance, "docs", MirrorPulseWorkerChangeKind.Delete, path, null, null, path == "tree", item, now));
            }
            Guid later = Guid.NewGuid();
            await tx.Items.UpsertAsync(new CloudItemState(later, "later", "Docs/tree/leaf.txt",
                CloudItemKind.File, null, 3, false, now));
            await tx.Operations.EnqueueAsync(new CloudOperationJournalEntry(successor, CloudStateOperationKind.Create, later, [7, 8, 9], now));
            await tx.CommitAsync();
        }
        var request = new AdapterDirectoryAdmissionRequest(1, instance.Value, admissionId, originals.Select(value =>
            new AdapterOriginalDirectoryOperation("Delete", new(value.OperationId, "docs", value.RelativePath,
                Preconditions: new("rev:" + value.RelativePath), IsDirectory: value.IsDirectory))).ToArray(), string.Empty);
        request = request with { RequestFingerprint = request.CalculateFingerprint() };
        var root = new AdapterDirectoryGenerationWitness(1, instance.Value, admissionId, Guid.NewGuid(), new("docs", ""),
            "root-generation", "root-revision", null, request.RequestFingerprint, "AQID");
        var tree = new AdapterDirectoryGenerationWitness(1, instance.Value, admissionId, Guid.NewGuid(), new("docs", "tree"),
            "tree-generation", "rev:tree", root.Generation, request.RequestFingerprint, "AQID");
        var admission = new AdapterDirectoryAdmission(1, instance.Value, admissionId, request.RequestFingerprint,
            [new(originals[0].OperationId, "leaf-generation", null, [tree]), new(admissionId, tree.Generation, tree, [root])], string.Empty);
        admission = admission with { AdmissionFingerprint = admission.CalculateFingerprint() };
        foreach (var original in originals)
            await catalog.PrepareMutationAsync(new MirrorPulseMutationIntent(original.OperationId, instance, "docs", original.Kind,
                original.RelativePath, null, original.IsDirectory, "rev:" + original.RelativePath, null, null, MirrorPulseMutationOrigin.Journal)
            { JournalItemId = original.ItemId });
        var started = await catalog.StartAtomicDirectoryBatchAsync(request, admission, default, originals);
        var receipt = new AdapterAtomicDirectoryDeletionReceipt(1, instance.Value, admissionId, Guid.NewGuid(),
            started.Request.RequestFingerprint, admission.AdmissionFingerprint, started.Request.ManifestFingerprint,
            originals.Select((value, index) => new AdapterAtomicDirectoryDeletedOriginal(value.OperationId,
                new("docs", value.RelativePath), admission.Operations[index].OriginalGeneration)).ToArray(), "AQID");
        await catalog.AcceptAtomicDirectoryBatchAsync(admissionId, receipt, default);
        return (request, originals);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        internal MirrorPulseStoragePaths Paths { get; }

        internal Fixture()
        {
            Paths = new(Path.Combine(_path, "sync"), Path.Combine(_path, "data"));
            Directory.CreateDirectory(Paths.SyncRootPath);
        }

        internal ValueTask<ICloudStateStore> OpenJournalAsync() =>
            new MirrorPulseCfSharpStateSession(Paths).OpenAsync(new CloudStateStoreContext(Paths.SyncRootPath));

        internal async Task ProductSqlAsync(string sql)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public void Dispose()
        {
            string directory = Path.GetFullPath(_path);
            if (!SourceDirectoryPathNormalizer.IsWithin(Path.Combine(Path.GetTempPath(), "MirrorPulse-tests"), directory))
                throw new InvalidOperationException("The test directory escaped its expected parent.");
            Directory.Delete(directory, recursive: true);
        }
    }
}
#endif
