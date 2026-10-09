using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>The original object and access descriptor, captured before MP first changes its DACL.</summary>
/// <remarks>RelativePath and RootId describe the capture location; later moves never rewrite this baseline.</remarks>
public sealed record MirrorPulseNamespacePermissionBaseline(Guid EvidenceId, RootId? RootId,
    MirrorPulseLocalFileBinding LocalObject, string RelativePath, bool IsDirectory,
    string OwnerSid, string OriginalDacl, DateTimeOffset CapturedAt);

public enum MirrorPulseNamespacePermissionChangeKind { Protect, RotateRole, Restore }
public enum MirrorPulseNamespacePermissionPhase { Prepared, Applied, Verified, RecoveryRequired }
public enum MirrorPulseNamespacePermissionRecoveryReason
{
    ObjectChanged, OwnerChanged, DaclChanged, ReparsePoint, AccessDenied, Interrupted, Failed,
}

/// <summary>An immutable per-object DACL change, not authorization to mutate the namespace.</summary>
public sealed record MirrorPulseNamespacePermissionIntent(Guid OperationId, Guid EvidenceId,
    MirrorPulseLocalFileBinding LocalObject, RootId? RootId, string RelativePath,
    MirrorPulseNamespacePermissionChangeKind Kind, string RoleSid, string ExpectedDacl,
    string TargetDacl, DateTimeOffset PreparedAt);

/// <summary>A separate read of the same object's owner and access descriptor after application.</summary>
public sealed record MirrorPulseNamespacePermissionVerification(MirrorPulseLocalFileBinding LocalObject,
    string OwnerSid, string Dacl, DateTimeOffset ObservedAt);

