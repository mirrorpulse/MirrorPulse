using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseJournalCoalescingCheckpoint { AfterEffectsCompleted, AfterOriginalsAcknowledged }

public sealed partial class MirrorPulseJournalUploadPump
{
    private MirrorPulseJournalUploadBatch? _planningBatch;
    private readonly HashSet<Guid> _completedCoalescedOriginals = [];
    internal Func<MirrorPulseJournalCoalescingCheckpoint, CancellationToken, ValueTask>? CoalescingCheckpoint { get; init; }

    private async ValueTask<bool?> TryDispatchCoalescedAsync(MirrorPulseWorkerChangeCommand command, CancellationToken token)
    {
        var plan = await _catalog.ReadJournalCoalescingOwnerAsync(command.OperationId, token).ConfigureAwait(false);
        if (plan is null && (_mutations is null || _fileSystem is null || command.ItemId is null ||
            _planningBatch?.CompleteWindow is not { Count: > 1 } || _planningBatch.WindowUpperSequence <= 0)) return null;
        try
        {
            if (plan is null)
            {
                var window = _planningBatch!.CompleteWindow!;
                var committed = new HashSet<Guid>();
                foreach (var observation in window.Where(observation => observation.ItemId == command.ItemId))
                    if (await _catalog.ReadMutationAsync(observation.OperationId, token).ConfigureAwait(false) is not null ||
                        await _catalog.ReadJournalCoalescingOwnerAsync(observation.OperationId, token).ConfigureAwait(false) is not null)
                        committed.Add(observation.OperationId);
                plan = MirrorPulseJournalCoalescingPlanner.TryPlan(window, command.ItemId!.Value,
                    _planningBatch.WindowUpperSequence, committed);
                if (plan is null || plan.Members[0].OperationId != command.OperationId) return null;
                var ready = _planningBatch.ReadyCommands.Select(candidate => candidate.OperationId).ToHashSet();
                if (plan.Members.Any(member => !ready.Contains(member.OperationId))) return null;
                string localPath = _router.ResolveUploadPath(plan.InstanceId, plan.RootKey, plan.FinalPath);
                string relative = Path.GetRelativePath(_syncRootPath, localPath).Replace(Path.DirectorySeparatorChar, '/');
                string? baseline = await MirrorPulseJournalUploadRevisionGuard.ResolveAsync(_state.OpenStore, _stats, command,
                    relative, remoteStatPath: plan.OriginalPath,
                    allowTombstone: plan.Effect is MirrorPulseCoalescedEffect.DeleteFile or MirrorPulseCoalescedEffect.VerifyRemoteAbsence,
                    cancellationToken: token).ConfigureAwait(false);
                if (plan.Effect is MirrorPulseCoalescedEffect.CreateFile or MirrorPulseCoalescedEffect.UpdateFile &&
                    MirrorPulseJournalContentPolicy.IsAcceptedObservation(await _fileSystem!.GetFile(relative).InspectAsync(token).ConfigureAwait(false),
                        _router.GetRegistration(plan.InstanceId, plan.RootKey), baseline))
                {
                    // Retain the existing observation-only policy for already
                    // accepted content. A notification chain must not turn a
                    // repeated hydration/read into another remote upload.
                    await _feed.AcknowledgeAsync(plan.Members.Select(member =>
                        new CloudLocalChangeAcknowledgement(member.OperationId, baseline)), token).ConfigureAwait(false);
                    foreach (var member in plan.Members) _completedCoalescedOriginals.Add(member.OperationId);
                    return true;
                }
                if (plan.Effect is MirrorPulseCoalescedEffect.CreateFile or MirrorPulseCoalescedEffect.UpdateFile or MirrorPulseCoalescedEffect.MoveAndUpdateFile)
                {
                    await using var content = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        64 * 1024, FileOptions.Asynchronous);
                    var file = _fileSystem!.GetFile(relative);
                    var binding = await _contentConfirmation.CaptureUploadBindingAsync(file,
                        await file.InspectAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
                    var final = new MirrorPulseCoalescingContent(content.Length,
                        Convert.ToHexString(await SHA256.HashDataAsync(content, token).ConfigureAwait(false)), binding);
                    _ = await _catalog.PrepareJournalCoalescingAndExecutionAsync(plan, window, baseline, final, token).ConfigureAwait(false);
                }
                else
                    _ = await _catalog.PrepareJournalCoalescingAndExecutionAsync(plan, window, baseline, cancellationToken: token).ConfigureAwait(false);
            }
            RequireOriginalObservation(plan, command);
            if (_mutations is null || _fileSystem is null)
                throw new NotSupportedException("The retained coalesced execution requires the production mutation and Cloud Files boundaries.");
            var execution = await _catalog.ReadJournalCoalescingExecutionAsync(plan.PlanId, token).ConfigureAwait(false)
                ?? throw new MirrorPulseMutationAmbiguousException("The owned journal group has no immutable execution preparation.");
            var executor = new MirrorPulseJournalCoalescingExecutor(_catalog, _stats, _uploads, _mutations, _readback);
            var results = await executor.ExecuteAsync(plan.PlanId,
                (content, stop) => OpenCoalescedContentAsync(plan, content, stop),
                (accepted, stop) => AcknowledgeAsync(accepted.Intent.OperationId, accepted.AcceptedRevision, stop,
                    acknowledgeJournal: false), token).ConfigureAwait(false);
            if (results.Count != execution.Steps.Count || results.Any(result => result.State != MirrorPulseMutationState.Acknowledged))
                throw new MirrorPulseMutationAmbiguousException("Every owned effect must complete before the original journal group is acknowledged.");
            string? revision = plan.Effect is MirrorPulseCoalescedEffect.DeleteFile or MirrorPulseCoalescedEffect.VerifyRemoteAbsence
                ? null : results[^1].AcceptedRevision;
            if (CoalescingCheckpoint is not null)
                await CoalescingCheckpoint(MirrorPulseJournalCoalescingCheckpoint.AfterEffectsCompleted, token).ConfigureAwait(false);
            // One official transaction settles only the immutable original IDs. A
            // later notification for the same file remains independently pending.
            await _feed.AcknowledgeAsync(plan.Members.Select(member =>
                new CloudLocalChangeAcknowledgement(member.OperationId, revision)), token).ConfigureAwait(false);
            await _catalog.CompleteJournalCoalescingRetryCommandsAsync(plan.PlanId, token).ConfigureAwait(false);
            foreach (var member in plan.Members)
            {
                _completedCoalescedOriginals.Add(member.OperationId);
                _runner.ClearRecoveredFault(member.OperationId);
            }
            if (CoalescingCheckpoint is not null)
                await CoalescingCheckpoint(MirrorPulseJournalCoalescingCheckpoint.AfterOriginalsAcknowledged, token).ConfigureAwait(false);
            return true;
        }
        catch (MirrorPulseUploadConflictException conflict)
        {
            return await DeferCoalescedConflictAsync(command, plan, conflict.ExpectedRevision, conflict.ActualRevision, token).ConfigureAwait(false);
        }
        catch (MirrorPulseWorkerMutationConflictException conflict)
        {
            return await DeferCoalescedConflictAsync(command, plan, conflict.ExpectedRevision, conflict.ActualRevision, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await LogDispatchBoundaryAsync(command, "CoalescedExecution", error, token).ConfigureAwait(false);
            if (error is not MirrorPulseJournalAcknowledgementException)
                await _completion.DeferFailedUploadAsync(command.OperationId, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
            throw;
        }
    }

    private static void RequireOriginalObservation(MirrorPulseJournalCoalescingPlan plan, MirrorPulseWorkerChangeCommand command)
    {
        var member = plan.Members.SingleOrDefault(member => member.OperationId == command.OperationId);
        if (member is null || plan.InstanceId != command.InstanceId || plan.RootKey != command.RootKey ||
            command.ItemId is { } item && item != plan.ItemId || command.Sequence != member.Sequence ||
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command with { ItemId = plan.ItemId }))) != member.Fingerprint)
            throw new InvalidDataException("A retained journal group cannot adopt another original observation.");
    }

    private async ValueTask<Stream> OpenCoalescedContentAsync(MirrorPulseJournalCoalescingPlan plan,
        MirrorPulseCoalescingContent expected, CancellationToken token)
    {
        string path = _router.ResolveUploadPath(plan.InstanceId, plan.RootKey, plan.FinalPath);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        try
        {
            string relative = Path.GetRelativePath(_syncRootPath, path).Replace(Path.DirectorySeparatorChar, '/');
            var file = _fileSystem!.GetFile(relative);
            var binding = await _contentConfirmation.CaptureUploadBindingAsync(file,
                await file.InspectAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
            if (binding != expected.UploadBinding || stream.Length != expected.Length ||
                !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)), expected.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new MirrorPulseMutationAmbiguousException("The final content no longer matches the retained upload-time object and bytes.");
            stream.Position = 0; return stream;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private async ValueTask<bool> DeferCoalescedConflictAsync(MirrorPulseWorkerChangeCommand command,
        MirrorPulseJournalCoalescingPlan? plan, string? expected, string? actual, CancellationToken token)
    {
        if (plan is null || await _catalog.ReadJournalCoalescingOwnerAsync(command.OperationId, token).ConfigureAwait(false) is null)
        {
            string path = _router.ResolveUploadPath(command.InstanceId, command.RootKey, plan?.FinalPath ?? command.RelativePath);
            return await DeferConflictAsync(command, Path.GetRelativePath(_syncRootPath, path).Replace(Path.DirectorySeparatorChar, '/'), expected, actual, token).ConfigureAwait(false);
        }
        var execution = await _catalog.ReadJournalCoalescingExecutionAsync(plan.PlanId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The rejected group lost its execution.");
        var rejected = new List<MirrorPulseMutationRecord>();
        for (int ordinal = 0; ordinal < execution.Steps.Count; ordinal++)
        {
            var record = await _catalog.PrepareJournalCoalescingMutationAsync(plan.PlanId, ordinal, token).ConfigureAwait(false);
            if (record.State == MirrorPulseMutationState.Conflict) { rejected.Add(record); break; }
            if (record.State != MirrorPulseMutationState.Acknowledged) break;
        }
        if (rejected.Count != 1) throw new InvalidDataException("A rejected group requires the actual current effect conflict.");
        foreach (var member in plan.Members)
        {
            string path = _router.ResolveUploadPath(plan.InstanceId, plan.RootKey, plan.FinalPath);
            byte[] epoch = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("MirrorPulse.Coalescing.Conflict.v1/" +
                rejected[0].Intent.OperationId.ToString("D") + "/" + member.OperationId.ToString("D")));
            var conflict = new MirrorPulseConflictRecord(new Guid(epoch.AsSpan(0, 16)), plan.InstanceId, member.OperationId.ToString("D"),
                Path.GetRelativePath(_syncRootPath, path).Replace(Path.DirectorySeparatorChar, '/'),
                MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged, expected, actual, DateTimeOffset.UtcNow);
            await _catalog.SaveUploadConflictAsync(conflict, token).ConfigureAwait(false);
            _conflicts?.Upsert(conflict);
            if (_notifications is not null)
            {
                try { await _notifications.NotifyAsync(conflict, token).ConfigureAwait(false); }
                catch (Exception notificationFailure) when (notificationFailure is not OperationCanceledException) { }
            }
            await _completion.DeferFailedUploadAsync(member.OperationId, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
        }
        return false;
    }

    private async ValueTask RecoverCoalescingUserCommandsAsync(CancellationToken token)
    {
        Guid? after = null;
        while (true)
        {
            var groups = await _catalog.ReadPendingJournalCoalescingRetryGroupsAsync(after, token).ConfigureAwait(false);
            if (groups.Count == 0) return;
            foreach (Guid id in groups)
            {
                var plan = await _catalog.ReadJournalCoalescingPlanAsync(id, token).ConfigureAwait(false);
                bool pending = false;
                await using (var transaction = await _state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false))
                {
                    foreach (var member in plan.Members)
                        if (await transaction.Operations.GetAsync(member.OperationId, token).ConfigureAwait(false) is not null)
                        { pending = true; break; }
                    await transaction.RollbackAsync(token).ConfigureAwait(false);
                }
                if (!pending) await _catalog.CompleteJournalCoalescingRetryCommandsAsync(id, token).ConfigureAwait(false);
            }
            after = groups[^1];
        }
    }
}
