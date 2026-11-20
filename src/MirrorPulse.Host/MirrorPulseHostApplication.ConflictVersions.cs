using MirrorPulse.Control.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Host;

public sealed partial class MirrorPulseHostApplication
{
    private async Task<MirrorPulseControlConflictVersionRefresh> RefreshConflictVersionAsync(ConflictRefreshVersionArguments arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.CommandId == Guid.Empty || arguments.ConflictId == Guid.Empty)
            throw new ArgumentException("An explicit operation ID and selected conflict ID are required.");
        // A completed request is historical catalog data. It does not depend on
        // the current root being enabled and cannot silently choose another version.
        var observed = await _catalog.ReadCoalescedConflictRemoteRefreshAsync(arguments.CommandId, cancellationToken).ConfigureAwait(false);
        if (observed is null)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) throw new PlatformNotSupportedException();
            observed = await _session.RefreshConflictVersionAsync(arguments.CommandId, arguments.ConflictId, cancellationToken).ConfigureAwait(false);
        }
        if (observed is not null && observed.EntryConflictId != arguments.ConflictId)
            throw new InvalidDataException("The refresh operation belongs to another selected conflict.");
        MirrorPulseUserCommandRecord command = await _catalog.ReadUserCommandAsync(arguments.CommandId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The explicit version request lost its durable command.");
        if (command.Action != "refreshconflict" || command.TargetId != arguments.ConflictId.ToString("D") ||
            command.State != (observed is null ? "pending" : "resolved"))
            throw new InvalidDataException("The explicit version request and retained outcome disagree.");
        return new(1, arguments.CommandId, arguments.ConflictId, command.State, observed is null ? null :
            new(observed.Version, observed.ObservationId, observed.RejectedEffect.OperationId, observed.RejectedEffect.ExpectedRevision,
                observed.OriginalErrorExpectedRevision, observed.OriginalErrorActualRevision, observed.RemoteRevision, observed.StartedAt,
                observed.ObservedAt, observed.PreviousObservationId, Array.AsReadOnly(observed.SelectedConflictIds.ToArray())));
    }
}
