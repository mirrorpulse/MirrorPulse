using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>
/// Polls Adapter-backed remote directory pages and turns deterministic snapshot changes into
/// CfSharp remote batches. The last successful snapshot is kept in memory and can be restored
/// from MP's data directory; CfSharp remains the authority for applying and checkpointing each
/// batch.
/// </summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class MirrorPulseActiveRemotePoller : IAsyncDisposable
{
    private readonly IMirrorPulseDirectoryPageSource _source;
    private readonly IReadOnlyList<InstanceId> _instances;
    private readonly Dictionary<InstanceId, IReadOnlyList<RootRegistration>> _roots;
    private readonly Func<IReadOnlyList<RootRegistration>>? _currentRoots;
    private readonly Func<InstanceId, CloudRemoteChangeBatch, CancellationToken, ValueTask<MirrorPulseRemotePollApplyOutcome>> _apply;
    private readonly TimeSpan _interval;
    private readonly int _pageSize;
    private readonly int _maximumPages;
    private readonly IMirrorPulseRemotePollSnapshotStore? _snapshotStore;
    private readonly IMirrorPulseRemotePollPendingStore? _pendingStore;
    private readonly Func<RootRegistration, CancellationToken, ValueTask<bool>>? _mayPoll;
    private readonly ConcurrentDictionary<(InstanceId, RootId, string), IReadOnlyDictionary<string, SnapshotEntry>> _snapshots = new();
    private readonly ConcurrentDictionary<InstanceId, RootId> _lastRoot = new(); // Different instance schedules can run concurrently.
    private readonly MirrorPulseInstanceScheduler _scheduler;
    private readonly bool _ownsScheduler;
    private readonly MirrorPulseNamespaceWorkAdmission? _namespaceWork;
    private readonly Func<RootRegistration, CloudRemoteChangeBatch, CancellationToken, ValueTask<bool>>? _mayReplay;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;
    private readonly LocalRollingLogWriter? _failureLog;
    private readonly MirrorPulseRemoteIdentityResolver? _identityResolver;

    public MirrorPulseActiveRemotePoller(
        IMirrorPulseDirectoryPageSource source,
        IEnumerable<AdapterInstance> instances,
        IEnumerable<RootRegistration> roots,
        Func<InstanceId, CloudRemoteChangeBatch, CancellationToken, ValueTask<MirrorPulseRemotePollApplyOutcome>> apply,
        TimeSpan? interval = null,
        int pageSize = 128,
        int maximumPages = 2048,
        IMirrorPulseRemotePollSnapshotStore? snapshotStore = null,
        IMirrorPulseRemotePollPendingStore? pendingStore = null,
        MirrorPulseInstanceScheduler? scheduler = null,
        Func<RootRegistration, CancellationToken, ValueTask<bool>>? mayPoll = null,
        Func<IReadOnlyList<RootRegistration>>? currentRoots = null,
        MirrorPulseNamespaceWorkAdmission? namespaceWork = null,
        Func<RootRegistration, CloudRemoteChangeBatch, CancellationToken, ValueTask<bool>>? mayReplay = null,
        string? diagnosticsDirectory = null,
        MirrorPulseRemoteIdentityResolver? identityResolver = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(roots);
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        if (pageSize is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(pageSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPages, 1);
        if (interval is { } value && value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));

        _instances = instances.Where(instance => instance.Enabled)
            .Select(instance => instance.InstanceId).Distinct().ToArray();
        _roots = roots.GroupBy(root => root.InstanceId)
            .ToDictionary(group => group.Key,
                group => (IReadOnlyList<RootRegistration>)group
                    .OrderBy(root => root.DirectoryName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(root => root.DirectoryName, StringComparer.Ordinal)
                    .ToArray(), EqualityComparer<InstanceId>.Default);
        _interval = interval ?? TimeSpan.FromSeconds(30);
        _pageSize = pageSize;
        _maximumPages = maximumPages;
        _snapshotStore = snapshotStore;
        _pendingStore = pendingStore;
        _mayPoll = mayPoll;
        _currentRoots = currentRoots;
        _ownsScheduler = scheduler is null;
        _scheduler = scheduler ?? new MirrorPulseInstanceScheduler();
        _namespaceWork = namespaceWork;
        _mayReplay = mayReplay;
        _identityResolver = identityResolver;
        if (pendingStore is not null && snapshotStore is null)
            throw new ArgumentException("Pending batches require a durable snapshot store.", nameof(snapshotStore));
        if (diagnosticsDirectory is not null) _failureLog = new(diagnosticsDirectory);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loop is not null) throw new InvalidOperationException("The remote poller has already started.");
        _loop = RunAsync(cancellationToken);
        await Task.Yield();
    }

    /// <summary>Runs one bounded root turn for an enabled instance, rotating even after a blocked or failed turn.</summary>
    public async ValueTask<bool> PollOnceAsync(InstanceId instanceId, CancellationToken cancellationToken = default)
    {
        try { return await _scheduler.RunAsync(instanceId, token => PollCoreAsync(instanceId, token), cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            if (_failureLog is not null)
            {
                try
                {
                    await _failureLog.WriteAsync(new(LogLevel.Warning, "sync", "RemotePollFailed", DateTimeOffset.UtcNow,
                        [new("instanceId", instanceId.ToString()), new("hresult", error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)),
                            new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(error)),
                            new("remoteFailureBoundary", MirrorPulseRemoteFailureClassifier.ReadBoundary(error).ToString())]), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception observation) when (observation is not OutOfMemoryException)
                {
                    // Diagnostics cannot replace the original polling failure or
                    // change the retained snapshot, intent or public retry behavior.
                }
            }
            throw;
        }
    }

    private async ValueTask<bool> PollCoreAsync(InstanceId instanceId, CancellationToken cancellationToken)
    {
        if (!_instances.Contains(instanceId)) return false;
        IReadOnlyList<RootRegistration>? configured = _currentRoots is null
            ? _roots.GetValueOrDefault(instanceId)
            : _currentRoots().Where(root => root.InstanceId == instanceId).ToArray();
        var roots = configured?.Where(root => root.State == RootRegistrationState.Active)
            .OrderBy(root => root.RootId.ToString(), StringComparer.Ordinal).ToArray() ?? [];
        if (roots.Length == 0) return false;
        bool scoped = (_snapshotStore is null or IMirrorPulseRootRemotePollSnapshotStore) &&
            (_pendingStore is null or IMirrorPulseRootRemotePollPendingStore);
        if (!scoped && configured!.Count != 1)
            throw new NotSupportedException("Multiple roots require root-scoped polling stores.");
        if (!scoped && _identityResolver is not null)
            throw new NotSupportedException("Generation selection requires root-scoped polling stores.");
        if (configured!.Count > 1 && configured.Any(value => value.IdentityScope != RootIdentityScope.InstanceRoot))
            throw new InvalidDataException("Several roots require independent logical item identity scopes.");
        var root = _lastRoot.TryGetValue(instanceId, out var last)
            ? roots.FirstOrDefault(candidate => string.CompareOrdinal(candidate.RootId.ToString(), last.ToString()) > 0) ?? roots[0]
            : roots[0];
        _lastRoot[instanceId] = root.RootId;
        var key = (instanceId, root.RootId, root.UniquenessKey);
        var legacy = _pendingStore is null ? null : await _pendingStore.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var legacySnapshot = scoped && _snapshotStore is not null
            ? await _snapshotStore.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false) : null;
        if (scoped && configured!.Count != 1 && (legacy is not null || legacySnapshot is not null))
            throw new InvalidDataException("An unscoped legacy poll cannot be attributed to several roots; retain the original mapping first.");
        MirrorPulsePendingRemotePoll? pending = legacy ?? (scoped && _pendingStore is IMirrorPulseRootRemotePollPendingStore rootPending
            ? await rootPending.LoadAsync(root, cancellationToken).ConfigureAwait(false) : null);
        if (pending is not null)
        {
            if (!string.Equals(root.DirectoryName, pending.RootDirectoryName, StringComparison.Ordinal))
                throw new InvalidDataException("A pending batch belongs to a different root mapping.");
            IReadOnlyDictionary<string, SnapshotEntry> prior = FromDurableSnapshot(pending.Previous);
            IReadOnlyDictionary<string, SnapshotEntry> candidate = FromDurableSnapshot(pending.Candidate);
            CloudRemoteChangeBatch replay = CreateBatch(instanceId, root, prior, candidate, pending.ProjectionVersion);
            if (replay.BatchId != pending.BatchId || !replay.Fingerprint.Span.SequenceEqual(pending.Fingerprint))
                throw new InvalidDataException("The pending poll intent does not recreate its immutable batch.");
            bool allowed = _mayReplay is not null
                ? await _mayReplay(root, replay, cancellationToken).ConfigureAwait(false)
                : _mayPoll is null || await _mayPoll(root, cancellationToken).ConfigureAwait(false);
            if (!allowed) return false;
            MirrorPulseNamespaceWorkAdmission.WorkLease? replayAdmission = null;
            if (_namespaceWork is not null && !_namespaceWork.TryEnter([root.RootId], out replayAdmission)) return false;
            using var retainedReplayAdmission = replayAdmission;
            // Do not enumerate a newer remote tree until this exact batch has converged.
            return await ApplyAndCommitAsync(root, replay, candidate, scoped, legacy is not null, cancellationToken).ConfigureAwait(false);
        }

        if (_mayPoll is not null && !await _mayPoll(root, cancellationToken).ConfigureAwait(false)) return false;
        MirrorPulseNamespaceWorkAdmission.WorkLease? admission = null;
        if (_namespaceWork is not null && !_namespaceWork.TryEnter([root.RootId], out admission)) return false;
        using var retainedAdmission = admission;

        if (!_snapshots.TryGetValue(key, out IReadOnlyDictionary<string, SnapshotEntry>? previous))
        {
            IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry>? persisted =
                _snapshotStore is null
                    ? null
                    : scoped ? await ((IMirrorPulseRootRemotePollSnapshotStore)_snapshotStore).LoadAsync(root, cancellationToken).ConfigureAwait(false) ?? legacySnapshot
                    : await _snapshotStore.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false);
            if (persisted is not null)
            {
                previous = FromDurableSnapshot(persisted);
                _snapshots[key] = previous;
            }
        }

        IReadOnlyDictionary<string, SnapshotEntry> current = await ReadSnapshotAsync(
            instanceId, root.UniquenessKey, cancellationToken).ConfigureAwait(false);
        if (_identityResolver is not null)
            current = await CaptureIdentitiesAsync(root, current, cancellationToken).ConfigureAwait(false);
        else if (previous?.Values.Any(entry => entry.PublicIdentity is not null) == true)
            throw new InvalidOperationException("A captured generation snapshot requires its original identity resolver.");
        if (previous is null)
        {
            await SaveSnapshotAsync(root, current, scoped, cancellationToken).ConfigureAwait(false);
            _snapshots[key] = current;
            return false;
        }

        if (SnapshotsEqual(root, previous!, current))
        {
            // Re-saving a loaded snapshot repairs a stale temporary file without changing the
            // CfSharp cursor or creating another remote batch.
            await SaveSnapshotAsync(root, current, scoped, cancellationToken).ConfigureAwait(false);
            return false;
        }

        int projectionVersion = _identityResolver is not null ? 4 : scoped ? 3 : 2;
        CloudRemoteChangeBatch batch = CreateBatch(instanceId, root, previous!, current, projectionVersion);
        if (_pendingStore is not null)
        {
            var captured = new MirrorPulsePendingRemotePoll(batch.BatchId, batch.Fingerprint.ToArray(), root.DirectoryName,
                ToDurableSnapshot(previous!), ToDurableSnapshot(current), projectionVersion)
            { RootId = scoped ? root.RootId : null, RootKey = scoped ? root.UniquenessKey : null };
            if (scoped) await ((IMirrorPulseRootRemotePollPendingStore)_pendingStore).SaveAsync(root, captured, cancellationToken).ConfigureAwait(false);
            else await _pendingStore.SaveAsync(instanceId, captured, cancellationToken).ConfigureAwait(false);
        }
        return await ApplyAndCommitAsync(root, batch, current, scoped, false, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> ApplyAndCommitAsync(RootRegistration root, CloudRemoteChangeBatch batch,
        IReadOnlyDictionary<string, SnapshotEntry> current, bool scoped, bool legacy, CancellationToken cancellationToken)
    {
        MirrorPulseRemotePollApplyOutcome outcome = await _apply(root.InstanceId, batch, cancellationToken).ConfigureAwait(false);
        if (!outcome.Completed) return false;
        if (!outcome.SafeCursor.Span.SequenceEqual(batch.FinalCursor.Span))
            throw new InvalidDataException("A completed remote poll batch must have its final safe cursor.");
        await SaveSnapshotAsync(root, current, scoped, cancellationToken).ConfigureAwait(false);
        _snapshots[(root.InstanceId, root.RootId, root.UniquenessKey)] = current;
        if (_pendingStore is not null)
        {
            if (scoped && !legacy) await ((IMirrorPulseRootRemotePollPendingStore)_pendingStore).ClearAsync(root, batch.BatchId, cancellationToken).ConfigureAwait(false);
            else await _pendingStore.ClearAsync(root.InstanceId, batch.BatchId, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    private async ValueTask SaveSnapshotAsync(
        RootRegistration root,
        IReadOnlyDictionary<string, SnapshotEntry> snapshot,
        bool scoped,
        CancellationToken cancellationToken)
    {
        if (_snapshotStore is null) return;
        if (scoped)
        {
            var store = (IMirrorPulseRootRemotePollSnapshotStore)_snapshotStore;
            await store.SaveAsync(root, ToDurableSnapshot(snapshot), cancellationToken).ConfigureAwait(false);
            await store.ClearLegacyAsync(root.InstanceId, cancellationToken).ConfigureAwait(false);
        }
        else await _snapshotStore.SaveAsync(root.InstanceId, ToDurableSnapshot(snapshot), cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, MirrorPulseRemoteSnapshotEntry> ToDurableSnapshot(
        IReadOnlyDictionary<string, SnapshotEntry> snapshot) => snapshot.ToDictionary(
            item => item.Key,
            item => new MirrorPulseRemoteSnapshotEntry(item.Value.RemoteId,
                item.Value.RemoteRevision, item.Value.ItemKind, item.Value.RelativePath,
                item.Value.Length, ToSnapshotMetadata(item.Value.Metadata))
            { PublicIdentity = item.Value.PublicIdentity },
            StringComparer.Ordinal);

    private static Dictionary<string, SnapshotEntry> FromDurableSnapshot(
        IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry> snapshot) => snapshot.ToDictionary(
            item => item.Key, item => FromSnapshotEntry(item.Value), StringComparer.Ordinal);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        while (!linked.Token.IsCancellationRequested)
        {
            foreach (InstanceId instanceId in _instances)
            {
                try
                {
                    await PollOnceAsync(instanceId, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // A transient remote failure leaves the previous snapshot intact. The next
                    // interval retries the same comparison and lets the normal status surfaces
                    // report any Worker-side error.
                }
            }

            try
            {
                await Task.Delay(_interval, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, SnapshotEntry>> ReadSnapshotAsync(
        InstanceId instanceId,
        string rootKey,
        CancellationToken cancellationToken)
    {
        var entries = new Dictionary<string, SnapshotEntry>(StringComparer.Ordinal);
        var directories = new Queue<string>([string.Empty]);
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = 0;
        while (directories.Count > 0)
        {
            string directory = directories.Dequeue();
            if (!visitedDirectories.Add(directory)) continue;
            ReadOnlyMemory<byte> cursor = ReadOnlyMemory<byte>.Empty;
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                if (++pages > _maximumPages)
                    throw new InvalidDataException("The active remote poll exceeded its page limit.");
                CloudRemoteDirectoryPage page = await _source.ReadPageAsync(
                    instanceId, rootKey, directory, cursor, _pageSize, cancellationToken).ConfigureAwait(false);
                foreach (CloudRemoteDirectoryEntry entry in page.Entries)
                {
                    string relativePath = NormalizeRemotePath(entry.RelativePath);
                    string key = entry.RemoteId;
                    CloudPlaceholderMetadata metadata = entry.Metadata ?? (entry.ItemKind == CloudItemKind.Directory
                        ? CloudPlaceholderMetadata.CreateDirectoryBuilder().Build()
                        : CloudPlaceholderMetadata.CreateFileBuilder().Build());
                    var candidate = new SnapshotEntry(entry.RemoteId, entry.RemoteRevision,
                            entry.ItemKind, relativePath, entry.Length, metadata);
                    if (!entries.TryAdd(key, candidate) &&
                        !entries[key].Equals(new SnapshotEntry(entry.RemoteId, entry.RemoteRevision,
                            entry.ItemKind, relativePath, entry.Length, metadata)))
                    {
                        throw new InvalidDataException("The remote poll returned duplicate object identifiers.");
                    }

                    if (entry.ItemKind == CloudItemKind.Directory && !entry.IsDeleted)
                        directories.Enqueue(relativePath);
                }

                if (page.IsComplete) break;
                if (page.ContinuationCursor.IsEmpty ||
                    !seenCursors.Add(Convert.ToBase64String(page.ContinuationCursor.Span)))
                {
                    throw new InvalidDataException("The remote poll returned an invalid continuation cursor.");
                }

                cursor = page.ContinuationCursor;
            }
        }

        return new ReadOnlyDictionary<string, SnapshotEntry>(entries);
    }

    private static CloudRemoteChangeBatch CreateBatch(
        InstanceId instanceId,
        RootRegistration root,
        IReadOnlyDictionary<string, SnapshotEntry> previous,
        IReadOnlyDictionary<string, SnapshotEntry> current,
        int projectionVersion)
    {
        if (projectionVersion is not (1 or 2 or 3 or 4))
            throw new InvalidDataException("The remote poll projection version is unsupported.");
        if (projectionVersion == 4) ValidateCapturedSnapshots(root, previous, current);
        byte[] initialCursor = projectionVersion >= 3 ? RootFingerprint(root, previous, projectionVersion) : Fingerprint(previous);
        byte[] finalCursor = projectionVersion >= 3 ? RootFingerprint(root, current, projectionVersion) : Fingerprint(current);
        var changes = new List<CloudRemoteChange>();
        foreach ((string id, SnapshotEntry entry) in current.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!previous.TryGetValue(id, out SnapshotEntry? prior) ||
                projectionVersion == 4 && !SamePublicIdentity(root, prior, entry))
            {
                changes.Add(ToChange(instanceId, root, entry, UpsertKind(entry.ItemKind),
                    previousRevision: null, previousPath: null, finalCursor, projectionVersion));
                continue;
            }

            CloudRemoteChangeKind kind = prior.ItemKind != entry.ItemKind
                ? UpsertKind(entry.ItemKind)
                : prior.RelativePath != entry.RelativePath
                    ? CloudRemoteChangeKind.Move
                    : UpsertKind(entry.ItemKind);
            // Each enumeration builds new metadata objects. Reference equality
            // invalidated unchanged file bytes whenever an unrelated object changed.
            // Version 2 follows the content policy: metadata alone is not a content
            // revision. Keep version 1 only to reproduce an already captured intent.
            if ((kind is CloudRemoteChangeKind.FileUpsert or CloudRemoteChangeKind.DirectoryUpsert) &&
                prior.ItemKind == entry.ItemKind && prior.RemoteRevision == entry.RemoteRevision && prior.Length == entry.Length &&
                (projectionVersion >= 2 || Equals(prior.Metadata, entry.Metadata))) continue;
            changes.Add(ToChange(instanceId, root, entry, kind, prior.RemoteRevision,
                kind == CloudRemoteChangeKind.Move ? prior.RelativePath : null, finalCursor, projectionVersion));
        }

        foreach ((string id, SnapshotEntry entry) in previous.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (current.ContainsKey(id)) continue;
            changes.Add(ToChange(instanceId, root, entry, CloudRemoteChangeKind.Delete,
                previousRevision: null, previousPath: null, finalCursor, projectionVersion));
        }

        return new CloudRemoteChangeBatch(
            (projectionVersion >= 3 ? MirrorPulseRemotePollBatchScope.Prefix(root) : instanceId + "/") +
                $"{Convert.ToHexString(initialCursor)}/{Convert.ToHexString(finalCursor)}",
            initialCursor,
            changes,
            finalCursor);
    }

    private static CloudRemoteChangeKind UpsertKind(CloudItemKind itemKind) =>
        itemKind == CloudItemKind.Directory
            ? CloudRemoteChangeKind.DirectoryUpsert
            : CloudRemoteChangeKind.FileUpsert;

    private static CloudRemoteChange ToChange(
        InstanceId instanceId,
        RootRegistration root,
        SnapshotEntry entry,
        CloudRemoteChangeKind kind,
        string? previousRevision,
        string? previousPath,
        ReadOnlyMemory<byte> cursor, int projectionVersion)
    {
        CloudItemKind itemKind = entry.ItemKind;
        CloudPlaceholderMetadata metadata = entry.Metadata;
        string path = root.DirectoryName + "\\" + entry.RelativePath.Replace('/', '\\');
        CloudPlaceholderIdentity identity = projectionVersion == 4 ? CapturedIdentity(root, entry) :
            MirrorPulsePlaceholderIdentity.CreateForRoot(root, entry.RemoteId, entry.RemoteRevision).ToCfSharp();
        return new CloudRemoteChange(
            (projectionVersion >= 3 ? MirrorPulseRemotePollBatchScope.Prefix(root) : instanceId + "/") +
                $"{entry.RemoteId}/{Convert.ToHexString(cursor.Span)}",
            kind,
            identity.RemoteId,
            entry.RemoteRevision,
            itemKind,
            path,
            identity.ItemId,
            previousRevision,
            previousPath is null ? null : root.DirectoryName + "\\" + previousPath.Replace('/', '\\'),
            entry.Length,
            metadata,
            cursorAfter: cursor);
    }

    private static bool SnapshotsEqual(
        RootRegistration root,
        IReadOnlyDictionary<string, SnapshotEntry> left,
        IReadOnlyDictionary<string, SnapshotEntry> right) =>
        left.Count == right.Count && GenerationFingerprint(root, left).AsSpan().SequenceEqual(GenerationFingerprint(root, right));

    private static byte[] RootFingerprint(RootRegistration root, IReadOnlyDictionary<string, SnapshotEntry> snapshot, int projectionVersion) =>
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]
        { root.InstanceId.ToString(), root.RootId.ToString(), root.UniquenessKey,
            Convert.ToHexString(projectionVersion == 4 ? GenerationFingerprint(root, snapshot) : CanonicalFingerprint(snapshot)) }));

    private static byte[] CanonicalFingerprint(IReadOnlyDictionary<string, SnapshotEntry> snapshot)
    {
        using var buffer = new MemoryStream();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        foreach (var pair in snapshot.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var entry = pair.Value;
            writer.WriteStartArray(); writer.WriteStringValue(pair.Key); writer.WriteStringValue(entry.RemoteRevision);
            writer.WriteNumberValue((int)entry.ItemKind); writer.WriteStringValue(entry.RelativePath);
            if (entry.Length is { } length) writer.WriteNumberValue(length); else writer.WriteNullValue();
            writer.WriteEndArray();
        }
        writer.WriteEndArray(); writer.Flush();
        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
    }

    private static byte[] Fingerprint(IReadOnlyDictionary<string, SnapshotEntry> snapshot)
    {
        var builder = new StringBuilder();
        foreach ((string id, SnapshotEntry entry) in snapshot.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            builder.Append(id).Append('\n').Append(entry.RemoteRevision).Append('\n')
                .Append((int)entry.ItemKind).Append('\n').Append(entry.RelativePath).Append('\n')
                .Append(entry.Length?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "")
                .Append('\n');
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string NormalizeRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new InvalidDataException("The remote poll returned an invalid path.");
        string normalized = path.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || normalized.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("The remote poll returned an unsafe path.");
        return normalized;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        }

        _shutdown.Dispose();
        if (_ownsScheduler) await _scheduler.DisposeAsync().ConfigureAwait(false);
        _failureLog?.Dispose();
    }

    private static SnapshotEntry FromSnapshotEntry(MirrorPulseRemoteSnapshotEntry entry) =>
        new(entry.RemoteId, entry.RemoteRevision, entry.ItemKind, entry.RelativePath, entry.Length,
            FromSnapshotMetadata(entry.Metadata))
        { PublicIdentity = entry.PublicIdentity };

    private static MirrorPulseRemoteSnapshotMetadata ToSnapshotMetadata(CloudPlaceholderMetadata metadata) =>
        new(metadata.Kind, metadata.Attributes, metadata.CreationTime, metadata.LastAccessTime,
            metadata.LastWriteTime, metadata.ChangeTime);

    private static CloudPlaceholderMetadata FromSnapshotMetadata(
        MirrorPulseRemoteSnapshotMetadata metadata)
    {
        CloudPlaceholderMetadata.Builder builder = metadata.Kind == CloudItemKind.Directory
            ? CloudPlaceholderMetadata.CreateDirectoryBuilder()
            : CloudPlaceholderMetadata.CreateFileBuilder();
        builder.WithAttributes(metadata.Attributes);
        if (metadata.CreationTime is { } creation) builder.WithCreationTime(creation);
        if (metadata.LastAccessTime is { } access) builder.WithLastAccessTime(access);
        if (metadata.LastWriteTime is { } write) builder.WithLastWriteTime(write);
        if (metadata.ChangeTime is { } change) builder.WithChangeTime(change);
        return builder.Build();
    }

    private sealed record SnapshotEntry(
        string RemoteId,
        string RemoteRevision,
        CloudItemKind ItemKind,
        string RelativePath,
        long? Length,
        CloudPlaceholderMetadata Metadata)
    {
        public MirrorPulseRemoteSnapshotIdentity? PublicIdentity { get; init; }
    }
}
