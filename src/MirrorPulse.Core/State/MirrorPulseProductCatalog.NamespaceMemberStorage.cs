using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private sealed record NamespaceMemberProof(Guid ProtectionId, string RoleSid, DateTimeOffset VerifiedAt);

    private async Task<NamespaceMemberProof> ReadNamespaceMemberProofAsync(MirrorPulseNamespaceMemberRegistration member,
        bool current, SqliteTransaction transaction, CancellationToken token, Guid? protectionId = null, DateTimeOffset? retainedAt = null)
    {
        NamespaceMemberProof proof;
        if (member.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal)
        {
            var original = await ReadPermissionBaselineCoreAsync(member.OriginId, token, transaction).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The member's pre-protection original is missing.");
            var permission = current
                ? await ReadLatestPermissionChangeCoreAsync(member.OriginId, token, transaction).ConfigureAwait(false)
                : await ReadPermissionChangeCoreAsync(protectionId ?? member.ProtectionId, token, transaction).ConfigureAwait(false);
            if (permission is null || permission.Intent.EvidenceId != original.EvidenceId ||
                permission.Intent.LocalObject != member.LocalObject || original.LocalObject != member.LocalObject ||
                original.OwnerSid != member.OwnerSid || original.IsDirectory != member.IsDirectory ||
                permission.Intent.RootId != member.RootId ||
                permission.Phase != MirrorPulseNamespacePermissionPhase.Verified || permission.Intent.Kind == MirrorPulseNamespacePermissionChangeKind.Restore)
                throw new InvalidOperationException("Original membership requires verified protection of its retained object and location.");
            // Reject a future epoch before following its completed-move references. This
            // also prevents cyclic protection/move references from recursing through history.
            if (!current && (retainedAt ?? member.RegisteredAt) < permission.Verification!.ObservedAt)
                throw new InvalidOperationException("Membership cannot predate its retained protection verification.");
            if (permission.Intent.Relocation is null)
            {
                if (permission.Intent.RelativePath != member.RelativePath)
                    throw new InvalidOperationException("Unlocated permission epochs must retain the first registered path.");
            }
            else
            {
                if (permission.Intent.Relocation.MemberId != member.MemberId)
                    throw new InvalidOperationException("The permission epoch belongs to another registered member.");
                await ValidatePermissionMemberLocationAsync(original, permission.Intent, current: false, transaction, token).ConfigureAwait(false);
            }
            if (current) await ValidatePermissionTreeAdmissionAsync(original, permission.Intent, transaction, token).ConfigureAwait(false);
            proof = new(permission.Intent.OperationId, permission.Intent.RoleSid, permission.Verification!.ObservedAt);
        }
        else
        {
            var birth = await ReadNamespaceBirthCoreAsync(member.OriginId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original member birth is missing.");
            var observation = await ReadNamespaceBirthObservationCoreAsync(member.OriginId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The member has no native birth observation.");
            var protection = current
                ? await ReadLatestNamespaceBirthProtectionCoreAsync(member.OriginId, transaction, token).ConfigureAwait(false)
                : await ReadNamespaceBirthProtectionCoreAsync(protectionId ?? member.ProtectionId, transaction, token).ConfigureAwait(false);
            if (protection is null) throw new FileNotFoundException("The member's verified birth protection is missing.");
            if (protection.BirthOperationId != member.OriginId ||
                observation.LocalObject != member.LocalObject || observation.OwnerSid != member.OwnerSid ||
                observation.IsDirectory != member.IsDirectory || birth.RootId != member.RootId || birth.RelativePath != member.RelativePath)
                throw new InvalidOperationException("Born membership must retain its first observed object, owner and location.");
            if (member.ParentMemberId is not { } parentId)
                throw new InvalidOperationException("A born member requires its stable direct parent.");
            var parent = await ReadNamespaceMemberRowAsync(parentId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The historical direct birth parent is missing.");
            if (parent.Origin != (birth.BornParent is null ? MirrorPulseNamespaceMemberOrigin.CapturedOriginal : MirrorPulseNamespaceMemberOrigin.ProtectedBirth) ||
                parent.OriginId != NamespaceBirthParentId(birth) || parent.LocalObject != birth.ParentLocalObject)
                throw new InvalidOperationException("Born membership cannot substitute another stable direct parent.");
            if (current) _ = await ValidateNamespaceBirthLineageAsync(birth, protection, true, transaction, token).ConfigureAwait(false);
            proof = new(protection.ProtectionId, protection.RoleSid, protection.Verification.ObservedAt);
        }
        if (!current && (retainedAt ?? member.RegisteredAt) < proof.VerifiedAt)
            throw new InvalidOperationException("Membership cannot predate its retained protection verification.");
        return proof;
    }

    private async Task RequireNamespaceMemberNameAsync(MirrorPulseNamespaceMemberRegistration member,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (member.ParentMemberId is null) return;
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT parent_member_id,child_name_key FROM namespace_member_names WHERE member_id=$id;";
        query.Parameters.AddWithValue("$id", member.MemberId.ToString("D"));
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
            throw new InvalidDataException("The member lost its current owned name.");
        Guid currentParentId = ParseNamespaceMemberId(reader.GetString(0));
        var currentParent = await ReadNamespaceMemberRowAsync(currentParentId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The current member parent lost its provenance.");
        if (!currentParent.IsDirectory ||
            (currentParent.RootId is not null && currentParent.RootId != member.RootId) ||
            !await IsCurrentNamespaceMemberNameAsync(member, currentParentId, reader.GetString(1), transaction, token).ConfigureAwait(false))
            throw new InvalidDataException("The current member name points outside its managed directory.");
    }

    private async Task<bool> IsCurrentNamespaceMemberNameAsync(MirrorPulseNamespaceMemberRegistration member, Guid parentMemberId, string nameKey,
        SqliteTransaction transaction, CancellationToken token)
    {
        var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, token).ConfigureAwait(false);
        return location.ParentMemberId == parentMemberId && NamespaceMemberNameKey(location.RelativePath) == nameKey;
    }

    private async Task RequireNamespaceMemberPendingNameAsync(MirrorPulseNamespaceMemberRegistration member,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (member.ParentMemberId is not { } parentId) return;
        var parent = await ReadNamespaceMemberRowAsync(parentId, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The registered direct parent is missing.");
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT operation_id FROM namespace_birth_reservations WHERE parent_kind=$kind AND parent_id=$parent AND child_name_key=$name;
            """;
        query.Parameters.AddWithValue("$kind", (int)parent.Origin);
        query.Parameters.AddWithValue("$parent", parent.OriginId.ToString("D"));
        query.Parameters.AddWithValue("$name", NamespaceMemberNameKey(member.RelativePath));
        object? reservation = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (reservation is not null && (member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth ||
            ParseNamespaceMemberId(reservation) != member.OriginId))
            throw new InvalidOperationException("An unresolved birth owns the direct child name.");
    }

    private async Task RequireNamespaceBirthUnoccupiedMemberNameAsync(MirrorPulseNamespaceBirthIntent birth,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = birth.BornParent is null
            ? """
                SELECT n.member_id FROM namespace_member_names n JOIN namespace_members p ON p.member_id=n.parent_member_id
                WHERE p.original_evidence_id=$parent AND n.child_name_key=$name;
                """
            : """
                SELECT n.member_id FROM namespace_member_names n JOIN namespace_members p ON p.member_id=n.parent_member_id
                WHERE p.birth_operation_id=$parent AND n.child_name_key=$name;
                """;
        query.Parameters.AddWithValue("$parent", NamespaceBirthParentId(birth).ToString("D"));
        query.Parameters.AddWithValue("$name", NamespaceMemberNameKey(birth.RelativePath));
        object? value = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (value is not null)
        {
            _ = await ReadNamespaceMemberCoreAsync(ParseNamespaceMemberId(value), transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The occupied member name lost its provenance.");
            throw new InvalidOperationException("A registered member already owns the direct child name.");
        }
    }

    private async Task<MirrorPulseNamespaceMemberRegistration?> ReadNamespaceMemberRowAsync(Guid id,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = """
            SELECT member_id,origin_kind,original_evidence_id,birth_operation_id,permission_operation_id,
                birth_protection_id,parent_member_id,volume_serial,sync_root_file_id,local_file_id,payload,fingerprint
            FROM namespace_members WHERE member_id=$id;
            """;
        query.Parameters.AddWithValue("$id", id.ToString("D"));
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        byte[] payload = (byte[])reader[10], fingerprint = (byte[])reader[11];
        if (payload.Length > 131_072 || fingerprint.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), fingerprint))
            throw new InvalidDataException("The member provenance fingerprint is invalid.");
        MirrorPulseNamespaceMemberRegistration member;
        try
        {
            member = JsonSerializer.Deserialize<MirrorPulseNamespaceMemberRegistration>(payload, TopologyJsonOptions)
                ?? throw new InvalidDataException("The member provenance is empty.");
            ValidateNamespaceMember(member);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        { throw new InvalidDataException("The member provenance is invalid.", exception); }
        bool original = member.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal;
        if (member.MemberId != id || reader.GetString(0) != id.ToString("D") || reader.GetInt32(1) != (int)member.Origin ||
            Text(2) != (original ? member.OriginId.ToString("D") : null) || Text(3) != (original ? null : member.OriginId.ToString("D")) ||
            Text(4) != (original ? member.ProtectionId.ToString("D") : null) || Text(5) != (original ? null : member.ProtectionId.ToString("D")) ||
            Text(6) != member.ParentMemberId?.ToString("D") || reader.GetString(7) != member.LocalObject.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture) ||
            reader.GetString(8) != member.LocalObject.SyncRootFileId.ToString("D") || reader.GetString(9) != member.LocalObject.LocalFileId.ToString("D") ||
            !JsonSerializer.SerializeToUtf8Bytes(member, TopologyJsonOptions).AsSpan().SequenceEqual(payload))
            throw new InvalidDataException("The member provenance index or canonical payload is inconsistent.");
        return member;

        string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    }

    private static void AddNamespaceMemberParameters(SqliteCommand command, MirrorPulseNamespaceMemberRegistration member)
    {
        bool original = member.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal;
        command.Parameters.AddWithValue("$id", member.MemberId.ToString("D"));
        command.Parameters.AddWithValue("$kind", (int)member.Origin);
        command.Parameters.AddWithValue("$original", original ? member.OriginId.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$birth", original ? DBNull.Value : member.OriginId.ToString("D"));
        command.Parameters.AddWithValue("$permission", original ? member.ProtectionId.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$protection", original ? DBNull.Value : member.ProtectionId.ToString("D"));
        command.Parameters.AddWithValue("$parent", (object?)member.ParentMemberId?.ToString("D") ?? DBNull.Value);
        AddNamespaceBirthBindingParameters(command, member.LocalObject);
    }

    private static async Task EnsureNamespaceMemberSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        await using SqliteCommand schema = connection.CreateCommand();
        schema.Transaction = transaction;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_members (
                member_id TEXT NOT NULL PRIMARY KEY,
                origin_kind INTEGER NOT NULL CHECK(origin_kind IN (0,1)),
                original_evidence_id TEXT NULL UNIQUE REFERENCES namespace_permission_baselines(evidence_id),
                birth_operation_id TEXT NULL UNIQUE REFERENCES namespace_birth_observations(operation_id),
                permission_operation_id TEXT NULL REFERENCES namespace_permission_changes(operation_id),
                birth_protection_id TEXT NULL REFERENCES namespace_birth_protections(protection_id),
                parent_member_id TEXT NULL REFERENCES namespace_members(member_id),
                volume_serial TEXT NOT NULL,
                sync_root_file_id TEXT NOT NULL,
                local_file_id TEXT NOT NULL,
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
                UNIQUE(volume_serial,sync_root_file_id,local_file_id),
                CHECK((origin_kind=0 AND original_evidence_id IS NOT NULL AND permission_operation_id IS NOT NULL
                    AND birth_operation_id IS NULL AND birth_protection_id IS NULL) OR
                    (origin_kind=1 AND original_evidence_id IS NULL AND permission_operation_id IS NULL
                    AND birth_operation_id IS NOT NULL AND birth_protection_id IS NOT NULL))
            );
            CREATE TABLE IF NOT EXISTS namespace_member_names (
                member_id TEXT NOT NULL PRIMARY KEY REFERENCES namespace_members(member_id),
                parent_member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                child_name_key TEXT NOT NULL,
                UNIQUE(parent_member_id,child_name_key)
            );
            CREATE TABLE IF NOT EXISTS namespace_member_retirements (
                operation_id TEXT NOT NULL PRIMARY KEY,
                member_id TEXT NOT NULL REFERENCES namespace_members(member_id),
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32),
                phase INTEGER NOT NULL CHECK(phase IN (0,1,2,3))
            );
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        transaction.Commit();
    }
}
