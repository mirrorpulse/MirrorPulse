using System.Text.Json;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;

namespace MirrorPulse.CfSharp.CrashProbe;

public static partial class CliFixtureProbe
{
    /// <summary>Observes the original polling intent and public SDK state while no Host owns the fixture.</summary>
    public static async Task<int> RunTransferPollAuditAsync(string directory, string originalBatchId)
    {
        await using var fixture = await OpenTransferFixtureAsync(directory, _ => { });
        if (fixture.Marker.SchemaVersion is not (2 or 3))
            throw new InvalidDataException("Polling restart requires exactly two authenticated single-root deployments.");
        var left = fixture.Inventory.Roots[0];
        var pendingPolls = new MirrorPulseCatalogRemotePollPendingStore(fixture.Catalog);
        var snapshots = new MirrorPulseFileRemotePollSnapshotStore(fixture.Paths.DataRootPath);
        var retained = await pendingPolls.LoadAsync(left, default);
        bool interrupted = originalBatchId == "pending";
        if (interrupted)
        {
            if (retained is null || retained.Previous.Count != 1 || retained.Candidate.Count != 1 ||
                retained.Previous.Values.Single().RelativePath != "forward.bin" ||
                retained.Candidate.Values.Single().RelativePath != "forward.bin" ||
                retained.Previous.Values.Single().RemoteRevision == retained.Candidate.Values.Single().RemoteRevision)
                throw new InvalidDataException("The interrupted Host lost the exact original file observation.");
            originalBatchId = retained.BatchId;
        }
        else if (retained is not null)
            throw new InvalidDataException("The restarted Host did not clear its completed original polling intent.");

        if (!originalBatchId.StartsWith($"{left.InstanceId}/poll-root/{left.RootId}/", StringComparison.Ordinal))
            throw new InvalidDataException("The original polling batch is outside the authenticated deployment.");
        await using var store = await MirrorPulseCfSharpStateStoreFactory.Create(fixture.Paths)
            .OpenAsync(new(fixture.Paths.SyncRootPath));
        await using var read = await store.BeginTransactionAsync();
        var progress = await read.RemoteBatches.GetAsync(originalBatchId);
        if (progress is not { Status: CloudRemoteBatchStatus.Applied, AppliedEntryCount: 1, TotalEntryCount: 1 } ||
            retained is not null && !progress.Fingerprint.Span.SequenceEqual(retained.Fingerprint))
            throw new InvalidDataException("The original SDK batch is missing or differs from its durable intent.");

        var routes = new List<object>();
        foreach (var root in fixture.Inventory.Roots)
        {
            var baseline = await snapshots.LoadBaselineAsync(root);
            var published = await read.Checkpoints.GetAsync(MirrorPulseRemoteBatchCoordinator.GetCheckpointName(root.InstanceId, root.RootId));
            if (baseline is null || published is null || published.Value.Length != 32)
                throw new InvalidDataException("The fixture root lacks its snapshot or published cursor.");
            bool matches = baseline.Cursor.Span.SequenceEqual(published.Value.Span);
            bool expectedMatch = !interrupted || root != left;
            if (matches != expectedMatch)
                throw new InvalidDataException("The two root checkpoint states do not match the observed interruption phase.");
            routes.Add(new
            {
                rootId = root.RootId.ToString(),
                baselineCursor = Convert.ToBase64String(baseline.Cursor.Span),
                publishedCursor = Convert.ToBase64String(published.Value.Span)
            });
        }
        await read.RollbackAsync();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            batchId = originalBatchId,
            fingerprint = Convert.ToHexString(progress.Fingerprint.Span),
            interrupted,
            routes
        }, JsonOptions));
        return 0;
    }
}
