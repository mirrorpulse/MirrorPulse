using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private const int NamespaceIdentityAcceptanceLimit = 65_536;

    private async Task<MirrorPulseNamespaceIdentityAcceptance?> ReadNamespaceIdentityAcceptanceCoreAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        var accepted = await ReadNamespaceIdentityAcceptanceRowAsync(operationId, transaction, token).ConfigureAwait(false);
        if (accepted is not null) _ = await ValidateNamespaceIdentityAcceptanceChainAsync(accepted, transaction, token).ConfigureAwait(false);
        return accepted;
    }

    private async Task<MirrorPulseNamespaceIdentityAcceptance?> ReadNamespaceIdentityAcceptanceRowAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT operation_id,member_id,previous_operation_id,payload,fingerprint FROM namespace_member_identity_acceptances WHERE operation_id=$operation;";
        query.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var accepted = ReadNamespaceMovePayload<MirrorPulseNamespaceIdentityAcceptance>((byte[])reader[3], (byte[])reader[4],
            NamespaceIdentityAcceptanceLimit, ValidateNamespaceIdentityAcceptance);
        if (accepted.Proof.OperationId != operationId || reader.GetString(0) != operationId.ToString("D") ||
            reader.GetString(1) != accepted.MemberId.ToString("D") ||
            (reader.IsDBNull(2) ? null : reader.GetString(2)) != accepted.PreviousAcceptanceOperationId?.ToString("D"))
            throw new InvalidDataException("The accepted identity indexes disagree with their immutable correlation.");
        return accepted;
    }

    private async Task<long> ValidateNamespaceIdentityAcceptanceChainAsync(MirrorPulseNamespaceIdentityAcceptance head,
        SqliteTransaction transaction, CancellationToken token)
    {
        var visited = new HashSet<Guid>(); var current = head; long length = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!visited.Add(current.Proof.OperationId)) throw new InvalidDataException("The accepted identity history contains a cycle.");
                var member = await ReadNamespaceMemberCoreAsync(current.MemberId, transaction, token).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The accepted identity lost its original member.");
                await ValidateNamespaceIdentityAcceptanceReferencesAsync(current, member, transaction, token).ConfigureAwait(false);
                length++;
                if (current.PreviousAcceptanceOperationId is not { } previous) return length;
                current = await ReadNamespaceIdentityAcceptanceRowAsync(previous, transaction, token).ConfigureAwait(false)
                    ?? throw new FileNotFoundException("The accepted identity predecessor is missing.");
            }
        }
        catch (Exception error) when (error is InvalidOperationException or FileNotFoundException or ArgumentException or JsonException)
        { throw new InvalidDataException("The accepted identity lost its original member, upload or predecessor proof.", error); }
    }

    private async Task ValidateNamespaceIdentityAcceptanceReferencesAsync(MirrorPulseNamespaceIdentityAcceptance value,
        MirrorPulseNamespaceMemberRegistration member, SqliteTransaction transaction, CancellationToken token)
    {
        var proof = value.Proof;
        var retained = await ReadContentAcceptanceProofCoreAsync(proof.OperationId, token, transaction).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original upload acceptance proof is missing.");
        var mutation = await ReadMutationCoreAsync(proof.OperationId, token, transaction).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original accepted upload intent is missing.");
        if (member.IsDirectory || member.RootId != value.RootId || member.LocalObject != proof.UploadBinding.LocalObject ||
            retained != proof || mutation.State is not (MirrorPulseMutationState.RemoteAccepted or MirrorPulseMutationState.Acknowledged) ||
            mutation.Intent.UploadBinding != proof.UploadBinding || mutation.Intent.IsDirectory ||
            mutation.Intent.Kind is not (MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.ContentUpdate) ||
            mutation.Intent.ContentLength != proof.Length ||
            !string.Equals(mutation.Intent.ContentSha256, proof.Sha256, StringComparison.OrdinalIgnoreCase) ||
            mutation.AcceptedRevision != proof.AcceptedRevision || value.Confirmation.ObservedAt < member.RegisteredAt ||
            value.UploadIntentSha256 != Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(mutation.Intent, TopologyJsonOptions))))
            throw new InvalidOperationException("Accepted identity correlation requires the same registered object and durable upload-time proof.");
        if (value.PreviousAcceptanceOperationId is { } previousId)
        {
            var previous = await ReadNamespaceIdentityAcceptanceRowAsync(previousId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The accepted identity predecessor is missing.");
            if (previous.MemberId != value.MemberId || previous.RootId != value.RootId ||
                previous.Proof.UploadBinding.LocalObject != proof.UploadBinding.LocalObject ||
                previous.Proof.AcceptedItemId != value.PreviousItemId || previous.Proof.RemoteId != value.PreviousRemoteId ||
                previous.Confirmation.ObservedAt > value.Confirmation.ObservedAt)
                throw new InvalidOperationException("An accepted identity must follow the same member's confirmed identity and chronology.");
        }
        else
        {
            var original = await ReadOriginalNamespaceMemberIdentityAsync(member, transaction, token).ConfigureAwait(false);
            if (original.ItemId != value.PreviousItemId || original.RemoteId != value.PreviousRemoteId)
                throw new InvalidOperationException("The first accepted identity must retain the member's first public identity.");
        }
    }

    private async Task RequireNamespaceIdentityRootScopeAsync(MirrorPulseNamespaceIdentityAcceptance value,
        SqliteTransaction transaction, CancellationToken token)
    {
        var mutation = await ReadMutationCoreAsync(value.Proof.OperationId, token, transaction).ConfigureAwait(false)
            ?? throw new InvalidDataException("The accepted upload intent disappeared.");
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload FROM adapter_topology WHERE id=1;";
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not string payload)
            throw new InvalidOperationException("Identity correlation requires the owning Adapter topology.");
        var roots = DeserializeTopology(payload).Roots.Where(root => root.RootId == value.RootId).ToArray();
        if (roots.Length != 1 || roots[0].InstanceId != mutation.Intent.InstanceId || roots[0].UniquenessKey != mutation.Intent.RootKey ||
            mutation.Intent.PreviousRootKey is { } previousRoot && previousRoot != mutation.Intent.RootKey)
            throw new InvalidOperationException("The accepted upload must belong to this member's managed root and instance.");
    }

    private static void ValidateNamespaceIdentityAcceptance(MirrorPulseNamespaceIdentityAcceptance value)
    {
        ArgumentNullException.ThrowIfNull(value.Proof); ArgumentNullException.ThrowIfNull(value.Proof.UploadBinding);
        ArgumentNullException.ThrowIfNull(value.Confirmation);
        var proof = value.Proof; var receipt = value.Confirmation;
        proof.UploadBinding.Validate();
        if (value.Version != 1 || value.MemberId == Guid.Empty || value.RootId.Value == Guid.Empty || value.PreviousItemId == Guid.Empty ||
            value.PreviousAcceptanceOperationId == Guid.Empty || value.PreviousAcceptanceOperationId == proof.OperationId ||
            proof.OperationId == Guid.Empty || proof.AcceptedItemId == Guid.Empty || string.IsNullOrWhiteSpace(value.PreviousRemoteId) ||
            value.PreviousRemoteId.Length > 16_384 || string.IsNullOrWhiteSpace(proof.RemoteId) || proof.RemoteId.Length > 16_384 ||
            proof.Length < 0 || proof.Sha256 is null || proof.Sha256.Length != 64 || !proof.Sha256.All(Uri.IsHexDigit) ||
            value.UploadIntentSha256 is null || value.UploadIntentSha256.Length != 64 || !value.UploadIntentSha256.All(Uri.IsHexDigit) ||
            proof.UploadBinding.Preparation != MirrorPulseContentPreparation.ReplacePlaceholderIdentity ||
            !receipt.MayAcknowledge || receipt.Stage != MirrorPulseContentConfirmationStage.Complete ||
            !Enum.IsDefined(receipt.NativeStage) || receipt.BytesVerified != proof.Length || receipt.SegmentsRead < 0 || receipt.ObservedAt == default)
            throw new ArgumentException("Accepted identity correlation requires a complete upload binding and successful public confirmation.");
        try
        {
            byte[] encoded = Convert.FromBase64String(receipt.ObservedPlaceholderIdentity ?? string.Empty);
            if (encoded.Length is 0 or > 4096 || Convert.ToBase64String(encoded) != receipt.ObservedPlaceholderIdentity)
                throw new ArgumentException("Confirmation requires its exact public native identity.");
        }
        catch (FormatException error) { throw new ArgumentException("Confirmation requires a public base64 identity.", error); }
    }

    private static async Task EnsureNamespaceIdentityAcceptanceSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_member_identity_acceptances(
                operation_id TEXT NOT NULL PRIMARY KEY REFERENCES content_acceptance_proofs(operation_id),
                member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                previous_operation_id TEXT NULL REFERENCES namespace_member_identity_acceptances(operation_id),
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
            CREATE UNIQUE INDEX IF NOT EXISTS ux_namespace_identity_first ON namespace_member_identity_acceptances(member_id)
                WHERE previous_operation_id IS NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_namespace_identity_previous ON namespace_member_identity_acceptances(previous_operation_id)
                WHERE previous_operation_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_namespace_identity_member ON namespace_member_identity_acceptances(member_id);
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
