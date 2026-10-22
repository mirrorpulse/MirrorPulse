using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Original verified copy ownership, independent of an SDK remote batch or generic import.</summary>
public sealed record MirrorPulseNamespaceTransferBirthReference(int Version, Guid OperationId, string TargetVerificationSha256)
{
    public static MirrorPulseNamespaceTransferBirthReference Create(MirrorPulseNamespaceTransferCopy copy)
    {
        ArgumentNullException.ThrowIfNull(copy);
        var verification = copy.TargetVerification ?? throw new InvalidOperationException("A target birth requires the original verified upload receipt.");
        return new(1, copy.Plan.OperationId, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(verification))));
    }
}

/// <summary>One immutable target birth and public identity plan, selected before native creation.</summary>
/// <remarks>The Host validates the opaque identity through its public SDK adapter. This catalog fact
/// is not a native binding, protection observation, content confirmation or source-delete authority.</remarks>
public sealed record MirrorPulseNamespaceTransferTargetBirth(int Version, Guid OperationId,
    MirrorPulseNamespaceBirthIntent Birth, MirrorPulseNamespaceBirthPlan Plan);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Atomically reserves the target name and original birth/identity under its verified transfer owner.</summary>
    public async Task<MirrorPulseNamespaceTransferCopy> PrepareNamespaceTransferTargetBirthAsync(
        MirrorPulseNamespaceTransferTargetBirth target, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var copy = await ReadNamespaceTransferCoreAsync(target.OperationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original transfer is missing.");
            ValidateNamespaceTransferTargetBirth(copy, target);
            if (copy.TargetBirth is { } retained)
            {
                if (retained != target) throw new InvalidOperationException("The first transfer target birth and identity are immutable.");
                transaction.Commit(); return copy;
            }
            await RequireCurrentNamespaceTransferAsync(copy.Plan, transaction, token, admitted: true).ConfigureAwait(false);
            await RequireNamespaceTransferIdAvailableAsync(target.Birth.OperationId, transaction, token).ConfigureAwait(false);
            await ValidateNamespaceBirthCurrentParentAsync(target.Birth, transaction, token).ConfigureAwait(false);
            await RequireNamespaceMoveRootUnfencedAsync(target.Birth.RootId, null, transaction, token,
                allowedTransfer: copy.Plan.OperationId).ConfigureAwait(false);
            await RequireNamespaceBirthUnoccupiedMemberNameAsync(target.Birth, transaction, token).ConfigureAwait(false);
            await using (var existing = _connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = """
                    SELECT 1 FROM namespace_permission_local_identities WHERE item_id=$item
                    UNION ALL SELECT 1 FROM namespace_birth_plans WHERE item_id=$item
                    UNION ALL SELECT 1 FROM namespace_remote_birth_entries WHERE item_id=$item LIMIT 1;
                    """;
                existing.Parameters.AddWithValue("$item", target.Plan.ItemId.ToString("D"));
                if (await existing.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("A transfer target cannot adopt an existing public item identity.");
            }
            await InsertNamespaceTransferBirthArtifactsAsync(target, transaction, token).ConfigureAwait(false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(target, TopologyJsonOptions);
            if (payload.Length > 262_144) throw new ArgumentException("The transfer target birth is not bounded.", nameof(target));
            await using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO namespace_transfer_target_births(transfer_id,birth_id,item_id,payload,fingerprint) VALUES($transfer,$birth,$item,$payload,$hash);";
                insert.Parameters.AddWithValue("$transfer", target.OperationId.ToString("D"));
                insert.Parameters.AddWithValue("$birth", target.Birth.OperationId.ToString("D"));
                insert.Parameters.AddWithValue("$item", target.Plan.ItemId.ToString("D"));
                insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            copy = copy with { TargetBirthOperationId = target.Birth.OperationId, TargetBirth = target };
            await WriteNamespaceTransferCoreAsync(copy, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return copy;
        }
        finally { _gate.Release(); }
    }

    private static void ValidateNamespaceTransferTargetBirth(MirrorPulseNamespaceTransferCopy copy, MirrorPulseNamespaceTransferTargetBirth target)
    {
        ArgumentNullException.ThrowIfNull(target.Birth); ArgumentNullException.ThrowIfNull(target.Plan);
        var parent = copy.Plan.TargetParent;
        if (target.Version != 1 || target.OperationId != copy.Plan.OperationId || copy.TargetVerification is null ||
            copy.CopyAcceptedAt is null || copy.CopyStartedAt is null || target.Birth.TransferEntry != MirrorPulseNamespaceTransferBirthReference.Create(copy))
            throw new ArgumentException("A target birth requires its original accepted and whole-verified transfer.", nameof(target));
        ValidateNamespaceTransferTargetVerification(copy, copy.TargetVerification);
        ValidateNamespaceBirth(target.Birth); ValidateNamespaceBirthPlan(target.Plan); ValidateNamespaceBirthPlanReference(target.Plan, target.Birth);
        if (target.Birth.Version != 4 || target.Birth.Origin != MirrorPulseNamespaceBirthOrigin.TransferTarget || target.Birth.IsDirectory ||
            new[] { copy.Plan.OperationId, copy.Plan.CopyOperationId, copy.Plan.DeleteOperationId, copy.Plan.PrivateContentId }.Contains(target.Birth.OperationId) ||
            target.Birth.RootId != copy.Plan.TargetRoute.RootId || target.Birth.RelativePath != copy.Plan.TargetRelativePath ||
            target.Birth.ParentLocalObject != parent.Context.Member.LocalObject || target.Birth.RoleSid != parent.Context.Protection.RoleSid ||
            target.Birth.BornParent != parent.BornParent ||
            target.Birth.ParentEvidenceId != (parent.BornParent is null ? parent.Context.Member.OriginId : Guid.Empty) ||
            target.Birth.ParentPermissionOperationId != (parent.BornParent is null ? parent.PermissionOperationId : Guid.Empty) ||
            target.Birth.PreparedAt < copy.TargetVerification.VerifiedAt || target.Plan.PreparedAt != target.Birth.PreparedAt ||
            target.Plan.Version != 2 || target.Plan.Content != new MirrorPulseNamespaceImportContent(copy.Plan.Length, copy.Plan.Sha256) ||
            target.Plan.RemoteRevision != copy.AcceptedRevision || target.Plan.ItemId == copy.Plan.Source.Identity.ItemId)
            throw new ArgumentException("The target birth must retain its separate identity, exact protected destination and original accepted bytes.", nameof(target));
    }

    private async Task InsertNamespaceTransferBirthArtifactsAsync(MirrorPulseNamespaceTransferTargetBirth target,
        SqliteTransaction transaction, CancellationToken token)
    {
        var birth = target.Birth; byte[] birthPayload = JsonSerializer.SerializeToUtf8Bytes(birth, TopologyJsonOptions);
        byte[] planPayload = JsonSerializer.SerializeToUtf8Bytes(target.Plan, TopologyJsonOptions);
        await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO namespace_birth_intents(operation_id,parent_evidence_id,parent_operation_id,parent_birth_operation_id,
                parent_birth_protection_id,relative_path_key,payload,fingerprint)
            VALUES($birth,$original,$permission,$born,$epoch,$path,$birthPayload,$birthHash);
            INSERT INTO namespace_birth_reservations(parent_kind,parent_id,child_name_key,operation_id) VALUES($kind,$parent,$name,$birth);
            INSERT INTO namespace_birth_plans(operation_id,item_id,payload,fingerprint) VALUES($birth,$item,$planPayload,$planHash);
            """;
        insert.Parameters.AddWithValue("$birth", birth.OperationId.ToString("D"));
        insert.Parameters.AddWithValue("$original", birth.BornParent is null ? birth.ParentEvidenceId.ToString("D") : DBNull.Value);
        insert.Parameters.AddWithValue("$permission", birth.BornParent is null ? birth.ParentPermissionOperationId.ToString("D") : DBNull.Value);
        insert.Parameters.AddWithValue("$born", (object?)birth.BornParent?.BirthOperationId.ToString("D") ?? DBNull.Value);
        insert.Parameters.AddWithValue("$epoch", (object?)birth.BornParent?.ProtectionId.ToString("D") ?? DBNull.Value);
        insert.Parameters.AddWithValue("$path", birth.RelativePath.ToUpperInvariant());
        insert.Parameters.AddWithValue("$kind", birth.BornParent is null ? 0 : 1);
        insert.Parameters.AddWithValue("$parent", NamespaceBirthParentId(birth).ToString("D"));
        insert.Parameters.AddWithValue("$name", birth.RelativePath[(birth.RelativePath.LastIndexOf('/') + 1)..].ToUpperInvariant());
        insert.Parameters.AddWithValue("$item", target.Plan.ItemId.ToString("D"));
        insert.Parameters.AddWithValue("$birthPayload", birthPayload); insert.Parameters.AddWithValue("$birthHash", SHA256.HashData(birthPayload));
        insert.Parameters.AddWithValue("$planPayload", planPayload); insert.Parameters.AddWithValue("$planHash", SHA256.HashData(planPayload));
        await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async Task<MirrorPulseNamespaceTransferTargetBirth?> ReadNamespaceTransferTargetBirthCoreAsync(MirrorPulseNamespaceTransferCopy copy,
        SqliteTransaction transaction, CancellationToken token)
    {
        var target = await ReadNamespaceTransferTargetBirthRowAsync(copy.Plan.OperationId, null, transaction, token).ConfigureAwait(false);
        if (target is null)
        {
            if (copy.TargetBirthOperationId is not null) throw new InvalidDataException("The transfer lost its original target birth owner.");
            return null;
        }
        if (copy.TargetBirthOperationId != target.Birth.OperationId) throw new InvalidDataException("The transfer target birth pointer changed.");
        try { ValidateNamespaceTransferTargetBirth(copy, target); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { throw new InvalidDataException("The original transfer target birth is inconsistent.", error); }
        if (await ReadNamespaceBirthRowAsync(target.Birth.OperationId, transaction, token).ConfigureAwait(false) != target.Birth ||
            await ReadNamespaceBirthPlanRowAsync(target.Birth.OperationId, transaction, token).ConfigureAwait(false) != target.Plan)
            throw new InvalidDataException("The original target birth lost its exact admission or public identity plan.");
        return target;
    }

    private async Task<MirrorPulseNamespaceTransferTargetBirth?> ReadNamespaceTransferTargetBirthRowAsync(Guid? transferId, Guid? birthId,
        SqliteTransaction? transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT transfer_id,birth_id,item_id,payload,fingerprint FROM namespace_transfer_target_births WHERE transfer_id=$transfer OR birth_id=$birth;";
        query.Parameters.AddWithValue("$transfer", (object?)transferId?.ToString("D") ?? DBNull.Value);
        query.Parameters.AddWithValue("$birth", (object?)birthId?.ToString("D") ?? DBNull.Value);
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var target = ReadNamespaceMovePayload<MirrorPulseNamespaceTransferTargetBirth>((byte[])reader[3], (byte[])reader[4], 262_144, value =>
        {
            if (value.Version != 1 || value.OperationId == Guid.Empty || value.Birth is null || value.Plan is null)
                throw new ArgumentException("The transfer target owner is incomplete.");
            ValidateNamespaceBirth(value.Birth); ValidateNamespaceBirthPlan(value.Plan); ValidateNamespaceBirthPlanReference(value.Plan, value.Birth);
        });
        if (reader.GetString(0) != target.OperationId.ToString("D") || reader.GetString(1) != target.Birth.OperationId.ToString("D") ||
            reader.GetString(2) != target.Plan.ItemId.ToString("D") || await reader.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidDataException("The transfer target owner index is ambiguous or inconsistent.");
        return target;
    }

    private async Task ValidateNamespaceTransferBirthReferenceIndexAsync(MirrorPulseNamespaceBirthIntent birth, SqliteTransaction? transaction, CancellationToken token)
    {
        var target = await ReadNamespaceTransferTargetBirthRowAsync(null, birth.OperationId, transaction, token).ConfigureAwait(false);
        if (birth.TransferEntry is null)
        {
            if (target is not null) throw new InvalidDataException("A generic birth cannot adopt transfer target ownership.");
            return;
        }
        if (target is null || target.OperationId != birth.TransferEntry.OperationId || target.Birth != birth)
            throw new InvalidDataException("The birth lost its exact original transfer owner.");
        if (await ReadNamespaceBirthPlanRowAsync(birth.OperationId, transaction, token).ConfigureAwait(false) != target.Plan)
            throw new InvalidDataException("The transfer target lost its immutable original public identity plan.");
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload,fingerprint FROM namespace_transfers WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", target.OperationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidDataException("The original target birth lost its transfer admission.");
        var copy = ReadNamespaceMovePayload<MirrorPulseNamespaceTransferCopy>((byte[])reader[0], (byte[])reader[1], 2_097_152, ValidateNamespaceTransferCopyProgress);
        if (copy.Plan.OperationId != target.OperationId || copy.TargetBirthOperationId != birth.OperationId)
            throw new InvalidDataException("The target birth no longer has its original transfer pointer.");
        try { ValidateNamespaceTransferTargetBirth(copy, target); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { throw new InvalidDataException("The target birth transfer receipt changed.", error); }
    }

    private static void ValidateNamespaceTransferCopyProgress(MirrorPulseNamespaceTransferCopy copy)
    {
        ValidateNamespaceTransferPlan(copy.Plan);
        if (copy.CopyStartedAt is { } start && start < copy.Plan.PreparedAt || (copy.AcceptedRevision is null) != (copy.CopyAcceptedAt is null) ||
            copy.CopyAcceptedAt is { } accepted && (copy.CopyStartedAt is null || accepted < copy.CopyStartedAt || InvalidRemoteUpdateText(copy.AcceptedRevision, 4096)) ||
            copy.TargetBirthOperationId is { } birth && (birth == Guid.Empty || new[] { copy.Plan.OperationId, copy.Plan.CopyOperationId, copy.Plan.DeleteOperationId, copy.Plan.PrivateContentId }.Contains(birth)))
            throw new ArgumentException("Transfer progress lost its distinct original effects or chronology.");
        if (copy.TargetVerification is { } verification) ValidateNamespaceTransferTargetVerification(copy, verification);
        if (copy.TargetBirthOperationId is not null && copy.TargetVerification is null)
            throw new ArgumentException("A transfer birth cannot precede the original whole-target verification.");
        if (copy.TargetPublication is { } publication) ValidateNamespaceTransferPublicationShape(copy, publication);
        if (copy.SourceDeletion is { } deletion) ValidateNamespaceTransferSourceDeletion(copy, deletion);
        if (copy.JournalClaim is { } claim) ValidateNamespaceTransferJournalClaim(copy.Plan, claim);
        if (copy.JournalClaim is { } originalClaim && copy.CopyStartedAt < originalClaim.CapturedAt)
            throw new ArgumentException("The original journal reservation must precede the first copy invocation.");
        if (copy.Acknowledgement is { } acknowledgement) ValidateNamespaceTransferAcknowledgement(copy, acknowledgement);
    }

    private static async Task EnsureNamespaceTransferBirthSchemaAsync(SqliteConnection connection, bool requireExisting, bool stampVersion, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction(); await using var schema = connection.CreateCommand(); schema.Transaction = transaction;
        if (requireExisting)
        {
            schema.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='namespace_transfer_target_births';";
            if (Convert.ToInt64(await schema.ExecuteScalarAsync(token).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 1)
                throw new InvalidDataException("Schema81 lost its original transfer target birth ownership; empty history cannot replace it.");
        }
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_transfer_target_births(
                transfer_id TEXT NOT NULL UNIQUE REFERENCES namespace_transfers(operation_id),birth_id TEXT PRIMARY KEY,
                item_id TEXT NOT NULL UNIQUE,payload BLOB NOT NULL,fingerprint BLOB NOT NULL);
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (stampVersion) { schema.CommandText = "PRAGMA user_version=81;"; await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        transaction.Commit();
    }
}
