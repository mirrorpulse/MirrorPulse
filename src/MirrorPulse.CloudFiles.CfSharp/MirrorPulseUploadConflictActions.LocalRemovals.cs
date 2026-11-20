using System.Security.Cryptography;
using System.Text;
using CfSharp;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal enum MirrorPulseConflictLocalRemovalCheckpoint { BeforeOriginalsAcknowledged, AfterOriginalsAcknowledged }

public sealed partial class MirrorPulseUploadConflictActions
{
    internal Func<MirrorPulseConflictLocalRemovalCheckpoint, CancellationToken, ValueTask>? LocalRemovalCheckpoint { get; init; }
    internal async ValueTask<MirrorPulseConflictResolution> ResumeConflictLocalRemovalAsync(Guid commandId, CancellationToken token)
    {
        var decision = await _catalog.ReadConflictLocalRemovalAsync(commandId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original local-only choice is missing.");
        var root = _router!.GetRegistration(decision.Intent.InstanceId, decision.Intent.RootKey);
        await using var admission = _namespaceWork is null ? null : await _namespaceWork.EnterPendingOperationAsync(root.RootId, token).ConfigureAwait(false);
        return _scheduler is null ? await ExecuteLocalRemovalAdmittedAsync(commandId, root.RootId, token).ConfigureAwait(false) :
            await _scheduler.RunAsync(decision.Intent.InstanceId, stop => ExecuteLocalRemovalAdmittedAsync(commandId, root.RootId, stop), token).ConfigureAwait(false);
    }

    private async ValueTask<MirrorPulseConflictResolution> ExecuteLocalRemovalAdmittedAsync(Guid commandId, RootId? admittedRoot, CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw new PlatformNotSupportedException();
        RequireLocalRemovalService();
        var decision = await _catalog.ReadConflictLocalRemovalAsync(commandId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The retained local-only choice is missing.");
        var root = _router!.GetRegistration(decision.Intent.InstanceId, decision.Intent.RootKey);
        if (root.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled) || admittedRoot != root.RootId)
            throw new InvalidOperationException("A local-only choice requires its explicitly admitted managed root.");
        var completed = await new MirrorPulseConflictLocalRemovalExecutor(_catalog).ExecuteAsync(commandId,
            (removal, stop) =>
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw new PlatformNotSupportedException();
                return new(_localDeletion!.CompleteLocalAsync(removal,
                    (snapshot, current) => ArmLocalRemovalEchoAsync(removal.Intent, snapshot, current), stop));
            },
            async (intent, stop) =>
            {
                if (LocalRemovalCheckpoint is not null)
                    await LocalRemovalCheckpoint(MirrorPulseConflictLocalRemovalCheckpoint.BeforeOriginalsAcknowledged, stop).ConfigureAwait(false);
                await AcknowledgeRemovedOriginalsAsync(intent.ItemId, intent.OriginalOperationIds, stop).ConfigureAwait(false);
                if (LocalRemovalCheckpoint is not null)
                    await LocalRemovalCheckpoint(MirrorPulseConflictLocalRemovalCheckpoint.AfterOriginalsAcknowledged, stop).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        await foreach (var conflict in ReadOriginalConflictEpochsAsync(completed.Intent.InstanceId, completed.Intent.OriginalOperationIds, token).ConfigureAwait(false))
            if (!conflict.IsPending) _conflictCenter?.Remove(conflict.ConflictId);
        return new(commandId, completed.Intent.ConflictId, MirrorPulseConflictAction.DeleteLocal,
            await _localDeletion!.ReadPreservedPathAsync(completed.Intent, token).ConfigureAwait(false), DateTimeOffset.UtcNow);
    }

    private async ValueTask ArmLocalRemovalEchoAsync(MirrorPulseConflictLocalRemovalIntent intent, CloudItemSnapshot snapshot, CancellationToken token)
    {
        if (!snapshot.Exists || snapshot.ItemId != intent.ItemId || snapshot.LocalBinding is not { } binding || intent.LocalContent is null ||
            new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) != intent.LocalContent.UploadBinding.LocalObject)
            throw new InvalidDataException("Local-only echo suppression requires the same original native object immediately before deletion.");
        Guid id = new(SHA256.HashData(Encoding.UTF8.GetBytes("MirrorPulse.ConflictLocalRemoval.Echo.v1/" + intent.CommandId.ToString("D"))).AsSpan(0, 16));
        string path = _fileSystem!.GetFile(intent.LocalRelativePath).RelativePath;
        await using var transaction = await _state.OpenStore.BeginTransactionAsync(token).ConfigureAwait(false);
        // The public repository owns persistence. Our marker is application-owned;
        // the stable ID prevents duplicate windows across pre-native retries.
        await transaction.EchoSuppressions.UpsertAsync(new CloudEchoSuppressionState(id, intent.ItemId,
            CloudStateOperationKind.Delete, path, "MirrorPulse/local-only/v1"u8.ToArray(), DateTimeOffset.UtcNow.AddMinutes(2)), token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }

    private void RequireLocalRemovalService()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) || _localDeletion is null || _router is null || _fileSystem is null)
            throw new NotSupportedException("Local-only conflict choices require the protected current-user Host.");
    }
}
