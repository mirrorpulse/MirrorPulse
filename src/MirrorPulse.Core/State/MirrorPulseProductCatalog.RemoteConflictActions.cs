using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Conflicts;

namespace MirrorPulse.Core.State;

/// <summary>Retains the exact product command and conflict epoch before a public SDK decision.</summary>
public sealed record MirrorPulseRemoteConflictActionIntent(int Version, Guid CommandId,
    MirrorPulseConflictRecord Conflict, MirrorPulseConflictAction Action, MirrorPulseConflictPreservedSide? PreservedSide,
    string PackageVersion, string PackageSource);

public enum MirrorPulseRemoteConflictObservedStatus { Applied, AlreadyApplied, Dismissed, AlreadyDismissed }

/// <summary>The observed public SDK return for one exact command, committed with its terminal product state.</summary>
/// <remarks>This historical receipt supplies no missing SDK result, current native proof, or namespace-member settlement.</remarks>
public sealed record MirrorPulseRemoteConflictActionReceipt(MirrorPulseRemoteConflictActionIntent Intent,
    MirrorPulseRemoteConflictObservedStatus Status, string? PreservedPath, DateTimeOffset ObservedAt);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseRemoteConflictActionReceipt?> PrepareRemoteConflictActionAsync(
        MirrorPulseRemoteConflictActionIntent intent, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(intent); ValidateRemoteConflictActionIntent(intent);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            await RequireRemoteDeleteDecisionOwnershipAsync(intent, transaction, token).ConfigureAwait(false);
            var existing = await ReadRemoteConflictActionCoreAsync(intent.CommandId, transaction, token).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.Value.Intent != intent) throw new InvalidDataException("A remote decision command cannot substitute another conflict epoch, choice or publication.");
                if (existing.Value.Receipt is { } receipt) { transaction.Commit(); return receipt; }
            }
            string? state = await ReadRemoteActionCommandStateAsync(intent, transaction, token).ConfigureAwait(false);
            if (state is not null and not ("pending" or "failed"))
                throw new InvalidDataException("A historical command without an owned observed-result receipt cannot be replayed as a completed decision.");
            await using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO user_commands(command_id,action,target_id,state) VALUES($id,$action,$target,'pending')
                    ON CONFLICT(command_id) DO UPDATE SET state='pending';
                    """;
                AddRemoteActionCommandParameters(command, intent);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            if (existing is null)
            {
                byte[] payload = RemoteActionPayload(intent);
                await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO remote_conflict_actions(command_id,payload,fingerprint) VALUES($id,$payload,$hash);";
                insert.Parameters.AddWithValue("$id", intent.CommandId.ToString("D"));
                insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            transaction.Commit(); return null;
        }
        finally { _gate.Release(); }
    }

    public async Task RecordRemoteConflictActionReceiptAsync(MirrorPulseRemoteConflictActionReceipt receipt,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(receipt); ValidateRemoteConflictActionReceipt(receipt);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var existing = await ReadRemoteConflictActionCoreAsync(receipt.Intent.CommandId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("An observed SDK result requires its original product decision admission.");
            if (existing.Intent != receipt.Intent) throw new InvalidDataException("The observed SDK return belongs to another retained command or conflict epoch.");
            var deletion = await ReadRemoteDeleteResolutionCoreAsync(receipt.Intent.CommandId, transaction, token).ConfigureAwait(false);
            var deletionBatch = await ReadNamespaceRetirementBatchCoreAsync(receipt.Intent.CommandId, transaction, token).ConfigureAwait(false);
            if (deletion is not null && deletionBatch is null)
                throw new InvalidDataException("The explicit delete command lost its original protected tree owner.");
            if (deletion is not null && (deletion.CompletedAt is null || deletion.Plan.Decision != receipt.Intent))
                throw new InvalidOperationException("The explicit delete command still has unsettled protected members.");
            if (deletion is not null && (receipt.ObservedAt < deletion.CompletedAt || receipt.Status !=
                (deletion.Outcome!.Status == MirrorPulseRemoteDeleteResolutionStatus.Applied
                    ? MirrorPulseRemoteConflictObservedStatus.Applied : MirrorPulseRemoteConflictObservedStatus.AlreadyApplied)))
                throw new InvalidOperationException("The completed delete command must retain its actual observed or reconciled SDK disposition.");
            if (existing.Receipt is { } retained)
            {
                if (retained != receipt) throw new InvalidOperationException("An observed remote decision result is immutable.");
                transaction.Commit(); return;
            }
            byte[] payload = RemoteActionPayload(receipt);
            await using (var update = _connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE remote_conflict_actions SET outcome_payload=$payload,outcome_fingerprint=$hash WHERE command_id=$id AND outcome_payload IS NULL;";
                update.Parameters.AddWithValue("$id", receipt.Intent.CommandId.ToString("D"));
                update.Parameters.AddWithValue("$payload", payload); update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidDataException("The original decision lost its pending receipt slot.");
            }
            await using (var command = _connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE user_commands SET state='resolved' WHERE command_id=$id AND action=$action AND target_id=$target AND state IN ('pending','failed');";
                AddRemoteActionCommandParameters(command, receipt.Intent);
                if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidDataException("The observed SDK return lost its original pending command.");
            }
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseRemoteConflictActionReceipt?> ReadRemoteConflictActionReceiptAsync(Guid commandId,
        CancellationToken token = default)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("The original command is required.", nameof(commandId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadRemoteConflictActionCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result?.Receipt;
        }
        finally { _gate.Release(); }
    }

    private async Task<(MirrorPulseRemoteConflictActionIntent Intent, MirrorPulseRemoteConflictActionReceipt? Receipt)?>
        ReadRemoteConflictActionCoreAsync(Guid commandId, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload,fingerprint,outcome_payload,outcome_fingerprint FROM remote_conflict_actions WHERE command_id=$id;";
        query.Parameters.AddWithValue("$id", commandId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var intent = DecodeRemoteActionPayload<MirrorPulseRemoteConflictActionIntent>((byte[])reader[0], (byte[])reader[1]);
        ValidateRemoteConflictActionIntent(intent);
        if (intent.CommandId != commandId || reader.IsDBNull(2) != reader.IsDBNull(3)) throw new InvalidDataException("The remote decision index or result pair is inconsistent.");
        var receipt = reader.IsDBNull(2) ? null : DecodeRemoteActionPayload<MirrorPulseRemoteConflictActionReceipt>((byte[])reader[2], (byte[])reader[3]);
        if (receipt is not null)
        {
            ValidateRemoteConflictActionReceipt(receipt);
            if (receipt.Intent != intent) throw new InvalidDataException("The remote result lost its original command intent.");
        }
        await reader.DisposeAsync().ConfigureAwait(false);
        string? state = await ReadRemoteActionCommandStateAsync(intent, transaction, token).ConfigureAwait(false);
        if (receipt is null ? state is not ("pending" or "failed") : state != "resolved")
            throw new InvalidDataException("The remote result and its correlated user command disagree.");
        return (intent, receipt);
    }

    private async Task<string?> ReadRemoteActionCommandStateAsync(MirrorPulseRemoteConflictActionIntent intent,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT action,target_id,state FROM user_commands WHERE command_id=$id;";
        query.Parameters.AddWithValue("$id", intent.CommandId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        if (reader.GetString(0) != RemoteActionName(intent) || reader.GetString(1) != intent.Conflict.ConflictId.ToString("D"))
            throw new InvalidDataException("The remote decision lost its exact user-command action or target.");
        return reader.GetString(2);
    }

    private static void ValidateRemoteConflictActionIntent(MirrorPulseRemoteConflictActionIntent intent)
    {
        if (intent.Version != 1 || intent.CommandId == Guid.Empty || intent.Conflict is not { } conflict ||
            conflict.InstanceId.Value == Guid.Empty || conflict.Source != MirrorPulseConflictSource.CfSharpRemote || !conflict.IsPending ||
            !Enum.IsDefined(conflict.Reason) || !Enum.IsDefined(conflict.VersionComparison) || conflict.DetectedAt == default ||
            intent.Action is not (MirrorPulseConflictAction.KeepLocal or MirrorPulseConflictAction.KeepRemote or MirrorPulseConflictAction.KeepBoth) ||
            (intent.Action == MirrorPulseConflictAction.KeepBoth) != (intent.PreservedSide is not null) ||
            intent.PreservedSide is { } side && !Enum.IsDefined(side) ||
            !MirrorPulseRemoteLibraryProvenance.IsSupported(intent.PackageVersion, intent.PackageSource) ||
            new[] { conflict.ChangeId, conflict.RelativePath, conflict.LocalRevision, conflict.RemoteRevision }.Any(value =>
                value is not null && (value.Length > 16_384 || value.Contains('\0'))))
            throw new InvalidDataException("The remote command requires its complete original conflict epoch, explicit choice and SDK publication.");
    }

    private static void ValidateRemoteConflictActionReceipt(MirrorPulseRemoteConflictActionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt.Intent); ValidateRemoteConflictActionIntent(receipt.Intent);
        if (!Enum.IsDefined(receipt.Status) || receipt.ObservedAt == default ||
            (receipt.Intent.Action == MirrorPulseConflictAction.KeepLocal) != (receipt.Status is MirrorPulseRemoteConflictObservedStatus.Dismissed or MirrorPulseRemoteConflictObservedStatus.AlreadyDismissed) ||
            (receipt.Intent.Action == MirrorPulseConflictAction.KeepBoth) != (receipt.PreservedPath is not null) ||
            receipt.PreservedPath is { } path && (string.IsNullOrWhiteSpace(path) || path.Length > 32_768 || path.Contains('\0') || !Path.IsPathFullyQualified(path)))
            throw new InvalidDataException("The observed SDK return does not match its original decision or retained-copy receipt.");
    }

    private static string RemoteActionName(MirrorPulseRemoteConflictActionIntent intent) => intent.Action == MirrorPulseConflictAction.KeepBoth
        ? $"keep-both-{intent.PreservedSide!.Value.ToString().ToLowerInvariant()}" : intent.Action.ToString().ToLowerInvariant();

    private static void AddRemoteActionCommandParameters(SqliteCommand command, MirrorPulseRemoteConflictActionIntent intent)
    {
        command.Parameters.AddWithValue("$id", intent.CommandId.ToString("D"));
        command.Parameters.AddWithValue("$action", RemoteActionName(intent));
        command.Parameters.AddWithValue("$target", intent.Conflict.ConflictId.ToString("D"));
    }

    private static byte[] RemoteActionPayload<T>(T value)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, TopologyJsonOptions);
        if (payload.Length > 131_072) throw new InvalidDataException("The remote decision evidence exceeds its catalog bound.");
        return payload;
    }

    private static T DecodeRemoteActionPayload<T>(byte[] payload, byte[] hash)
    {
        if (payload.Length > 131_072 || hash.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), hash))
            throw new InvalidDataException("The retained remote decision evidence is corrupt.");
        var value = JsonSerializer.Deserialize<T>(payload, TopologyJsonOptions)
            ?? throw new InvalidDataException("The retained remote decision evidence is empty.");
        if (!RemoteActionPayload(value).AsSpan().SequenceEqual(payload)) throw new InvalidDataException("The remote decision evidence is not canonical.");
        return value;
    }

    private static async Task EnsureRemoteConflictActionSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var schema = connection.CreateCommand(); schema.Transaction = transaction;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS remote_conflict_actions(command_id TEXT PRIMARY KEY REFERENCES user_commands(command_id),
                payload BLOB NOT NULL,fingerprint BLOB NOT NULL,outcome_payload BLOB NULL,outcome_fingerprint BLOB NULL,
                CHECK((outcome_payload IS NULL)=(outcome_fingerprint IS NULL)));
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false); transaction.Commit();
    }
}
