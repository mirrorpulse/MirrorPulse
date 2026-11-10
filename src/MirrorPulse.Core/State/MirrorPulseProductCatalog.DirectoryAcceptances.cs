using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

/// <summary>A typed directory acceptance, separate from file bytes and the member's first identity.</summary>
/// <remarks>Preparation grants no native authority or permission to acknowledge a journal operation.</remarks>
public sealed record MirrorPulseDirectoryAcceptance(int Version, Guid OperationId, Guid MemberId, RootId RootId,
    MirrorPulseLocalFileBinding LocalObject, Guid? PreviousAcceptanceOperationId, Guid PreviousItemId,
    string PreviousRemoteId, Guid AcceptedItemId, string RemoteId, string AcceptedRevision,
    string RelativePath, Guid? PositionOperationId, string IntentSha256);

/// <summary>Host readback after public directory identity and official projection confirmation.</summary>
/// <remarks>Historical receipt only; the Host must still guard the current object, parent and owning role.</remarks>
public sealed record MirrorPulseDirectoryConfirmationReceipt(Guid OperationId, MirrorPulseLocalFileBinding LocalObject,
    string ObservedPlaceholderIdentity, Guid ObservedItemId, string ObservedRemoteId, string ObservedRevision,
    bool NativeInSync, bool OfficialProjectionVerified, DateTimeOffset ObservedAt);

