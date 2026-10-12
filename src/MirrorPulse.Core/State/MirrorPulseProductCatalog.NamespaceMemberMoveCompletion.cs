using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>A fresh, guarded observation that permits a previously admitted member move to settle.</summary>
/// <remarks>
/// The Host records this only after the native move and the public CfSharp projection have both
/// been inspected again. It transfers the product-owned name atomically; it does not acknowledge
/// an upload. Directory settlement requires a separate subtree completion boundary.
/// </remarks>
public sealed record MirrorPulseNamespaceMemberMoveCompletion(int Version, Guid OperationId, Guid ItemId,
    string RemoteId, string RelativePath, bool IsDirectory, DateTimeOffset VerifiedAt,
    MirrorPulseNamespacePermissionVerification Child, MirrorPulseNamespacePermissionVerification Parent,
    string? OfficialRemoteRevision);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Commits a fresh native/public move result and transfers the owned current name.</summary>
    public async Task<MirrorPulseNamespaceMemberMoveCompletion> CompleteNamespaceMemberMoveAsync(
        MirrorPulseNamespaceMemberMoveCompletion completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        ValidateNamespaceMemberMoveCompletion(completion);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var replay = await ReadNamespaceMemberMoveCompletionCoreAsync(completion.OperationId, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay != completion) throw new InvalidOperationException("A retained move completion is immutable.");
                transaction.Commit();
                return replay;
            }

            var intent = await RequireNamespaceMemberMoveExecutionCoreAsync(completion.OperationId, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (completion.IsDirectory)
                throw new InvalidOperationException("A single-item receipt cannot settle a directory move or release its subtree fence.");
            await ValidateNamespaceMemberMoveCompletionReferencesAsync(completion, transaction, cancellationToken).ConfigureAwait(false);
            await using (SqliteCommand current = _connection.CreateCommand())
            {
                current.Transaction = transaction;
                current.CommandText = "SELECT parent_member_id,child_name_key FROM namespace_member_names WHERE member_id=$member;";
                current.Parameters.AddWithValue("$member", intent.MemberId.ToString("D"));
                await using SqliteDataReader reader = await current.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                    reader.GetString(0) != intent.Source.ParentMemberId.ToString("D") ||
                    reader.GetString(1) != NamespaceMemberNameKey(intent.Source.RelativePath))
                    throw new InvalidDataException("The source member no longer owns its admitted current name.");
            }

            await using (SqliteCommand update = _connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE namespace_member_names SET parent_member_id=$parent,child_name_key=$name WHERE member_id=$member;";
                update.Parameters.AddWithValue("$parent", intent.Target.ParentMemberId.ToString("D"));
                update.Parameters.AddWithValue("$name", NamespaceMemberNameKey(intent.Target.RelativePath));
                update.Parameters.AddWithValue("$member", intent.MemberId.ToString("D"));
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidDataException("The source member name disappeared before handover.");
            }

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(completion, TopologyJsonOptions);
            if (payload.Length > NamespaceMemberMoveFactLimit)
                throw new ArgumentException("Move completion must be bounded.", nameof(completion));
            await using (SqliteCommand insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO namespace_member_move_completions(operation_id,member_id,payload,fingerprint)
                    VALUES($operation,$member,$payload,$fingerprint);
                    DELETE FROM namespace_member_move_pending WHERE root_id=$root AND operation_id=$operation;
                    DELETE FROM namespace_member_move_reservations WHERE operation_id=$operation;
                    """;
                insert.Parameters.AddWithValue("$operation", completion.OperationId.ToString("D"));
                insert.Parameters.AddWithValue("$member", intent.MemberId.ToString("D"));
                insert.Parameters.AddWithValue("$root", intent.RootId.ToString());
                insert.Parameters.AddWithValue("$payload", payload);
                insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            transaction.Commit();
            return completion;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceMemberMoveCompletion?> ReadNamespaceMemberMoveCompletionAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A move ID is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadNamespaceMemberMoveCompletionCoreAsync(operationId, transaction, cancellationToken)
                .ConfigureAwait(false);
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceMemberMoveCompletion?> ReadNamespaceMemberMoveCompletionCoreAsync(
        Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT operation_id,member_id,payload,fingerprint FROM namespace_member_move_completions WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", operationId.ToString("D"));
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var completion = ReadNamespaceMovePayload<MirrorPulseNamespaceMemberMoveCompletion>(
            (byte[])reader[2], (byte[])reader[3], NamespaceMemberMoveFactLimit, ValidateNamespaceMemberMoveCompletion);
        var intent = await ReadNamespaceMemberMoveCoreAsync(operationId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The move completion lost its retained intent.");
        if (completion.OperationId != operationId || reader.GetString(0) != operationId.ToString("D") ||
            reader.GetString(1) != intent.MemberId.ToString("D"))
            throw new InvalidDataException("The move completion index is invalid.");
        try
        {
            if (completion.IsDirectory)
                throw new InvalidOperationException("A directory cannot be settled by a single-item receipt.");
            await ValidateNamespaceMemberMoveCompletionReferencesAsync(completion, transaction, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is InvalidOperationException or FileNotFoundException)
        { throw new InvalidDataException("The move completion lost its original native or official projection evidence.", error); }
        return completion;
    }

    private async Task ValidateNamespaceMemberMoveCompletionReferencesAsync(MirrorPulseNamespaceMemberMoveCompletion completion,
        SqliteTransaction transaction, CancellationToken token)
    {
        var observation = await ReadNamespaceMoveFactCoreAsync<MirrorPulseNamespaceMemberMoveObservation>(
            completion.OperationId, true, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The native move observation is missing.");
        var projection = await ReadNamespaceMoveFactCoreAsync<MirrorPulseNamespaceMemberMoveProjection>(
            completion.OperationId, false, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The official move projection is missing.");
        if (completion.ItemId != observation.ItemId || completion.RemoteId != observation.RemoteId ||
            completion.RelativePath != observation.RelativePath || completion.IsDirectory != observation.IsDirectory ||
            completion.OfficialRemoteRevision != projection.ProjectedRemoteRevision ||
            completion.VerifiedAt <= projection.ObservedAt ||
            !SameMoveVerification(completion.Child, observation.Child) || !SameMoveVerification(completion.Parent, observation.Parent) ||
            completion.Child.ObservedAt <= observation.Child.ObservedAt || completion.Parent.ObservedAt <= observation.Parent.ObservedAt ||
            completion.VerifiedAt < completion.Child.ObservedAt || completion.VerifiedAt < completion.Parent.ObservedAt)
            throw new InvalidOperationException("Move completion requires fresh binding and protection observations for the admitted target.");
    }

    private static void ValidateNamespaceMemberMoveCompletion(MirrorPulseNamespaceMemberMoveCompletion completion)
    {
        ValidateNamespaceMoveFactIdentity(completion.Version, completion.OperationId, completion.ItemId,
            completion.RemoteId, completion.OfficialRemoteRevision, completion.RelativePath);
        ArgumentNullException.ThrowIfNull(completion.Child);
        ArgumentNullException.ThrowIfNull(completion.Parent);
        ValidatePermissionBinding(completion.Child.LocalObject);
        ValidatePermissionBinding(completion.Parent.LocalObject);
        ValidatePermissionSid(completion.Child.OwnerSid, false);
        ValidatePermissionSid(completion.Parent.OwnerSid, false);
        ValidatePermissionDacl(completion.Child.Dacl);
        ValidatePermissionDacl(completion.Parent.Dacl);
        if (completion.VerifiedAt == default || completion.Child.ObservedAt == default || completion.Parent.ObservedAt == default ||
            !completion.IsDirectory && completion.RelativePath.EndsWith('/'))
            throw new ArgumentException("A move completion requires fresh bounded observations.", nameof(completion));
    }

    private static bool SameMoveVerification(MirrorPulseNamespacePermissionVerification current,
        MirrorPulseNamespacePermissionVerification retained) =>
        current.LocalObject == retained.LocalObject && current.OwnerSid == retained.OwnerSid && current.Dacl == retained.Dacl;
}
