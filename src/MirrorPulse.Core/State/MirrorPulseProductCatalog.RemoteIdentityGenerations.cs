using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>A durable successor address selected by the Cloud Files boundary after an original source retirement.</summary>
/// <remarks>Addresses are opaque to Core. This grants no native publication, journal ACK, or name ownership.</remarks>
public sealed record MirrorPulseRemoteIdentityGeneration(int Version, RootId RootId, InstanceId InstanceId,
    string RootKey, Guid BaseItemId, string BaseRemoteId, Guid? PreviousGeneration, Guid Generation,
    Guid PreviousItemId, string PreviousRemoteId, Guid ItemId, string RemoteId,
    Guid TransferOperationId, Guid RetirementOperationId, long RetiredNameEpoch, DateTimeOffset SelectedAt);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads one indexed current address without loading an identity map or rewriting historical identities.</summary>
    public async Task<MirrorPulseRemoteIdentityGeneration?> ReadRemoteIdentityGenerationAsync(
        RootId rootId, Guid baseItemId, CancellationToken token = default)
    {
        if (rootId.Value == Guid.Empty || baseItemId == Guid.Empty) throw new ArgumentException("A stable root and base item are required.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadRemoteIdentityGenerationCoreAsync(rootId, baseItemId, transaction, token).ConfigureAwait(false);
            if (result is not null) await RequireRemoteIdentityGenerationRetirementAsync(result, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Atomically retains an immutable successor and advances only its expected predecessor.</summary>
    /// <remarks>The boundary derives the new public address from Generation before calling this method.
    /// A sole completed original retirement authorizes selection; a transfer also requires its original ACK.</remarks>
    public async Task<MirrorPulseRemoteIdentityGeneration> SelectRemoteIdentityGenerationAsync(
        MirrorPulseRemoteIdentityGeneration selection, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(selection); ValidateRemoteIdentityGeneration(selection);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var current = await ReadRemoteIdentityGenerationCoreAsync(selection.RootId, selection.BaseItemId, transaction, token).ConfigureAwait(false);
            if (current?.Generation == selection.Generation)
            {
                if (current != selection) throw new InvalidOperationException("A selected generation is immutable.");
                await RequireRemoteIdentityGenerationRetirementAsync(current, transaction, token).ConfigureAwait(false);
                transaction.Commit(); return current;
            }
            if (current?.Generation != selection.PreviousGeneration ||
                current is null && (selection.PreviousItemId != selection.BaseItemId || selection.PreviousRemoteId != selection.BaseRemoteId) ||
                current is not null && (selection.PreviousItemId != current.ItemId || selection.PreviousRemoteId != current.RemoteId ||
                    selection.InstanceId != current.InstanceId || selection.RootKey != current.RootKey || selection.BaseRemoteId != current.BaseRemoteId))
                throw new InvalidOperationException("A generation must retain its exact current predecessor and original address.");
            if (current is not null) await RequireRemoteIdentityGenerationRetirementAsync(current, transaction, token).ConfigureAwait(false);
            await RequireRemoteIdentityGenerationRetirementAsync(selection, transaction, token).ConfigureAwait(false);
            await using (var occupied = _connection.CreateCommand())
            {
                occupied.Transaction = transaction;
                occupied.CommandText = """
                    SELECT 1 FROM namespace_permission_local_identities WHERE item_id=$item
                    UNION ALL SELECT 1 FROM namespace_birth_plans WHERE item_id=$item
                    UNION ALL SELECT 1 FROM namespace_birth_observations WHERE item_id=$item
                    UNION ALL SELECT 1 FROM namespace_remote_birth_entries WHERE item_id=$item
                    UNION ALL SELECT 1 FROM namespace_transfer_target_births WHERE item_id=$item LIMIT 1;
                    """;
                occupied.Parameters.AddWithValue("$item", selection.ItemId.ToString("D"));
                if (await occupied.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("A successor cannot adopt an existing or historical public item.");
            }
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(selection, TopologyJsonOptions);
            if (payload.Length > 131_072) throw new ArgumentException("A generation selection must remain bounded.", nameof(selection));
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO remote_identity_generation_history(generation,root_id,base_item_id,item_id,retirement_id,payload,fingerprint)
                VALUES($generation,$root,$base,$item,$retirement,$payload,$hash);
                INSERT INTO remote_identity_generations(root_id,base_item_id,generation) VALUES($root,$base,$generation)
                ON CONFLICT(root_id,base_item_id) DO UPDATE SET generation=excluded.generation;
                """;
            insert.Parameters.AddWithValue("$generation", selection.Generation.ToString("D"));
            insert.Parameters.AddWithValue("$root", selection.RootId.ToString()); insert.Parameters.AddWithValue("$base", selection.BaseItemId.ToString("D"));
            insert.Parameters.AddWithValue("$item", selection.ItemId.ToString("D")); insert.Parameters.AddWithValue("$retirement", selection.RetirementOperationId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            transaction.Commit(); return selection;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseRemoteIdentityGeneration?> ReadRemoteIdentityGenerationCoreAsync(
        RootId rootId, Guid baseItemId, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = """
            SELECT c.generation,h.root_id,h.base_item_id,h.item_id,h.retirement_id,h.payload,h.fingerprint
            FROM remote_identity_generations c LEFT JOIN remote_identity_generation_history h ON h.generation=c.generation
            WHERE c.root_id=$root AND c.base_item_id=$base;
            """;
        query.Parameters.AddWithValue("$root", rootId.ToString()); query.Parameters.AddWithValue("$base", baseItemId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        if (reader.IsDBNull(5) || reader.IsDBNull(6)) throw new InvalidDataException("The current generation lost its immutable history.");
        byte[] payload = (byte[])reader[5], hash = (byte[])reader[6];
        if (payload.Length > 131_072 || hash.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), hash))
            throw new InvalidDataException("The retained generation fingerprint is invalid.");
        var result = JsonSerializer.Deserialize<MirrorPulseRemoteIdentityGeneration>(payload, TopologyJsonOptions)
            ?? throw new InvalidDataException("The generation history is missing.");
        ValidateRemoteIdentityGeneration(result);
        if (result.RootId != rootId || result.BaseItemId != baseItemId || reader.GetString(0) != result.Generation.ToString("D") ||
            reader.GetString(1) != rootId.ToString() || reader.GetString(2) != baseItemId.ToString("D") ||
            reader.GetString(3) != result.ItemId.ToString("D") || reader.GetString(4) != result.RetirementOperationId.ToString("D") ||
            !payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(result, TopologyJsonOptions)))
            throw new InvalidDataException("The indexed current generation disagrees with its immutable address.");
        return result;
    }

    private async Task RequireRemoteIdentityGenerationRetirementAsync(MirrorPulseRemoteIdentityGeneration selection,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (selection.Version == 2)
        {
            await RequireOrdinaryRemoteIdentityRetirementAsync(selection, transaction, token).ConfigureAwait(false);
            return;
        }
        if ((await ReadRemoteRetirementGenerationsCoreAsync(selection.PreviousItemId, transaction, token).ConfigureAwait(false)).Count != 0)
            throw new InvalidOperationException("A transfer generation cannot borrow an ordinary remote retirement.");
        var (copy, retirement) = await ReadTransferRetirementAsync(selection.TransferOperationId, transaction, token).ConfigureAwait(false);
        var projection = retirement.TransferSourceProjection;
        if (retirement.OperationId != selection.RetirementOperationId || retirement.Phase != MirrorPulseNamespaceRetirementPhase.Completed ||
            retirement.TransferReceipt is not { NativeMissing: true, OfficialTombstone: true } ||
            retirement.TransferSourceNameEpoch != selection.RetiredNameEpoch || projection is null ||
            projection.ItemId != selection.PreviousItemId || projection.RemoteId != selection.PreviousRemoteId ||
            copy.Plan.SourceRoute.RootId != selection.RootId || copy.Plan.SourceRoute.InstanceId != selection.InstanceId ||
            copy.Plan.SourceRoute.RootKey != selection.RootKey || copy.Acknowledgement?.AcknowledgedAt is not { } acknowledgedAt ||
            selection.SelectedAt < acknowledgedAt)
            throw new InvalidOperationException("A successor requires its exact completed source generation, native retirement, name epoch and original ACK.");
        await RequireSoleRetiredTransferSourceAsync(selection.PreviousItemId, selection.RetirementOperationId, transaction, token).ConfigureAwait(false);
    }

    private static void ValidateRemoteIdentityGeneration(MirrorPulseRemoteIdentityGeneration selection)
    {
        if (!(selection.Version == 1 && selection.TransferOperationId != Guid.Empty ||
                selection.Version == 2 && selection.TransferOperationId == Guid.Empty) ||
            selection.RootId.Value == Guid.Empty || selection.InstanceId.Value == Guid.Empty ||
            selection.BaseItemId == Guid.Empty || selection.PreviousItemId == Guid.Empty || selection.ItemId == Guid.Empty ||
            selection.Generation == Guid.Empty || selection.PreviousGeneration == Guid.Empty || selection.PreviousGeneration == selection.Generation ||
            selection.ItemId == selection.PreviousItemId || selection.ItemId == selection.BaseItemId ||
            selection.RetirementOperationId == Guid.Empty || selection.RetiredNameEpoch < 2 ||
            string.IsNullOrWhiteSpace(selection.RootKey) || selection.RootKey.Length > 16_384 ||
            string.IsNullOrWhiteSpace(selection.BaseRemoteId) || selection.BaseRemoteId.Length > 16_384 ||
            string.IsNullOrWhiteSpace(selection.PreviousRemoteId) || selection.PreviousRemoteId.Length > 16_384 ||
            string.IsNullOrWhiteSpace(selection.RemoteId) || selection.RemoteId.Length > 16_384 ||
            selection.RemoteId == selection.PreviousRemoteId || selection.RemoteId == selection.BaseRemoteId)
            throw new ArgumentException("A successor requires bounded distinct addresses and original retirement ownership.", nameof(selection));
    }
}
