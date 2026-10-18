using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>The actual public SDK root result, independently inspected by the Host after invocation.</summary>
public sealed record MirrorPulseNamespaceRemoteRetirementTreeOutcome(int Version, Guid OperationId,
    string BatchFingerprint, string TreeFingerprint, DateTimeOffset StartedAt, int AppliedEntryCount,
    string LastAppliedChangeId, string SafeCursorFingerprint, bool SdkBatchCompleted, string EntryStatus,
    Guid? ConflictId, DateTimeOffset? ConflictDetectedAt, string? ConflictReason, DateTimeOffset VerifiedAt,
    bool NativeMissing, bool OfficialTombstone, string? OfficialRemoteRevision,
    MirrorPulseNamespacePermissionVerification Parent, MirrorPulseNamespacePermissionVerification? Child);

/// <summary>One original member's actual disposition after the original SDK root result.</summary>
public sealed record MirrorPulseNamespaceRemoteRetirementTreeMemberSettlement(int Version, Guid OperationId,
    int Ordinal, Guid MemberId, bool NativeMissing, bool OfficialTombstone, string? OfficialRemoteRevision,
    DateTimeOffset VerifiedAt, MirrorPulseNamespacePermissionVerification Parent,
    MirrorPulseNamespacePermissionVerification? Child);

/// <summary>Durable children-first settlement progress; it never authorizes an SDK operation.</summary>
public sealed record MirrorPulseNamespaceRemoteRetirementTreeSettlementProgress(int Version, Guid OperationId,
    string BatchFingerprint, string TreeFingerprint, string StartFingerprint, int MemberCount, int NextOrdinal,
    string? OutcomeFingerprint, string SettledFingerprint, DateTimeOffset? CompletedAt);

/// <summary>Historical tree-owned retirement, distinct from a controlled or SDK leaf delete intent.</summary>
public sealed record MirrorPulseNamespaceRemoteRetirementTreeRetiredMember(Guid OperationId, int Ordinal,
    MirrorPulseNamespaceMemberProtectionSnapshot Original, DateTimeOffset StartedAt, DateTimeOffset VerifiedAt);

