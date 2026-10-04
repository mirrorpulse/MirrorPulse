using System.Text.Json;

namespace MirrorPulse.Core.State;

/// <summary>Opaque native IDs scoped to the volume and registered root; not a content proof.</summary>
public sealed record MirrorPulseLocalFileBinding(ulong VolumeSerialNumber, Guid SyncRootFileId, Guid LocalFileId);

public enum MirrorPulseContentPreparation { None, ConvertRegularFile, ReplacePlaceholderIdentity }

/// <summary>Captured before upload, including the exact previous identity when replacement is needed.</summary>
public sealed record MirrorPulseUploadBinding(MirrorPulseLocalFileBinding LocalObject,
    MirrorPulseContentPreparation Preparation, string? PreviousPlaceholderIdentity = null)
{
    internal void Validate()
    {
        if (LocalObject is null || LocalObject.VolumeSerialNumber == 0 || LocalObject.SyncRootFileId == Guid.Empty ||
            LocalObject.LocalFileId == Guid.Empty || !Enum.IsDefined(Preparation))
            throw new ArgumentException("The upload-time native object binding is invalid.");
        if (Preparation == MirrorPulseContentPreparation.ReplacePlaceholderIdentity)
        {
            byte[] previous;
            try { previous = Convert.FromBase64String(PreviousPlaceholderIdentity ?? string.Empty); }
            catch (FormatException exception) { throw new ArgumentException("The previous placeholder identity is invalid.", exception); }
            if (previous.Length == 0 || previous.Length > 4096 || Convert.ToBase64String(previous) != PreviousPlaceholderIdentity)
                throw new ArgumentException("Replacement requires the exact nonempty previous placeholder identity.");
        }
        else if (PreviousPlaceholderIdentity is not null)
            throw new ArgumentException("A previous identity is only valid for explicit replacement.");
    }
}

/// <summary>Durable remote acceptance and upload-time object proof, saved before local confirmation.</summary>
public sealed record MirrorPulseContentAcceptanceProof(Guid OperationId, MirrorPulseUploadBinding UploadBinding,
    Guid AcceptedItemId, string RemoteId, string? AcceptedRevision, long Length, string Sha256);

public enum MirrorPulseContentConfirmationOutcome
{
    Confirmed, AlreadyConfirmed, ContentMismatch, IdentityMismatch, LocalObjectMismatch, NotFullyLocal,
    NotApplicable, Busy, ProtectionLost, DeadlineExceeded, Canceled, Failed,
    NativeAppliedProjectionPending, ProjectionConflict,
}

public enum MirrorPulseContentConfirmationStage { Open, Reference, Read, Verify, Prepare, Mark, Projection, Complete }

/// <summary>A past confirmation observation. Later writes may already have made the file dirty.</summary>
public sealed record MirrorPulseContentConfirmationReceipt(MirrorPulseContentConfirmationOutcome Outcome,
    MirrorPulseContentConfirmationStage Stage, MirrorPulseContentConfirmationStage NativeStage,
    bool NativeIdentityPrepared, bool NativeApplied, bool NativeConfirmationVerified, bool DurableProjectionCommitted,
    long BytesVerified, int SegmentsRead, int? PreparationHResult, int? NativeMarkHResult,
    int? NativeErrorHResult, int? ProjectionErrorHResult, bool? ObservedInSync,
    string? ObservedPlaceholderIdentity, DateTimeOffset ObservedAt)
{
    public bool MayAcknowledge => (Outcome is MirrorPulseContentConfirmationOutcome.Confirmed or
        MirrorPulseContentConfirmationOutcome.AlreadyConfirmed) && NativeConfirmationVerified && DurableProjectionCommitted;
}

public sealed partial class MirrorPulseProductCatalog
{
    public async Task SaveContentAcceptanceProofAsync(MirrorPulseContentAcceptanceProof proof,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (proof.OperationId == Guid.Empty || proof.AcceptedItemId == Guid.Empty || string.IsNullOrEmpty(proof.RemoteId) ||
            proof.UploadBinding is null || proof.Length < 0 || proof.Sha256 is null ||
            proof.Sha256.Length != 64 || !proof.Sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("The content acceptance proof is invalid.", nameof(proof));
        proof.UploadBinding.Validate();
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(proof, TopologyJsonOptions);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            MirrorPulseMutationRecord record = await ReadMutationCoreAsync(proof.OperationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The accepted mutation is missing.");
            if (record.Intent.UploadBinding is null)
                throw new InvalidDataException("The historical mutation has no upload-time binding; its current path cannot supply one.");
            if (record.State != MirrorPulseMutationState.RemoteAccepted || record.Intent.UploadBinding != proof.UploadBinding ||
                record.Intent.ContentLength != proof.Length ||
                !string.Equals(record.Intent.ContentSha256, proof.Sha256, StringComparison.OrdinalIgnoreCase) ||
                record.AcceptedRevision != proof.AcceptedRevision)
                throw new InvalidDataException("The proof does not match the durable remote acceptance and upload intent.");
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                INSERT INTO content_acceptance_proofs(operation_id,payload,receipt)
                VALUES($operation,$payload,NULL) ON CONFLICT(operation_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$operation", proof.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$payload", payload);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            MirrorPulseContentAcceptanceProof existing = await ReadContentAcceptanceProofCoreAsync(proof.OperationId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The acceptance proof disappeared.");
            if (existing != proof) throw new InvalidDataException("A retained acceptance proof cannot be replaced with different facts.");
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseContentAcceptanceProof?> ReadContentAcceptanceProofAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadContentAcceptanceProofCoreAsync(operationId, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task SaveContentConfirmationReceiptAsync(Guid operationId, MirrorPulseContentConfirmationReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!Enum.IsDefined(receipt.Outcome) || !Enum.IsDefined(receipt.Stage) || !Enum.IsDefined(receipt.NativeStage) ||
            receipt.BytesVerified < 0 || receipt.SegmentsRead < 0)
            throw new ArgumentException("The confirmation receipt is invalid.", nameof(receipt));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var command = _connection.CreateCommand();
            command.CommandText = "UPDATE content_acceptance_proofs SET receipt=$receipt WHERE operation_id=$operation;";
            command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            command.Parameters.AddWithValue("$receipt", JsonSerializer.SerializeToUtf8Bytes(receipt, TopologyJsonOptions));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidDataException("A receipt cannot precede its durable acceptance proof.");
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseContentConfirmationReceipt?> ReadContentConfirmationReceiptAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT receipt FROM content_acceptance_proofs WHERE operation_id=$operation;";
            command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
            object? payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return payload is byte[] bytes ? JsonSerializer.Deserialize<MirrorPulseContentConfirmationReceipt>(bytes, TopologyJsonOptions)
                ?? throw new InvalidDataException("The confirmation receipt is empty.") : null;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseContentAcceptanceProof?> ReadContentAcceptanceProofCoreAsync(Guid operationId, CancellationToken token)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT payload FROM content_acceptance_proofs WHERE operation_id=$operation;";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        object? payload = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (payload is not byte[] bytes) return null;
        MirrorPulseContentAcceptanceProof proof = JsonSerializer.Deserialize<MirrorPulseContentAcceptanceProof>(bytes, TopologyJsonOptions)
            ?? throw new InvalidDataException("The content acceptance proof is empty.");
        if (proof.OperationId != operationId) throw new InvalidDataException("The proof has a different operation identity.");
        proof.UploadBinding.Validate();
        return proof;
    }
}
