using System.Runtime.Versioning;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseJournalUploadPump
{
    private readonly MirrorPulseJournalDirectoryMoveAdmissionReader? _directoryMoveAdmission;
    private readonly MirrorPulseJournalDirectoryMoveRecovery? _directoryMoveRecovery;
    private Guid? _afterJournalDirectoryMove;
    private RootId? _afterDirectoryMoveReadmission;
    private readonly Dictionary<RootId, Guid> _directoryMoveReadmission = [];
    private Func<RootRegistration, Guid, MirrorPulseJournalDirectoryMoveRecovery, CancellationToken, ValueTask<bool>>? _resumeClosedDirectoryMove;

    [SupportedOSPlatform("windows10.0.26100")]
    internal void UseClosedDirectoryMoveRecovery(
        Func<RootRegistration, Guid, MirrorPulseJournalDirectoryMoveRecovery, CancellationToken, ValueTask<bool>> recovery) =>
        _resumeClosedDirectoryMove = recovery;

    [SupportedOSPlatform("windows10.0.26100")]
    private async ValueTask<bool?> TryDispatchJournalDirectoryMoveAsync(MirrorPulseWorkerChangeCommand command, CancellationToken token)
    {
        if (command.Kind != MirrorPulseWorkerChangeKind.Move || !command.IsDirectory || _directoryMoveAdmission is null ||
            _directoryMoveRecovery is null) return null;
        var originalOwner = await _catalog.ReadJournalDirectoryMoveAsync(command.OperationId, token).ConfigureAwait(false);
        if (originalOwner is not null) return originalOwner.Mutation.State == MirrorPulseMutationState.Acknowledged;
        var retained = await _catalog.ReadMutationAsync(command.OperationId, token).ConfigureAwait(false);
        if (retained is { State: not MirrorPulseMutationState.Prepared }) return null;
        if (retained is { ExecutionEvidence: not MirrorPulseMutationExecutionEvidence.NeverStarted }) return null;
        var admission = await _directoryMoveAdmission.ReadAsync(command, token).ConfigureAwait(false);
        if (admission is null) return null;
        if (_mutations?.SupportsDurableDirectoryMove(command.InstanceId) != true || string.IsNullOrEmpty(admission.Root.OriginalRevision))
        {
            await _catalog.SaveBlockedLocalOperationAsync(new(command.OperationId, command.InstanceId, command.RelativePath,
                MirrorPulseLocalOperationBlockReason.UnsupportedDurableDirectoryMove, command.ObservedAt), token).ConfigureAwait(false);
            return false;
        }
        var intent = Intent(command, admission.Root.OriginalRevision);
        if (retained is not null && retained.Intent != intent) return null;
        await _catalog.StartJournalDirectoryMoveAsync(admission, intent, token).ConfigureAwait(false);
        bool done = await _directoryMoveRecovery.ResumeAsync(command.OperationId, token).ConfigureAwait(false);
        if (done) _runner.ClearRecoveredFault(command.OperationId);
        return done;
    }

    [SupportedOSPlatform("windows10.0.26100")]
    private async ValueTask<bool> RecoverJournalDirectoryMovesAsync(CancellationToken token)
    {
        if (_directoryMoveRecovery is null || _namespaceWork is null) return false;
        const int pageSize = 4;
        var pending = await _catalog.ReadPendingJournalDirectoryMovesAsync(_afterJournalDirectoryMove, pageSize, token).ConfigureAwait(false);
        var retryPage = _directoryMoveReadmission
            .Where(entry => _afterDirectoryMoveReadmission is null || entry.Key.Value.CompareTo(_afterDirectoryMoveReadmission.Value.Value) > 0)
            .OrderBy(entry => entry.Key.Value).Take(pageSize).ToArray();
        _afterDirectoryMoveReadmission = retryPage.Length == pageSize ? retryPage[^1].Key : null;
        var retries = retryPage.Select(entry => new MirrorPulseNamespacePendingOperation(entry.Value, entry.Key, MirrorPulseNamespacePendingOperationKind.JournalDirectoryMove));
        var selected = pending.Concat(retries).Distinct().ToArray();
        foreach (var operation in selected)
        {
            var root = _router.FindRegistration(operation.RootId);
            if (root is not { State: RootRegistrationState.Active })
            {
                _directoryMoveReadmission.Remove(operation.RootId);
                continue;
            }
            try
            {
                bool completed;
                if (!_directoryMoveReadmission.ContainsKey(root.RootId) && _namespaceWork.TryEnter([root.RootId], out var admission))
                {
                    using (admission)
                        completed = await _scheduler.RunAsync(root.InstanceId,
                            stop => _directoryMoveRecovery.ResumeAsync(operation.OperationId, stop,
                                mayRequestSource: _mayDispatch(root.InstanceId)), token).ConfigureAwait(false);
                }
                else
                {
                    if (_resumeClosedDirectoryMove is null) continue;
                    _directoryMoveReadmission[root.RootId] = operation.OperationId;
                    completed = await _resumeClosedDirectoryMove(root, operation.OperationId, _directoryMoveRecovery, token).ConfigureAwait(false);
                }
                if (completed)
                {
                    _directoryMoveReadmission.Remove(root.RootId);
                    _runner.ClearRecoveredFault(operation.OperationId);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _runner.RetainDirectoryMoveRecoveryFault(operation.OperationId);
                await ReportFailureAsync(null, "JournalDirectoryMoveRecoveryFailed", error, token).ConfigureAwait(false);
            }
        }
        _afterJournalDirectoryMove = pending.Count == pageSize ? pending[^1].OperationId : null;
        return selected.Length != 0;
    }
}
