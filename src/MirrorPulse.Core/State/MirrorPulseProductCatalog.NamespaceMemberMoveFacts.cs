using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>A past native observation of the original moved object at its admitted target.</summary>
/// <remarks>It neither transfers a name nor authorizes replay; Host supplies fresh guarded native facts.</remarks>
public sealed record MirrorPulseNamespaceMemberMoveObservation(int Version, Guid OperationId, Guid ItemId,
    string RemoteId, string? NativeRemoteRevision, string RelativePath, bool IsDirectory, bool IsPlaceholder,
    uint LinkCount, MirrorPulseNamespacePermissionVerification Child, MirrorPulseNamespacePermissionVerification Parent);

/// <summary>A separate observation of CfSharp's official item projection after native movement.</summary>
/// <remarks>This is local projection evidence, not source acceptance, native authorization or journal acknowledgement.</remarks>
public sealed record MirrorPulseNamespaceMemberMoveProjection(int Version, Guid OperationId, Guid ItemId,
    string RemoteId, string? ProjectedRemoteRevision, string RelativePath, DateTimeOffset ObservedAt);

public sealed partial class MirrorPulseProductCatalog
{
    private const int NamespaceMemberMoveFactLimit = 524_288;

    public Task<MirrorPulseNamespaceMemberMoveObservation> RecordNamespaceMemberMoveObservationAsync(
        MirrorPulseNamespaceMemberMoveObservation observation, CancellationToken cancellationToken = default) =>
        RecordNamespaceMoveFactAsync(observation, observation?.OperationId ?? Guid.Empty, true, cancellationToken);

    public Task<MirrorPulseNamespaceMemberMoveProjection> RecordNamespaceMemberMoveProjectionAsync(
        MirrorPulseNamespaceMemberMoveProjection projection, CancellationToken cancellationToken = default) =>
        RecordNamespaceMoveFactAsync(projection, projection?.OperationId ?? Guid.Empty, false, cancellationToken);

    public Task<MirrorPulseNamespaceMemberMoveObservation?> ReadNamespaceMemberMoveObservationAsync(Guid operationId,
        CancellationToken cancellationToken = default) => ReadNamespaceMoveFactAsync<MirrorPulseNamespaceMemberMoveObservation>(operationId, true, cancellationToken);

    public Task<MirrorPulseNamespaceMemberMoveProjection?> ReadNamespaceMemberMoveProjectionAsync(Guid operationId,
        CancellationToken cancellationToken = default) => ReadNamespaceMoveFactAsync<MirrorPulseNamespaceMemberMoveProjection>(operationId, false, cancellationToken);

    private async Task<T> RecordNamespaceMoveFactAsync<T>(T fact, Guid operationId, bool native, CancellationToken token) where T : class
    {
        ArgumentNullException.ThrowIfNull(fact);
        ValidateNamespaceMoveFact(fact);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var replay = await ReadNamespaceMoveFactCoreAsync<T>(operationId, native, transaction, token).ConfigureAwait(false);
            if (replay is not null)
            {
                if (!EqualityComparer<T>.Default.Equals(fact, replay)) throw new InvalidOperationException("A retained move fact is immutable.");
                transaction.Commit(); return replay;
            }
            _ = await RequireNamespaceMemberMoveExecutionCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            await ValidateNamespaceMoveFactReferencesAsync(fact, operationId, transaction, token).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(fact, TopologyJsonOptions);
            if (payload.Length > NamespaceMemberMoveFactLimit) throw new ArgumentException("Move facts must be bounded.", nameof(fact));
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = native
                ? "INSERT INTO namespace_member_move_observations(operation_id,payload,fingerprint) VALUES($id,$payload,$hash);"
                : "INSERT INTO namespace_member_move_projections(operation_id,payload,fingerprint) VALUES($id,$payload,$hash);";
            insert.Parameters.AddWithValue("$id", operationId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            transaction.Commit(); return fact;
        }
        finally { _gate.Release(); }
    }

    private async Task<T?> ReadNamespaceMoveFactAsync<T>(Guid operationId, bool native, CancellationToken token) where T : class
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A move ID is required.", nameof(operationId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var fact = await ReadNamespaceMoveFactCoreAsync<T>(operationId, native, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return fact;
        }
        finally { _gate.Release(); }
    }

    private async Task<T?> ReadNamespaceMoveFactCoreAsync<T>(Guid operationId, bool native,
        SqliteTransaction transaction, CancellationToken token, bool validateRelocationChain = true) where T : class
    {
        T fact;
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = native
                ? "SELECT operation_id,payload,fingerprint FROM namespace_member_move_observations WHERE operation_id=$id;"
                : "SELECT operation_id,payload,fingerprint FROM namespace_member_move_projections WHERE operation_id=$id;";
            query.Parameters.AddWithValue("$id", operationId.ToString("D"));
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            fact = ReadNamespaceMovePayload<T>((byte[])reader[1], (byte[])reader[2], NamespaceMemberMoveFactLimit, ValidateNamespaceMoveFact);
            if (reader.GetString(0) != operationId.ToString("D")) throw new InvalidDataException("The move fact index is invalid.");
        }
        try { await ValidateNamespaceMoveFactReferencesAsync(fact, operationId, transaction, token, validateRelocationChain).ConfigureAwait(false); }
        catch (Exception error) when (error is InvalidOperationException or FileNotFoundException)
        { throw new InvalidDataException("The move fact lost its retained intent, start or verified object.", error); }
        return fact;
    }

    private static void ValidateNamespaceMoveFact<T>(T fact) where T : class
    {
        switch (fact)
        {
            case MirrorPulseNamespaceMemberMoveObservation native:
                ArgumentNullException.ThrowIfNull(native.Child); ArgumentNullException.ThrowIfNull(native.Parent);
                ValidateNamespaceMoveFactIdentity(native.Version, native.OperationId, native.ItemId, native.RemoteId,
                    native.NativeRemoteRevision, native.RelativePath);
                foreach (var verification in new[] { native.Child, native.Parent })
                {
                    ValidatePermissionBinding(verification.LocalObject); ValidatePermissionSid(verification.OwnerSid, false);
                    ValidatePermissionDacl(verification.Dacl);
                    if (verification.ObservedAt == default) throw new ArgumentException("A move verification time is required.");
                }
                if (!native.IsPlaceholder || native.LinkCount != 1) throw new ArgumentException("A moved member requires one placeholder binding.");
                break;
            case MirrorPulseNamespaceMemberMoveProjection projection:
                ValidateNamespaceMoveFactIdentity(projection.Version, projection.OperationId, projection.ItemId, projection.RemoteId,
                    projection.ProjectedRemoteRevision, projection.RelativePath);
                if (projection.ObservedAt == default) throw new ArgumentException("A projection observation time is required.");
                break;
            default: throw new ArgumentException("One supported move fact is required.", nameof(fact));
        }
    }

    private static void ValidateNamespaceMoveFactIdentity(int version, Guid operationId, Guid itemId,
        string remoteId, string? revision, string path)
    {
        ValidatePermissionRelativePath(path);
        if (version is not (1 or 2) || operationId == Guid.Empty || itemId == Guid.Empty || string.IsNullOrWhiteSpace(remoteId) ||
            remoteId.Length > 16_384 || revision is { Length: > 16_384 } ||
            (version == 2 ? path.Length == 0 || path.Contains('/') : path.LastIndexOf('/') <= 0))
            throw new ArgumentException("Move facts require complete identities, a child path and bounded opaque tokens.");
    }
}
