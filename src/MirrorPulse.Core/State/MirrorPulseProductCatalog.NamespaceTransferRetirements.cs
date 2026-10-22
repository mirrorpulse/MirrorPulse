using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Only the original accepted conditional transfer delete may own this source retirement.</summary>
public sealed record MirrorPulseNamespaceTransferRetirementReference(int Version, Guid OperationId,
    Guid DeleteOperationId, DateTimeOffset SourceDeleteAcceptedAt, Guid TargetProtectionId,
    MirrorPulseTransferTargetVerification TargetAtStart);

/// <summary>Actual public SDK tombstone and unchanged parent after deletion of the guarded original source.</summary>
public sealed record MirrorPulseNamespaceTransferRetirementReceipt(int Version, Guid OperationId,
    Guid DeleteOperationId, Guid OfficialItemId, MirrorPulseLocalFileBinding LocalObject,
    bool NativeMissing, bool OfficialTombstone, MirrorPulseNamespacePermissionVerification Parent,
    DateTimeOffset VerifiedAt);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reserves the original source name for transfer-owned retirement, retaining both root fences.</summary>
    /// <remarks>The Host must retain current native source/parents/target guards, private bytes and the
    /// write-excluding source data object. This admission is not evidence that native deletion happened.</remarks>
    public async Task<MirrorPulseNamespaceRetirement> PrepareNamespaceTransferSourceRetirementAsync(Guid operationId,
        MirrorPulseTransferTargetVerification freshTarget, Guid targetProtectionId, DateTimeOffset preparedAt,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(freshTarget);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original transfer is missing.");
            var reference = new MirrorPulseNamespaceTransferRetirementReference(1, operationId, copy.Plan.DeleteOperationId,
                copy.SourceDeletion?.AcceptedAt ?? throw new InvalidOperationException("Source retirement requires its owned conditional delete acceptance."),
                targetProtectionId, freshTarget);
            var source = copy.Plan.Source;
            var intent = new MirrorPulseNamespaceRetirementIntent(3, copy.Plan.DeleteOperationId, source.Context.Member.MemberId,
                copy.Plan.SourceRoute.RootId, source.Context.Member.LocalObject, source.Context.Location.RelativePath, false,
                source.Context.Member.OwnerSid, source.Context.Protection.RoleSid, preparedAt)
            { TransferEntry = reference };
            ValidateNamespaceRetirementIntent(intent); ValidateTransferRetirementReference(copy, intent);
            var existing = await ReadNamespaceRetirementCoreAsync(intent.OperationId, transaction, token).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.Intent != intent) throw new InvalidOperationException("The original transfer retirement admission is immutable.");
                transaction.Commit(); return existing;
            }
            var target = await RequireCurrentTransferSourceDeletionAsync(copy, transaction, token).ConfigureAwait(false);
            if (target.Context.Protection.ProtectionId != targetProtectionId)
                throw new InvalidOperationException("Source retirement requires the current target protection epoch.");
            await RequireNamespaceMemberNameAsync(source.Context.Member, transaction, token).ConfigureAwait(false);
            await RequireNoRegisteredDescendantsAsync(intent.MemberId, transaction, token).ConfigureAwait(false);
            var retirement = new MirrorPulseNamespaceRetirement(intent.OperationId, intent, MirrorPulseNamespaceRetirementPhase.Prepared);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(retirement, TopologyJsonOptions);
            if (payload.Length > 131_072) throw new ArgumentException("The original source retirement must be bounded.", nameof(freshTarget));
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO namespace_member_retirements(operation_id,member_id,payload,fingerprint,phase) VALUES($id,$member,$payload,$hash,0);";
            insert.Parameters.AddWithValue("$id", intent.OperationId.ToString("D")); insert.Parameters.AddWithValue("$member", intent.MemberId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            transaction.Commit(); return retirement;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Retains the original public source item before the first guarded SDK deletion.</summary>
    public async Task<MirrorPulseNamespaceRetirement> StartNamespaceTransferSourceRetirementAsync(Guid operationId,
        DateTimeOffset startedAt, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var (copy, retirement) = await ReadTransferRetirementAsync(operationId, transaction, token).ConfigureAwait(false);
            if (retirement.StartedAt is not null) { transaction.Commit(); return retirement; }
            if (retirement.Phase != MirrorPulseNamespaceRetirementPhase.Prepared || startedAt < retirement.Intent.PreparedAt)
                throw new InvalidOperationException("The original local retirement start must follow admission.");
            var target = await RequireCurrentTransferSourceDeletionAsync(copy, transaction, token).ConfigureAwait(false);
            if (target.Context.Protection.ProtectionId != retirement.Intent.TransferEntry!.TargetProtectionId)
                throw new InvalidOperationException("The target epoch changed after original source retirement admission.");
            var started = retirement with
            {
                StartedAt = startedAt,
                StartedItemId = copy.Plan.Source.Identity.ItemId
                ?? throw new InvalidDataException("Source retirement requires the original public logical item.")
            };
            await WriteNamespaceRetirementCoreAsync(started, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return started;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Records an actual original native deletion and public SDK tombstone; no journal is acknowledged.</summary>
    public async Task<MirrorPulseNamespaceRetirement> RecordNamespaceTransferSourceRetiredAsync(Guid operationId,
        MirrorPulseNamespaceTransferRetirementReceipt receipt, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var (copy, retirement) = await ReadTransferRetirementAsync(operationId, transaction, token).ConfigureAwait(false);
            ValidateTransferRetirementReceipt(copy, retirement, receipt);
            if (retirement.TransferReceipt is not null)
            {
                if (retirement.TransferReceipt != receipt) throw new InvalidOperationException("The first original native retirement receipt is immutable.");
                transaction.Commit(); return retirement;
            }
            if (retirement.Phase != MirrorPulseNamespaceRetirementPhase.Prepared)
                throw new InvalidOperationException("A source retirement cannot replace a different terminal outcome.");
            var deleted = retirement with
            {
                Phase = MirrorPulseNamespaceRetirementPhase.NativeDeleted,
                NativeDeletedAt = receipt.VerifiedAt,
                TransferReceipt = receipt
            };
            await WriteNamespaceRetirementCoreAsync(deleted, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return deleted;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Settles only the original source name after its native receipt, keeping transfer fences and journal ownership.</summary>
    public async Task<MirrorPulseNamespaceRetirement> CompleteNamespaceTransferSourceRetirementAsync(Guid operationId,
        DateTimeOffset completedAt, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var (copy, retirement) = await ReadTransferRetirementAsync(operationId, transaction, token).ConfigureAwait(false);
            if (retirement.Phase == MirrorPulseNamespaceRetirementPhase.Completed) { transaction.Commit(); return retirement; }
            if (retirement.Phase != MirrorPulseNamespaceRetirementPhase.NativeDeleted || retirement.TransferReceipt is null ||
                completedAt < retirement.NativeDeletedAt)
                throw new InvalidOperationException("Source name settlement requires its original native/public retirement receipt.");
            await RequireNoRegisteredDescendantsAsync(retirement.Intent.MemberId, transaction, token).ConfigureAwait(false);
            var location = await ReadNamespaceMemberLocationCoreAsync(copy.Plan.Source.Context.Member, transaction, token).ConfigureAwait(false);
            if (location != copy.Plan.Source.Context.Location)
                throw new InvalidDataException("The original source position changed before transfer name settlement.");
            await RequireNamespaceMemberNameAsync(copy.Plan.Source.Context.Member, transaction, token).ConfigureAwait(false);
            await using var remove = _connection.CreateCommand(); remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM namespace_member_names WHERE member_id=$member;";
            remove.Parameters.AddWithValue("$member", retirement.Intent.MemberId.ToString("D"));
            if (await remove.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                throw new InvalidDataException("The transfer source lost its original owned name before settlement.");
            var completed = retirement with { Phase = MirrorPulseNamespaceRetirementPhase.Completed, CompletedAt = completedAt };
            await WriteNamespaceRetirementCoreAsync(completed, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return completed;
        }
        finally { _gate.Release(); }
    }

    private async Task<(MirrorPulseNamespaceTransferCopy Copy, MirrorPulseNamespaceRetirement Retirement)> ReadTransferRetirementAsync(
        Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original transfer is missing.");
        var retirement = await ReadNamespaceRetirementCoreAsync(copy.Plan.DeleteOperationId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original source retirement is missing.");
        if (retirement.Intent.TransferEntry?.OperationId != operationId)
            throw new InvalidDataException("Another owner cannot retire the original transfer source.");
        return (copy, retirement);
    }

    private async Task ValidateTransferRetirementReferencesAsync(MirrorPulseNamespaceRetirement retirement,
        SqliteTransaction? transaction, CancellationToken token, MirrorPulseNamespaceTransferCopy? ownedTransfer = null)
    {
        if (retirement.Intent.TransferEntry is not { } reference)
        {
            if (retirement.TransferReceipt is not null) throw new InvalidDataException("A non-transfer retirement borrowed a transfer receipt.");
            return;
        }
        var copy = ownedTransfer ?? await ReadNamespaceTransferCoreAsync(reference.OperationId,
            transaction ?? throw new InvalidDataException("Transfer retirement validation requires a consistent transaction."), token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Source retirement lost its original transfer.");
        ValidateTransferRetirementReference(copy, retirement.Intent);
        var protection = await ReadNamespaceBirthProtectionCoreAsync(reference.TargetProtectionId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Source retirement lost its original target protection epoch.");
        if (protection.BirthOperationId != copy.TargetBirth!.Birth.OperationId ||
            protection.Verification.LocalObject != copy.TargetPublication!.LocalObject ||
            protection.RoleSid != retirement.Intent.RoleSid || protection.Verification.ObservedAt > retirement.Intent.PreparedAt ||
            retirement.StartedItemId is { } item && item != copy.Plan.Source.Identity.ItemId ||
            (retirement.StartedAt is not null) != (retirement.StartedItemId is not null) ||
            retirement.Phase == MirrorPulseNamespaceRetirementPhase.Prepared && (retirement.NativeDeletedAt is not null || retirement.CompletedAt is not null) ||
            retirement.Phase == MirrorPulseNamespaceRetirementPhase.NativeDeleted && retirement.CompletedAt is not null ||
            retirement.Phase == MirrorPulseNamespaceRetirementPhase.Completed && retirement.CompletedAt is null ||
            retirement.Phase is not (MirrorPulseNamespaceRetirementPhase.Prepared or MirrorPulseNamespaceRetirementPhase.NativeDeleted or MirrorPulseNamespaceRetirementPhase.Completed) ||
            (retirement.Phase != MirrorPulseNamespaceRetirementPhase.Prepared) != (retirement.TransferReceipt is not null))
            throw new InvalidDataException("The source retirement lost its exact target epoch, original item or native receipt.");
        if (retirement.TransferReceipt is { } receipt)
        {
            ValidateTransferRetirementReceipt(copy, retirement, receipt);
            if (retirement.NativeDeletedAt != receipt.VerifiedAt || retirement.CompletedAt is { } complete && complete < receipt.VerifiedAt)
                throw new InvalidDataException("The source retirement chronology disagrees with its original native receipt.");
        }
    }

    private static void ValidateTransferRetirementReference(MirrorPulseNamespaceTransferCopy copy, MirrorPulseNamespaceRetirementIntent intent)
    {
        var reference = intent.TransferEntry!; var source = copy.Plan.Source;
        if (reference.Version != 1 || reference.OperationId != copy.Plan.OperationId || reference.DeleteOperationId != copy.Plan.DeleteOperationId ||
            copy.SourceDeletion?.AcceptedAt is not { } accepted || reference.SourceDeleteAcceptedAt != accepted || copy.TargetPublication is null ||
            copy.TargetBirth is null || source.Identity.ItemId is null || reference.TargetProtectionId == Guid.Empty || reference.TargetAtStart is null ||
            reference.TargetAtStart.RemoteId != copy.TargetVerification!.RemoteId || reference.TargetAtStart.VerifiedAt < accepted ||
            intent.PreparedAt < reference.TargetAtStart.VerifiedAt || intent.OperationId != copy.Plan.DeleteOperationId ||
            intent.MemberId != source.Context.Member.MemberId || intent.RootId != copy.Plan.SourceRoute.RootId ||
            intent.LocalObject != source.Context.Member.LocalObject || intent.RelativePath != source.Context.Location.RelativePath || intent.IsDirectory ||
            intent.OwnerSid != source.Context.Member.OwnerSid || intent.RoleSid != source.Context.Protection.RoleSid)
            throw new InvalidDataException("Source retirement requires the original accepted conditional transfer and a fresh original target proof.");
        ValidateNamespaceTransferTargetVerification(copy, reference.TargetAtStart);
    }

    private static void ValidateTransferRetirementReceipt(MirrorPulseNamespaceTransferCopy copy, MirrorPulseNamespaceRetirement retirement,
        MirrorPulseNamespaceTransferRetirementReceipt receipt)
    {
        var parent = copy.Plan.SourceParent;
        if (retirement.StartedAt is null || receipt.Version != 1 || receipt.OperationId != copy.Plan.OperationId ||
            receipt.DeleteOperationId != copy.Plan.DeleteOperationId || receipt.OfficialItemId != copy.Plan.Source.Identity.ItemId ||
            retirement.StartedItemId != receipt.OfficialItemId || receipt.LocalObject != copy.Plan.Source.Context.Member.LocalObject ||
            !receipt.NativeMissing || !receipt.OfficialTombstone || receipt.Parent is null || receipt.VerifiedAt < retirement.StartedAt ||
            receipt.Parent.LocalObject != parent.Context.Member.LocalObject || receipt.Parent.OwnerSid != parent.Context.Member.OwnerSid ||
            !MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(parent.ExpectedDacl, receipt.Parent.Dacl) ||
            receipt.Parent.ObservedAt < retirement.StartedAt || receipt.Parent.ObservedAt > receipt.VerifiedAt)
            throw new InvalidDataException("Source retirement requires its original guarded SDK deletion, tombstone and unchanged direct parent.");
    }

    private static void RejectGenericTransferRetirement(MirrorPulseNamespaceRetirementIntent intent)
    {
        if (intent.TransferEntry is not null)
            throw new InvalidOperationException("An original transfer source retirement requires its dedicated guarded continuation.");
    }
}
