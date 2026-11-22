using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Correlation with one complete original official journal row, without decoding its SDK payload.</summary>
public sealed record MirrorPulseConflictKeepRemoteJournalMember(Guid OperationId, long Sequence, Guid ItemId, string OriginalSha256);

/// <summary>Immutable journal correlations retained after both selected private copies.</summary>
/// <remarks>This is neither a durable SDK store identity nor native execution, application, recovery or ACK authority.</remarks>
public sealed record MirrorPulseConflictKeepRemoteJournalBinding(int Version, Guid CommandId, Guid PlanId, Guid ItemId,
    string IntentSha256, MirrorPulseConflictKeepRemoteLocalPreservation LocalPreservation, MirrorPulseConflictKeepBothCopy RemoteCopy,
    IReadOnlyList<MirrorPulseConflictKeepRemoteJournalMember> Originals, DateTimeOffset ObservedAt);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Retains all original row correlations atomically, without changing the rejected execution or any conflict.</summary>
    public async Task<MirrorPulseConflictKeepRemoteJournalBinding> RetainConflictKeepRemoteJournalAsync(
        MirrorPulseConflictKeepRemoteJournalBinding binding, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        // Copy caller-owned membership before awaiting the gate or serializing its immutable receipt.
        if (binding.Originals is null) throw new ArgumentException("Complete original journal membership is required.", nameof(binding));
        binding = binding with { Originals = Array.AsReadOnly(binding.Originals.ToArray()) };
        ValidateKeepRemoteJournalBinding(binding);
        byte[] payload = KeepRemoteJournalPayload(binding);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var retained = await ReadKeepRemoteJournalBindingCoreAsync(binding.CommandId, transaction, token).ConfigureAwait(false);
            if (retained is not null)
            {
                if (!KeepRemoteJournalPayload(retained).AsSpan().SequenceEqual(payload))
                    throw new InvalidDataException("The original KeepRemote journal correlations are immutable.");
                transaction.Commit(); return retained;
            }
            await RequireKeepRemoteJournalBindingCurrentAsync(binding, transaction, token).ConfigureAwait(false);
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO conflict_keep_remote_journal_bindings(command_id,payload,fingerprint) VALUES($id,$payload,$hash);";
            insert.Parameters.AddWithValue("$id", binding.CommandId.ToString("D")); insert.Parameters.AddWithValue("$payload", payload);
            insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload)); await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); transaction.Commit(); return binding;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads retained correlations for a still-owned pending decision. Fresh public SDK row validation remains required.</summary>
    public async Task<MirrorPulseConflictKeepRemoteJournalBinding?> ReadConflictKeepRemoteJournalAsync(Guid commandId,
        CancellationToken token = default)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("The original KeepRemote command is required.", nameof(commandId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var binding = await ReadKeepRemoteJournalBindingCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return binding;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseConflictKeepRemoteJournalBinding?> ReadKeepRemoteJournalBindingCoreAsync(Guid commandId,
        SqliteTransaction transaction, CancellationToken token)
    {
        byte[] payload; byte[] fingerprint;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT payload,fingerprint FROM conflict_keep_remote_journal_bindings WHERE command_id=$id;";
            query.Parameters.AddWithValue("$id", commandId.ToString("D")); await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            payload = (byte[])reader[0]; fingerprint = (byte[])reader[1];
        }
        if (payload.Length > 16_777_216 || !SHA256.HashData(payload).AsSpan().SequenceEqual(fingerprint))
            throw new InvalidDataException("The original KeepRemote journal correlation fingerprint changed.");
        MirrorPulseConflictKeepRemoteJournalBinding binding;
        try
        {
            binding = JsonSerializer.Deserialize<MirrorPulseConflictKeepRemoteJournalBinding>(payload, TopologyJsonOptions)
                ?? throw new InvalidDataException("The original KeepRemote journal correlations are empty.");
            ValidateKeepRemoteJournalBinding(binding);
            if (binding.CommandId != commandId || !KeepRemoteJournalPayload(binding).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("The original KeepRemote journal correlations changed their canonical owner or payload.");
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException)
        { throw new InvalidDataException("The retained KeepRemote journal correlations are invalid.", failure); }
        await RequireKeepRemoteJournalBindingCurrentAsync(binding, transaction, token).ConfigureAwait(false);
        return binding with { Originals = Array.AsReadOnly(binding.Originals.ToArray()) };
    }

    private async Task RequireKeepRemoteJournalBindingCurrentAsync(MirrorPulseConflictKeepRemoteJournalBinding binding,
        SqliteTransaction transaction, CancellationToken token)
    {
        var retained = await ReadConflictKeepRemoteCoreAsync(binding.CommandId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original KeepRemote preparation is missing.");
        var intent = retained.Intent;
        var plan = await ReadCoalescingPlanCoreAsync(intent.PlanId, transaction, token).ConfigureAwait(false);
        if (binding.PlanId != intent.PlanId || binding.ItemId != intent.ItemId || binding.ObservedAt < intent.PreparedAt ||
            binding.IntentSha256 != Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(intent))) ||
            retained.LocalPreservation != binding.LocalPreservation || retained.RemoteCopy != binding.RemoteCopy ||
            binding.Originals.Count != plan.Members.Count)
            throw new InvalidDataException("The journal correlations lost their complete selected KeepRemote decision or either retained copy.");
        for (int index = 0; index < plan.Members.Count; index++)
        {
            var row = binding.Originals[index]; var member = plan.Members[index];
            if (row.OperationId != member.OperationId || row.Sequence != member.Sequence || row.ItemId != intent.ItemId)
                throw new InvalidDataException("The journal correlations cannot omit, reorder or substitute an original KeepRemote member.");
        }
    }

    private static void ValidateKeepRemoteJournalBinding(MirrorPulseConflictKeepRemoteJournalBinding binding)
    {
        static bool InvalidHash(string? value) => value is not { Length: 64 } || value.Any(character => !char.IsAsciiHexDigitUpper(character));
        if (binding.Version != 1 || binding.CommandId == Guid.Empty || binding.PlanId == Guid.Empty || binding.ItemId == Guid.Empty ||
            InvalidHash(binding.IntentSha256) || binding.LocalPreservation is null || binding.RemoteCopy is null ||
            binding.ObservedAt <= DateTimeOffset.UnixEpoch || binding.Originals is not { Count: >= 2 and <= 65_536 } ||
            binding.Originals.Any(row => row is null || row.OperationId == Guid.Empty || row.Sequence < 1 || row.ItemId != binding.ItemId || InvalidHash(row.OriginalSha256)) ||
            binding.Originals.Select(row => row.OperationId).Distinct().Count() != binding.Originals.Count ||
            binding.Originals.Zip(binding.Originals.Skip(1)).Any(pair => pair.First.Sequence >= pair.Second.Sequence))
            throw new ArgumentException("KeepRemote journal correlations require the complete ordered original group and canonical fingerprints.", nameof(binding));
        ValidateKeepRemoteCopy(binding.RemoteCopy, binding.RemoteCopy.Length);
        if (binding.LocalPreservation.Copy is { } local) ValidateKeepRemoteCopy(local, local.Length);
    }

    private static byte[] KeepRemoteJournalPayload(MirrorPulseConflictKeepRemoteJournalBinding binding)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(binding, TopologyJsonOptions);
        if (payload.Length > 16_777_216) throw new InvalidDataException("The complete KeepRemote journal correlations exceed their retained payload bound.");
        return payload;
    }
}
