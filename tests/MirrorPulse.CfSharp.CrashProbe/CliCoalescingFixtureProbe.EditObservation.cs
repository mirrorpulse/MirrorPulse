using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using CfSharp.Storage.Sqlite;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;

namespace MirrorPulse.CfSharp.CrashProbe;

public sealed record CliCoalescingJournalObservation(Guid OperationId, long Sequence, string Kind,
    Guid? ItemId, bool AssociatedWithTarget, int AttemptCount, DateTimeOffset CreatedAt, string PayloadSha256);
public sealed record CliCoalescingEchoObservation(Guid SuppressionId, string Kind, Guid? ItemId,
    int RemainingObservations, DateTimeOffset ExpiresAt, string PayloadSha256);
public sealed record CliCoalescingEditObservation(int SchemaVersion, bool ExclusiveCatalogOwner, Guid? TargetItemId,
    bool TargetItemIsTombstone, long ThroughSequence, long LastScannedSequence, bool JournalInventoryComplete,
    IReadOnlyList<CliCoalescingJournalObservation> Journal, IReadOnlyList<CliCoalescingEchoObservation> TargetEchoes,
    string? FeedCheckpointSha256, int? FeedCheckpointLength, DateTimeOffset? FeedCheckpointUpdatedAt,
    bool NativeInspected, CliFixtureItemObservation? Native, string? NativeIdentitySha256,
    bool OriginalSdkAcknowledgementExecuted, bool MutationExecuted);

