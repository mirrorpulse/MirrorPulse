using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Original conditional remote source-delete start and correlated acceptance; local source and both fences remain.</summary>
/// <remarks>A retained start is ambiguous until accepted. This is neither local retirement nor journal ACK authority.</remarks>
public sealed record MirrorPulseNamespaceTransferSourceDeletion(int Version, Guid OperationId, Guid DeleteOperationId,
    Guid TargetProtectionId, MirrorPulseTransferTargetVerification TargetAtStart, DateTimeOffset StartedAt,
    DateTimeOffset? AcceptedAt = null, string? ResultRevision = null);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Rechecks live original transfer/target ownership without granting another delete after a retained start.</summary>
    public async Task<MirrorPulseNamespaceTransferCopy> ValidateNamespaceTransferSourceDeleteAsync(Guid operationId,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original transfer is missing.");
            if (copy.SourceDeletion?.AcceptedAt is null)
                _ = await RequireCurrentTransferSourceDeletionAsync(copy, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return copy;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Claims the only first conditional source delete after current native guards and fresh whole-target readback.</summary>
    /// <remarks>The Host must retain its live writer/mapping exclusion across this claim, RPC and acceptance persistence.</remarks>
    public async Task<MirrorPulseNamespaceTransferCopy> StartNamespaceTransferSourceDeleteAsync(Guid operationId,
        MirrorPulseTransferTargetVerification targetAtStart, Guid targetProtectionId, DateTimeOffset startedAt,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(targetAtStart);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original transfer is missing.");
            if (copy.SourceDeletion is not null)
                throw new InvalidOperationException("The original source delete may already have executed; another dispatch is not authorized.");
            var target = await RequireCurrentTransferSourceDeletionAsync(copy, transaction, token).ConfigureAwait(false);
            if (target.Context.Protection.ProtectionId != targetProtectionId)
                throw new InvalidOperationException("Source deletion requires the current independently verified target protection epoch.");
            var deletion = new MirrorPulseNamespaceTransferSourceDeletion(1, operationId, copy.Plan.DeleteOperationId,
                targetProtectionId, targetAtStart, startedAt);
            ValidateNamespaceTransferSourceDeletion(copy, deletion);
            copy = copy with { SourceDeletion = deletion };
            await WriteNamespaceTransferCoreAsync(copy, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return copy;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Persists only the original correlated successful Worker response, even when cancellation races its return.</summary>
    public async Task<MirrorPulseNamespaceTransferCopy> RecordNamespaceTransferSourceDeleteAcceptedAsync(Guid operationId,
        string? resultRevision, DateTimeOffset acceptedAt, CancellationToken token = default)
    {
        if (resultRevision is not null && InvalidRemoteUpdateText(resultRevision, 4096))
            throw new ArgumentException("A returned deletion revision must be bounded and nonempty.", nameof(resultRevision));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original transfer is missing.");
            var deletion = copy.SourceDeletion ?? throw new InvalidOperationException("Source acceptance requires its original durable first dispatch claim.");
            if (deletion.AcceptedAt is not null)
            {
                if (deletion.ResultRevision != resultRevision || deletion.AcceptedAt != acceptedAt)
                    throw new InvalidOperationException("The first correlated source deletion receipt is immutable.");
                transaction.Commit(); return copy;
            }
            deletion = deletion with { AcceptedAt = acceptedAt, ResultRevision = resultRevision };
            ValidateNamespaceTransferSourceDeletion(copy, deletion);
            copy = copy with { SourceDeletion = deletion };
            await WriteNamespaceTransferCoreAsync(copy, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return copy;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceMemberProtectionSnapshot> RequireCurrentTransferSourceDeletionAsync(
        MirrorPulseNamespaceTransferCopy copy, SqliteTransaction transaction, CancellationToken token)
    {
        if (copy.TargetPublication is null || copy.TargetBirth is null || copy.Plan.SourceRemoteRevision is null ||
            copy.Plan.SourceRoute.ProtocolVersion != 2)
            throw new InvalidOperationException("Conditional source deletion requires a published target and the original V2 source revision; a local-only source needs separate reconciliation.");
        await RequireCurrentNamespaceTransferAsync(copy.Plan, transaction, token, admitted: true, targetBirth: copy.TargetBirth).ConfigureAwait(false);
        var target = await ReadNamespaceMemberProtectionSnapshotCoreAsync(copy.TargetPublication.MemberId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The published target lost its current protected membership.");
        if (target.Context.Member.LocalObject != copy.TargetPublication.LocalObject || target.Context.Member.RootId != copy.Plan.TargetRoute.RootId ||
            target.Context.Member.IsDirectory || target.Context.Location.RelativePath != copy.Plan.TargetRelativePath ||
            target.Context.Location.ParentMemberId != copy.Plan.TargetParent.Context.Member.MemberId ||
            target.Context.Protection.RoleSid != copy.Plan.Source.Context.Protection.RoleSid ||
            target.Identity.ItemId != copy.TargetPublication.ItemId || target.Identity.RemoteId != copy.TargetBirth.Plan.RemoteId)
            throw new InvalidOperationException("Source deletion requires the original published target, current role, identity and location.");
        return target;
    }

    private static void ValidateNamespaceTransferSourceDeletion(MirrorPulseNamespaceTransferCopy copy,
        MirrorPulseNamespaceTransferSourceDeletion deletion)
    {
        if (deletion.Version != 1 || deletion.OperationId != copy.Plan.OperationId || deletion.DeleteOperationId != copy.Plan.DeleteOperationId ||
            deletion.TargetProtectionId == Guid.Empty || copy.TargetPublication is null || copy.TargetVerification is null ||
            copy.Plan.SourceRemoteRevision is null || copy.Plan.SourceRoute.ProtocolVersion != 2 || deletion.TargetAtStart is null ||
            deletion.TargetAtStart.RemoteId != copy.TargetVerification.RemoteId ||
            deletion.TargetAtStart.VerifiedAt < copy.TargetPublication.PublishedAt || deletion.StartedAt < deletion.TargetAtStart.VerifiedAt ||
            deletion.AcceptedAt is { } accepted && accepted < deletion.StartedAt ||
            deletion.ResultRevision is not null && (deletion.AcceptedAt is null || InvalidRemoteUpdateText(deletion.ResultRevision, 4096)))
            throw new ArgumentException("Source deletion must retain its original conditional effect, published target, fresh whole proof and chronology.", nameof(deletion));
        ValidateNamespaceTransferTargetVerification(copy, deletion.TargetAtStart);
    }

    private async Task ValidateNamespaceTransferSourceDeletionReferencesAsync(MirrorPulseNamespaceTransferCopy copy,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (copy.SourceDeletion is not { } deletion) return;
        var protection = await ReadNamespaceBirthProtectionCoreAsync(deletion.TargetProtectionId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Source deletion lost its original target protection evidence.");
        if (copy.TargetBirth is null || protection.BirthOperationId != copy.TargetBirth.Birth.OperationId ||
            protection.Verification.LocalObject != copy.TargetPublication!.LocalObject ||
            protection.RoleSid != copy.Plan.Source.Context.Protection.RoleSid || protection.Verification.ObservedAt > deletion.StartedAt)
            throw new InvalidDataException("Source deletion no longer references its original target object and owned protection.");
    }
}
