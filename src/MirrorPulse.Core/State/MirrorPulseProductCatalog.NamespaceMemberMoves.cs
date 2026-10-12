using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>A retained native parent and protection epoch, with a before-move child path.</summary>
public sealed record MirrorPulseNamespaceMoveEndpoint(Guid ParentMemberId, MirrorPulseLocalFileBinding ParentLocalObject,
    Guid ParentProtectionId, string RelativePath);

/// <summary>Product admission retained before a controlled member's first relocation.</summary>
/// <remarks>
/// Paths are operation snapshots, not a second CfSharp item graph. The source name remains owned
/// and the target is reserved. Preparation neither moves an object nor authorizes native execution.
/// The Host quiesces earlier work, retains its shared actor and native guards, and verifies fresh
/// objects and public move evidence. DirectoryMoveEvidence is an opaque public CfSharp encoding.
/// Managed-root Label changes and cross-root copy/delete use their separate product lifecycles.
/// </remarks>
public sealed record MirrorPulseNamespaceMemberMoveIntent(int Version, Guid OperationId, Guid MemberId,
    Guid ProtectionId, RootId RootId, MirrorPulseLocalFileBinding LocalObject, Guid ItemId, string RemoteId,
    string OwnerSid, string RoleSid, MirrorPulseNamespaceMoveEndpoint Source, MirrorPulseNamespaceMoveEndpoint Target,
    DateTimeOffset PreparedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DirectoryMoveEvidence { get; init; }
}

/// <summary>A durable before-native start; it is not evidence that the move occurred.</summary>
public sealed record MirrorPulseNamespaceMemberMoveStart(int Version, Guid OperationId, DateTimeOffset StartedAt);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseNamespaceMemberMoveIntent> PrepareNamespaceMemberMoveAsync(
        MirrorPulseNamespaceMemberMoveIntent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ValidateNamespaceMemberMove(intent);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var replay = await ReadNamespaceMemberMoveCoreAsync(intent.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay != intent) throw new InvalidOperationException("A retained move intent is immutable.");
                transaction.Commit();
                return replay;
            }
            await ValidateNamespaceMemberMoveReferencesAsync(intent, current: true, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceMoveRootUnfencedAsync(intent.RootId, null, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceMoveTargetAvailableAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(intent, TopologyJsonOptions);
            if (payload.Length > NamespaceMemberMovePayloadLimit) throw new ArgumentException("The move evidence must be bounded.", nameof(intent));
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO namespace_member_moves(operation_id,member_id,root_id,source_parent_id,target_parent_id,target_name_key,payload,fingerprint)
                VALUES($id,$member,$root,$source,$target,$name,$payload,$fingerprint);
                INSERT INTO namespace_member_move_pending(root_id,member_id,operation_id) VALUES($root,$member,$id);
                INSERT INTO namespace_member_move_reservations(operation_id,parent_member_id,child_name_key) VALUES($id,$target,$name);
                """;
            AddNamespaceMemberMoveParameters(insert, intent);
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return intent;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads historical product intent without refreshing native authorization.</summary>
    public async Task<MirrorPulseNamespaceMemberMoveIntent?> ReadNamespaceMemberMoveAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A move ID is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadNamespaceMemberMoveCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads the unresolved move that fences new product namespace admission in this root.</summary>
    public async Task<MirrorPulseNamespaceMemberMoveIntent?> ReadPendingNamespaceMemberMoveAsync(RootId rootId,
        CancellationToken cancellationToken = default)
    {
        if (rootId.Value == Guid.Empty) throw new ArgumentException("A stable root is required.", nameof(rootId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadPendingNamespaceMemberMoveCoreAsync(rootId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Rechecks current product admission; the caller still performs fresh native checks.</summary>
    public async Task<MirrorPulseNamespaceMemberMoveIntent> ValidateNamespaceMemberMoveExecutionAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A move ID is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var intent = await RequireNamespaceMemberMoveExecutionCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return intent;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceMemberMoveStart> RecordNamespaceMemberMoveStartAsync(
        MirrorPulseNamespaceMemberMoveStart start, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (start.Version != 1 || start.OperationId == Guid.Empty || start.StartedAt == default)
            throw new ArgumentException("A versioned before-native start is required.", nameof(start));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var intent = await ReadNamespaceMemberMoveCoreAsync(start.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The move intent is missing.");
            if (start.StartedAt < intent.PreparedAt) throw new InvalidOperationException("Move execution cannot precede admission.");
            var replay = await ReadNamespaceMemberMoveStartCoreAsync(start.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay != start) throw new InvalidOperationException("The original move start is immutable.");
                transaction.Commit();
                return replay;
            }
            _ = await RequireNamespaceMemberMoveExecutionCoreAsync(start.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(start, TopologyJsonOptions);
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO namespace_member_move_starts(operation_id,payload,fingerprint) VALUES($id,$payload,$fingerprint);";
            insert.Parameters.AddWithValue("$id", start.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return start;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceMemberMoveStart?> ReadNamespaceMemberMoveStartAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A move ID is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadNamespaceMemberMoveStartCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }
}
