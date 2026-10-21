using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>First registered, independently protected native reference to the original verified remote target.</summary>
/// <remarks>This is an accepted online placeholder publication, not proof of resident bytes, source retirement,
/// another upload, journal settlement or root readiness. Both transfer fences remain owned.</remarks>
public sealed record MirrorPulseNamespaceTransferTargetPublication(int Version, Guid OperationId, Guid BirthOperationId,
    Guid MemberId, Guid ItemId, MirrorPulseLocalFileBinding LocalObject, Guid ProtectionId, DateTimeOffset PublishedAt);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Validates the exact original target continuation without admitting unrelated work in either root.</summary>
    public async Task<MirrorPulseNamespaceTransferCopy> ValidateNamespaceTransferTargetPublicationAsync(Guid operationId,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original transfer is missing.");
            if (copy.TargetBirth is null) throw new InvalidOperationException("The transfer target has no original birth and public identity plan.");
            if (copy.TargetPublication is null)
                await RequireCurrentNamespaceTransferAsync(copy.Plan, transaction, token, admitted: true, targetBirth: copy.TargetBirth).ConfigureAwait(false);
            transaction.Commit(); return copy;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Retains first target publication only after actual native observation, protection and membership.</summary>
    /// <remarks>The Host supplies independent native/public SDK checks before calling. This catalog transaction
    /// grants no native creation, deletes no source and releases neither durable transfer fence.</remarks>
    public async Task<MirrorPulseNamespaceTransferCopy> RecordNamespaceTransferTargetPublicationAsync(
        MirrorPulseNamespaceTransferTargetPublication publication, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(publication);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var copy = await ReadNamespaceTransferCoreAsync(publication.OperationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original transfer is missing.");
            ValidateNamespaceTransferPublicationShape(copy, publication);
            if (copy.TargetPublication is { } retained)
            {
                if (retained != publication) throw new InvalidOperationException("The first native target publication is immutable.");
                transaction.Commit(); return copy;
            }
            if (copy.TargetBirth is null) throw new InvalidOperationException("The original target birth is missing.");
            await RequireCurrentNamespaceTransferAsync(copy.Plan, transaction, token, admitted: true, targetBirth: copy.TargetBirth).ConfigureAwait(false);
            await ValidateNamespaceTransferPublicationReferencesAsync(copy, publication, transaction, token).ConfigureAwait(false);
            if ((await ReadLatestNamespaceBirthProtectionCoreAsync(publication.BirthOperationId, transaction, token).ConfigureAwait(false))?.ProtectionId != publication.ProtectionId)
                throw new InvalidOperationException("First publication requires the current target protection epoch.");
            copy = copy with { TargetPublication = publication };
            await WriteNamespaceTransferCoreAsync(copy, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return copy;
        }
        finally { _gate.Release(); }
    }

    private static void ValidateNamespaceTransferPublicationShape(MirrorPulseNamespaceTransferCopy copy,
        MirrorPulseNamespaceTransferTargetPublication publication)
    {
        ValidatePermissionBinding(publication.LocalObject);
        if (publication.Version != 1 || publication.OperationId != copy.Plan.OperationId || copy.TargetVerification is null ||
            copy.TargetBirthOperationId != publication.BirthOperationId || publication.BirthOperationId == Guid.Empty ||
            publication.MemberId != publication.BirthOperationId || publication.ItemId == Guid.Empty || publication.ProtectionId == Guid.Empty ||
            publication.PublishedAt < copy.TargetVerification.VerifiedAt)
            throw new ArgumentException("Target publication must retain its original verified transfer and separate native birth.", nameof(publication));
    }

    private async Task ValidateNamespaceTransferPublicationReferencesAsync(MirrorPulseNamespaceTransferCopy copy,
        MirrorPulseNamespaceTransferTargetPublication publication, SqliteTransaction transaction, CancellationToken token)
    {
        ValidateNamespaceTransferPublicationShape(copy, publication);
        var target = copy.TargetBirth ?? throw new InvalidOperationException("The original target birth is missing.");
        var observation = await ReadNamespaceBirthObservationCoreAsync(target.Birth.OperationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Target publication requires its actual first native birth observation.");
        var protection = await ReadNamespaceBirthProtectionCoreAsync(publication.ProtectionId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Target publication requires independent owned protection.");
        var member = await ReadNamespaceMemberCoreAsync(publication.MemberId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Target publication requires completed native member registration.");
        if (publication.ItemId != target.Plan.ItemId || observation.ItemId != publication.ItemId || !observation.IsInSync ||
            observation.LocalObject != publication.LocalObject || member.LocalObject != publication.LocalObject ||
            observation.RemoteId != target.Plan.RemoteId || observation.RemoteRevision != copy.AcceptedRevision ||
            member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth || member.OriginId != target.Birth.OperationId ||
            protection.BirthOperationId != target.Birth.OperationId ||
            protection.Verification.LocalObject != publication.LocalObject || protection.RoleSid != target.Birth.RoleSid ||
            protection.Verification.OwnerSid != member.OwnerSid || member.OwnerSid != copy.Plan.TargetParent.Context.Member.OwnerSid ||
            member.ParentMemberId != copy.Plan.TargetParent.Context.Member.MemberId || member.RootId != target.Birth.RootId ||
            member.RelativePath != target.Birth.RelativePath || member.IsDirectory ||
            publication.PublishedAt < member.RegisteredAt || publication.PublishedAt < observation.ObservedAt ||
            publication.PublishedAt < protection.Verification.ObservedAt)
            throw new InvalidOperationException("The target publication lost its original identity, complete native binding, parent, protection or chronology.");
    }
}