/// <summary>One object's retained application facts. Verified does not imply that a subtree is protected.</summary>
public sealed record MirrorPulseNamespacePermissionChange(MirrorPulseNamespacePermissionIntent Intent,
    MirrorPulseNamespacePermissionPhase Phase, DateTimeOffset? AppliedAt = null,
    MirrorPulseNamespacePermissionVerification? Verification = null,
    MirrorPulseNamespacePermissionRecoveryReason? RecoveryReason = null);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Atomically retains the original evidence and change intent before an ACL write.</summary>
    /// <remarks>The Windows coordinator must validate actual scope, handles, reparse state and policy first.</remarks>
    public async Task<MirrorPulseNamespacePermissionChange> PrepareNamespacePermissionChangeAsync(
        MirrorPulseNamespacePermissionBaseline baseline, MirrorPulseNamespacePermissionIntent intent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(intent);
        ValidatePermissionBaseline(baseline);
        ValidatePermissionIntent(intent);
        if (baseline.EvidenceId != intent.EvidenceId || baseline.LocalObject != intent.LocalObject ||
            intent.PreparedAt < baseline.CapturedAt)
            throw new ArgumentException("The permission intent does not refer to its original object evidence.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            MirrorPulseNamespacePermissionBaseline? retained = await ReadPermissionBaselineCoreAsync(
                baseline.EvidenceId, cancellationToken, transaction).ConfigureAwait(false);
            if (retained is not null && retained != baseline)
                throw new InvalidOperationException("The original permission evidence is immutable.");
            MirrorPulseNamespacePermissionChange? replay = await ReadPermissionChangeCoreAsync(
                intent.OperationId, cancellationToken, transaction).ConfigureAwait(false);
            if (replay is not null)
            {
                if (retained is null || replay.Intent != intent)
                    throw new InvalidOperationException("A permission operation cannot adopt different evidence.");
                return replay;
            }
            IReadOnlyList<MirrorPulseNamespacePermissionChange> history = await ReadPermissionChangesCoreAsync(
                baseline.EvidenceId, cancellationToken, transaction).ConfigureAwait(false);
            if (history.Any(change => change.Phase != MirrorPulseNamespacePermissionPhase.Verified))
                throw new InvalidOperationException("The object has an unresolved permission change.");
            MirrorPulseNamespacePermissionChange? previous = history.Count == 0 ? null : history[^1];
            if (previous is null && (intent.RootId != baseline.RootId || intent.RelativePath != baseline.RelativePath))
                throw new InvalidOperationException("Initial protection requires the original capture location.");
            if (intent.ExpectedDacl != (previous?.Intent.TargetDacl ?? baseline.OriginalDacl) ||
                previous is not null && intent.PreparedAt < previous.Verification!.ObservedAt)
                throw new InvalidOperationException("The expected DACL does not follow the retained verified history.");
            switch (intent.Kind)
            {
                case MirrorPulseNamespacePermissionChangeKind.Protect:
                    if (previous is not null && previous.Intent.Kind != MirrorPulseNamespacePermissionChangeKind.Restore)
                        throw new InvalidOperationException("An already protected object requires a role rotation.");
                    break;
                case MirrorPulseNamespacePermissionChangeKind.RotateRole:
                    if (previous is null || previous.Intent.Kind == MirrorPulseNamespacePermissionChangeKind.Restore ||
                        previous.Intent.RoleSid == intent.RoleSid)
                        throw new InvalidOperationException("Role rotation requires verified protection and a new role.");
                    break;
                case MirrorPulseNamespacePermissionChangeKind.Restore:
                    if (previous is null || previous.Intent.Kind == MirrorPulseNamespacePermissionChangeKind.Restore ||
                        previous.Intent.RoleSid != intent.RoleSid || intent.TargetDacl != baseline.OriginalDacl)
                        throw new InvalidOperationException("Restoration requires the owned DACL and exact original evidence.");
                    break;
            }
            if (retained is null)
            {
                await using SqliteCommand insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO namespace_permission_baselines
                        (evidence_id, volume_serial, sync_root_file_id, local_file_id, payload)
                    VALUES ($evidence, $volume, $sync, $file, $payload)
                    ON CONFLICT(volume_serial, sync_root_file_id, local_file_id) DO NOTHING;
                    """;
                insert.Parameters.AddWithValue("$evidence", baseline.EvidenceId.ToString("D"));
                insert.Parameters.AddWithValue("$volume", baseline.LocalObject.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$sync", baseline.LocalObject.SyncRootFileId.ToString("D"));
                insert.Parameters.AddWithValue("$file", baseline.LocalObject.LocalFileId.ToString("D"));
                insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(baseline, TopologyJsonOptions));
                if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                    throw new InvalidOperationException("This object already has original permission evidence.");
            }
            var change = new MirrorPulseNamespacePermissionChange(intent, MirrorPulseNamespacePermissionPhase.Prepared);
            await WritePermissionChangeCoreAsync(change, cancellationToken, transaction, insert: true).ConfigureAwait(false);
            transaction.Commit();
            return change;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespacePermissionBaseline?> ReadNamespacePermissionBaselineAsync(Guid evidenceId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadPermissionBaselineCoreAsync(evidenceId, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespacePermissionChange?> ReadNamespacePermissionChangeAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadPermissionChangeCoreAsync(operationId, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Returns retained changes in preparation order, including unresolved recovery records.</summary>
    public async Task<IReadOnlyList<MirrorPulseNamespacePermissionChange>> ReadNamespacePermissionChangesAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowIfDisposed(); return await ReadPermissionChangesCoreAsync(null, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Retains an application fact; a later independent read is still required.</summary>
    public async Task<MirrorPulseNamespacePermissionChange> RecordNamespacePermissionApplicationAsync(Guid operationId,
        MirrorPulseLocalFileBinding localObject, DateTimeOffset appliedAt, CancellationToken cancellationToken = default)
    {
        ValidatePermissionBinding(localObject);
        if (appliedAt == default) throw new ArgumentException("The application time is missing.", nameof(appliedAt));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            MirrorPulseNamespacePermissionChange change = await ReadPermissionChangeCoreAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The permission change is not registered.");
            if (change.Intent.LocalObject != localObject || appliedAt < change.Intent.PreparedAt)
                throw new InvalidOperationException("The application does not match the original object and intent.");
            if (change.AppliedAt is not null)
            {
                if (change.AppliedAt != appliedAt || change.Phase == MirrorPulseNamespacePermissionPhase.RecoveryRequired)
                    throw new InvalidOperationException("The retained permission application cannot be rewritten.");
                return change;
            }
            if (change.Phase != MirrorPulseNamespacePermissionPhase.Prepared)
                throw new InvalidOperationException("The permission change requires recovery.");
            var applied = change with { Phase = MirrorPulseNamespacePermissionPhase.Applied, AppliedAt = appliedAt };
            await WritePermissionChangeCoreAsync(applied, cancellationToken).ConfigureAwait(false);
            return applied;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespacePermissionChange> VerifyNamespacePermissionChangeAsync(Guid operationId,
        MirrorPulseNamespacePermissionVerification verification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verification);
        ValidatePermissionBinding(verification.LocalObject);
        ValidatePermissionSid(verification.OwnerSid, role: false);
        ValidatePermissionDacl(verification.Dacl);
        if (verification.ObservedAt == default) throw new ArgumentException("The verification time is missing.", nameof(verification));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            MirrorPulseNamespacePermissionChange change = await ReadPermissionChangeCoreAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The permission change is not registered.");
            MirrorPulseNamespacePermissionBaseline baseline = await ReadPermissionBaselineCoreAsync(change.Intent.EvidenceId,
                cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("The original permission evidence is missing.");
            if (change.AppliedAt is null || verification.ObservedAt < change.AppliedAt ||
                verification.LocalObject != baseline.LocalObject || verification.OwnerSid != baseline.OwnerSid ||
                verification.Dacl != change.Intent.TargetDacl)
                throw new InvalidOperationException("The permission verification does not match the retained application and original object.");
            if (change.Phase == MirrorPulseNamespacePermissionPhase.Verified)
            {
                if (change.Verification != verification)
                    throw new InvalidOperationException("The retained permission verification is immutable.");
                return change;
            }
            if (change.Phase != MirrorPulseNamespacePermissionPhase.Applied)
                throw new InvalidOperationException("The permission change requires recovery.");
            var verified = change with { Phase = MirrorPulseNamespacePermissionPhase.Verified, Verification = verification };
            await WritePermissionChangeCoreAsync(verified, cancellationToken).ConfigureAwait(false);
            return verified;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Fences an unresolved change without replacing any original or application evidence.</summary>
    public async Task<MirrorPulseNamespacePermissionChange> RequireNamespacePermissionRecoveryAsync(Guid operationId,
        MirrorPulseNamespacePermissionRecoveryReason reason, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            MirrorPulseNamespacePermissionChange change = await ReadPermissionChangeCoreAsync(operationId, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The permission change is not registered.");
            if (change.Phase == MirrorPulseNamespacePermissionPhase.Verified)
                throw new InvalidOperationException("A later observation cannot rewrite a historical verified change.");
            if (change.Phase == MirrorPulseNamespacePermissionPhase.RecoveryRequired)
            {
                if (change.RecoveryReason != reason) throw new InvalidOperationException("The recovery evidence is immutable.");
                return change;
            }
            var recovery = change with { Phase = MirrorPulseNamespacePermissionPhase.RecoveryRequired, RecoveryReason = reason };
            await WritePermissionChangeCoreAsync(recovery, cancellationToken).ConfigureAwait(false);
            return recovery;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespacePermissionBaseline?> ReadPermissionBaselineCoreAsync(Guid evidenceId,
        CancellationToken token, SqliteTransaction? transaction = null)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT payload, volume_serial, sync_root_file_id, local_file_id FROM namespace_permission_baselines WHERE evidence_id=$id;";
        query.Parameters.AddWithValue("$id", evidenceId.ToString("D"));
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        return ReadPermissionBaseline(reader, evidenceId, 0);
    }

    private static MirrorPulseNamespacePermissionBaseline ReadPermissionBaseline(SqliteDataReader reader, Guid evidenceId, int offset)
    {
        if (reader.IsDBNull(offset)) throw new InvalidDataException("The original permission evidence is missing.");
        var baseline = JsonSerializer.Deserialize<MirrorPulseNamespacePermissionBaseline>(reader.GetString(offset), TopologyJsonOptions)
            ?? throw new InvalidDataException("The permission baseline is invalid.");
        ValidatePermissionBaseline(baseline);
        if (baseline.EvidenceId != evidenceId ||
            baseline.LocalObject.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture) != reader.GetString(offset + 1) ||
            baseline.LocalObject.SyncRootFileId.ToString("D") != reader.GetString(offset + 2) || baseline.LocalObject.LocalFileId.ToString("D") != reader.GetString(offset + 3))
            throw new InvalidDataException("The permission baseline's object index does not match its evidence.");
        return baseline;
    }

    private async Task<MirrorPulseNamespacePermissionChange?> ReadPermissionChangeCoreAsync(Guid operationId,
        CancellationToken token, SqliteTransaction? transaction = null)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = PermissionChangeQuery + " WHERE c.operation_id=$id;";
        query.Parameters.AddWithValue("$id", operationId.ToString("D"));
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadPermissionChange(reader) : null;
    }

    private async Task<IReadOnlyList<MirrorPulseNamespacePermissionChange>> ReadPermissionChangesCoreAsync(Guid? evidenceId,
        CancellationToken token, SqliteTransaction? transaction = null)
    {
        await using SqliteCommand query = _connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = PermissionChangeQuery +
            (evidenceId is null ? string.Empty : " WHERE c.evidence_id=$id") + " ORDER BY c.rowid;";
        if (evidenceId is not null) query.Parameters.AddWithValue("$id", evidenceId.Value.ToString("D"));
        await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        var changes = new List<MirrorPulseNamespacePermissionChange>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) changes.Add(ReadPermissionChange(reader));
        return changes.AsReadOnly();
    }

    private static MirrorPulseNamespacePermissionChange ReadPermissionChange(SqliteDataReader reader)
    {
        var change = JsonSerializer.Deserialize<MirrorPulseNamespacePermissionChange>(reader.GetString(3), TopologyJsonOptions)
            ?? throw new InvalidDataException("The permission change is invalid.");
        ValidatePermissionIntent(change.Intent);
        MirrorPulseNamespacePermissionBaseline baseline = ReadPermissionBaseline(reader, change.Intent.EvidenceId, 4);
        if (change.Intent.OperationId.ToString("D") != reader.GetString(0) || change.Intent.EvidenceId.ToString("D") != reader.GetString(1) ||
            change.Intent.LocalObject != baseline.LocalObject || change.Intent.PreparedAt < baseline.CapturedAt ||
            (int)change.Phase != reader.GetInt32(2) || !Enum.IsDefined(change.Phase) ||
            change.AppliedAt is not null && change.AppliedAt < change.Intent.PreparedAt ||
            change.Phase == MirrorPulseNamespacePermissionPhase.Prepared && (change.AppliedAt is not null || change.Verification is not null) ||
            change.Phase == MirrorPulseNamespacePermissionPhase.Applied && (change.AppliedAt is null || change.Verification is not null) ||
            change.Phase == MirrorPulseNamespacePermissionPhase.Verified && (change.AppliedAt is null || change.Verification is null) ||
            (change.Phase == MirrorPulseNamespacePermissionPhase.RecoveryRequired) != (change.RecoveryReason is not null) ||
            change.RecoveryReason is not null && !Enum.IsDefined(change.RecoveryReason.Value))
            throw new InvalidDataException("The permission change's retained state is inconsistent.");
        if (change.Verification is not null && (change.Verification.LocalObject != change.Intent.LocalObject || change.Verification.OwnerSid != baseline.OwnerSid ||
            change.Verification.Dacl != change.Intent.TargetDacl || change.Verification.ObservedAt < change.AppliedAt))
            throw new InvalidDataException("The permission verification does not match its original intent.");
        if (change.Verification is not null)
        {
            ValidatePermissionSid(change.Verification.OwnerSid, role: false);
            ValidatePermissionDacl(change.Verification.Dacl);
        }
        return change;
    }

    private const string PermissionChangeQuery = """
        SELECT c.operation_id, c.evidence_id, c.phase, c.payload,
            b.payload, b.volume_serial, b.sync_root_file_id, b.local_file_id
        FROM namespace_permission_changes c
        LEFT JOIN namespace_permission_baselines b ON b.evidence_id=c.evidence_id
        """;

    private async Task WritePermissionChangeCoreAsync(MirrorPulseNamespacePermissionChange change, CancellationToken token,
        SqliteTransaction? transaction = null, bool insert = false)
    {
        await using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = insert
            ? "INSERT INTO namespace_permission_changes(operation_id,evidence_id,phase,payload) VALUES($id,$evidence,$phase,$payload);"
            : "UPDATE namespace_permission_changes SET phase=$phase,payload=$payload WHERE operation_id=$id AND evidence_id=$evidence;";
        command.Parameters.AddWithValue("$id", change.Intent.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$evidence", change.Intent.EvidenceId.ToString("D"));
        command.Parameters.AddWithValue("$phase", (int)change.Phase);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(change, TopologyJsonOptions));
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidDataException("The permission change could not be retained.");
    }

    private static void ValidatePermissionBaseline(MirrorPulseNamespacePermissionBaseline baseline)
    {
        ValidatePermissionBinding(baseline.LocalObject);
        ValidatePermissionLocation(baseline.RootId, baseline.RelativePath, baseline.LocalObject);
        ValidatePermissionSid(baseline.OwnerSid, role: false);
        ValidatePermissionDacl(baseline.OriginalDacl);
        if (baseline.EvidenceId == Guid.Empty || baseline.CapturedAt == default || baseline.RelativePath.Length == 0 && !baseline.IsDirectory)
            throw new ArgumentException("The original permission evidence is incomplete.");
    }

    private static void ValidatePermissionIntent(MirrorPulseNamespacePermissionIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ValidatePermissionBinding(intent.LocalObject);
        ValidatePermissionLocation(intent.RootId, intent.RelativePath, intent.LocalObject);
        ValidatePermissionSid(intent.RoleSid, role: true);
        ValidatePermissionDacl(intent.ExpectedDacl);
        ValidatePermissionDacl(intent.TargetDacl);
        if (intent.OperationId == Guid.Empty || intent.EvidenceId == Guid.Empty || intent.PreparedAt == default || !Enum.IsDefined(intent.Kind) ||
            intent.ExpectedDacl == intent.TargetDacl)
            throw new ArgumentException("The permission change intent is invalid.");
    }

    private static void ValidatePermissionBinding(MirrorPulseLocalFileBinding binding)
    {
        if (binding is null || binding.VolumeSerialNumber == 0 || binding.SyncRootFileId == Guid.Empty || binding.LocalFileId == Guid.Empty)
            throw new ArgumentException("The original namespace object binding is invalid.");
    }

    private static void ValidatePermissionLocation(RootId? rootId, string path, MirrorPulseLocalFileBinding binding)
    {
        if (path is null || path.Length > 32_767 || rootId?.Value == Guid.Empty ||
            (path.Length == 0) != (binding.LocalFileId == binding.SyncRootFileId) || (path.Length == 0) != (rootId is null))
            throw new ArgumentException("The permission evidence's root and relative location are inconsistent.");
        if (path.Length == 0) return;
        if (path.Contains('\\') || path.Split('/').Any(segment => segment.Length is 0 or > 255 || segment is "." or ".." ||
            segment.EndsWith(' ') || segment.EndsWith('.') || segment.Any(character => char.IsControl(character) || character is ':' or '<' or '>' or '"' or '|' or '?' or '*')))
            throw new ArgumentException("The permission evidence requires an exact relative namespace path.");
    }

    private static void ValidatePermissionSid(string sid, bool role)
    {
        if (string.IsNullOrEmpty(sid) || sid.Length > 184 || new SecurityIdentifier(sid).Value != sid ||
            role && (!sid.StartsWith("S-1-5-5-", StringComparison.Ordinal) || sid.Split('-').Length != 6))
            throw new ArgumentException("The namespace permission SID is invalid.");
    }

    private static void ValidatePermissionDacl(string dacl)
    {
        if (string.IsNullOrEmpty(dacl) || dacl.Length > 65_536)
            throw new ArgumentException("The namespace access descriptor is not bounded.");
        var descriptor = new RawSecurityDescriptor(dacl);
        if (descriptor.Owner is not null || descriptor.Group is not null || descriptor.SystemAcl is not null ||
            descriptor.DiscretionaryAcl is null || descriptor.BinaryLength > 65_536 || descriptor.GetSddlForm(AccessControlSections.Access) != dacl)
            throw new ArgumentException("Permission evidence requires a canonical DACL without owner, group or audit sections.");
    }
}
