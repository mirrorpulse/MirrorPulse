using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseUploadConflictActions
{
    private sealed class KeepRemoteProtectionLostException() : IOException("KeepRemote lost its protected SDK reference and requires fresh admission.");

    internal Func<CancellationToken, ValueTask>? KeepRemotePrepared { get; init; }
    internal Func<CancellationToken, ValueTask>? KeepRemoteFileStarted { get; init; }

    internal static async ValueTask<T> RetryKeepRemoteProtectionLossAsync<T>(Func<ValueTask<T>> executeOnce)
    {
        try { return await executeOnce().ConfigureAwait(false); }
        catch (KeepRemoteProtectionLostException) { return await executeOnce().ConfigureAwait(false); }
    }

    internal static async ValueTask<T> RetryKeepRemoteReconciliationAsync<T>(Func<ValueTask<T>> execute,
        Func<CancellationToken, ValueTask<bool>> reconcile, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await execute().ConfigureAwait(false); }
            catch (MirrorPulseKeepRemoteJournalDeferredException error) when (error.RequiresFullRescan && attempt < 3)
            {
                if (!await reconcile(token).ConfigureAwait(false)) throw;
            }
        }
    }

    private async ValueTask<MirrorPulseConflictResolution> PrepareKeepRemoteAdmittedAsync(MirrorPulseConflictRecord conflict,
        MirrorPulseJournalCoalescingPlan group, RootId? admittedRoot, CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw new PlatformNotSupportedException();
        RequireKeepRemoteService();
        var root = _router!.GetRegistration(group.InstanceId, group.RootKey);
        RequireKeepRemoteRoot(root, admittedRoot);
        if (!_mayDispatch(root.InstanceId))
            throw new InvalidOperationException("KeepRemote requires an available original Adapter instance.");
        var target = await _catalog.ReadCoalescedConflictRemoteTargetAsync(conflict.ConflictId, token).ConfigureAwait(false);
        if (target.InstanceId != group.InstanceId || target.RootKey != group.RootKey || target.ItemId != group.ItemId)
            throw new InvalidDataException("KeepRemote lost the original rejected file update.");
        if (!MirrorPulseConflictKeepRemote.SupportsProtectedFileEffect(group, target.RejectedEffect))
            throw new NotSupportedException("This KeepRemote effect requires its own protected execution.");
        var source = await MirrorPulseConflictCommandDiagnostics.AtStageAsync(MirrorPulseConflictCommandStage.RemoteSource,
            () =>
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw new PlatformNotSupportedException();
                return _namespaceExecution!.RunNormalUserOperationAsync(() => _remoteCopies!.CaptureSourceAsync(conflict,
                    target.RootKey, target.RemoteRelativePath, token));
            }).ConfigureAwait(false);
        var content = await MirrorPulseConflictCommandDiagnostics.AtStageAsync(MirrorPulseConflictCommandStage.LocalContent,
            () => CaptureGroupKeepLocalContentAsync(group, admittedRoot, token)).ConfigureAwait(false);
        if (content.UploadBinding.PreviousPlaceholderIdentity is null)
            throw new InvalidOperationException("KeepRemote requires the original resident placeholder.");
        Guid command = Guid.NewGuid();
        await MirrorPulseConflictCommandDiagnostics.AtStageAsync(MirrorPulseConflictCommandStage.Acceptance, async () =>
        {
            _ = await _catalog.AcceptConflictKeepRemoteAsync(command, conflict.ConflictId, group.ItemId, conflict.RelativePath,
                source, content, token).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (KeepRemotePrepared is not null) await KeepRemotePrepared(token).ConfigureAwait(false);
        return await ExecuteKeepRemoteOrReportPendingAsync(command, conflict.ConflictId, admittedRoot, token).ConfigureAwait(false);
    }

    internal async ValueTask<bool> RecoverKeepRemoteBeforeAuditAsync(RootRegistration root, CancellationToken token)
    {
        if (_namespaceWork is null) return false;
        var original = await _catalog.ReadPendingConflictKeepRemoteFileStartAsync(root.RootId, token).ConfigureAwait(false);
        if (original is null) return false;
        await using var admission = await _namespaceWork.EnterPendingOperationAsync(root.RootId, token).ConfigureAwait(false);
        _ = await ExecuteKeepRemoteAdmittedAsync(original.CommandId, root.RootId, token).ConfigureAwait(false);
        return true;
    }

    internal ValueTask<MirrorPulseConflictResolution> ExecuteKeepRemoteAdmittedAsync(Guid commandId, RootId? admittedRoot,
        CancellationToken token)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw new PlatformNotSupportedException();
        return ExecuteKeepRemoteOnSupportedWindowsAsync(commandId, admittedRoot, token);
    }

    private async ValueTask<MirrorPulseConflictResolution> ExecuteKeepRemoteOrReportPendingAsync(Guid commandId,
        Guid conflictId, RootId? admittedRoot, CancellationToken token)
    {
        try { return await ExecuteKeepRemoteAdmittedAsync(commandId, admittedRoot, token).ConfigureAwait(false); }
        catch (MirrorPulseKeepRemoteJournalDeferredException)
        {
            // The public result reads its state from the durable user command. The pump will
            // settle metadata or reconcile the official feed before resuming this same command.
            return new(commandId, conflictId, MirrorPulseConflictAction.KeepRemote, null, DateTimeOffset.UtcNow);
        }
    }

    [SupportedOSPlatform("windows10.0.26100")]
    private async ValueTask<MirrorPulseConflictResolution> ExecuteKeepRemoteOnSupportedWindowsAsync(Guid commandId, RootId? admittedRoot,
        CancellationToken token)
    {
        RequireKeepRemoteService();
        var preparation = await _catalog.ReadConflictKeepRemoteAsync(commandId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original KeepRemote preparation disappeared.");
        var intent = preparation.Intent;
        var root = _router!.GetRegistration(intent.InstanceId, intent.RemoteSource.RootKey);
        RequireKeepRemoteRoot(root, admittedRoot);
        var execution = _namespaceExecution!;
        var actor = _namespaceActor!;
        var fileSystem = _fileSystem!;
        var preserver = new MirrorPulseConflictKeepRemotePreserver(_catalog, _copies, _remoteCopies!);
        await using var copies = await MirrorPulseConflictCommandDiagnostics.AtStageAsync(MirrorPulseConflictCommandStage.CopyRetention,
            () => execution.RunNormalUserOperationAsync(() => preserver.RetainAsync(commandId,
                (original, stop) => new(OpenKeepRemoteSourceAsync(original, stop)), token))).ConfigureAwait(false);
        var settled = await actor.RunAsync(async stop =>
        {
            var journal = new MirrorPulseConflictKeepRemoteJournal(_state.OpenStore, _catalog, _router, _feed);
            var first = await _catalog.ReadConflictKeepRemoteFileStartAsync(commandId, stop).ConfigureAwait(false);
            if (first is null)
            {
                await MirrorPulseConflictCommandDiagnostics.AtStageAsync(MirrorPulseConflictCommandStage.OriginalJournal,
                    () => execution.RunNormalUserOperationAsync(() => journal.RetainAsync(copies, stop))).ConfigureAwait(false);
                await MirrorPulseConflictCommandDiagnostics.AtStageAsync(MirrorPulseConflictCommandStage.FileStart,
                    () => RetainKeepRemoteStartAsync(root.RootId, intent, stop)).ConfigureAwait(false);
                if (KeepRemoteFileStarted is not null) await KeepRemoteFileStarted(stop).ConfigureAwait(false);
            }

            var access = await _catalog.ReadConflictKeepRemoteRestoreAttemptsAsync(commandId, stop).ConfigureAwait(false);
            if (!access.Any(attempt => attempt.Result?.ProductVerified == true))
            {
                var action = await _catalog.ReadConflictKeepRemoteFileActionAsync(commandId, stop).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The original KeepRemote permission action disappeared.");
                var roles = await _catalog.ReadConflictKeepRemoteRoleAttemptsAsync(commandId, stop).ConfigureAwait(false);
                if (roles.Count == 0 ? action.RoleSid != execution.RoleSid.Value :
                    roles[^1].Result is null || roles[^1].Start.NextAction.RoleSid != execution.RoleSid.Value)
                    await MirrorPulseConflictCommandDiagnostics.AtStageAsync(MirrorPulseConflictCommandStage.RoleRecovery, async () =>
                    {
                        _ = await new MirrorPulseConflictKeepRemoteResidentRoleRecovery(fileSystem, _catalog, _state, _feed, _router, execution, actor)
                            .RecoverAsync(root.RootId, copies, stop).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                await MirrorPulseConflictCommandDiagnostics.AtStageAsync(MirrorPulseConflictCommandStage.ContentWrite, async () =>
                {
                    _ = await new MirrorPulseConflictKeepRemoteResidentWriter(fileSystem, _catalog, _state, _feed, _router, execution, actor)
                        .CopyAsync(root.RootId, copies, stop).ConfigureAwait(false);
                }).ConfigureAwait(false);
                var native = await MirrorPulseConflictCommandDiagnostics.AtStageAsync(MirrorPulseConflictCommandStage.NativeConfirmation,
                    async () => await new MirrorPulseConflictKeepRemoteResidentConfirmation(fileSystem, _catalog, _state, _feed, _router, execution, actor)
                        .ConfirmAsync(root.RootId, copies, stop).ConfigureAwait(false)).ConfigureAwait(false);
                if (native.Receipt.Outcome == MirrorPulseContentConfirmationOutcome.ProtectionLost)
                    throw new KeepRemoteProtectionLostException();
                if (!native.Receipt.MayAcknowledge)
                    throw new IOException("KeepRemote could not verify the original selected native content.");
                var restorer = new MirrorPulseConflictKeepRemoteResidentRestoration(fileSystem, _catalog, _state, _feed, _router, execution, actor)
                {
                    RestoreRegisteredDescriptor = true
                };
                var restored = await MirrorPulseConflictCommandDiagnostics.AtStageAsync(MirrorPulseConflictCommandStage.AccessRestoration,
                    async () => await restorer.RestoreAsync(root.RootId, copies, stop).ConfigureAwait(false)).ConfigureAwait(false);
                if (!restored.ProductVerified)
                    throw new IOException("KeepRemote could not verify restored access to the original file.");
            }
            return await MirrorPulseConflictCommandDiagnostics.AtStageAsync(MirrorPulseConflictCommandStage.Acknowledgement,
                async () => await new MirrorPulseConflictKeepRemoteResidentAcknowledgement(_catalog, _state, _feed, _router, execution, actor)
                    .AcknowledgeAsync(root.RootId, copies, stop).ConfigureAwait(false)).ConfigureAwait(false);
        }, token).ConfigureAwait(false);

        foreach (Guid conflict in intent.ConflictIds) _conflictCenter?.Remove(conflict);
        return new(commandId, intent.ConflictId, MirrorPulseConflictAction.KeepRemote, copies.Local!.Path, settled.ReadBackAt);
    }

    [SupportedOSPlatform("windows10.0.26100")]
    private async Task<Stream> OpenKeepRemoteSourceAsync(MirrorPulseConflictKeepRemoteIntent intent, CancellationToken token)
    {
        var file = _fileSystem!.GetFile(intent.LocalRelativePath);
        await RequireKeepRemoteOriginalAsync(file, intent, token).ConfigureAwait(false);
        var input = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        try
        {
            await RequireKeepRemoteOriginalAsync(file, intent, token).ConfigureAwait(false);
            return input;
        }
        catch
        {
            await input.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    [SupportedOSPlatform("windows10.0.26100")]
    private async Task RetainKeepRemoteStartAsync(RootId root, MirrorPulseConflictKeepRemoteIntent intent, CancellationToken token)
    {
        var execution = _namespaceExecution!;
        var binding = intent.LocalContent!.UploadBinding.LocalObject;
        var member = await _catalog.ReadNamespaceMemberByBindingAsync(binding, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("KeepRemote requires the original registered file.");
        var original = await _catalog.ReadNamespaceMemberProtectionSnapshotAsync(member.MemberId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("KeepRemote requires the original file protection.");
        var parent = original.Context.Location.ParentMemberId is { } parentId ?
            await _catalog.ReadNamespaceMemberProtectionSnapshotAsync(parentId, token).ConfigureAwait(false) : null;
        if (parent is null || original.Context.Member.RootId != root || parent.Context.Member.RootId != root ||
            original.Context.Protection.RoleSid != execution.RoleSid.Value || parent.Context.Protection.RoleSid != execution.RoleSid.Value ||
            original.Context.Member.OwnerSid != execution.OwnerSid.Value || parent.Context.Member.OwnerSid != execution.OwnerSid.Value)
            throw new InvalidDataException("KeepRemote requires the original protected file and direct parent in its active role.");
        var file = _fileSystem!.GetFile(original.Context.Location.RelativePath);
        var directory = _fileSystem.GetDirectory(parent.Context.Location.RelativePath);
        if (!string.Equals(file.Parent?.FullPath, directory.FullPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("KeepRemote lost the original direct parent.");
        using var store = await execution.RunNormalUserOperationAsync(() => _state.OpenStoreFileLeaseAsync(token).AsTask()).ConfigureAwait(false);
        await execution.RunNamespaceOperationAsync(async () =>
        {
            await using var parentGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(directory, _router!, token).ConfigureAwait(false);
            await using var childGuard = await MirrorPulseWindowsNamespacePermissionLease.OpenMetadataAsync(file, _router!, token).ConfigureAwait(false);
            RequireOriginal(await parentGuard.InspectAsync(token).ConfigureAwait(false), parent);
            RequireOriginal(await childGuard.InspectAsync(token).ConfigureAwait(false), original);
            await RequireKeepRemoteOriginalAsync(file, intent, token).ConfigureAwait(false);
            await execution.RunNormalUserOperationAsync(() => store.RequireCurrentAsync(store.Binding, token).AsTask()).ConfigureAwait(false);
            var action = new MirrorPulseConflictKeepRemoteFileAction(1, intent.CommandId, Guid.NewGuid(), binding, execution.OwnerSid.Value,
                execution.RoleSid.Value, MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(execution), original.ExpectedDacl, DateTimeOffset.UtcNow);
            await _catalog.RetainConflictKeepRemoteFileStartAndActionAsync(intent.CommandId, store.Binding.VolumeSerialNumber,
                store.Binding.FileId, original, parent, action, token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        void RequireOriginal(MirrorPulseNamespacePermissionObject value, MirrorPulseNamespaceMemberProtectionSnapshot expected)
        {
            var originalMember = expected.Context.Member;
            if (value.LocalObject != originalMember.LocalObject || value.OwnerSid != originalMember.OwnerSid ||
                value.IsDirectory != originalMember.IsDirectory || value.RootId != root || value.LinkCount != 1 ||
                value.RelativePath != expected.Context.Location.RelativePath ||
                !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(expected.ExpectedDacl, value.Dacl))
                throw new InvalidDataException("KeepRemote requires the original protected file and direct parent before any write.");
        }
    }

    private async Task RequireKeepRemoteOriginalAsync(CloudFile file, MirrorPulseConflictKeepRemoteIntent intent,
        CancellationToken token)
    {
        _ = await MirrorPulseKeepRemotePublicIdentity.ReadOriginalAsync(_catalog,
            _router!.GetRegistration(intent.InstanceId, intent.RemoteSource.RootKey), intent, token).ConfigureAwait(false);
        var current = await file.InspectAsync(token).ConfigureAwait(false);
        if (!current.Exists || !current.IsPlaceholder || current.IsTombstone || current.ItemId != intent.ItemId ||
            current.ContentAvailability != CloudContentAvailability.FullyAvailable || current.LocalBinding is not { } native ||
            intent.LocalContent?.UploadBinding is not { } original ||
            original.LocalObject != new MirrorPulseLocalFileBinding(native.VolumeSerialNumber, native.SyncRootFileId, native.LocalFileId) ||
            original.PreviousPlaceholderIdentity != Convert.ToBase64String(current.PlaceholderIdentity.Span))
            throw new InvalidDataException("KeepRemote requires the exact original resident placeholder without source recall.");
    }

    private void RequireKeepRemoteService()
    {
        if (_fileSystem is null || _router is null || _namespaceExecution is null || _namespaceActor is null || _remoteCopies is null)
            throw new NotSupportedException("KeepRemote requires the active protected Cloud Files owner and ranged Adapter source.");
    }

    private static void RequireKeepRemoteRoot(RootRegistration root, RootId? admittedRoot)
    {
        if (root.State != RootRegistrationState.Active || root.RootId != admittedRoot)
            throw new InvalidOperationException("KeepRemote requires the original admitted active root.");
    }
}
