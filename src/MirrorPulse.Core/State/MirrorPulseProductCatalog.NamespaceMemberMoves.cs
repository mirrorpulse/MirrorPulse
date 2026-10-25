using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>A retained native parent and protection epoch, with a before-move child path.</summary>
public sealed record MirrorPulseNamespaceMoveEndpoint(Guid ParentMemberId, MirrorPulseLocalFileBinding ParentLocalObject,
    Guid ParentProtectionId, string RelativePath)
{
    /// <summary>The completed placement of this parent when the endpoint was admitted.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ParentPositionOperationId { get; init; }
}

/// <summary>The immutable remote batch entry that owns a move; this is not evidence of native success.</summary>
public sealed record MirrorPulseNamespaceRemoteMoveReference(int Version, InstanceId InstanceId,
    string BatchId, string BatchFingerprint, int BatchEntryCount, int Ordinal, string ChangeId,
    string? PreviousRemoteRevision, string? RemoteRevision, string? OriginalRemoteRevision);

/// <summary>Product admission retained before a controlled member relocation.</summary>
/// <remarks>
/// Paths are operation snapshots, not a second CfSharp item graph. The source name remains owned
/// and the target is reserved. Preparation neither moves an object nor authorizes native execution.
/// The Host quiesces earlier work, retains its shared actor and native guards, and verifies fresh
/// objects and public move evidence. DirectoryMoveEvidence is an opaque public CfSharp encoding.
/// Version 3 links this same operation ID to an original managed-root Label change;
/// only that mode may retain the sync-root anchor as both parents. Cross-root
/// copy/delete uses its separate product lifecycle.
/// </remarks>
public sealed record MirrorPulseNamespaceMemberMoveIntent(int Version, Guid OperationId, Guid MemberId,
    Guid ProtectionId, RootId RootId, MirrorPulseLocalFileBinding LocalObject, Guid ItemId, string RemoteId,
    string OwnerSid, string RoleSid, MirrorPulseNamespaceMoveEndpoint Source, MirrorPulseNamespaceMoveEndpoint Target,
    DateTimeOffset PreparedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DirectoryMoveEvidence { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? PreviousMoveOperationId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? IdentityAcceptanceOperationId { get; init; }

    /// <summary>The source placement when an ancestor move superseded this member's direct move destination.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? SourcePositionOperationId { get; init; }

    /// <summary>Present only on version 2 admissions owned by an original CfSharp remote batch.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MirrorPulseNamespaceRemoteMoveReference? RemoteEntry { get; init; }
}

/// <summary>A durable before-native start; it is not evidence that the move occurred.</summary>
public sealed record MirrorPulseNamespaceMemberMoveStart(int Version, Guid OperationId, DateTimeOffset StartedAt)
{
    /// <summary>The actual official source revision observed before remote dispatch, including an explicit null in version 2.</summary>
    /// <remarks>Version 1 did not retain this fact. A remote request's expected revision is a different value.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginalOfficialRemoteRevision { get; init; }
}

/// <summary>Read-only progress assembled from the retained move intent and durable facts.</summary>
public sealed record MirrorPulseNamespaceMemberMoveStatus(Guid OperationId, Guid MemberId, bool IsDirectory,
    string SourceRelativePath, string TargetRelativePath, string Phase, bool Started,
    bool NativeObserved, bool OfficialProjectionRecorded, bool CurrentNameCommitted);

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
            await RequireNotNamespaceTransferOwnerAsync(intent.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            await ValidateNamespaceMemberMoveReferencesAsync(intent, current: true, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceMoveRootUnfencedAsync(intent.RootId, null, transaction, cancellationToken,
                allowedRootRename: intent.Version == 3 ? intent.OperationId : null).ConfigureAwait(false);
            await RequireNamespaceMoveTargetAvailableAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(intent, TopologyJsonOptions);
            if (payload.Length > NamespaceMemberMovePayloadLimit) throw new ArgumentException("The move evidence must be bounded.", nameof(intent));
            await using SqliteCommand insert = _connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO namespace_member_moves(operation_id,member_id,root_id,source_parent_id,target_parent_id,target_name_key,previous_move_id,payload,fingerprint)
                VALUES($id,$member,$root,$source,$target,$name,$previous,$payload,$fingerprint);
                INSERT INTO namespace_member_move_pending(root_id,member_id,operation_id) VALUES($root,$member,$id);
                INSERT INTO namespace_member_move_reservations(operation_id,parent_member_id,child_name_key) VALUES($id,$target,$name);
                INSERT INTO namespace_member_move_predecessor_claims(previous_move_id,operation_id)
                SELECT $previous,$id WHERE $previous IS NOT NULL;
                """;
            AddNamespaceMemberMoveParameters(insert, intent);
            insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await InsertNamespaceRemoteMoveReferenceAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            if (intent.DirectoryMoveEvidence is not null)
                await SealNamespaceDirectoryMoveCoreAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
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
        ValidateNamespaceMemberMoveStart(start);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            var intent = await ReadNamespaceMemberMoveCoreAsync(start.OperationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The move intent is missing.");
            if (start.StartedAt < intent.PreparedAt) throw new InvalidOperationException("Move execution cannot precede admission.");
            if (start.Version == 2 && intent.RemoteEntry is null)
                throw new InvalidOperationException("Only an original remote move may retain a pre-dispatch official revision.");
            var replay = await ReadNamespaceMemberMoveStartCoreAsync(start.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay != start) throw new InvalidOperationException("The original move start is immutable.");
                transaction.Commit();
                return replay;
            }
            _ = await RequireNamespaceMemberMoveExecutionCoreAsync(start.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(start, TopologyJsonOptions);
            if (payload.Length > NamespaceMemberMoveStartPayloadLimit) throw new ArgumentException("Move start evidence must be bounded.", nameof(start));
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

    private const int NamespaceMemberMoveStartPayloadLimit = 131_072;

    private static void ValidateNamespaceMemberMoveStart(MirrorPulseNamespaceMemberMoveStart start)
    {
        if (start.Version is not (1 or 2) || start.OperationId == Guid.Empty || start.StartedAt == default ||
            start.Version == 1 && start.OriginalOfficialRemoteRevision is not null ||
            start.OriginalOfficialRemoteRevision is { } revision && (revision.Length > 16_384 || revision.Contains('\0')))
            throw new ArgumentException("A versioned before-native start requires its bounded original evidence.", nameof(start));
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

    /// <summary>Reads move progress without refreshing native authorization or changing the fence.</summary>
    public async Task<MirrorPulseNamespaceMemberMoveStatus?> ReadNamespaceMemberMoveStatusAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A move ID is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var intent = await ReadNamespaceMemberMoveCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            if (intent is null) { transaction.Commit(); return null; }
            var member = await ReadNamespaceMemberRowAsync(intent.MemberId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The move member provenance is missing.");
            var start = await ReadNamespaceMemberMoveStartCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            var observation = await ReadNamespaceMoveFactCoreAsync<MirrorPulseNamespaceMemberMoveObservation>(
                operationId, true, transaction, cancellationToken, false).ConfigureAwait(false);
            var projection = await ReadNamespaceMoveFactCoreAsync<MirrorPulseNamespaceMemberMoveProjection>(
                operationId, false, transaction, cancellationToken, false).ConfigureAwait(false);
            var completion = await ReadNamespaceMemberMoveCompletionCoreAsync(operationId, transaction, cancellationToken, false).ConfigureAwait(false);
            var rejected = await ReadNamespaceRemoteMoveRejectionRowAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            string phase = rejected is not null ? "Rejected" : completion is not null ? "Completed" : projection is not null ? "Projected" :
                observation is not null ? "Observed" : start is not null ? "Started" : "Prepared";
            transaction.Commit();
            return new(operationId, member.MemberId, member.IsDirectory, intent.Source.RelativePath,
                intent.Target.RelativePath, phase, start is not null, observation is not null,
                projection is not null, completion is not null);
        }
        finally { _gate.Release(); }
    }
}