public sealed partial class MirrorPulseProductCatalog
{
    /// <remarks>The Host must inspect the exact public SDK prefix, decoded conflict, root projection
    /// and actual native objects under its shared actor, current role and retained parent guard.</remarks>
    public async Task<MirrorPulseNamespaceRemoteRetirementTreeOutcome> RecordNamespaceRemoteRetirementTreeOutcomeAsync(
        MirrorPulseNamespaceRemoteRetirementTreeOutcome outcome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.OperationId == Guid.Empty) throw new ArgumentException("An original SDK tree operation is required.", nameof(outcome));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var batch = await RequireRemoteTreeSettlementBatchAsync(outcome.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            var start = (await ReadRemoteRetirementTreeStartCoreAsync(batch.OperationId, batch, transaction, cancellationToken).ConfigureAwait(false))!;
            var progress = await ReadRemoteTreeSettlementProgressCoreAsync(batch, start, transaction, cancellationToken).ConfigureAwait(false);
            ValidateRemoteTreeOutcome(batch, start, outcome);
            var existing = await ReadRemoteTreeOutcomeCoreAsync(batch.OperationId, transaction, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing != outcome) throw new InvalidOperationException("The original SDK tree result is immutable.");
                transaction.Commit(); return existing;
            }
            await RequireRemoteTreeSettlementUnfencedAsync(batch, transaction, cancellationToken).ConfigureAwait(false);
            await ValidateRemoteRetirementCurrentAsync(TreeRootIntent(batch.OperationId, batch.RemoteEntry!, batch.PreparedAt), transaction, cancellationToken).ConfigureAwait(false);
            byte[] payload = TreeSettlementPayload(outcome);
            await InsertRemoteTreeOutcomeCoreAsync(batch.OperationId,
                payload, transaction, cancellationToken).ConfigureAwait(false);
            await WriteRemoteTreeSettlementProgressCoreAsync(progress with { OutcomeFingerprint = Convert.ToHexString(SHA256.HashData(payload)) },
                transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return outcome;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceRemoteRetirementTreeOutcome?> ReadNamespaceRemoteRetirementTreeOutcomeAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An original SDK tree is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var batch = await ReadNamespaceRetirementBatchCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            var outcome = await ReadRemoteTreeOutcomeCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            if (outcome is not null && batch?.RemoteEntry is null) throw new InvalidDataException("The original SDK tree result lost its owning admission.");
            transaction.Commit(); return outcome;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceRemoteRetirementTreeSettlementProgress?> ReadNamespaceRemoteRetirementTreeSettlementAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An original SDK tree is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var batch = await ReadNamespaceRetirementBatchCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            var start = await ReadRemoteRetirementTreeStartCoreAsync(operationId, batch, transaction, cancellationToken).ConfigureAwait(false);
            var result = start is null ? null : await ReadRemoteTreeSettlementProgressCoreAsync(batch!, start, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Atomically settles at most 64 original members, children before parents.</summary>
    /// <remarks>Only actual removed originals release names. A genuine root conflict can retain
    /// some members and remove others. It does not become an intact-tree rejection.</remarks>
    public async Task<MirrorPulseNamespaceRemoteRetirementTreeSettlementProgress> SettleNamespaceRemoteRetirementTreePageAsync(
        Guid operationId, IReadOnlyList<MirrorPulseNamespaceRemoteRetirementTreeMemberSettlement> members,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (operationId == Guid.Empty || members.Count is < 1 or > 64) throw new ArgumentException("A settlement page requires its original tree and at most 64 members.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var batch = await RequireRemoteTreeSettlementBatchAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            var start = (await ReadRemoteRetirementTreeStartCoreAsync(operationId, batch, transaction, cancellationToken).ConfigureAwait(false))!;
            var progress = await ReadRemoteTreeSettlementProgressCoreAsync(batch, start, transaction, cancellationToken).ConfigureAwait(false);
            var outcome = await ReadRemoteTreeOutcomeCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Members cannot settle before the actual original SDK tree result.");
            for (int index = 0; index < members.Count; index++)
                if (members[index] is not { } member || member.OperationId != operationId || member.Ordinal != members[0].Ordinal - index ||
                    member.Ordinal < 0 || member.Ordinal >= batch.Entries.Count)
                    throw new ArgumentException("An original tree page must be contiguous and children-first.", nameof(members));
            if (members[^1].Ordinal > progress.NextOrdinal)
            {
                foreach (var member in members)
                    if (await ReadRemoteTreeMemberSettlementCoreAsync(batch, member.Ordinal, transaction, cancellationToken).ConfigureAwait(false) != member)
                        throw new InvalidOperationException("An original member disposition is immutable.");
                transaction.Commit(); return progress;
            }
            if (members[0].Ordinal != progress.NextOrdinal) throw new InvalidOperationException("The original tree settlement page skipped its next member.");
            await RequireRemoteTreeSettlementUnfencedAsync(batch, transaction, cancellationToken).ConfigureAwait(false);
            foreach (var member in members)
            {
                var original = (await ReadRemoteRetirementTreePageCoreAsync(batch, member.Ordinal - 1, 1, transaction, cancellationToken).ConfigureAwait(false)).Entries[0];
                ValidateRemoteTreeMemberSettlement(batch, outcome, original, member);
                if (await ReadNamespaceMemberProtectionSnapshotCoreAsync(member.MemberId, transaction, cancellationToken).ConfigureAwait(false) != original.Original)
                    throw new InvalidOperationException("The original member's current name, position, protection or identity changed before settlement.");
                if (member.NativeMissing) await RequireNoRegisteredDescendantsAsync(member.MemberId, transaction, cancellationToken).ConfigureAwait(false);
                byte[] payload = TreeSettlementPayload(member), hash = SHA256.HashData(payload);
                await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO namespace_remote_retirement_tree_member_settlements(operation_id,ordinal,member_id,native_missing,payload,fingerprint) VALUES($id,$ordinal,$member,$missing,$payload,$hash);";
                insert.Parameters.AddWithValue("$id", operationId.ToString("D")); insert.Parameters.AddWithValue("$ordinal", member.Ordinal);
                insert.Parameters.AddWithValue("$member", member.MemberId.ToString("D")); insert.Parameters.AddWithValue("$missing", member.NativeMissing ? 1 : 0);
                insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", hash);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (member.NativeMissing)
                {
                    await using var remove = _connection.CreateCommand(); remove.Transaction = transaction;
                    remove.CommandText = "DELETE FROM namespace_member_names WHERE member_id=$member;";
                    remove.Parameters.AddWithValue("$member", member.MemberId.ToString("D"));
                    if (await remove.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                        throw new InvalidDataException("A removed original tree member lost its name before atomic settlement.");
                }
                progress = progress with
                {
                    NextOrdinal = member.Ordinal - 1,
                    SettledFingerprint = ExtendTreeSettlementDigest(progress.SettledFingerprint, member.Ordinal, hash)
                };
                await WriteRemoteTreeSettlementProgressCoreAsync(progress, transaction, cancellationToken).ConfigureAwait(false);
            }
            transaction.Commit(); return progress;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceRemoteRetirementTreeMemberSettlement?> ReadNamespaceRemoteRetirementTreeMemberSettlementAsync(
        Guid operationId, int ordinal, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || ordinal < 0) throw new ArgumentException("An original tree and member ordinal are required.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var batch = await RequireRemoteTreeSettlementBatchAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, batch.Entries.Count);
            var result = await ReadRemoteTreeMemberSettlementCoreAsync(batch, ordinal, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Releases only this tree's fence after every actual member disposition is durable.</summary>
    public async Task<MirrorPulseNamespaceRetirementBatch> CompleteNamespaceRemoteRetirementTreeAsync(Guid operationId,
        MirrorPulseNamespacePermissionVerification parent, DateTimeOffset verifiedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (operationId == Guid.Empty || verifiedAt == default) throw new ArgumentException("Original tree completion requires a fresh parent verification.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var batch = await RequireRemoteTreeSettlementBatchAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            var start = (await ReadRemoteRetirementTreeStartCoreAsync(operationId, batch, transaction, cancellationToken).ConfigureAwait(false))!;
            var progress = await ReadRemoteTreeSettlementProgressCoreAsync(batch, start, transaction, cancellationToken).ConfigureAwait(false);
            if (progress.CompletedAt is not null) { transaction.Commit(); return batch; }
            var outcome = await ReadRemoteTreeOutcomeCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            if (outcome is null || progress.NextOrdinal != -1) throw new InvalidOperationException("The original tree still has an unsettled SDK result or member.");
            await RequireRemoteTreeSettlementUnfencedAsync(batch, transaction, cancellationToken).ConfigureAwait(false);
            RequireTreeSettlementVerification(batch.RemoteEntry!.Parent, parent, outcome.VerifiedAt, verifiedAt);
            for (int ordinal = batch.Entries.Count - 1; ordinal >= 0; ordinal--)
            {
                var member = await ReadRemoteTreeMemberSettlementCoreAsync(batch, ordinal, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Original tree completion lost a member disposition.");
                if (member.VerifiedAt > parent.ObservedAt) throw new InvalidOperationException("Completion requires a fresh parent after every member observation.");
            }
            var completed = batch with { Phase = MirrorPulseNamespaceRetirementBatchPhase.Completed };
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(completed, TopologyJsonOptions);
            await using var update = _connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE namespace_member_retirement_batches SET payload=$payload,fingerprint=$hash,phase=1 WHERE operation_id=$id AND phase=0;";
            update.Parameters.AddWithValue("$id", operationId.ToString("D")); update.Parameters.AddWithValue("$payload", payload);
            update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1) throw new InvalidDataException("Original tree completion lost its pending owner.");
            await WriteRemoteTreeSettlementProgressCoreAsync(progress with { CompletedAt = verifiedAt }, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return completed;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceRetirementBatch> RequireRemoteTreeSettlementBatchAsync(Guid operation,
        SqliteTransaction transaction, CancellationToken token)
    {
        var batch = await ReadNamespaceRetirementBatchCoreAsync(operation, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original SDK tree admission is missing.");
        if (batch.Version != 2 || batch.RemoteEntry is null || batch.RemoteTreeStartFingerprint is null)
            throw new InvalidOperationException("Tree settlement requires its original SDK admission and before-invocation start.");
        return batch;
    }

    private async Task RequireRemoteTreeSettlementUnfencedAsync(MirrorPulseNamespaceRetirementBatch batch,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (batch.Phase != MirrorPulseNamespaceRetirementBatchPhase.Prepared ||
            (await ReadNamespaceRootFencesCoreAsync(true, transaction, token, admittedRemoteRetirementTreeId: batch.OperationId).ConfigureAwait(false)).Contains(batch.RootId))
            throw new InvalidOperationException("Another namespace owner or terminal state prevents original tree settlement.");
    }

    private static void ValidateRemoteTreeOutcome(MirrorPulseNamespaceRetirementBatch batch,
        MirrorPulseNamespaceRemoteRetirementTreeStart start, MirrorPulseNamespaceRemoteRetirementTreeOutcome outcome)
    {
        var remote = batch.RemoteEntry!;
        if (outcome.Version != 1 || outcome.OperationId != batch.OperationId || outcome.BatchFingerprint != remote.BatchFingerprint ||
            outcome.TreeFingerprint != batch.RemoteTreeFingerprint || outcome.StartedAt != start.StartedAt ||
            outcome.AppliedEntryCount != remote.Ordinal + 1 || outcome.LastAppliedChangeId != remote.ChangeId ||
            !IsTreeSettlementFingerprint(outcome.SafeCursorFingerprint) || outcome.SdkBatchCompleted != (outcome.AppliedEntryCount == remote.BatchEntryCount) ||
            outcome.EntryStatus is not ("Applied" or "AlreadyApplied" or "Conflict") || outcome.VerifiedAt < start.StartedAt)
            throw new InvalidOperationException("The actual SDK tree result lost its original start, immutable input or exact public prefix.");
        RequireTreeSettlementVerification(remote.Parent, outcome.Parent, start.StartedAt, outcome.VerifiedAt);
        if (outcome.EntryStatus == "Conflict")
        {
            if (outcome.ConflictId is null || outcome.ConflictId == Guid.Empty || outcome.ConflictDetectedAt is not { } detected ||
                detected < start.StartedAt || detected > outcome.VerifiedAt || string.IsNullOrWhiteSpace(outcome.ConflictReason) ||
                outcome.ConflictReason.Length > 128 || outcome.ConflictReason.Contains('\0') ||
                outcome.NativeMissing || outcome.OfficialTombstone || outcome.OfficialRemoteRevision != remote.OfficialRemoteRevision || outcome.Child is null)
                throw new InvalidOperationException("A genuine root conflict must preserve its actual original root; a missing root cannot invent a successful rejection.");
            RequireTreeSettlementVerification(remote.Child, outcome.Child, detected, outcome.VerifiedAt);
            if (outcome.Parent.ObservedAt < detected) throw new InvalidOperationException("The conflict requires a fresh unchanged parent.");
        }
        else if (outcome.ConflictId is not null || outcome.ConflictDetectedAt is not null || outcome.ConflictReason is not null ||
            !outcome.NativeMissing || !outcome.OfficialTombstone || outcome.OfficialRemoteRevision != remote.RemoteRevision || outcome.Child is not null)
            throw new InvalidOperationException("An applied original SDK root requires actual native absence and its exact public tombstone.");
    }

    private static void ValidateRemoteTreeMemberSettlement(MirrorPulseNamespaceRetirementBatch batch,
        MirrorPulseNamespaceRemoteRetirementTreeOutcome outcome, MirrorPulseNamespaceRemoteRetirementTreeEntry original,
        MirrorPulseNamespaceRemoteRetirementTreeMemberSettlement member)
    {
        if (member.Version != 1 || member.OperationId != batch.OperationId || member.Ordinal != original.Ordinal ||
            member.MemberId != original.Original.Context.Member.MemberId || member.VerifiedAt < outcome.VerifiedAt)
            throw new InvalidOperationException("The member disposition lost its original tree, identity or observation chronology.");
        RequireTreeSettlementVerification(batch.RemoteEntry!.Parent, member.Parent, outcome.VerifiedAt, member.VerifiedAt);
        if (member.NativeMissing)
        {
            string? expected = member.Ordinal == 0 ? batch.RemoteEntry.RemoteRevision : original.OfficialRemoteRevision;
            if (!member.OfficialTombstone || member.OfficialRemoteRevision != expected || member.Child is not null)
                throw new InvalidOperationException("A removed original member requires its matching public tombstone, without forging a child revision.");
        }
        else
        {
            if (outcome.ConflictId is null || member.OfficialTombstone || member.OfficialRemoteRevision != original.OfficialRemoteRevision || member.Child is null)
                throw new InvalidOperationException("Only a genuine SDK conflict can retain an unchanged original member.");
            RequireTreeSettlementVerification(original.Original, member.Child, outcome.VerifiedAt, member.VerifiedAt);
        }
        if (member.Ordinal == 0 && (member.NativeMissing != outcome.NativeMissing || member.OfficialTombstone != outcome.OfficialTombstone ||
            member.OfficialRemoteRevision != outcome.OfficialRemoteRevision))
            throw new InvalidOperationException("The root member disposition disagrees with its actual original SDK result.");
    }

    private static void RequireTreeSettlementVerification(MirrorPulseNamespaceMemberProtectionSnapshot original,
        MirrorPulseNamespacePermissionVerification actual, DateTimeOffset after, DateTimeOffset through)
    {
        if (actual is null || actual.LocalObject != original.Context.Member.LocalObject || actual.OwnerSid != original.Context.Member.OwnerSid ||
            actual.Dacl != original.Context.Verification.Dacl || actual.ObservedAt < after || actual.ObservedAt > through)
            throw new InvalidOperationException("Tree settlement requires fresh unchanged original object, owner and protection observations.");
    }

    private static bool IsTreeSettlementFingerprint(string? fingerprint) => fingerprint is { Length: 64 } && fingerprint.All(char.IsAsciiHexDigitUpper);
    private static string EmptyTreeSettlementDigest => Convert.ToHexString(SHA256.HashData([]));
    private static string ExtendTreeSettlementDigest(string previous, int ordinal, byte[] fingerprint)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(Convert.FromHexString(previous)); AppendRemoteTreeDigest(digest, ordinal, fingerprint);
        return Convert.ToHexString(digest.GetHashAndReset());
    }

    private static byte[] TreeSettlementPayload<T>(T value)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, TopologyJsonOptions);
        if (payload.Length > 131_072) throw new ArgumentException("An original tree settlement record must be bounded.", nameof(value));
        return payload;
    }
}
