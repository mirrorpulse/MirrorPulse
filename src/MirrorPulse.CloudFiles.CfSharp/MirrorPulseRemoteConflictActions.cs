using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed record MirrorPulseRemoteConflictActionOutcome(
    bool Resolved,
    bool CommandQueued,
    string? PreservedPath,
    CloudRemoteApplyEntryStatus? CfSharpStatus,
    CloudRemoteConflictDismissalStatus? CfSharpDismissalStatus = null,
    Guid CommandId = default);

/// <summary>Executes product conflict choices through CfSharp's public resolution API.</summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseRemoteConflictActions
{
    private readonly Func<Guid, CloudRemoteConflictDecision, CancellationToken,
        ValueTask<CloudRemoteApplyEntryStatus>> _resolve;
    private readonly Func<Guid, CancellationToken, ValueTask<CloudRemoteConflictDismissalStatus>>? _dismiss;
    private readonly MirrorPulseConflictCopyStore _copies;
    private readonly MirrorPulseProductCatalog _catalog;
    private readonly MirrorPulseConflictCenter _center;

    public MirrorPulseRemoteConflictActions(
        Func<Guid, CloudRemoteConflictDecision, CancellationToken,
            ValueTask<CloudRemoteApplyEntryStatus>> resolve,
        MirrorPulseConflictCopyStore copies,
        MirrorPulseProductCatalog catalog,
        MirrorPulseConflictCenter center,
        Func<Guid, CancellationToken, ValueTask<CloudRemoteConflictDismissalStatus>>? dismiss = null)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _dismiss = dismiss;
        _copies = copies ?? throw new ArgumentNullException(nameof(copies));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _center = center ?? throw new ArgumentNullException(nameof(center));
    }

    public static MirrorPulseRemoteConflictActions For(
        CloudFileSystem fileSystem,
        MirrorPulseConflictCopyStore copies,
        MirrorPulseProductCatalog catalog,
        MirrorPulseConflictCenter center)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        return new(async (id, decision, cancellationToken) =>
            (await fileSystem.ResolveRemoteConflictAsync(
                id, new CloudRemoteConflictResolution(decision), cancellationToken)
                .ConfigureAwait(false)).Status,
            copies, catalog, center,
            (id, cancellationToken) => fileSystem.DismissRemoteConflictAsync(id, cancellationToken));
    }

    public async Task<MirrorPulseRemoteConflictActionOutcome> ApplyAsync(
        Guid commandId,
        MirrorPulseConflictRecord conflict,
        MirrorPulseConflictAction action,
        MirrorPulseConflictPreservedSide? preservedSide = null,
        Func<CancellationToken, ValueTask<Stream>>? openRemote = null,
        CancellationToken cancellationToken = default)
    {
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("The user command ID cannot be empty.", nameof(commandId));
        }

        ArgumentNullException.ThrowIfNull(conflict);
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        if (conflict.Source != MirrorPulseConflictSource.CfSharpRemote || !conflict.IsPending)
        {
            throw new ArgumentException("This action requires a pending CfSharp remote conflict.", nameof(conflict));
        }

        if (action == MirrorPulseConflictAction.KeepLocal && _dismiss is null)
        {
            throw new NotSupportedException(
                "This conflict action requires the CfSharp preview.2 dismissal API.");
        }

        if (action == MirrorPulseConflictAction.KeepBoth && preservedSide is null)
        {
            throw new ArgumentException("KeepBoth requires the side to preserve.", nameof(preservedSide));
        }

        if (action != MirrorPulseConflictAction.KeepBoth && preservedSide is not null)
        {
            throw new ArgumentException("Only KeepBoth may select a preserved side.", nameof(preservedSide));
        }

        if (action == MirrorPulseConflictAction.KeepBoth &&
            preservedSide == MirrorPulseConflictPreservedSide.Remote && openRemote is null)
        {
            throw new ArgumentException(
                "Preserving the remote side requires an Adapter stream.", nameof(openRemote));
        }

        string actionName = action == MirrorPulseConflictAction.KeepBoth
            ? $"keep-both-{preservedSide!.Value.ToString().ToLowerInvariant()}"
            : action.ToString().ToLowerInvariant();
        await _catalog.SaveUserCommandAsync(new MirrorPulseUserCommandRecord(
            commandId, actionName, conflict.ConflictId.ToString("D"), "pending"), cancellationToken)
            .ConfigureAwait(false);
        try
        {

            if (action == MirrorPulseConflictAction.KeepLocal)
            {
                CloudRemoteConflictDismissalStatus dismissal = await _dismiss!(
                    conflict.ConflictId, cancellationToken).ConfigureAwait(false);
                bool dismissed = dismissal is CloudRemoteConflictDismissalStatus.Dismissed or
                    CloudRemoteConflictDismissalStatus.AlreadyDismissed;
                if (dismissed)
                {
                    await _catalog.SaveUserCommandAsync(new MirrorPulseUserCommandRecord(
                        commandId, actionName, conflict.ConflictId.ToString("D"), "resolved"), cancellationToken)
                        .ConfigureAwait(false);
                    _center.Remove(conflict.ConflictId);
                }

                if (!dismissed) await SaveFailedAsync(commandId, actionName, conflict, cancellationToken).ConfigureAwait(false);
                return new(dismissed, false, null, null, dismissal, commandId);
            }

            if (action is MirrorPulseConflictAction.Retry or
                MirrorPulseConflictAction.DeleteLocal or MirrorPulseConflictAction.DeleteRemote)
            {
                // These are product/Worker commands. CfSharp exposes no equivalent terminal decision.
                return new(false, true, null, null, CommandId: commandId);
            }

            string? preservedPath = null;
            if (action == MirrorPulseConflictAction.KeepBoth)
            {
                Func<CancellationToken, ValueTask<Stream>> source = preservedSide == MirrorPulseConflictPreservedSide.Local
                    ? token => _copies.OpenLocalAsync(conflict, token)
                    : openRemote!;
                preservedPath = await _copies.PreserveAsync(
                    conflict, preservedSide!.Value, source, cancellationToken).ConfigureAwait(false);
            }

            CloudRemoteConflictDecision decision = action == MirrorPulseConflictAction.Defer
                ? CloudRemoteConflictDecision.Defer
                : CloudRemoteConflictDecision.KeepRemote;
            // A missing SDK conflict proves no outcome for this product command.
            // Preserve an unavailable/ambiguous result instead of inventing AlreadyApplied.
            CloudRemoteApplyEntryStatus status = await _resolve(conflict.ConflictId, decision, cancellationToken).ConfigureAwait(false);

            bool resolved = action != MirrorPulseConflictAction.Defer &&
                status is CloudRemoteApplyEntryStatus.Applied or CloudRemoteApplyEntryStatus.AlreadyApplied;
            if (resolved)
            {
                await _catalog.SaveUserCommandAsync(new MirrorPulseUserCommandRecord(
                    commandId, actionName, conflict.ConflictId.ToString("D"), "resolved"), cancellationToken)
                    .ConfigureAwait(false);
                _center.Remove(conflict.ConflictId);
            }

            if (!resolved && action != MirrorPulseConflictAction.Defer)
                await SaveFailedAsync(commandId, actionName, conflict, cancellationToken).ConfigureAwait(false);
            return new(resolved, false, preservedPath, status, CommandId: commandId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            try
            {
                await SaveFailedAsync(commandId, actionName, conflict, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception persistenceError)
            {
                throw new AggregateException("The remote conflict action and failure-state persistence both failed; retain the original command and conflict.",
                    exception, persistenceError);
            }
            throw;
        }
    }

    private Task SaveFailedAsync(Guid id, string action, MirrorPulseConflictRecord conflict, CancellationToken cancellationToken) =>
        _catalog.SaveUserCommandAsync(new(id, action, conflict.ConflictId.ToString("D"), "failed"), cancellationToken);
}