public static partial class CliCoalescingFixtureProbe
{
    /// <summary>Retains a bounded actual journal inventory after the exact synthetic Host has stopped.</summary>
    public static async Task<int> ObserveEditAsync(string directory, bool inspectNative)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        const string prefix = "MirrorPulse-cli-integration-"; string name = Path.GetFileName(root);
        if (!string.Equals(Path.GetDirectoryName(root), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _) ||
            inspectNative && Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1") return 2;
        RequireExistingUnredirected(root);
        string markerPath = Path.Combine(root, ".mp-cli-fixture.json"); RequireExistingUnredirected(markerPath);
        if (new FileInfo(markerPath).Length > 4096) return 2;
        var marker = JsonSerializer.Deserialize<CliFixtureMarker>(await File.ReadAllTextAsync(markerPath), JsonOptions)
            ?? throw new InvalidDataException("The synthetic edit marker is missing.");
        if (marker.SchemaVersion != 1 || !Guid.TryParse(marker.InstanceId, out Guid instance) || instance == Guid.Empty ||
            !Guid.TryParse(marker.RootId, out Guid rootId) || rootId == Guid.Empty || string.IsNullOrWhiteSpace(marker.RootKey) ||
            string.IsNullOrWhiteSpace(marker.DirectoryName) || marker.DirectoryName is "." or ".." ||
            marker.DirectoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return 2;
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        foreach (string path in new[] { paths.SyncRootPath, paths.DataRootPath, Path.GetDirectoryName(paths.ProductCatalogDatabasePath)!, paths.ProductCatalogDatabasePath })
            RequireExistingUnredirected(path);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
        var topology = await catalog.ReadAdapterTopologyAsync();
        if (topology.Roots.Count != 1 || topology.Instances.Count != 1) throw new InvalidDataException("The edit observation requires one original route.");
        var route = topology.Roots[0];
        if (route.InstanceId.Value != instance || topology.Instances[0].InstanceId != route.InstanceId || route.RootId.Value != rootId ||
            route.UniquenessKey != marker.RootKey || route.DirectoryName != marker.DirectoryName)
            throw new InvalidDataException("The edit observation cannot adopt another route.");
        RequireExistingUnredirected(paths.CfSharpStateDatabasePath);
        await using var store = await MirrorPulseCfSharpStateStoreFactory.Create(paths).OpenAsync(new(paths.SyncRootPath));
        var rows = new List<CliCoalescingJournalObservation>(); var echoes = new List<CliCoalescingEchoObservation>();
        CloudItemState? target; CloudStateCheckpoint? checkpoint; long through, after = 0; bool complete = false;
        string relative = Path.Combine(route.DirectoryName, "cli-roundtrip.txt");
        await using (var transaction = await store.BeginTransactionAsync())
        {
            target = await transaction.Items.GetByRelativePathAsync(relative);
            var portable = await transaction.Items.GetByRelativePathAsync(relative.Replace('\\', '/'));
            if (target is not null && portable is not null && target.ItemId != portable.ItemId)
                throw new InvalidDataException("The target has inconsistent public path identities.");
            target ??= portable;
            if (transaction.Operations is not ICloudOperationJournalPaging paging) throw new NotSupportedException("The official journal paging capability is required.");
            through = await paging.GetHighWaterSequenceAsync();
            for (int pageNumber = 0; pageNumber < 16; pageNumber++)
            {
                var page = await paging.ReadPageAsync(after, through, 128);
                rows.AddRange(page.Operations.Select(operation => new CliCoalescingJournalObservation(operation.OperationId, operation.Sequence,
                    operation.Kind.ToString(), operation.ItemId, target is not null && operation.ItemId == target.ItemId,
                    operation.AttemptCount, operation.CreatedAt, Convert.ToHexString(SHA256.HashData(operation.Payload.Span)))));
                after = page.LastScannedSequence;
                if (!page.HasMore) { complete = true; break; }
            }
            checkpoint = await transaction.Checkpoints.GetAsync("cfsharp.local-change-feed.v1");
            foreach (var echo in await transaction.EchoSuppressions.ListActiveAsync(DateTimeOffset.UtcNow))
                if (string.Equals(echo.RelativePath.Replace('\\', '/'), relative.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    echoes.Add(new(echo.SuppressionId, echo.Kind.ToString(), echo.ItemId, echo.RemainingObservations, echo.ExpiresAt,
                        Convert.ToHexString(SHA256.HashData(echo.Payload.Span))));
            await transaction.RollbackAsync();
        }
        CliFixtureItemObservation? native = null; string? nativeIdentity = null;
        if (inspectNative)
        {
            string inspection = Path.Combine(root, "inspection");
            if (Path.Exists(inspection)) RequireExistingUnredirected(inspection);
            Directory.CreateDirectory(inspection); string database = Path.Combine(inspection, "cfsharp.db");
            if (Path.Exists(database)) RequireExistingUnredirected(database);
            // Match the existing independent inspection boundary: no root registration,
            // provider, remote application, journal feed, ACL operation or Host execution.
            await using var fileSystem = CloudFileSystem.CreateBuilder(paths.SyncRootPath).WithStateStore(new SqliteCloudStateStoreFactory(database)).Build();
            await fileSystem.StartAsync();
            var snapshot = await fileSystem.GetFile(relative).InspectAsync();
            native = new("RoundTripFile", snapshot.Exists, snapshot.IsPlaceholder, snapshot.PlaceholderState.ToString(),
                snapshot.ContentAvailability.ToString(), snapshot.SynchronizationState.ToString(), snapshot.Length,
                snapshot.OnDiskDataSize, snapshot.ValidatedDataSize, snapshot.ModifiedDataSize);
            if (!snapshot.PlaceholderIdentity.IsEmpty) nativeIdentity = Convert.ToHexString(SHA256.HashData(snapshot.PlaceholderIdentity.Span));
        }
        var result = new CliCoalescingEditObservation(1, true, target?.ItemId, target?.IsTombstone ?? false, through, after, complete,
            rows.AsReadOnly(), echoes.AsReadOnly(), checkpoint is null ? null : Convert.ToHexString(SHA256.HashData(checkpoint.Value.Span)),
            checkpoint?.Value.Length, checkpoint?.UpdatedAt, inspectNative, native, nativeIdentity, false, false);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions)); return 0;
    }
}
