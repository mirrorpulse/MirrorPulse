using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Conflicts;

namespace MirrorPulse.Core.State;

/// <summary>A sealed product tree for an explicit SDK conflict decision, separate from its original poll outcome.</summary>
public sealed record MirrorPulseRemoteDeleteResolutionPlan(int Version, MirrorPulseRemoteConflictActionIntent Decision,
    string RemoteChangeFingerprint, MirrorPulseNamespaceMemberProtectionSnapshot Parent,
    IReadOnlyList<MirrorPulseNamespaceRemoteRetirementTreeEntry> Entries, DateTimeOffset PreparedAt);

public enum MirrorPulseRemoteDeleteResolutionStatus { Applied, AlreadyApplied, Reconciled }

/// <summary>Host-observed deletion facts after a public SDK return or owned lost-return reconciliation.</summary>
public sealed record MirrorPulseRemoteDeleteResolutionOutcome(MirrorPulseRemoteDeleteResolutionStatus Status,
    DateTimeOffset VerifiedAt, MirrorPulseNamespacePermissionVerification Parent);

public sealed record MirrorPulseRemoteDeleteResolution(MirrorPulseRemoteDeleteResolutionPlan Plan,
    DateTimeOffset? StartedAt, MirrorPulseRemoteDeleteResolutionOutcome? Outcome, int NextOrdinal, DateTimeOffset? CompletedAt);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Atomically retains every original member and fences its root before any SDK decision.</summary>
    public async Task<MirrorPulseRemoteDeleteResolution> PrepareRemoteDeleteResolutionAsync(MirrorPulseRemoteDeleteResolutionPlan plan,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(plan.Entries);
        plan = plan with { Entries = Array.AsReadOnly(plan.Entries.ToArray()) };
        ValidateRemoteDeleteResolutionPlan(plan);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(); Guid id = plan.Decision.CommandId;
            var existing = await ReadRemoteDeleteResolutionCoreAsync(id, transaction, token).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!ResolutionPayload(existing.Plan).AsSpan().SequenceEqual(ResolutionPayload(plan)))
                    throw new InvalidOperationException("The explicit delete decision cannot replace its sealed original tree or SDK conflict.");
                _ = await ReadNamespaceRetirementBatchCoreAsync(id, transaction, token).ConfigureAwait(false);
                transaction.Commit(); return existing;
            }
            var decision = await ReadRemoteConflictActionCoreAsync(id, transaction, token).ConfigureAwait(false);
            if (decision is null || decision.Value.Intent != plan.Decision || decision.Value.Receipt is not null)
                throw new InvalidOperationException("Deletion requires the same pending explicit product decision admission.");
            var root = plan.Entries[0].Original.Context.Member;
            await RequireNamespaceRetirableMemberAsync(root, transaction, token).ConfigureAwait(false);
            if ((await ReadNamespaceRootFencesCoreAsync(true, transaction, token).ConfigureAwait(false)).Contains(root.RootId!.Value))
                throw new InvalidOperationException("Another namespace owner prevents explicit conflict deletion.");
            var graph = await ReadNamespaceMemberTreeCoreAsync(root.MemberId, transaction, token).ConfigureAwait(false);
            if (graph.Count != plan.Entries.Count) throw new InvalidOperationException("The explicit decision must retain the complete current registered subtree.");
            var graphById = graph.ToDictionary(entry => entry.MemberId);
            if (await ReadNamespaceMemberProtectionSnapshotCoreAsync(plan.Parent.Context.Member.MemberId, transaction, token).ConfigureAwait(false) != plan.Parent)
                throw new InvalidOperationException("The decision lost its original protected direct parent.");
            foreach (var entry in plan.Entries)
            {
                var member = entry.Original.Context.Member;
                if (!graphById.TryGetValue(member.MemberId, out var actual) || actual.Depth != entry.Depth ||
                    actual.ParentMemberId != entry.Original.Context.Location.ParentMemberId || actual.RelativePath != entry.Original.Context.Location.RelativePath ||
                    actual.LocalObject != member.LocalObject || actual.IsDirectory != member.IsDirectory ||
                    await ReadNamespaceMemberProtectionSnapshotCoreAsync(member.MemberId, transaction, token).ConfigureAwait(false) != entry.Original)
                    throw new InvalidOperationException("The decision changed a current member, position, identity or protection.");
            }
            var batch = new MirrorPulseNamespaceRetirementBatch(3, id, root.MemberId, root.RootId.Value,
                plan.Entries.Select(entry => new MirrorPulseNamespaceRetirementBatchEntry(entry.Original.Context.Member.MemberId,
                    entry.Original.Context.Location.ParentMemberId, entry.Original.Context.Location.RelativePath,
                    entry.Original.Context.Member.IsDirectory, entry.Original.Context.Member.LocalObject, entry.Original.Context.Member.OwnerSid, entry.Depth)).ToArray(),
                plan.PreparedAt)
            { RemoteConflictCommandId = id };
            await using (var insert = _connection.CreateCommand())
            {
                byte[] payload = ResolutionPayload(batch); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO namespace_member_retirement_batches(operation_id,member_id,root_id,payload,fingerprint,phase) VALUES($id,$member,$root,$payload,$hash,0);";
                insert.Parameters.AddWithValue("$id", id.ToString("D")); insert.Parameters.AddWithValue("$member", root.MemberId.ToString("D"));
                insert.Parameters.AddWithValue("$root", batch.RootId.ToString()); AddResolutionPayload(insert, payload);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            var prepared = new MirrorPulseRemoteDeleteResolution(plan, null, null, plan.Entries.Count - 1, null);
            await WriteRemoteDeleteResolutionCoreAsync(prepared, transaction, token, insert: true).ConfigureAwait(false);
            foreach (var entry in plan.Entries)
            {
                var member = entry.Original.Context.Member;
                var intent = new MirrorPulseNamespaceRetirementIntent(1, batch.MemberOperationId(member.MemberId), member.MemberId,
                    batch.RootId, member.LocalObject, entry.Original.Context.Location.RelativePath, member.IsDirectory, member.OwnerSid,
                    entry.Original.Context.Protection.RoleSid, plan.PreparedAt)
                { BatchOperationId = id };
                byte[] payload = ResolutionPayload(new MirrorPulseNamespaceRetirement(intent.OperationId, intent, MirrorPulseNamespaceRetirementPhase.Prepared));
                await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO namespace_member_retirements(operation_id,member_id,payload,fingerprint,phase) VALUES($id,$member,$payload,$hash,0);";
                insert.Parameters.AddWithValue("$id", intent.OperationId.ToString("D")); insert.Parameters.AddWithValue("$member", member.MemberId.ToString("D"));
                AddResolutionPayload(insert, payload); await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            transaction.Commit(); return prepared;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseRemoteDeleteResolution?> ReadRemoteDeleteResolutionAsync(Guid commandId, CancellationToken token = default)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("The original conflict command is required.", nameof(commandId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadRemoteDeleteResolutionCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            if (result is not null) _ = await ReadNamespaceRetirementBatchCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Retains the whole SDK invocation boundary; it claims no per-member native deletion.</summary>
    public async Task<MirrorPulseRemoteDeleteResolution> StartRemoteDeleteResolutionAsync(Guid commandId, DateTimeOffset startedAt,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var current = await RequireRemoteDeleteResolutionAsync(commandId, transaction, token).ConfigureAwait(false);
            if (current.StartedAt is not null) { transaction.Commit(); return current; }
            if (startedAt < current.Plan.PreparedAt) throw new InvalidOperationException("The explicit SDK start must follow its original admission.");
            await RequireRemoteResolutionUnfencedAsync(current, transaction, token).ConfigureAwait(false);
            if (await ReadNamespaceMemberProtectionSnapshotCoreAsync(current.Plan.Parent.Context.Member.MemberId, transaction, token).ConfigureAwait(false) != current.Plan.Parent)
                throw new InvalidOperationException("The protected direct parent changed before the explicit SDK invocation.");
            foreach (var entry in current.Plan.Entries)
                if (await ReadNamespaceMemberProtectionSnapshotCoreAsync(entry.Original.Context.Member.MemberId, transaction, token).ConfigureAwait(false) != entry.Original)
                    throw new InvalidOperationException("The complete original tree changed before the SDK invocation.");
            var batch = (await ReadNamespaceRetirementBatchCoreAsync(commandId, transaction, token).ConfigureAwait(false))!;
            foreach (var entry in current.Plan.Entries)
            {
                var member = (await ReadNamespaceRetirementCoreAsync(batch.MemberOperationId(entry.Original.Context.Member.MemberId), transaction, token).ConfigureAwait(false))!;
                await WriteNamespaceRetirementCoreAsync(member with { StartedAt = startedAt, StartedItemId = entry.Original.Identity.ItemId }, transaction, token).ConfigureAwait(false);
            }
            current = current with { StartedAt = startedAt };
            await WriteRemoteDeleteResolutionCoreAsync(current, transaction, token).ConfigureAwait(false); transaction.Commit(); return current;
        }
        finally { _gate.Release(); }
    }

    /// <remarks>The Host must hold its shared actor/current role and inspect the actual public SDK outcome,
    /// conflict absence, sealed object absence, exact official tombstones and protected parent.</remarks>
    public async Task<MirrorPulseRemoteDeleteResolution> RecordRemoteDeleteResolutionOutcomeAsync(Guid commandId,
        MirrorPulseRemoteDeleteResolutionOutcome outcome, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var current = await RequireRemoteDeleteResolutionAsync(commandId, transaction, token).ConfigureAwait(false);
            if (current.Outcome is { } existing)
            {
                if (existing != outcome) throw new InvalidOperationException("The SDK decision outcome is immutable.");
                transaction.Commit(); return current;
            }
            if (current.StartedAt is not { } started || !Enum.IsDefined(outcome.Status))
                throw new InvalidOperationException("A deletion outcome requires the original retained SDK start and observed disposition.");
            RequireTreeSettlementVerification(current.Plan.Parent, outcome.Parent, started, outcome.VerifiedAt);
            await RequireRemoteResolutionUnfencedAsync(current, transaction, token).ConfigureAwait(false);
            current = current with { Outcome = outcome };
            await WriteRemoteDeleteResolutionCoreAsync(current, transaction, token).ConfigureAwait(false); transaction.Commit(); return current;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Commits at most64 actual removed originals, children first, retaining every first membership.</summary>
    public async Task<MirrorPulseRemoteDeleteResolution> SettleRemoteDeleteResolutionPageAsync(Guid commandId,
        IReadOnlyList<MirrorPulseNamespaceRemoteRetirementTreeMemberSettlement> members, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count is < 1 or > 64) throw new ArgumentException("A decision settlement page requires one to64 members.", nameof(members));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var current = await RequireRemoteDeleteResolutionAsync(commandId, transaction, token).ConfigureAwait(false);
            if (current.Outcome is not { } outcome) throw new InvalidOperationException("No member may settle before the actual SDK decision outcome.");
            if (current.CompletedAt is not null || members[0].Ordinal != current.NextOrdinal)
                throw new InvalidOperationException("The decision page must follow its retained next original member.");
            await RequireRemoteResolutionUnfencedAsync(current, transaction, token).ConfigureAwait(false);
            var batch = (await ReadNamespaceRetirementBatchCoreAsync(commandId, transaction, token).ConfigureAwait(false))!;
            for (int index = 0; index < members.Count; index++)
            {
                var settled = members[index]; int ordinal = current.NextOrdinal;
                if (ordinal < 0 || settled.Version != 1 || settled.OperationId != commandId || settled.Ordinal != ordinal)
                    throw new InvalidOperationException("The decision page cannot skip, repeat or substitute an original member.");
                var original = current.Plan.Entries[ordinal]; Guid memberId = original.Original.Context.Member.MemberId;
                if (settled.MemberId != memberId || !settled.NativeMissing || !settled.OfficialTombstone || settled.Child is not null ||
                    settled.OfficialRemoteRevision != (ordinal == 0 ? current.Plan.Decision.Conflict.RemoteRevision : original.OfficialRemoteRevision))
                    throw new InvalidOperationException("Explicit deletion requires actual absence and each original public SDK tombstone.");
                RequireTreeSettlementVerification(current.Plan.Parent, settled.Parent, outcome.VerifiedAt, settled.VerifiedAt);
                if (await ReadNamespaceMemberProtectionSnapshotCoreAsync(memberId, transaction, token).ConfigureAwait(false) != original.Original)
                    throw new InvalidOperationException("The deleted member changed its original position, identity or protection before settlement.");
                await RequireNoRegisteredDescendantsAsync(memberId, transaction, token).ConfigureAwait(false);
                var retirement = (await ReadNamespaceRetirementCoreAsync(batch.MemberOperationId(memberId), transaction, token).ConfigureAwait(false))!;
                if (retirement.Phase != MirrorPulseNamespaceRetirementPhase.Prepared || retirement.StartedAt != current.StartedAt)
                    throw new InvalidDataException("The deleted member lost its original invocation admission.");
                await using var remove = _connection.CreateCommand(); remove.Transaction = transaction;
                remove.CommandText = "DELETE FROM namespace_member_names WHERE member_id=$member;"; remove.Parameters.AddWithValue("$member", memberId.ToString("D"));
                if (await remove.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidDataException("The deleted member lost its owned name before atomic settlement.");
                await WriteNamespaceRetirementCoreAsync(retirement with
                {
                    Phase = MirrorPulseNamespaceRetirementPhase.Completed,
                    NativeDeletedAt = settled.VerifiedAt,
                    CompletedAt = settled.VerifiedAt
                }, transaction, token).ConfigureAwait(false);
                byte[] payload = ResolutionPayload(settled);
                await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO remote_delete_resolution_members(command_id,ordinal,member_id,payload,fingerprint) VALUES($id,$ordinal,$member,$payload,$hash);";
                insert.Parameters.AddWithValue("$id", commandId.ToString("D")); insert.Parameters.AddWithValue("$ordinal", ordinal);
                insert.Parameters.AddWithValue("$member", memberId.ToString("D")); AddResolutionPayload(insert, payload);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                current = current with { NextOrdinal = ordinal - 1 };
                await WriteRemoteDeleteResolutionCoreAsync(current, transaction, token).ConfigureAwait(false);
            }
            await WriteRemoteDeleteResolutionCoreAsync(current, transaction, token).ConfigureAwait(false); transaction.Commit(); return current;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseRemoteDeleteResolution> CompleteRemoteDeleteResolutionAsync(Guid commandId,
        MirrorPulseNamespacePermissionVerification parent, DateTimeOffset completedAt, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var current = await RequireRemoteDeleteResolutionAsync(commandId, transaction, token).ConfigureAwait(false);
            if (current.CompletedAt is not null) { transaction.Commit(); return current; }
            if (current.Outcome is null || current.NextOrdinal != -1) throw new InvalidOperationException("The explicit deletion still has an unsettled outcome or member.");
            RequireTreeSettlementVerification(current.Plan.Parent, parent, current.Outcome.VerifiedAt, completedAt);
            await RequireRemoteResolutionUnfencedAsync(current, transaction, token).ConfigureAwait(false);
            var batch = (await ReadNamespaceRetirementBatchCoreAsync(commandId, transaction, token).ConfigureAwait(false))!;
            foreach (var entry in batch.Entries)
            {
                var retired = await ReadNamespaceRetirementCoreAsync(batch.MemberOperationId(entry.MemberId), transaction, token).ConfigureAwait(false);
                if (retired?.Phase != MirrorPulseNamespaceRetirementPhase.Completed || retired.CompletedAt > parent.ObservedAt)
                    throw new InvalidDataException("Explicit deletion completion lost a settled original or fresh final parent observation.");
            }
            byte[] payload = ResolutionPayload(batch with { Phase = MirrorPulseNamespaceRetirementBatchPhase.Completed });
            await using var update = _connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE namespace_member_retirement_batches SET payload=$payload,fingerprint=$hash,phase=1 WHERE operation_id=$id AND phase=0;";
            update.Parameters.AddWithValue("$id", commandId.ToString("D")); AddResolutionPayload(update, payload);
            if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidDataException("The explicit deletion lost its pending batch owner.");
            current = current with { CompletedAt = completedAt };
            await WriteRemoteDeleteResolutionCoreAsync(current, transaction, token).ConfigureAwait(false); transaction.Commit(); return current;
        }
        finally { _gate.Release(); }
    }

    private async Task RequireRemoteResolutionUnfencedAsync(MirrorPulseRemoteDeleteResolution current, SqliteTransaction transaction, CancellationToken token)
    {
        var root = current.Plan.Entries[0].Original.Context.Member.RootId!.Value;
        if ((await ReadNamespaceRootFencesCoreAsync(true, transaction, token, admittedRemoteResolutionId: current.Plan.Decision.CommandId).ConfigureAwait(false)).Contains(root))
            throw new InvalidOperationException("Another namespace owner prevents the original explicit delete decision.");
    }

    private async Task<MirrorPulseRemoteDeleteResolution> RequireRemoteDeleteResolutionAsync(Guid id, SqliteTransaction transaction, CancellationToken token)
    {
        var current = await ReadRemoteDeleteResolutionCoreAsync(id, transaction, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original explicit delete decision is missing.");
        _ = await ReadNamespaceRetirementBatchCoreAsync(id, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The explicit delete decision lost its owned batch fence.");
        return current;
    }

    private static void ValidateRemoteDeleteResolutionPlan(MirrorPulseRemoteDeleteResolutionPlan plan)
    {
        ValidateRemoteConflictActionIntent(plan.Decision);
        if (plan.Version != 1 || plan.Decision.Action != MirrorPulseConflictAction.KeepRemote ||
            plan.Decision.PackageVersion != MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion ||
            plan.Decision.PackageSource != MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit ||
            plan.RemoteChangeFingerprint is not { Length: 64 } || !plan.RemoteChangeFingerprint.All(char.IsAsciiHexDigitUpper) ||
            plan.Entries.Count is < 1 or > 65_536 || plan.PreparedAt < plan.Decision.Conflict.DetectedAt || plan.Parent is null)
            throw new ArgumentException("Explicit deletion requires a bounded original conflict and complete protected tree.", nameof(plan));
        var root = plan.Entries[0].Original; var parent = plan.Parent;
        if (root.Context.Member.RootId is null || root.Context.Member.RootId != parent.Context.Member.RootId ||
            root.Context.Location.ParentMemberId != parent.Context.Member.MemberId || !parent.Context.Member.IsDirectory ||
            root.Context.Location.RelativePath != plan.Decision.Conflict.RelativePath.Replace('\\', '/') ||
            parent.Context.Verification.ObservedAt > plan.PreparedAt)
            throw new ArgumentException("The explicit delete decision requires the original non-root member and protected direct parent.", nameof(plan));
        var seen = new Dictionary<Guid, MirrorPulseNamespaceRemoteRetirementTreeEntry>();
        for (int ordinal = 0; ordinal < plan.Entries.Count; ordinal++)
        {
            var entry = plan.Entries[ordinal]; var member = entry.Original.Context.Member; var context = entry.Original.Context;
            if (entry.Ordinal != ordinal || member.RootId != root.Context.Member.RootId || member.OwnerSid != parent.Context.Member.OwnerSid ||
                context.Protection.RoleSid != parent.Context.Protection.RoleSid || entry.Original.Identity.ItemId is null ||
                context.Verification.ObservedAt > plan.PreparedAt || !seen.TryAdd(member.MemberId, entry) ||
                (ordinal == 0 ? entry.Depth != 0 : entry.Depth <= 0 || context.Location.ParentMemberId is not { } parentId ||
                    !seen.TryGetValue(parentId, out var retainedParent) || !retainedParent.Original.Context.Member.IsDirectory || retainedParent.Depth + 1 != entry.Depth))
                throw new ArgumentException("The explicit delete decision lost its connected ordered members, identities or protection.", nameof(plan));
        }
        _ = ResolutionPayload(plan);
    }

    private async Task<MirrorPulseRemoteDeleteResolution?> ReadRemoteDeleteResolutionCoreAsync(Guid id, SqliteTransaction? transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload,fingerprint,conflict_id,instance_id,root_id,completed FROM remote_delete_resolutions WHERE command_id=$id;"; query.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        var current = DecodeResolutionPayload<MirrorPulseRemoteDeleteResolution>((byte[])reader[0], (byte[])reader[1]);
        ValidateRemoteDeleteResolutionPlan(current.Plan);
        if (current.Plan.Decision.CommandId != id || reader.GetString(2) != current.Plan.Decision.Conflict.ConflictId.ToString("D") ||
            reader.GetString(3) != current.Plan.Decision.Conflict.InstanceId.ToString() ||
            reader.GetString(4) != current.Plan.Entries[0].Original.Context.Member.RootId!.Value.ToString() ||
            reader.GetInt32(5) != (current.CompletedAt is null ? 0 : 1) || current.NextOrdinal < -1 || current.NextOrdinal >= current.Plan.Entries.Count ||
            current.StartedAt < current.Plan.PreparedAt || current.StartedAt is null && (current.Outcome is not null || current.NextOrdinal != current.Plan.Entries.Count - 1) ||
            current.Outcome is null && (current.NextOrdinal != current.Plan.Entries.Count - 1 || current.CompletedAt is not null) ||
            current.Outcome is { } outcome && (!Enum.IsDefined(outcome.Status) || outcome.VerifiedAt < current.StartedAt) ||
            current.CompletedAt is not null && (current.NextOrdinal != -1 || current.CompletedAt < current.Outcome!.VerifiedAt))
            throw new InvalidDataException("The explicit delete decision lost its original owner, progress or chronology.");
        if (current.Outcome is { } observed) RequireTreeSettlementVerification(current.Plan.Parent, observed.Parent, current.StartedAt!.Value, observed.VerifiedAt);
        await reader.DisposeAsync().ConfigureAwait(false);
        await using var count = _connection.CreateCommand(); count.Transaction = transaction;
        count.CommandText = "SELECT count(*),min(ordinal),max(ordinal) FROM remote_delete_resolution_members WHERE command_id=$id;";
        count.Parameters.AddWithValue("$id", id.ToString("D"));
        await using var progress = await count.ExecuteReaderAsync(token).ConfigureAwait(false); _ = await progress.ReadAsync(token).ConfigureAwait(false);
        int expected = current.Plan.Entries.Count - current.NextOrdinal - 1;
        if (progress.GetInt64(0) != expected || expected != 0 &&
            (progress.GetInt64(1) != current.NextOrdinal + 1 || progress.GetInt64(2) != current.Plan.Entries.Count - 1))
            throw new InvalidDataException("The explicit deletion progress lost its contiguous settled original members.");
        return current with { Plan = current.Plan with { Entries = Array.AsReadOnly(current.Plan.Entries.ToArray()) } };
    }

    private async Task WriteRemoteDeleteResolutionCoreAsync(MirrorPulseRemoteDeleteResolution current, SqliteTransaction transaction,
        CancellationToken token, bool insert = false)
    {
        byte[] payload = ResolutionPayload(current); await using var command = _connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = insert ? "INSERT INTO remote_delete_resolutions(command_id,conflict_id,instance_id,root_id,payload,fingerprint,completed) VALUES($id,$conflict,$instance,$root,$payload,$hash,$completed);"
            : "UPDATE remote_delete_resolutions SET payload=$payload,fingerprint=$hash,completed=$completed WHERE command_id=$id;";
        command.Parameters.AddWithValue("$id", current.Plan.Decision.CommandId.ToString("D")); AddResolutionPayload(command, payload);
        command.Parameters.AddWithValue("$completed", current.CompletedAt is null ? 0 : 1);
        if (insert)
        {
            command.Parameters.AddWithValue("$conflict", current.Plan.Decision.Conflict.ConflictId.ToString("D"));
            command.Parameters.AddWithValue("$instance", current.Plan.Decision.Conflict.InstanceId.ToString());
            command.Parameters.AddWithValue("$root", current.Plan.Entries[0].Original.Context.Member.RootId!.Value.ToString());
        }
        if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) throw new InvalidDataException("The explicit deletion lost its durable decision owner.");
    }

    private async Task ValidateRemoteDeleteResolutionBatchAsync(MirrorPulseNamespaceRetirementBatch batch, SqliteTransaction? transaction, CancellationToken token)
    {
        var owner = await ReadRemoteDeleteResolutionCoreAsync(batch.OperationId, transaction, token).ConfigureAwait(false);
        if (batch.RemoteConflictCommandId is null)
        {
            if (owner is not null) throw new InvalidDataException("The explicit deletion lost its batch ownership marker.");
            return;
        }
        if (owner is null || owner.Plan.Entries.Count != batch.Entries.Count || owner.Plan.PreparedAt != batch.PreparedAt ||
            owner.Plan.Entries[0].Original.Context.Member.RootId != batch.RootId || owner.Plan.Entries[0].Original.Context.Member.MemberId != batch.MemberId ||
            (batch.Phase == MirrorPulseNamespaceRetirementBatchPhase.Completed) != (owner.CompletedAt is not null))
            throw new InvalidDataException("The explicit deletion batch lost its sealed decision owner.");
        var command = await ReadRemoteConflictActionCoreAsync(batch.OperationId, transaction!, token).ConfigureAwait(false);
        if (command is null || command.Value.Intent != owner.Plan.Decision || command.Value.Receipt is not null && owner.CompletedAt is null)
            throw new InvalidDataException("The explicit deletion lost its correlated original user decision.");
        foreach (var entry in owner.Plan.Entries)
        {
            var member = entry.Original.Context.Member; var retained = batch.Entries[entry.Ordinal];
            if (retained != new MirrorPulseNamespaceRetirementBatchEntry(member.MemberId, entry.Original.Context.Location.ParentMemberId,
                entry.Original.Context.Location.RelativePath, member.IsDirectory, member.LocalObject, member.OwnerSid, entry.Depth))
                throw new InvalidDataException("The explicit deletion batch changed an original tree member.");
        }
    }

    private async Task<bool> IsRemoteDeleteResolutionBatchAsync(Guid id, SqliteTransaction? transaction, CancellationToken token) =>
        (await ReadRemoteDeleteResolutionCoreAsync(id, transaction, token).ConfigureAwait(false)) is not null;

    private async Task RequireRemoteDeleteResolutionOwnershipIntegrityAsync(SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT 1 FROM remote_delete_resolutions r LEFT JOIN namespace_member_retirement_batches b ON b.operation_id=r.command_id WHERE b.operation_id IS NULL LIMIT 1;";
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
            throw new InvalidDataException("An explicit delete decision lost its durable root-fence batch.");
    }

    private async Task RequireRemoteDeleteDecisionOwnershipAsync(MirrorPulseRemoteConflictActionIntent intent, SqliteTransaction transaction, CancellationToken token)
    {
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT command_id FROM remote_delete_resolutions WHERE conflict_id=$conflict;";
        query.Parameters.AddWithValue("$conflict", intent.Conflict.ConflictId.ToString("D"));
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is string owner && owner != intent.CommandId.ToString("D"))
            throw new InvalidOperationException("Another original command owns this SDK conflict's protected deletion and recovery.");
    }

    private async Task RequireNotRemoteDeleteResolutionMemberAsync(MirrorPulseNamespaceRetirementIntent intent, SqliteTransaction transaction, CancellationToken token)
    {
        if (intent.BatchOperationId is { } batch &&
            (await ReadNamespaceRetirementBatchCoreAsync(batch, transaction, token).ConfigureAwait(false))?.RemoteConflictCommandId is not null)
            throw new InvalidOperationException("An explicit SDK decision cannot use controlled native deletion or completion.");
    }

    private async Task ValidateRemoteDeleteResolutionMemberAsync(MirrorPulseNamespaceRetirement member, SqliteTransaction? transaction, CancellationToken token)
    {
        if (member.Intent.BatchOperationId is not { } batchId) return;
        var batch = await ReadNamespaceRetirementBatchCoreAsync(batchId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The retirement member lost its original batch owner.");
        var owner = await ReadRemoteDeleteResolutionCoreAsync(batchId, transaction, token).ConfigureAwait(false);
        if (owner is null) return;
        var entry = owner.Plan.Entries.SingleOrDefault(entry => entry.Original.Context.Member.MemberId == member.Intent.MemberId)
            ?? throw new InvalidDataException("The explicit deletion member is outside its original tree.");
        var original = entry.Original.Context;
        if (member.Intent.OperationId != batch.MemberOperationId(member.Intent.MemberId) ||
            member.Intent.LocalObject != original.Member.LocalObject || member.Intent.RelativePath != original.Location.RelativePath ||
            member.Intent.RootId != original.Member.RootId || member.Intent.OwnerSid != original.Member.OwnerSid || member.Intent.IsDirectory != original.Member.IsDirectory ||
            member.Intent.RoleSid != original.Protection.RoleSid || member.Intent.PreparedAt != owner.Plan.PreparedAt ||
            member.StartedAt != owner.StartedAt || member.StartedAt is not null && member.StartedItemId != entry.Original.Identity.ItemId ||
            member.Phase is not (MirrorPulseNamespaceRetirementPhase.Prepared or MirrorPulseNamespaceRetirementPhase.Completed))
            throw new InvalidDataException("The explicit deletion member lost its original start, position, binding or owner.");
        bool settled = entry.Ordinal > owner.NextOrdinal;
        // During one atomic settlement transaction the next ordinal is written after its member rows.
        if (member.Phase == MirrorPulseNamespaceRetirementPhase.Completed && !settled)
            throw new InvalidDataException("The explicit deletion member is ahead of its owned settlement progress.");
        if (member.Phase == MirrorPulseNamespaceRetirementPhase.Prepared && settled)
            throw new InvalidDataException("The explicit deletion progress lost a completed member receipt.");
        if (!settled) return;
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT member_id,payload,fingerprint FROM remote_delete_resolution_members WHERE command_id=$id AND ordinal=$ordinal;";
        query.Parameters.AddWithValue("$id", batchId.ToString("D")); query.Parameters.AddWithValue("$ordinal", entry.Ordinal);
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) throw new InvalidDataException("The completed explicit deletion lost its member observation.");
        var observation = DecodeResolutionPayload<MirrorPulseNamespaceRemoteRetirementTreeMemberSettlement>((byte[])reader[1], (byte[])reader[2]);
        if (reader.GetString(0) != member.Intent.MemberId.ToString("D") || observation.MemberId != member.Intent.MemberId || observation.Ordinal != entry.Ordinal ||
            observation.OperationId != batchId || !observation.NativeMissing || !observation.OfficialTombstone || observation.Child is not null ||
            observation.OfficialRemoteRevision != (entry.Ordinal == 0 ? owner.Plan.Decision.Conflict.RemoteRevision : entry.OfficialRemoteRevision) ||
            observation.VerifiedAt != member.CompletedAt || member.NativeDeletedAt != member.CompletedAt || owner.Outcome is null)
            throw new InvalidDataException("The completed explicit deletion changed its original native or official observation.");
        RequireTreeSettlementVerification(owner.Plan.Parent, observation.Parent, owner.Outcome.VerifiedAt, observation.VerifiedAt);
    }

    private static byte[] ResolutionPayload<T>(T value)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value, TopologyJsonOptions);
        if (payload.Length > 4 * 1024 * 1024) throw new InvalidDataException("The explicit deletion evidence exceeds its catalog bound.");
        return payload;
    }
    private static T DecodeResolutionPayload<T>(byte[] payload, byte[] hash)
    {
        if (payload.Length > 4 * 1024 * 1024 || hash.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), hash))
            throw new InvalidDataException("The explicit delete decision evidence is corrupt.");
        var value = JsonSerializer.Deserialize<T>(payload, TopologyJsonOptions) ?? throw new InvalidDataException("The explicit deletion evidence is empty.");
        if (!ResolutionPayload(value).AsSpan().SequenceEqual(payload)) throw new InvalidDataException("The explicit deletion evidence is not canonical.");
        return value;
    }
    private static void AddResolutionPayload(SqliteCommand command, byte[] payload)
    { command.Parameters.AddWithValue("$payload", payload); command.Parameters.AddWithValue("$hash", SHA256.HashData(payload)); }

    private static async Task EnsureRemoteDeleteResolutionSchemaAsync(SqliteConnection connection, CancellationToken token, bool stampVersion = true)
    {
        using var transaction = connection.BeginTransaction(); await using var schema = connection.CreateCommand(); schema.Transaction = transaction;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS remote_delete_resolutions(command_id TEXT PRIMARY KEY REFERENCES user_commands(command_id),
                conflict_id TEXT NOT NULL UNIQUE,instance_id TEXT NOT NULL,root_id TEXT NOT NULL,
                payload BLOB NOT NULL,fingerprint BLOB NOT NULL,completed INTEGER NOT NULL CHECK(completed IN (0,1)),
                FOREIGN KEY(command_id) REFERENCES namespace_member_retirement_batches(operation_id));
            CREATE INDEX IF NOT EXISTS remote_delete_resolutions_pending ON remote_delete_resolutions(instance_id,completed,command_id);
            CREATE TABLE IF NOT EXISTS remote_delete_resolution_members(command_id TEXT NOT NULL REFERENCES remote_delete_resolutions(command_id),
                ordinal INTEGER NOT NULL,member_id TEXT NOT NULL,payload BLOB NOT NULL,fingerprint BLOB NOT NULL,
                PRIMARY KEY(command_id,ordinal),UNIQUE(command_id,member_id));
            """;
        await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        if (stampVersion)
        {
            schema.CommandText = "PRAGMA user_version=78;";
            await schema.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        transaction.Commit();
    }
}
