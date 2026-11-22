using System.Security.Cryptography;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    private static async ValueTask ReconcileClosedFixtureJournalAsync(CloudFileSystem fileSystem,
        MirrorPulseCfSharpStateSession state, CloudLocalChangeFeed feed, MirrorPulseProductCatalog catalog,
        MirrorPulseRootRouter router, MirrorPulseNamespaceWorkAdmission? work, CancellationToken token)
    {
        foreach (var root in router.Registrations)
            Assert.IsTrue(root.State == RootRegistrationState.Disabled || work is not null && !work.IsAdmitted(root.RootId),
                "The fixture must retain disabled or closed roots through reconciliation.");
        var original = await ReadRowsAsync();
        var remote = new ReconciliationFixtureTransport();
        var policy = new MirrorPulseFullRescanPolicy(fileSystem, feed, state, router, catalog,
            remote, remote, _ => true, namespaceWork: work);
        var recovery = new MirrorPulseFullRescanRecovery(catalog, policy.ReconcileAsync,
            feed.AcknowledgeFullRescanAsync, (_, _) => ValueTask.CompletedTask);
        var result = await recovery.RunAsync(new(true), token);
        Assert.IsTrue(result.WasRequired);
        Assert.AreEqual(0, result.ReconciledEntryCount);
        Assert.AreEqual(0, remote.Calls);
        var retained = await ReadRowsAsync();
        foreach (var row in original)
        {
            Assert.IsTrue(retained.TryGetValue(row.Key, out string? value));
            Assert.AreEqual(row.Value, value, "Official reconciliation must preserve every original pending row.");
        }
        var deferred = await catalog.ReadDeferredRescanRootsAsync(token);
        foreach (var root in router.Registrations) Assert.Contains(root.RootId, deferred);

        async Task<Dictionary<Guid, string>> ReadRowsAsync()
        {
            await using var read = await state.OpenStore.BeginTransactionAsync(token);
            var paging = read.Operations as ICloudOperationJournalPaging;
            Assert.IsNotNull(paging);
            long through = await paging.GetHighWaterSequenceAsync(token);
            var rows = new Dictionary<Guid, string>();
            long after = 0;
            for (int index = 0; index < 16; index++)
            {
                var page = await paging.ReadPageAsync(after, through, 256, token);
                foreach (var row in page.Operations)
                    rows.Add(row.OperationId, $"{row.Sequence}/{row.Kind}/{row.ItemId}/{row.CreatedAt:O}/" +
                        $"{row.AttemptCount}/{row.RetryAfter:O}/{Convert.ToHexString(SHA256.HashData(row.Payload.Span))}");
                if (!page.HasMore) { await read.RollbackAsync(token); return rows; }
                Assert.IsGreaterThan(after, page.LastScannedSequence);
                after = page.LastScannedSequence;
            }
            Assert.Fail("The synthetic journal exceeded its complete observation bound.");
            return rows;
        }
    }

    private sealed class ReconciliationFixtureTransport : IMirrorPulseWorkerUploadTransport, IMirrorPulseWorkerStatTransport
    {
        internal int Calls { get; private set; }

        public ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("A closed fixture root must retain its remote work.");
        }

        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("A closed fixture root must retain its remote work.");
        }
    }
}
