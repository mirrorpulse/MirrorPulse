using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task ValidateNamespaceMoveFactReferencesAsync<T>(T fact, Guid operationId,
        SqliteTransaction transaction, CancellationToken token) where T : class
    {
        var intent = await ReadNamespaceMemberMoveCoreAsync(operationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original move admission is missing.");
        var start = await ReadNamespaceMemberMoveStartCoreAsync(operationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The before-native move start is missing.");
        switch (fact)
        {
            case MirrorPulseNamespaceMemberMoveObservation native:
                var member = await ReadNamespaceMemberCoreAsync(intent.MemberId, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The original moved member is missing.");
                var parent = await ReadNamespaceMemberCoreAsync(intent.Target.ParentMemberId, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The admitted target parent is missing.");
                var childProof = await ReadNamespaceMoveRetainedVerificationAsync(member, intent.ProtectionId, transaction, token).ConfigureAwait(false);
                var parentProof = await ReadNamespaceMoveRetainedVerificationAsync(parent, intent.Target.ParentProtectionId, transaction, token).ConfigureAwait(false);
                if (native.OperationId != operationId || native.ItemId != intent.ItemId || native.RemoteId != intent.RemoteId ||
                    native.RelativePath != intent.Target.RelativePath || native.IsDirectory != member.IsDirectory ||
                    native.Child.LocalObject != intent.LocalObject || native.Parent.LocalObject != intent.Target.ParentLocalObject ||
                    native.Child.OwnerSid != intent.OwnerSid || native.Parent.OwnerSid != intent.OwnerSid ||
                    native.Child.Dacl != childProof.Dacl || native.Parent.Dacl != parentProof.Dacl ||
                    native.Child.ObservedAt < start.StartedAt || native.Parent.ObservedAt < start.StartedAt)
                    throw new InvalidOperationException("Native move facts require the original child, actual target parent and retained protected descriptors.");
                break;
            case MirrorPulseNamespaceMemberMoveProjection projection:
                var observation = await ReadNamespaceMoveFactCoreAsync<MirrorPulseNamespaceMemberMoveObservation>(operationId, true, transaction, token).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The native move observation is missing.");
                if (projection.OperationId != operationId || projection.ItemId != observation.ItemId || projection.RemoteId != observation.RemoteId ||
                    projection.RelativePath != observation.RelativePath || projection.ObservedAt < observation.Child.ObservedAt ||
                    projection.ObservedAt < observation.Parent.ObservedAt)
                    throw new InvalidOperationException("The official projection must follow the retained native object at its admitted target.");
                // Native identity revision and official accepted baseline are different observations.
                // No revision comparison here proves that the source accepted content or metadata.
                break;
        }
    }

    private async Task<MirrorPulseNamespacePermissionVerification> ReadNamespaceMoveRetainedVerificationAsync(
        MirrorPulseNamespaceMemberRegistration member, Guid protectionId, SqliteTransaction transaction, CancellationToken token)
    {
        if (member.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal)
            return (await ReadPermissionChangeCoreAsync(protectionId, token, transaction).ConfigureAwait(false))?.Verification
                ?? throw new FileNotFoundException("The retained original move protection is missing.");
        return (await ReadNamespaceBirthProtectionCoreAsync(protectionId, transaction, token).ConfigureAwait(false))?.Verification
            ?? throw new FileNotFoundException("The retained born move protection is missing.");
    }

    private static async Task EnsureNamespaceMemberMoveFactSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using SqliteCommand schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_member_move_observations (
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_move_starts(operation_id),
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
            );
            CREATE TABLE IF NOT EXISTS namespace_member_move_projections (
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_member_move_observations(operation_id),
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32)
            );
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
