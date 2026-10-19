using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

public enum MirrorPulseNamespaceRetirementPhase { Prepared, NativeDeleted, Completed, RecoveryRequired, RemoteRejected }

/// <summary>Immutable admission to remove one registered non-root member through the Host.</summary>
public sealed record MirrorPulseNamespaceRetirementIntent(int Version, Guid OperationId, Guid MemberId,
    RootId RootId, MirrorPulseLocalFileBinding LocalObject, string RelativePath, bool IsDirectory,
    string OwnerSid, string RoleSid, DateTimeOffset PreparedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? BatchOperationId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MirrorPulseNamespaceRemoteRetirementReference? RemoteEntry { get; init; }
}

/// <summary>Durable native and membership settlement facts for a controlled deletion.</summary>
public sealed record MirrorPulseNamespaceRetirement(Guid OperationId, MirrorPulseNamespaceRetirementIntent Intent,
    MirrorPulseNamespaceRetirementPhase Phase, DateTimeOffset? NativeDeletedAt = null,
    DateTimeOffset? CompletedAt = null, string? RecoveryReason = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? StartedAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? StartedItemId { get; init; }
}

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reserves a non-root member for a Host controlled, leaf-first deletion.</summary>
    public async Task<MirrorPulseNamespaceRetirement> PrepareNamespaceRetirementAsync(
        MirrorPulseNamespaceRetirementIntent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent); ValidateNamespaceRetirementIntent(intent);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using SqliteTransaction transaction = _connection.BeginTransaction();
            var replay = await ReadNamespaceRetirementCoreAsync(intent.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            { if (replay.Intent != intent) throw new InvalidOperationException("A retirement operation is immutable."); transaction.Commit(); return replay; }
            var member = await ReadNamespaceMemberCoreAsync(intent.MemberId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The namespace member is not registered.");
            if (member.RootId != intent.RootId || member.LocalObject != intent.LocalObject || member.IsDirectory != intent.IsDirectory || member.OwnerSid != intent.OwnerSid)
                throw new InvalidOperationException("Retirement must retain the registered member binding and owner.");
            await RequireNamespaceRetirableMemberAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceMemberNameAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            if (location.RelativePath != intent.RelativePath) throw new InvalidOperationException("Retirement requires the member's latest committed location.");
            await RequireNamespaceRetirementBatchEntryAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceMoveRootUnfencedAsync(intent.RootId, null, transaction, cancellationToken,
                allowedBatch: intent.BatchOperationId).ConfigureAwait(false);
            await RequireNoRegisteredDescendantsAsync(intent.MemberId, transaction, cancellationToken).ConfigureAwait(false);
            if (intent.RemoteEntry is not null && (await ReadNamespaceRootFencesCoreAsync(true, transaction, cancellationToken).ConfigureAwait(false)).Contains(intent.RootId))
                throw new InvalidOperationException("An unresolved namespace owner prevents original remote retirement admission.");
            var retirement = new MirrorPulseNamespaceRetirement(intent.OperationId, intent, MirrorPulseNamespaceRetirementPhase.Prepared);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(retirement, TopologyJsonOptions);
            if (payload.Length > 131_072) throw new ArgumentException("The retirement admission must be bounded.", nameof(intent));
            await using SqliteCommand insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO namespace_member_retirements(operation_id,member_id,payload,fingerprint,phase) VALUES($id,$member,$payload,$fingerprint,$phase);";
            insert.Parameters.AddWithValue("$id", intent.OperationId.ToString("D")); insert.Parameters.AddWithValue("$member", intent.MemberId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload)); insert.Parameters.AddWithValue("$phase", 0);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await InsertRemoteRetirementReferenceAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return retirement;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceRetirement?> ReadNamespaceRetirementAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A retirement operation is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadNamespaceRetirementCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Rechecks the admitted current member and root fence before a native deletion.</summary>
    public async Task<MirrorPulseNamespaceRetirement> ValidateNamespaceRetirementExecutionAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A retirement operation is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var retirement = await ReadNamespaceRetirementCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retirement operation is missing.");
            await RequireNotRemoteDeleteResolutionMemberAsync(retirement.Intent, transaction, cancellationToken).ConfigureAwait(false);
            if (retirement.Phase is MirrorPulseNamespaceRetirementPhase.Completed or MirrorPulseNamespaceRetirementPhase.RemoteRejected) { transaction.Commit(); return retirement; }
            if (retirement.Phase == MirrorPulseNamespaceRetirementPhase.RecoveryRequired)
                throw new InvalidOperationException("The retirement requires explicit recovery.");
            var member = await ReadNamespaceMemberCoreAsync(retirement.Intent.MemberId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The retirement member provenance is missing.");
            var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceMemberNameAsync(member, transaction, cancellationToken).ConfigureAwait(false);
            if (location.RelativePath != retirement.Intent.RelativePath || member.LocalObject != retirement.Intent.LocalObject ||
                member.RootId != retirement.Intent.RootId || member.OwnerSid != retirement.Intent.OwnerSid || member.IsDirectory != retirement.Intent.IsDirectory)
                throw new InvalidDataException("The retirement no longer identifies its committed protected member.");
            await RequireNamespaceRetirementBatchEntryAsync(retirement.Intent, transaction, cancellationToken).ConfigureAwait(false);
            await RequireNamespaceMoveRootUnfencedAsync(retirement.Intent.RootId, null, transaction, cancellationToken,
                operationId, retirement.Intent.BatchOperationId).ConfigureAwait(false);
            await RequireNoRegisteredDescendantsAsync(member.MemberId, transaction, cancellationToken).ConfigureAwait(false);
            await ValidateRemoteRetirementCurrentAsync(retirement.Intent, transaction, cancellationToken).ConfigureAwait(false);
            if (retirement.Intent.RemoteEntry is not null && (await ReadNamespaceRootFencesCoreAsync(true, transaction, cancellationToken,
                admittedRemoteRetirementId: operationId).ConfigureAwait(false)).Contains(retirement.Intent.RootId))
                throw new InvalidOperationException("Another original namespace owner prevents remote retirement execution.");
            transaction.Commit(); return retirement;
        }
        finally { _gate.Release(); }
    }

    private async Task<List<MirrorPulseNamespaceRetirement>> ReadPendingNamespaceRetirementsCoreAsync(
        SqliteTransaction transaction, CancellationToken token)
    {
        var operations = new List<Guid>();
        await using (SqliteCommand query = _connection.CreateCommand())
        {
            query.Transaction = transaction; query.CommandText = "SELECT operation_id FROM namespace_member_retirements WHERE phase NOT IN (2,4) ORDER BY operation_id;";
            await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) operations.Add(ParseNamespaceMemberId(reader.GetString(0)));
        }
        var pending = new List<MirrorPulseNamespaceRetirement>(operations.Count);
        foreach (Guid operation in operations)
            pending.Add(await ReadNamespaceRetirementCoreAsync(operation, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The pending retirement lost its receipt."));
        return pending;
    }

    public Task<MirrorPulseNamespaceRetirement> RecordNamespaceRetirementNativeDeletedAsync(Guid operationId,
        DateTimeOffset deletedAt, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || deletedAt == default) throw new ArgumentException("The retirement native receipt is incomplete.");
        return TransitionNamespaceRetirementAsync(operationId, MirrorPulseNamespaceRetirementPhase.NativeDeleted, deletedAt, cancellationToken);
    }

    /// <summary>Retains the before-native boundary after the Host verifies the original object.</summary>
    public async Task<MirrorPulseNamespaceRetirement> RecordNamespaceRetirementStartAsync(Guid operationId,
        DateTimeOffset startedAt, Guid? officialItemId = null, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || startedAt == default || officialItemId == Guid.Empty) throw new ArgumentException("The retirement start is incomplete.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using SqliteTransaction transaction = _connection.BeginTransaction();
            var current = await ReadNamespaceRetirementCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retirement operation is missing.");
            await RequireNotRemoteDeleteResolutionMemberAsync(current.Intent, transaction, cancellationToken).ConfigureAwait(false);
            if (current.Intent.RemoteEntry is { } remote && officialItemId != remote.ItemId)
                throw new InvalidOperationException("An original SDK retirement must retain its exact logical identity before native work.");
            if (current.StartedAt is not null) { transaction.Commit(); return current; }
            if (current.Phase != MirrorPulseNamespaceRetirementPhase.Prepared || startedAt < current.Intent.PreparedAt)
                throw new InvalidOperationException("A native retirement start must follow its original admission.");
            var started = current with { StartedAt = startedAt, StartedItemId = officialItemId };
            await WriteNamespaceRetirementCoreAsync(started, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return started;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceRetirement> TransitionNamespaceRetirementAsync(Guid operationId,
        MirrorPulseNamespaceRetirementPhase phase, DateTimeOffset at, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using SqliteTransaction transaction = _connection.BeginTransaction();
            var current = await ReadNamespaceRetirementCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retirement operation is missing.");
            await RequireNotRemoteDeleteResolutionMemberAsync(current.Intent, transaction, token).ConfigureAwait(false);
            if (current.Intent.RemoteEntry is not null)
                throw new InvalidOperationException("An SDK-owned retirement requires its actual original SDK outcome settlement.");
            if (current.Phase == phase) { transaction.Commit(); return current; }
            if (current.Phase != MirrorPulseNamespaceRetirementPhase.Prepared || phase != MirrorPulseNamespaceRetirementPhase.NativeDeleted)
                throw new InvalidOperationException("The retirement phase cannot advance from its retained state.");
            if (current.StartedAt is null || at < current.StartedAt)
                throw new InvalidOperationException("Native deletion requires its retained before-native start.");
            var next = current with { Phase = phase, NativeDeletedAt = at };
            await WriteNamespaceRetirementCoreAsync(next, transaction, token).ConfigureAwait(false); transaction.Commit(); return next;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceRetirement> CompleteNamespaceRetirementAsync(Guid operationId,
        DateTimeOffset completedAt, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || completedAt == default) throw new ArgumentException("The retirement completion is incomplete.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using SqliteTransaction transaction = _connection.BeginTransaction();
            var current = await ReadNamespaceRetirementCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retirement operation is missing.");
            await RequireNotRemoteDeleteResolutionMemberAsync(current.Intent, transaction, cancellationToken).ConfigureAwait(false);
            if (current.Intent.RemoteEntry is not null)
                throw new InvalidOperationException("An SDK-owned retirement cannot use controlled local completion.");
            if (current.Phase == MirrorPulseNamespaceRetirementPhase.Completed) { transaction.Commit(); return current; }
            if (current.Phase != MirrorPulseNamespaceRetirementPhase.NativeDeleted) throw new InvalidOperationException("Retirement completion requires a native deletion receipt.");
            await RequireNoRegisteredDescendantsAsync(current.Intent.MemberId, transaction, cancellationToken).ConfigureAwait(false);
            await using SqliteCommand remove = _connection.CreateCommand(); remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM namespace_member_names WHERE member_id=$member;"; remove.Parameters.AddWithValue("$member", current.Intent.MemberId.ToString("D"));
            if (await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) throw new InvalidDataException("The retired member lost its owned name.");
            var completed = current with { Phase = MirrorPulseNamespaceRetirementPhase.Completed, CompletedAt = completedAt };
            await WriteNamespaceRetirementCoreAsync(completed, transaction, cancellationToken).ConfigureAwait(false); transaction.Commit(); return completed;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceRetirement?> ReadNamespaceRetirementCoreAsync(Guid operationId, SqliteTransaction? transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand(); query.Transaction = transaction; query.CommandText = "SELECT operation_id,payload,fingerprint,phase,member_id FROM namespace_member_retirements WHERE operation_id=$id;"; query.Parameters.AddWithValue("$id", operationId.ToString("D"));
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false); if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        byte[] payload = (byte[])reader[1], fingerprint = (byte[])reader[2];
        if (payload.Length > 131_072 || fingerprint.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint)) throw new InvalidDataException("The namespace retirement fingerprint is invalid.");
        var retirement = JsonSerializer.Deserialize<MirrorPulseNamespaceRetirement>(payload, TopologyJsonOptions) ?? throw new InvalidDataException("The namespace retirement is empty.");
        ValidateNamespaceRetirementIntent(retirement.Intent);
        if (retirement.StartedAt < retirement.Intent.PreparedAt || retirement.NativeDeletedAt < retirement.StartedAt ||
            retirement.CompletedAt < retirement.NativeDeletedAt)
            throw new InvalidDataException("The retirement receipt chronology is inconsistent.");
        if (retirement.OperationId != operationId || retirement.Intent.OperationId != operationId || reader.GetString(0) != operationId.ToString("D") ||
            reader.GetString(4) != retirement.Intent.MemberId.ToString("D") || reader.GetInt32(3) != (int)retirement.Phase || !Enum.IsDefined(retirement.Phase) ||
            !JsonSerializer.SerializeToUtf8Bytes(retirement, TopologyJsonOptions).AsSpan().SequenceEqual(payload)) throw new InvalidDataException("The namespace retirement index is inconsistent.");
        await reader.DisposeAsync().ConfigureAwait(false);
        await ValidateRemoteDeleteResolutionMemberAsync(retirement, transaction, token).ConfigureAwait(false);
        await ValidateRemoteRetirementIndexAsync(retirement.Intent, transaction, token).ConfigureAwait(false);
        if (retirement.Intent.RemoteEntry is not null)
        {
            var outcome = await ReadRemoteRetirementOutcomeCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            bool terminal = retirement.Phase is MirrorPulseNamespaceRetirementPhase.Completed or MirrorPulseNamespaceRetirementPhase.RemoteRejected;
            if (terminal != (outcome is not null)) throw new InvalidDataException("The original SDK retirement lost its correlated terminal outcome.");
            if (outcome is not null)
            {
                ValidateRemoteRetirementOutcome(retirement, outcome);
                if (retirement.Phase != (outcome.ConflictId is null ? MirrorPulseNamespaceRetirementPhase.Completed : MirrorPulseNamespaceRetirementPhase.RemoteRejected) ||
                    retirement.CompletedAt != outcome.VerifiedAt || retirement.NativeDeletedAt != (outcome.ConflictId is null ? outcome.VerifiedAt : null))
                    throw new InvalidDataException("The SDK retirement and its actual terminal outcome disagree.");
            }
        }
        return retirement;
    }

    private async Task WriteNamespaceRetirementCoreAsync(MirrorPulseNamespaceRetirement retirement, SqliteTransaction transaction, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(retirement, TopologyJsonOptions);
        await using SqliteCommand update = _connection.CreateCommand(); update.Transaction = transaction; update.CommandText = "UPDATE namespace_member_retirements SET payload=$payload,fingerprint=$fingerprint,phase=$phase WHERE operation_id=$id;";
        update.Parameters.AddWithValue("$id", retirement.OperationId.ToString("D")); update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload)); update.Parameters.AddWithValue("$phase", (int)retirement.Phase);
        if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidDataException("The retirement receipt could not be retained.");
    }

    private async Task RequireNoRegisteredDescendantsAsync(Guid memberId, SqliteTransaction transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand(); query.Transaction = transaction; query.CommandText = "WITH descendants(member_id) AS (SELECT member_id FROM namespace_member_names WHERE parent_member_id=$member UNION ALL SELECT n.member_id FROM namespace_member_names n JOIN descendants d ON n.parent_member_id=d.member_id) SELECT member_id FROM descendants LIMIT 1;"; query.Parameters.AddWithValue("$member", memberId.ToString("D"));
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null) throw new InvalidOperationException("A directory with registered descendants must be retired leaf-first.");
    }

    private async Task RequireNamespaceRetirableMemberAsync(MirrorPulseNamespaceMemberRegistration member,
        SqliteTransaction transaction, CancellationToken token)
    {
        var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, token).ConfigureAwait(false);
        var parent = location.ParentMemberId is { } parentId
            ? await ReadNamespaceMemberCoreAsync(parentId, transaction, token).ConfigureAwait(false) : null;
        if (member.RootId is null || parent?.RootId != member.RootId)
            throw new InvalidOperationException("A managed root or sync-root anchor cannot be retired.");
    }

    private async Task<bool> ReadCompletedNamespaceRetirementForMemberCoreAsync(Guid memberId,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT phase FROM namespace_member_retirements WHERE member_id=$member ORDER BY rowid DESC LIMIT 1;";
        query.Parameters.AddWithValue("$member", memberId.ToString("D"));
        object? value = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        bool leaf = value is long phase && phase == (long)MirrorPulseNamespaceRetirementPhase.Completed;
        var tree = await ReadRemovedRemoteTreeMemberCoreAsync(memberId, transaction, token).ConfigureAwait(false);
        if (leaf && tree is not null) throw new InvalidDataException("A retired member has competing original leaf and tree owners.");
        return leaf || tree is not null;
    }

    private static void ValidateNamespaceRetirementIntent(MirrorPulseNamespaceRetirementIntent intent)
    {
        ValidatePermissionBinding(intent.LocalObject); ValidatePermissionRelativePath(intent.RelativePath); ValidatePermissionSid(intent.OwnerSid, role: false); ValidatePermissionSid(intent.RoleSid, role: true);
        if (!(intent.Version == 1 && intent.RemoteEntry is null || intent.Version == 2 && intent.RemoteEntry is not null && intent.BatchOperationId is null) ||
            intent.OperationId == Guid.Empty || intent.MemberId == Guid.Empty || intent.RootId.Value == Guid.Empty || intent.PreparedAt == default || intent.RelativePath.Length == 0 || intent.RelativePath.Contains("..", StringComparison.Ordinal) || intent.BatchOperationId == Guid.Empty) throw new ArgumentException("The namespace retirement intent is incomplete.", nameof(intent));
        if (intent.RemoteEntry is not null) ValidateRemoteRetirementReference(intent);
    }
}