public sealed partial class MirrorPulseProductCatalog
{
    public static string ComputeDirectoryAcceptanceIntentSha256(MirrorPulseMutationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(intent, TopologyJsonOptions)));
    }

    public async Task<MirrorPulseDirectoryAcceptance> PrepareDirectoryAcceptanceAsync(MirrorPulseDirectoryAcceptance value,
        CancellationToken token = default)
    {
        ValidateDirectoryAcceptance(value);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var replay = await ReadDirectoryAcceptanceCoreAsync(value.OperationId, transaction, token).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay != value) throw new InvalidOperationException("A directory acceptance cannot replace its original proof.");
                transaction.Commit(); return replay;
            }
            var member = await RequireDirectoryAcceptanceReferencesAsync(value, transaction, token).ConfigureAwait(false);
            var current = await ReadNamespaceMemberIdentityCoreAsync(member, transaction, token).ConfigureAwait(false);
            RequireDirectoryIdentityPredecessor(value, current);
            await RequireNamespaceMoveRootUnfencedAsync(value.RootId, null, transaction, token).ConfigureAwait(false);
            var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, token).ConfigureAwait(false);
            if (location.RelativePath != value.RelativePath || location.CurrentPositionOperationId != value.PositionOperationId)
                throw new InvalidOperationException("Directory acceptance requires the committed current member position.");
            await RequireDirectoryAcceptanceRootScopeAsync(value, transaction, token).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, TopologyJsonOptions);
            if (payload.Length > NamespaceIdentityAcceptanceLimit) throw new ArgumentException("Directory acceptance must be bounded.");
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO directory_acceptances(operation_id,member_id,previous_operation_id,payload,fingerprint)
                VALUES($operation,$member,$previous,$payload,$hash);
                """;
            insert.Parameters.AddWithValue("$operation", value.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$member", value.MemberId.ToString("D"));
            insert.Parameters.AddWithValue("$previous", (object?)value.PreviousAcceptanceOperationId?.ToString("D") ?? DBNull.Value);
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            transaction.Commit(); return value;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseDirectoryAcceptance?> ReadDirectoryAcceptanceAsync(Guid operationId, CancellationToken token = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An original operation is required.", nameof(operationId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var value = await ReadDirectoryAcceptanceCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return value;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseDirectoryConfirmationReceipt?> ReadDirectoryConfirmationAsync(Guid operationId, CancellationToken token = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An original operation is required.", nameof(operationId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var value = await ReadDirectoryConfirmationCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return value;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Promotes an exact directory identity only after the Host's actual native and official readback.</summary>
    public async Task<MirrorPulseDirectoryConfirmationReceipt> RecordDirectoryConfirmationAsync(
        MirrorPulseDirectoryConfirmationReceipt receipt, CancellationToken token = default)
    {
        ValidateDirectoryConfirmation(receipt);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var proof = await ReadDirectoryAcceptanceCoreAsync(receipt.OperationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The retained directory acceptance is missing.");
            RequireDirectoryReceiptMatches(proof, receipt);
            var replay = await ReadDirectoryConfirmationCoreAsync(receipt.OperationId, transaction, token).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay != receipt) throw new InvalidOperationException("The first directory confirmation is immutable.");
                transaction.Commit(); return replay;
            }
            var member = await RequireDirectoryAcceptanceReferencesAsync(proof, transaction, token).ConfigureAwait(false);
            RequireDirectoryIdentityPredecessor(proof, await ReadNamespaceMemberIdentityCoreAsync(member, transaction, token).ConfigureAwait(false));
            await RequireNamespaceMoveRootUnfencedAsync(proof.RootId, null, transaction, token).ConfigureAwait(false);
            var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, token).ConfigureAwait(false);
            if (location.RelativePath != proof.RelativePath || location.CurrentPositionOperationId != proof.PositionOperationId ||
                receipt.ObservedAt < member.RegisteredAt)
                throw new InvalidOperationException("Directory confirmation cannot adopt a later member position or earlier observation.");
            if (proof.PreviousAcceptanceOperationId is { } previous)
            {
                var predecessor = await ReadDirectoryConfirmationCoreAsync(previous, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Directory confirmation requires its confirmed predecessor.");
                if (receipt.ObservedAt < predecessor.ObservedAt)
                    throw new InvalidOperationException("Directory confirmation cannot precede its predecessor observation.");
            }
            await RequireDirectoryAcceptanceRootScopeAsync(proof, transaction, token).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(receipt, TopologyJsonOptions);
            await using var update = _connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE directory_acceptances SET receipt=$receipt,receipt_fingerprint=$hash WHERE operation_id=$operation AND receipt IS NULL;";
            update.Parameters.AddWithValue("$receipt", payload); update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            update.Parameters.AddWithValue("$operation", receipt.OperationId.ToString("D"));
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidDataException("Directory confirmation lost its original acceptance.");
            transaction.Commit(); return receipt;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseDirectoryAcceptance?> ReadDirectoryAcceptanceCoreAsync(Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT member_id,previous_operation_id,payload,fingerprint FROM directory_acceptances WHERE operation_id=$operation;";
        query.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        MirrorPulseDirectoryAcceptance value;
        await using (var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            value = ReadNamespaceMovePayload<MirrorPulseDirectoryAcceptance>((byte[])reader[2], (byte[])reader[3], NamespaceIdentityAcceptanceLimit, ValidateDirectoryAcceptance);
            if (value.OperationId != operationId || value.MemberId.ToString("D") != reader.GetString(0) ||
                value.PreviousAcceptanceOperationId?.ToString("D") != (reader.IsDBNull(1) ? null : reader.GetString(1)))
                throw new InvalidDataException("The directory acceptance indexes changed their original identity.");
        }
        _ = await RequireDirectoryAcceptanceReferencesAsync(value, transaction, token).ConfigureAwait(false);
        return value;
    }

    private async Task<MirrorPulseDirectoryConfirmationReceipt?> ReadDirectoryConfirmationCoreAsync(Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        var proof = await ReadDirectoryAcceptanceCoreAsync(operationId, transaction, token).ConfigureAwait(false);
        if (proof is null) return null;
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT receipt,receipt_fingerprint FROM directory_acceptances WHERE operation_id=$operation;";
        query.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidDataException("Directory acceptance disappeared.");
        if (reader.IsDBNull(0)) return null;
        var value = ReadNamespaceMovePayload<MirrorPulseDirectoryConfirmationReceipt>((byte[])reader[0], (byte[])reader[1],
            NamespaceIdentityAcceptanceLimit, ValidateDirectoryConfirmation);
        RequireDirectoryReceiptMatches(proof, value); return value;
    }

    private async Task<MirrorPulseNamespaceMemberRegistration> RequireDirectoryAcceptanceReferencesAsync(
        MirrorPulseDirectoryAcceptance value, SqliteTransaction transaction, CancellationToken token)
    {
        var member = await ReadNamespaceMemberCoreAsync(value.MemberId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original directory member is missing.");
        var mutation = await ReadMutationCoreAsync(value.OperationId, token, transaction).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original directory mutation is missing.");
        if (!member.IsDirectory || member.RootId != value.RootId || member.LocalObject != value.LocalObject ||
            mutation.State is not (MirrorPulseMutationState.RemoteAccepted or MirrorPulseMutationState.Acknowledged) ||
            !mutation.Intent.IsDirectory || mutation.Intent.Kind is not (MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.Move) ||
            mutation.Intent.UploadBinding is not null || mutation.Intent.ContentLength is not null || mutation.Intent.ContentSha256 is not null ||
            mutation.AcceptedRevision != value.AcceptedRevision ||
            value.IntentSha256 != ComputeDirectoryAcceptanceIntentSha256(mutation.Intent))
            throw new InvalidOperationException("Directory acceptance requires the same member and durable typed mutation without file content.");
        return member;
    }

    private async Task RequireDirectoryAcceptanceRootScopeAsync(MirrorPulseDirectoryAcceptance value, SqliteTransaction transaction, CancellationToken token)
    {
        var mutation = (await ReadMutationCoreAsync(value.OperationId, token, transaction).ConfigureAwait(false))!;
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload FROM adapter_topology WHERE id=1;";
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not string payload)
            throw new InvalidOperationException("Directory acceptance requires its owning Adapter topology.");
        var roots = DeserializeTopology(payload).Roots.Where(root => root.RootId == value.RootId).ToArray();
        if (roots.Length != 1 || roots[0].InstanceId != mutation.Intent.InstanceId || roots[0].UniquenessKey != mutation.Intent.RootKey ||
            roots[0].State != RootRegistrationState.Active ||
            value.RelativePath != (roots[0].DirectoryName + "/" + mutation.Intent.RelativePath).Replace('\\', '/') ||
            mutation.Intent.PreviousRootKey is { } previous && previous != mutation.Intent.RootKey)
            throw new InvalidOperationException("Directory acceptance requires its active owning root and instance.");
    }

    private static void RequireDirectoryIdentityPredecessor(MirrorPulseDirectoryAcceptance value, MirrorPulseNamespaceMemberIdentity current)
    {
        if (current.AcceptanceOperationId != value.PreviousAcceptanceOperationId || current.ItemId != value.PreviousItemId || current.RemoteId != value.PreviousRemoteId)
            throw new InvalidOperationException("Directory confirmation cannot fork or rewind the retained member identity.");
    }

    private static void RequireDirectoryReceiptMatches(MirrorPulseDirectoryAcceptance value, MirrorPulseDirectoryConfirmationReceipt receipt)
    {
        if (receipt.OperationId != value.OperationId || receipt.LocalObject != value.LocalObject || receipt.ObservedItemId != value.AcceptedItemId ||
            receipt.ObservedRemoteId != value.RemoteId || receipt.ObservedRevision != value.AcceptedRevision)
            throw new InvalidDataException("Directory confirmation does not match its original accepted identity and native object.");
    }

    private static void ValidateDirectoryAcceptance(MirrorPulseDirectoryAcceptance value)
    {
        ArgumentNullException.ThrowIfNull(value); ArgumentNullException.ThrowIfNull(value.LocalObject);
        if (value.Version != 1 || value.OperationId == Guid.Empty || value.MemberId == Guid.Empty || value.RootId.Value == Guid.Empty ||
            value.PreviousItemId == Guid.Empty || value.AcceptedItemId == Guid.Empty || value.PreviousAcceptanceOperationId == Guid.Empty ||
            value.PreviousAcceptanceOperationId == value.OperationId || value.PositionOperationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(value.PreviousRemoteId) || value.PreviousRemoteId.Length > 16_384 ||
            string.IsNullOrWhiteSpace(value.RemoteId) || value.RemoteId.Length > 16_384 || string.IsNullOrEmpty(value.AcceptedRevision) ||
            value.AcceptedRevision.Length > 16_384 || string.IsNullOrWhiteSpace(value.RelativePath) || value.RelativePath.Length > 32_768 ||
            value.IntentSha256 is not { Length: 64 } || !value.IntentSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Directory acceptance requires bounded, original typed identity facts.");
    }

    private static void ValidateDirectoryConfirmation(MirrorPulseDirectoryConfirmationReceipt value)
    {
        ArgumentNullException.ThrowIfNull(value); ArgumentNullException.ThrowIfNull(value.LocalObject);
        if (value.OperationId == Guid.Empty || value.ObservedItemId == Guid.Empty || !value.NativeInSync || !value.OfficialProjectionVerified ||
            value.ObservedAt == default || string.IsNullOrWhiteSpace(value.ObservedRemoteId) || value.ObservedRemoteId.Length > 16_384 ||
            string.IsNullOrEmpty(value.ObservedRevision) || value.ObservedRevision.Length > 16_384)
            throw new ArgumentException("Directory confirmation requires actual successful native and official readback.");
        try
        {
            byte[] encoded = Convert.FromBase64String(value.ObservedPlaceholderIdentity ?? string.Empty);
            if (encoded.Length is 0 or > 4096 || Convert.ToBase64String(encoded) != value.ObservedPlaceholderIdentity)
                throw new ArgumentException("Directory confirmation requires its exact encoded public identity.");
        }
        catch (FormatException error) { throw new ArgumentException("Directory confirmation requires its public encoded identity.", error); }
    }
}
