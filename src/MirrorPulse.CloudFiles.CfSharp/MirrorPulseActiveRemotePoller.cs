using System.Collections.Concurrent;
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
    private readonly Func<InstanceId, CloudRemoteChangeBatch, MirrorPulseNamespaceWorkAdmission.WorkLease, CancellationToken,
        ValueTask<MirrorPulseRemotePollApplyOutcome>>? _applyAdmitted;
    private readonly TimeSpan _interval;
    private readonly int _pageSize;
    private readonly int _maximumPages;
    private readonly IMirrorPulseRemotePollSnapshotStore? _snapshotStore;
    private readonly IMirrorPulseRemotePollPendingStore? _pendingStore;
    private readonly Func<RootRegistration, CancellationToken, ValueTask<bool>>? _mayPoll;
    private readonly ConcurrentDictionary<(InstanceId, RootId, string), SnapshotBaseline> _snapshots = new();
    private readonly ConcurrentDictionary<InstanceId, RootId> _lastRoot = new(); // Different instance schedules can run concurrently.
    private readonly MirrorPulseInstanceScheduler _scheduler;
    private readonly bool _ownsScheduler;
    private readonly MirrorPulseNamespaceWorkAdmission? _namespaceWork;
    private readonly Func<RootRegistration, CloudRemoteChangeBatch, CancellationToken, ValueTask<bool>>? _mayReplay;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;
    private readonly LocalRollingLogWriter? _failureLog;
    private readonly MirrorPulseRemoteIdentityResolver? _identityResolver;
    private readonly Func<RootRegistration, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>>? _readPublishedCursor;

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
        MirrorPulseRemoteIdentityResolver? identityResolver = null,
        Func<InstanceId, CloudRemoteChangeBatch, MirrorPulseNamespaceWorkAdmission.WorkLease, CancellationToken,
            ValueTask<MirrorPulseRemotePollApplyOutcome>>? applyAdmitted = null,
        Func<RootRegistration, CancellationToken, ValueTask<ReadOnlyMemory<byte>?>>? readPublishedCursor = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(roots);
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        if (applyAdmitted is not null && namespaceWork is null)
            throw new ArgumentException("Admitted polling requires the Host root-work owner.", nameof(namespaceWork));
        _applyAdmitted = applyAdmitted;
        if (pageSize is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(pageSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPages, 1);
        if (interval is { } value && value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));

        _instances = instances.Where(instance => instance.Enabled)
            .Select(instance => instance.InstanceId).Distinct().ToArray();
        _wakeSignals = _instances.ToDictionary(instance => instance, _ => new PollWakeSignal());
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
        if (readPublishedCursor is not null && (snapshotStore is not IMirrorPulseRootRemotePollBaselineStore ||
            pendingStore is not IMirrorPulseRootRemotePollPendingStore))
            throw new ArgumentException("Published cursor validation requires durable root baseline and replay stores.", nameof(readPublishedCursor));
        _readPublishedCursor = readPublishedCursor;
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
    public ValueTask<bool> PollOnceAsync(InstanceId instanceId, CancellationToken cancellationToken = default) =>
        RunTurnAsync(instanceId, PollTrigger.Forced, cancellationToken);

    public async ValueTask<bool> RefreshAsync(InstanceId instanceId, bool force, CancellationToken cancellationToken = default)
    {
        try { return await RunTurnAsync(instanceId, force ? PollTrigger.Forced : PollTrigger.Manual, cancellationToken).ConfigureAwait(false); }
        finally
        {
            if (_wakeSignals.TryGetValue(instanceId, out var wake)) wake.Notify(reconnected: false);
        }
    }

    private async ValueTask<bool> RunTurnAsync(InstanceId instanceId, PollTrigger trigger, CancellationToken cancellationToken)
    {
        var stage = MirrorPulseRemotePollStage.RootSelection;
        try
        {
            return await _scheduler.RunAsync(instanceId, token => PollCoreAsync(instanceId, trigger,
                value => stage = value, token), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            if (_failureLog is not null)
            {
                try
                {
                    await _failureLog.WriteAsync(new(LogLevel.Warning, "sync", "RemotePollFailed", DateTimeOffset.UtcNow,
                        [new("instanceId", instanceId.ToString()), new("hresult", error.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)),
                            new("failureCategory", SafeDiagnosticPolicy.ClassifyFailure(error)),
                            new("remotePollStage", stage.ToString()),
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

    private async ValueTask<bool> PollCoreAsync(InstanceId instanceId, PollTrigger trigger,
        Action<MirrorPulseRemotePollStage> reportStage, CancellationToken cancellationToken)
    {
        if (!_instances.Contains(instanceId)) return false;
        IReadOnlyList<RootRegistration>? configured = _currentRoots is null
            ? _roots.GetValueOrDefault(instanceId)
            : _currentRoots().Where(root => root.InstanceId == instanceId).ToArray();
        var now = DateTimeOffset.UtcNow;
        var roots = configured?.Where(root => root.State == RootRegistrationState.Active && CanAttempt(root.RootId, trigger, now))
            .OrderBy(root => root.RootId.ToString(), StringComparer.Ordinal).ToArray() ?? [];
        if (roots.Length == 0) return false;
        bool scoped = (_snapshotStore is null or IMirrorPulseRootRemotePollSnapshotStore) &&
            (_pendingStore is null or IMirrorPulseRootRemotePollPendingStore);
        var root = _lastRoot.TryGetValue(instanceId, out var last)
            ? roots.FirstOrDefault(candidate => string.CompareOrdinal(candidate.RootId.ToString(), last.ToString()) > 0) ?? roots[0]
            : roots[0];
        _lastRoot[instanceId] = root.RootId;
        var stage = MirrorPulseRemotePollStage.RootSelection;
        try
        {
            if (!scoped && configured!.Count != 1)
                throw new NotSupportedException("Multiple roots require root-scoped polling stores.");
            if (!scoped && _identityResolver is not null)
                throw new NotSupportedException("Generation selection requires root-scoped polling stores.");
            if (configured!.Count > 1 && configured.Any(value => value.IdentityScope != RootIdentityScope.InstanceRoot))
                throw new InvalidDataException("Several roots require independent logical item identity scopes.");
            bool applied = await PollRootAsync(root, configured.Count, scoped, value =>
            {
                stage = value;
                reportStage(value);
            }, cancellationToken).ConfigureAwait(false);
            RememberAttempt(root.RootId, stage, failure: null);
            return applied;
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            RememberAttempt(root.RootId, stage, PollFailureReason(error));
            throw;
        }
    }

    private async ValueTask<bool> PollRootAsync(RootRegistration root, int configuredRootCount, bool scoped,
        Action<MirrorPulseRemotePollStage> reportStage, CancellationToken cancellationToken)
    {
        InstanceId instanceId = root.InstanceId;
        var key = (instanceId, root.RootId, root.UniquenessKey);
        reportStage(MirrorPulseRemotePollStage.RetainedIntent);
        var legacy = _pendingStore is null ? null : await _pendingStore.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false);
        var legacySnapshot = scoped && _snapshotStore is not null
            ? await _snapshotStore.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false) : null;
        if (scoped && configuredRootCount != 1 && (legacy is not null || legacySnapshot is not null))
            throw new InvalidDataException("An unscoped legacy poll cannot be attributed to several roots; retain the original mapping first.");
        MirrorPulsePendingRemotePoll? pending = legacy ?? (scoped && _pendingStore is IMirrorPulseRootRemotePollPendingStore rootPending
            ? await rootPending.LoadAsync(root, cancellationToken).ConfigureAwait(false) : null);
        if (pending is not null)
        {
            bool chained = pending.ProjectionVersion == 5;
            if (chained && _snapshotStore is not IMirrorPulseRootRemotePollBaselineStore)
                throw new NotSupportedException("A chained poll requires a durable baseline cursor store.");
            var (replay, candidate) = RestoreOriginalCore(instanceId, root, pending);
            bool allowed = _mayReplay is not null
                ? await _mayReplay(root, replay, cancellationToken).ConfigureAwait(false)
                : _mayPoll is null || await _mayPoll(root, cancellationToken).ConfigureAwait(false);
            if (!allowed) return false;
            MirrorPulseNamespaceWorkAdmission.WorkLease? replayAdmission = null;
            if (_namespaceWork is not null && !_namespaceWork.TryEnter([root.RootId], out replayAdmission)) return false;
            using var retainedReplayAdmission = replayAdmission;
            // Do not enumerate a newer remote tree until this exact batch has converged.
            return await ApplyAndCommitAsync(root, replay, candidate, scoped, legacy is not null, chained,
                replayAdmission, reportStage, cancellationToken).ConfigureAwait(false);
        }

        if (_mayPoll is not null && !await _mayPoll(root, cancellationToken).ConfigureAwait(false)) return false;
        MirrorPulseNamespaceWorkAdmission.WorkLease? admission = null;
        if (_namespaceWork is not null && !_namespaceWork.TryEnter([root.RootId], out admission)) return false;
        using var retainedAdmission = admission;

        reportStage(MirrorPulseRemotePollStage.SnapshotRead);
        bool cached = _snapshots.TryGetValue(key, out SnapshotBaseline? baseline);
        ReadOnlyMemory<byte>? publishedCursor = null;
        if (!cached)
        {
            if (_readPublishedCursor is not null)
            {
                publishedCursor = await _readPublishedCursor(root, cancellationToken).ConfigureAwait(false);
                if (publishedCursor is { Length: not 32 }) throw new MirrorPulsePollCursorMismatchException();
            }
            var persistedBaseline = await LoadRootBaselineAsync(root, scoped, cancellationToken).ConfigureAwait(false);
            IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry>? persisted =
                persistedBaseline?.Entries ?? (_snapshotStore switch
                {
                    null => null,
                    IMirrorPulseRootRemotePollBaselineStore when scoped => legacySnapshot,
                    IMirrorPulseRootRemotePollSnapshotStore store when scoped =>
                        await store.LoadAsync(root, cancellationToken).ConfigureAwait(false) ?? legacySnapshot,
                    _ => await _snapshotStore.LoadAsync(instanceId, cancellationToken).ConfigureAwait(false),
                });
            if (persisted is not null)
            {
                baseline = new(FromDurableSnapshot(persisted), persistedBaseline?.Cursor ?? ReadOnlyMemory<byte>.Empty);
                if (_readPublishedCursor is not null) ValidatePublishedCursor(root, baseline, publishedCursor);
                _snapshots[key] = baseline;
            }
        }
        var previous = baseline?.Entries;

        reportStage(MirrorPulseRemotePollStage.WorkerEnumeration);
        IReadOnlyDictionary<string, SnapshotEntry> current = await ReadVerifiedSnapshotAsync(
            root, previous, cancellationToken).ConfigureAwait(false);
        reportStage(MirrorPulseRemotePollStage.IdentitySelection);
        if (_identityResolver is not null)
        {
            var selected = await CaptureIdentitiesAsync(root, current, cancellationToken).ConfigureAwait(false);
            if (selected is null) return false;
            current = selected;
        }
        else if (previous?.Values.Any(entry => entry.PublicIdentity is not null) == true)
            throw new InvalidOperationException("A captured generation snapshot requires its original identity resolver.");
        reportStage(MirrorPulseRemotePollStage.Projection);
        if (previous is null)
        {
            reportStage(MirrorPulseRemotePollStage.SnapshotCommit);
            // A disk rebuild starts with the already published cursor and compares no missing history.
            var first = new SnapshotBaseline(current, publishedCursor ?? ReadOnlyMemory<byte>.Empty);
            await SaveSnapshotAsync(root, first, scoped, cancellationToken).ConfigureAwait(false);
            _snapshots[key] = first;
            return false;
        }

        if (SnapshotsEqual(root, previous!, current))
        {
            reportStage(MirrorPulseRemotePollStage.SnapshotCommit);
            if (!cached || legacySnapshot is not null || !ObservationsEqual(previous, current))
            {
                var refreshed = new SnapshotBaseline(current, baseline!.Cursor);
                await SaveSnapshotAsync(root, refreshed, scoped, cancellationToken).ConfigureAwait(false);
                _snapshots[key] = refreshed;
            }
            return false;
        }

        bool useChain = scoped && _snapshotStore is IMirrorPulseRootRemotePollBaselineStore;
        int projectionVersion = useChain ? 5 : _identityResolver is not null ? 4 : scoped ? 3 : 2;
        ReadOnlyMemory<byte> initial = useChain && baseline!.Cursor.IsEmpty ? RootFingerprint(root, previous, 4) : baseline!.Cursor;
        CloudRemoteChangeBatch batch = CreateBatch(instanceId, root, previous, current, projectionVersion, initial);
        if (_pendingStore is not null)
        {
            reportStage(MirrorPulseRemotePollStage.RetainIntent);
            var captured = new MirrorPulsePendingRemotePoll(batch.BatchId, batch.Fingerprint.ToArray(), root.DirectoryName,
                ToDurableSnapshot(previous), ToDurableSnapshot(current), projectionVersion)
            { RootId = scoped ? root.RootId : null, RootKey = scoped ? root.UniquenessKey : null };
            if (scoped) await ((IMirrorPulseRootRemotePollPendingStore)_pendingStore).SaveAsync(root, captured, cancellationToken).ConfigureAwait(false);
            else await _pendingStore.SaveAsync(instanceId, captured, cancellationToken).ConfigureAwait(false);
        }
        return await ApplyAndCommitAsync(root, batch, current, scoped, legacy: false, useChain, admission, reportStage, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<MirrorPulseRemotePollBaseline?> LoadRootBaselineAsync(RootRegistration root,
        bool scoped, CancellationToken token)
    {
        if (!scoped || _snapshotStore is not IMirrorPulseRootRemotePollBaselineStore store) return null;
        try { return await store.LoadBaselineAsync(root, token).ConfigureAwait(false); }
        catch (MirrorPulsePollBaselineCorruptException) when (_readPublishedCursor is not null &&
            store is MirrorPulseFileRemotePollSnapshotStore)
        {
            await ((MirrorPulseFileRemotePollSnapshotStore)store).PreserveCorruptBaselineAsync(root, token).ConfigureAwait(false);
            return null;
        }
    }

    private static void ValidatePublishedCursor(RootRegistration root, SnapshotBaseline baseline, ReadOnlyMemory<byte>? published)
    {
        if (published is null)
        {
            if (!baseline.Cursor.IsEmpty) throw new MirrorPulsePollCursorMismatchException();
            return;
        }
        ReadOnlyMemory<byte> expected = baseline.Cursor.IsEmpty ? RootFingerprint(root, baseline.Entries, 4) : baseline.Cursor;
        if (!expected.Span.SequenceEqual(published.Value.Span)) throw new MirrorPulsePollCursorMismatchException();
    }

    private async ValueTask<bool> ApplyAndCommitAsync(RootRegistration root, CloudRemoteChangeBatch batch,
        IReadOnlyDictionary<string, SnapshotEntry> current, bool scoped, bool legacy, bool chained,
        MirrorPulseNamespaceWorkAdmission.WorkLease? admission,
        Action<MirrorPulseRemotePollStage> reportStage, CancellationToken cancellationToken)
    {
        reportStage(MirrorPulseRemotePollStage.ApplyAndCommit);
        MirrorPulseRemotePollApplyOutcome outcome = _applyAdmitted is null
            ? await _apply(root.InstanceId, batch, cancellationToken).ConfigureAwait(false)
            : await _applyAdmitted(root.InstanceId, batch, admission ??
                throw new InvalidOperationException("The current poll lost its root-work admission."), cancellationToken).ConfigureAwait(false);
        if (!outcome.Completed)
        {
            if (outcome.MadeProgress) reportStage(MirrorPulseRemotePollStage.BatchProgress);
            return false;
        }
        reportStage(MirrorPulseRemotePollStage.CheckpointValidation);
        if (!outcome.SafeCursor.Span.SequenceEqual(batch.FinalCursor.Span))
            throw new InvalidDataException("A completed remote poll batch must have its final safe cursor.");
        reportStage(MirrorPulseRemotePollStage.SnapshotCommit);
        var baseline = new SnapshotBaseline(current, chained ? batch.FinalCursor : ReadOnlyMemory<byte>.Empty);
        await SaveSnapshotAsync(root, baseline, scoped, cancellationToken).ConfigureAwait(false);
        _snapshots[(root.InstanceId, root.RootId, root.UniquenessKey)] = baseline;
        if (_pendingStore is not null)
        {
            reportStage(MirrorPulseRemotePollStage.IntentCompletion);
            if (scoped && !legacy) await ((IMirrorPulseRootRemotePollPendingStore)_pendingStore).ClearAsync(root, batch.BatchId, cancellationToken).ConfigureAwait(false);
            else await _pendingStore.ClearAsync(root.InstanceId, batch.BatchId, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    private async ValueTask SaveSnapshotAsync(
        RootRegistration root,
        SnapshotBaseline baseline,
        bool scoped,
        CancellationToken cancellationToken)
    {
        if (_snapshotStore is null) return;
        if (scoped)
        {
            var store = (IMirrorPulseRootRemotePollSnapshotStore)_snapshotStore;
            var entries = ToDurableSnapshot(baseline.Entries);
            if (store is IMirrorPulseRootRemotePollBaselineStore cursorStore)
                await cursorStore.SaveBaselineAsync(root, new(entries, baseline.Cursor), cancellationToken).ConfigureAwait(false);
            else
                await store.SaveAsync(root, entries, cancellationToken).ConfigureAwait(false);
            await store.ClearLegacyAsync(root.InstanceId, cancellationToken).ConfigureAwait(false);
        }
        else await _snapshotStore.SaveAsync(root.InstanceId, ToDurableSnapshot(baseline.Entries), cancellationToken).ConfigureAwait(false);
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
        await Task.WhenAll(_instances.Select(instance => RunInstanceAsync(instance, linked.Token))).ConfigureAwait(false);
    }

    internal static CloudRemoteChangeBatch RestoreOriginalBatch(InstanceId instance, RootRegistration root, MirrorPulsePendingRemotePoll pending)
        => RestoreOriginalCore(instance, root, pending).Batch;

    private static (CloudRemoteChangeBatch Batch, IReadOnlyDictionary<string, SnapshotEntry> Candidate) RestoreOriginalCore(
        InstanceId instance, RootRegistration root, MirrorPulsePendingRemotePoll pending)
    {
        if (root.InstanceId != instance || root.DirectoryName != pending.RootDirectoryName ||
            pending.RootId is { } originalRoot && originalRoot != root.RootId || pending.RootKey is { } originalKey && originalKey != root.UniquenessKey)
            throw new InvalidDataException("A pending batch belongs to a different original root mapping.");
        var candidate = FromDurableSnapshot(pending.Candidate);
        var batch = CreateBatch(instance, root, FromDurableSnapshot(pending.Previous), candidate, pending.ProjectionVersion,
            pending.ProjectionVersion == 5 ? ReadChainedInitialCursor(root, pending.BatchId) : ReadOnlyMemory<byte>.Empty);
        if (batch.BatchId != pending.BatchId || !batch.Fingerprint.Span.SequenceEqual(pending.Fingerprint))
            throw new InvalidDataException("The pending poll intent does not recreate its immutable batch.");
        return (batch, candidate);
    }

    private static CloudRemoteChangeBatch CreateBatch(
        InstanceId instanceId,
        RootRegistration root,
        IReadOnlyDictionary<string, SnapshotEntry> previous,
        IReadOnlyDictionary<string, SnapshotEntry> current,
        int projectionVersion, ReadOnlyMemory<byte> chainedInitialCursor)
    {
        if (projectionVersion is not (1 or 2 or 3 or 4 or 5))
            throw new InvalidDataException("The remote poll projection version is unsupported.");
        if (projectionVersion == 4) ValidateCapturedSnapshots(root, previous, current);
        byte[] initialCursor = projectionVersion == 5 ? chainedInitialCursor.ToArray() :
            projectionVersion >= 3 ? RootFingerprint(root, previous, projectionVersion) : Fingerprint(previous);
        byte[] finalCursor = projectionVersion == 5 ? SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]
            { "MirrorPulse/PollProjection/v5", Convert.ToHexString(initialCursor), Convert.ToHexString(RootFingerprint(root, current, 4)) })) :
            projectionVersion >= 3 ? RootFingerprint(root, current, projectionVersion) : Fingerprint(current);
        var changes = new List<CloudRemoteChange>();
        foreach ((string id, SnapshotEntry entry) in current.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (!previous.TryGetValue(id, out SnapshotEntry? prior) ||
                projectionVersion >= 4 && !SamePublicIdentity(root, prior, entry))
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
        CloudPlaceholderIdentity identity = projectionVersion >= 4 ? CapturedIdentity(root, entry) :
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
            // The batch model publishes FinalCursor on completion. A partial prefix keeps InitialCursor.
            cursorAfter: projectionVersion == 5 ? ReadOnlyMemory<byte>.Empty : cursor);
    }

    private static byte[] ReadChainedInitialCursor(RootRegistration root, string batchId)
    {
        string prefix = MirrorPulseRemotePollBatchScope.Prefix(root);
        if (!batchId.StartsWith(prefix, StringComparison.Ordinal) || batchId.Length != prefix.Length + 129 || batchId[prefix.Length + 64] != '/')
            throw new InvalidDataException("The retained poll batch lost its initial cursor.");
        try { return Convert.FromHexString(batchId.AsSpan(prefix.Length, 64)); }
        catch (FormatException error) { throw new InvalidDataException("The retained poll cursor is malformed.", error); }
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

    private sealed record SnapshotBaseline(IReadOnlyDictionary<string, SnapshotEntry> Entries, ReadOnlyMemory<byte> Cursor);
}
