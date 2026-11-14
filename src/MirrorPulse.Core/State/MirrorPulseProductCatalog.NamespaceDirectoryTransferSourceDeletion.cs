using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

internal sealed record MirrorPulseDirectoryTransferSourceDeletion(Guid TransferId,
    MirrorPulseDirectoryTransferTargetVerification FreshTarget, AdapterAtomicDirectoryDeletionRequest? Request,
    DateTimeOffset StartedAt, AdapterAtomicDirectoryDeletionReceipt? Receipt = null, DateTimeOffset? AcceptedAt = null);

public sealed partial class MirrorPulseProductCatalog
{
    private const string DirectoryTransferSourceDeletionTable = """
        CREATE TABLE namespace_directory_transfer_source_deletions(
            transfer_id TEXT PRIMARY KEY NOT NULL REFERENCES namespace_directory_transfer_target_verifications(transfer_id),
            payload BLOB NOT NULL CHECK(length(payload) BETWEEN 1 AND 8388608),
            fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32))
        """;

    private static async Task EnsureDirectoryTransferSourceDeletionSchemaAsync(SqliteConnection connection, long originalVersion,
        CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        await using var sql = connection.CreateCommand();
        sql.Transaction = transaction;
        sql.CommandText = "SELECT sql FROM sqlite_master WHERE name='namespace_directory_transfer_source_deletions';";
        var existing = await sql.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (existing is null && originalVersion < 113)
        {
            sql.CommandText = DirectoryTransferSourceDeletionTable;
            await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        else if (existing is not string text ||
            NormalizeRetiredTransferIndexSql(text) != NormalizeRetiredTransferIndexSql(DirectoryTransferSourceDeletionTable))
        {
            throw new InvalidDataException("The original directory transfer source deletion schema is missing or changed.");
        }
        if (originalVersion < 113)
        {
            sql.CommandText = "SELECT 1 FROM namespace_directory_transfer_source_deletions LIMIT 1;";
            if (await sql.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An earlier catalog cannot supply original directory source deletions.");
            sql.CommandText = "PRAGMA user_version=113;";
            await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }

    internal static AdapterDirectoryAdmissionRequest? CreateDirectoryTransferSourceAdmission(MirrorPulseDirectoryTransferAdmission plan)
    {
        int prefixLength = plan.SourceRoute.DirectoryName.Length + 1;
        var operations = plan.Members.Reverse().Where(member => member.SourceRevision is not null)
            .Select(member => new AdapterOriginalDirectoryOperation("Delete", new(member.DeleteOperationId,
                plan.SourceRoute.RootKey, member.Source.Protection.Context.Location.RelativePath[prefixLength..],
                Preconditions: new(member.SourceRevision), IsDirectory: member.Source.Protection.Context.Member.IsDirectory)))
            .ToArray();
        if (operations.Length == 0) return null;
        if (operations.Length > 2048)
            throw new NotSupportedException("The adapter's original directory deletion contract supports at most 2048 remote members.");
        var request = new AdapterDirectoryAdmissionRequest(1, plan.SourceRoute.InstanceId.Value,
            plan.Command.CommandId, operations, string.Empty);
        return request with { RequestFingerprint = request.CalculateFingerprint() };
    }

    internal static AdapterAtomicDirectoryDeletionRequest CreateDirectoryTransferSourceDeleteRequest(
        AdapterDirectoryAdmissionRequest original, AdapterDirectoryAdmission admitted)
    {
        admitted.RequireRequest(original);
        var manifest = BuildAtomicDirectoryPlan(original, admitted);
        string fingerprint = Convert.ToHexString(SHA256.HashData(EncodeDirectoryRetirementEvidence(manifest)));
        var request = new AdapterAtomicDirectoryDeletionRequest(1, original, admitted, fingerprint, string.Empty);
        return request with { RequestFingerprint = request.CalculateFingerprint() };
    }

    internal async Task<MirrorPulseDirectoryTransferSourceDeletion?> ReadNamespaceDirectoryTransferSourceDeletionAsync(
        Guid operationId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var result = await ReadDirectoryTransferSourceDeletionCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    internal async Task<MirrorPulseDirectoryTransferSourceDeletion> StartNamespaceDirectoryTransferSourceDeletionAsync(
        Guid operationId, MirrorPulseDirectoryTransferTargetVerification freshTarget,
        AdapterAtomicDirectoryDeletionRequest? request, DateTimeOffset startedAt, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            using var validation = BeginNamespaceRecoveryValidationReadPhase(transaction);
            var (plan, originalTarget) = await ReadDirectoryTransferBirthEvidenceAsync(operationId, transaction, token).ConfigureAwait(false);
            var prior = await ReadDirectoryTransferSourceDeletionCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            if (prior is not null)
            {
                if (prior.Request?.RequestFingerprint != request?.RequestFingerprint)
                    throw new InvalidOperationException("The first directory source deletion request is immutable.");
                transaction.Commit();
                return prior;
            }
            var start = new MirrorPulseDirectoryTransferSourceDeletion(operationId, freshTarget, request, startedAt,
                AcceptedAt: request is null ? startedAt : null);
            ValidateDirectoryTransferSourceDeletion(plan, originalTarget, start);
            await RequireNamespaceTransferRoutesAsync([plan.SourceRoute, plan.TargetRoute], transaction, token,
                requireSingleRoot: true).ConfigureAwait(false);
            await RequireCurrentDirectoryTransferPublicationAsync(plan, originalTarget, transaction, requireUnretiredSource: true, token)
                .ConfigureAwait(false);
            validation.Dispose();
            await WriteDirectoryTransferSourceDeletionAsync(start, true, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return start;
        }
        finally { _gate.Release(); }
    }

    internal async Task<MirrorPulseDirectoryTransferSourceDeletion> AcceptNamespaceDirectoryTransferSourceDeletionAsync(
        Guid operationId, AdapterAtomicDirectoryDeletionReceipt receipt, DateTimeOffset acceptedAt, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            using var validation = BeginNamespaceRecoveryValidationReadPhase(transaction);
            var retained = await ReadDirectoryTransferSourceDeletionCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The original directory source deletion has not started.");
            receipt.RequireRequest(retained.Request ?? throw new InvalidOperationException("A remote-absent source has no deletion receipt."));
            if (retained.Receipt is { } previous)
            {
                if (previous.Fingerprint() != receipt.Fingerprint())
                    throw new InvalidDataException("The first directory source deletion receipt is immutable.");
                transaction.Commit();
                return retained;
            }
            if (acceptedAt < retained.StartedAt) throw new ArgumentException("Acceptance cannot predate the original source deletion.");
            var accepted = retained with { Receipt = receipt, AcceptedAt = acceptedAt };
            validation.Dispose();
            await WriteDirectoryTransferSourceDeletionAsync(accepted, false, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return accepted;
        }
        finally { _gate.Release(); }
    }

    private async Task RequireCurrentDirectoryTransferPublicationAsync(MirrorPulseDirectoryTransferAdmission plan,
        MirrorPulseDirectoryTransferTargetVerification verified, SqliteTransaction transaction, bool requireUnretiredSource,
        CancellationToken token)
    {
        if (await ReadNamespaceMemberProtectionSnapshotCoreAsync(plan.TargetParent.Context.Member.MemberId, transaction, token)
                .ConfigureAwait(false) != plan.TargetParent)
            throw new InvalidOperationException("The original target parent changed before source retirement.");
        Guid operationId = plan.Command.CommandId;
        var members = new Dictionary<Guid, Guid> { [plan.SourceParent.Context.Member.MemberId] = plan.TargetParent.Context.Member.MemberId };
        var targetMembers = new HashSet<Guid>();
        Guid rootBirth = Guid.Empty;
        for (int ordinal = 0; ordinal < plan.Members.Count; ordinal++)
        {
            var source = plan.Members[ordinal].Source.Protection.Context;
            var target = await ReadDirectoryTransferBirthRowAsync(operationId, ordinal, null, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Source deletion requires every original target birth.");
            ValidateRetainedDirectoryTransferBirth(plan, verified, target);
            await ValidateDirectoryTransferBirthArtifactsAsync(target, transaction, token).ConfigureAwait(false);
            var snapshot = await ReadNamespaceMemberProtectionSnapshotCoreAsync(target.Birth.OperationId, transaction, token).ConfigureAwait(false);
            if (snapshot is null || snapshot.Observation is not { IsInSync: true } observation ||
                snapshot.Context.Member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth ||
                snapshot.Context.Member.OriginId != target.Birth.OperationId || snapshot.Context.Member.RootId != plan.TargetRoute.RootId ||
                snapshot.Context.Location.CurrentPositionOperationId is not null ||
                snapshot.Context.Location.ParentMemberId != members[source.Location.ParentMemberId!.Value] ||
                snapshot.Context.Location.RelativePath != target.Birth.RelativePath || snapshot.Context.Member.LocalObject != observation.LocalObject ||
                snapshot.Context.Protection.RoleSid != target.Birth.RoleSid || snapshot.Context.Member.IsDirectory != target.Birth.IsDirectory ||
                snapshot.Identity.ItemId != target.Plan.ItemId || snapshot.Identity.RemoteId != target.Plan.RemoteId)
                throw new InvalidOperationException("Source deletion requires every original published target and current protection.");
            members.Add(source.Member.MemberId, target.Birth.OperationId);
            targetMembers.Add(target.Birth.OperationId);
            if (ordinal == 0) rootBirth = target.Birth.OperationId;
        }
        var inventory = await ReadNamespaceMemberTreeCoreAsync(rootBirth, transaction, token).ConfigureAwait(false);
        if (inventory.Count != plan.Members.Count || inventory.Any(entry => !targetMembers.Contains(entry.MemberId)))
            throw new InvalidOperationException("The registered directory transfer target inventory changed.");
        if (requireUnretiredSource)
            await RequireCurrentDirectoryTransferTreeAsync(plan, transaction, token, rootBirth).ConfigureAwait(false);
    }

    private async Task<MirrorPulseDirectoryTransferSourceDeletion?> ReadDirectoryTransferSourceDeletionCoreAsync(
        Guid operationId, SqliteTransaction transaction, CancellationToken token)
    {
        byte[] payload, fingerprint;
        await using (var sql = _connection.CreateCommand())
        {
            sql.Transaction = transaction;
            sql.CommandText = "SELECT payload,fingerprint FROM namespace_directory_transfer_source_deletions WHERE transfer_id=$id;";
            sql.Parameters.AddWithValue("$id", operationId.ToString("D"));
            await using var reader = await sql.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            payload = (byte[])reader[0];
            fingerprint = (byte[])reader[1];
        }
        var (plan, verified) = await ReadDirectoryTransferBirthEvidenceAsync(operationId, transaction, token).ConfigureAwait(false);
        return ReadNamespaceMovePayload<MirrorPulseDirectoryTransferSourceDeletion>(payload, fingerprint, DirectoryTransferAdmissionLimit,
            result => ValidateDirectoryTransferSourceDeletion(plan, verified, result));
    }

    private static void ValidateDirectoryTransferSourceDeletion(MirrorPulseDirectoryTransferAdmission plan,
        MirrorPulseDirectoryTransferTargetVerification verified, MirrorPulseDirectoryTransferSourceDeletion value)
    {
        if (value.TransferId != plan.Command.CommandId || value.FreshTarget is null ||
            value.FreshTarget.Version != verified.Version || value.FreshTarget.OperationId != verified.OperationId ||
            value.FreshTarget.Members is null || !value.FreshTarget.Members.SequenceEqual(verified.Members) ||
            value.FreshTarget.VerifiedAt < verified.VerifiedAt || value.StartedAt < value.FreshTarget.VerifiedAt)
            throw new InvalidDataException("Source deletion must retain a fresh verification of the original whole target.");
        var original = CreateDirectoryTransferSourceAdmission(plan);
        if (original is null)
        {
            if (value.Request is not null || value.Receipt is not null || value.AcceptedAt != value.StartedAt)
                throw new InvalidDataException("A remote-absent source may not retain a remote deletion effect.");
            return;
        }
        if (value.Request is not { } request || request.OriginalRequest?.RequestFingerprint != original.RequestFingerprint ||
            (value.Receipt is null) != (value.AcceptedAt is null) || value.AcceptedAt < value.StartedAt)
            throw new InvalidDataException("Source deletion lost its original remote effects or acceptance chronology.");
        request.Validate();
        var expected = CreateDirectoryTransferSourceDeleteRequest(original, request.Admission);
        if (request.RequestFingerprint != expected.RequestFingerprint)
            throw new InvalidDataException("The atomic directory source deletion no longer binds its original manifest.");
        value.Receipt?.RequireRequest(request);
    }

    private async Task WriteDirectoryTransferSourceDeletionAsync(MirrorPulseDirectoryTransferSourceDeletion value,
        bool insert, SqliteTransaction transaction, CancellationToken token)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, TopologyJsonOptions);
        if (payload.Length > DirectoryTransferAdmissionLimit)
            throw new ArgumentException("The original directory source deletion is not bounded.", nameof(value));
        await using var sql = _connection.CreateCommand();
        sql.Transaction = transaction;
        sql.CommandText = insert
            ? "INSERT INTO namespace_directory_transfer_source_deletions(transfer_id,payload,fingerprint) VALUES($id,$payload,$hash);"
            : "UPDATE namespace_directory_transfer_source_deletions SET payload=$payload,fingerprint=$hash WHERE transfer_id=$id;";
        sql.Parameters.AddWithValue("$id", value.TransferId.ToString("D"));
        sql.Parameters.AddWithValue("$payload", payload);
        sql.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
        if (await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            throw new InvalidDataException("The directory source deletion could not retain its original receipt.");
    }
}
