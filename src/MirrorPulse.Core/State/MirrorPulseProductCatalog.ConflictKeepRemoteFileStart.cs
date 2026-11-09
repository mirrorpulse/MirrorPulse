using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

/// <summary>First before-write facts for applying selected remote bytes to one existing file.</summary>
/// <remarks>
/// This retains the original store file, journal correlations, member, parent and execution role.
/// It records intent only. Fresh native admission, verified byte application, recovery and ACK
/// require their separate boundaries. A later role or occupant never rewrites these first facts.
/// </remarks>
public sealed record MirrorPulseConflictKeepRemoteFileStart(int Version, Guid CommandId,
    ulong StoreVolumeSerialNumber, Guid StoreFileId, string JournalBindingSha256,
    MirrorPulseNamespaceMemberProtectionSnapshot Original, MirrorPulseNamespaceMemberProtectionSnapshot Parent,
    DateTimeOffset StartedAt);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Atomically retains original resident file execution facts before any local byte mutation.</summary>
    /// <remarks>The Host must retain lifecycle/namespace admission and independently guard actual native objects and store rows.</remarks>
    public Task<MirrorPulseConflictKeepRemoteFileStart> RetainConflictKeepRemoteFileStartAsync(Guid commandId,
        ulong storeVolumeSerialNumber, Guid storeFileId, MirrorPulseNamespaceMemberProtectionSnapshot original,
        MirrorPulseNamespaceMemberProtectionSnapshot parent, DateTimeOffset startedAt, CancellationToken token = default) =>
        RetainKeepRemoteFileStartAsync(commandId, storeVolumeSerialNumber, storeFileId, original, parent, startedAt, null, token);

    /// <summary>Retains the first permission action with its file start and fence before native application.</summary>
    public Task<MirrorPulseConflictKeepRemoteFileStart> RetainConflictKeepRemoteFileStartAndActionAsync(Guid commandId,
        ulong storeVolumeSerialNumber, Guid storeFileId, MirrorPulseNamespaceMemberProtectionSnapshot original,
        MirrorPulseNamespaceMemberProtectionSnapshot parent, MirrorPulseConflictKeepRemoteFileAction action, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        ValidateKeepRemoteFileAction(action);
        return RetainKeepRemoteFileStartAsync(commandId, storeVolumeSerialNumber, storeFileId, original, parent, action.PreparedAt, action, token);
    }

    private async Task<MirrorPulseConflictKeepRemoteFileStart> RetainKeepRemoteFileStartAsync(Guid commandId,
        ulong storeVolumeSerialNumber, Guid storeFileId, MirrorPulseNamespaceMemberProtectionSnapshot original,
        MirrorPulseNamespaceMemberProtectionSnapshot parent, DateTimeOffset startedAt, MirrorPulseConflictKeepRemoteFileAction? action,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(original); ArgumentNullException.ThrowIfNull(parent);
        // Snapshot the caller's complete facts before awaiting; no caller-owned object becomes durable authority.
        original = FreezeKeepRemoteSnapshot(original); parent = FreezeKeepRemoteSnapshot(parent);
        if (commandId == Guid.Empty || storeFileId == Guid.Empty || startedAt <= DateTimeOffset.UnixEpoch)
            throw new ArgumentException("A file start requires its command, physical store file and first start time.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var journal = await ReadKeepRemoteJournalBindingCoreAsync(commandId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("All original journal correlations must be retained before the native start.");
            var start = new MirrorPulseConflictKeepRemoteFileStart(1, commandId, storeVolumeSerialNumber, storeFileId,
                Convert.ToHexString(SHA256.HashData(KeepRemoteJournalPayload(journal))), original, parent, startedAt);
            await RequireKeepRemoteFileStartRelationsAsync(start, journal, transaction, token).ConfigureAwait(false);
            if (action is not null) RequireKeepRemoteFileActionReferences(action, start);
            byte[] payload = KeepRemoteFileStartPayload(start);
            var retained = await ReadKeepRemoteFileStartCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            if (retained is not null)
            {
                if (!KeepRemoteFileStartPayload(retained).AsSpan().SequenceEqual(payload))
                    throw new InvalidOperationException("The first KeepRemote file start is immutable across owners and roles.");
                if (action is not null && await ReadKeepRemoteFileActionCoreAsync(commandId, transaction, token).ConfigureAwait(false) != action)
                    throw new InvalidOperationException("A retained KeepRemote file start cannot adopt a different first permission action.");
                transaction.Commit(); return retained;
            }
            var rootId = original.Context.Member.RootId!.Value;
            await RequireNamespaceMoveRootUnfencedAsync(rootId, null, transaction, token).ConfigureAwait(false);
            var births = new HashSet<RootId>();
            await AddUnregisteredBirthRootFencesAsync(births, transaction, token).ConfigureAwait(false);
            if (births.Contains(rootId)) throw new InvalidOperationException("Unresolved births must finish before a file start.");
            var current = await ReadNamespaceMemberProtectionSnapshotCoreAsync(original.Context.Member.MemberId, transaction, token).ConfigureAwait(false);
            var currentParent = await ReadNamespaceMemberProtectionSnapshotCoreAsync(parent.Context.Member.MemberId, transaction, token).ConfigureAwait(false);
            if (current is not null && original.Identity != current.Identity)
                throw new InvalidDataException("KeepRemote requires the unchanged original public identity before its first file start.");
            if (!SameKeepRemoteSnapshot(original, current) || !SameKeepRemoteSnapshot(parent, currentParent))
                throw new InvalidOperationException("KeepRemote requires unchanged current member and direct-parent protection, location and identity.");
            await RequireKeepRemoteFileStartRootAsync(start, transaction, token).ConfigureAwait(false);
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO conflict_keep_remote_file_starts(command_id,member_id,root_id,payload,fingerprint) VALUES($id,$member,$root,$payload,$hash);
                INSERT INTO conflict_keep_remote_file_pending(root_id,command_id) VALUES($root,$id);
                """;
            insert.Parameters.AddWithValue("$id", commandId.ToString("D"));
            insert.Parameters.AddWithValue("$member", original.Context.Member.MemberId.ToString("D"));
            insert.Parameters.AddWithValue("$root", rootId.ToString());
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            if (action is not null)
                await InsertKeepRemoteFileActionAsync(action, transaction, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); transaction.Commit(); return start;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads first pending execution facts without inspecting today's object or replacing the first role.</summary>
    public async Task<MirrorPulseConflictKeepRemoteFileStart?> ReadConflictKeepRemoteFileStartAsync(Guid commandId,
        CancellationToken token = default)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("The original command is required.", nameof(commandId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadKeepRemoteFileStartCoreAsync(commandId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads the one original KeepRemote file start that still owns the selected root.</summary>
    public async Task<MirrorPulseConflictKeepRemoteFileStart?> ReadPendingConflictKeepRemoteFileStartAsync(RootId rootId,
        CancellationToken token = default)
    {
        if (rootId.Value == Guid.Empty) throw new ArgumentException("A stable root ID is required.", nameof(rootId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadPendingKeepRemoteFileStartForRootCoreAsync(rootId, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseConflictKeepRemoteFileStart?> ReadKeepRemoteFileStartCoreAsync(Guid commandId,
        SqliteTransaction transaction, CancellationToken token)
    {
        byte[]? payload = null; byte[] fingerprint = []; string memberId = string.Empty, rootId = string.Empty;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT member_id,root_id,payload,fingerprint FROM conflict_keep_remote_file_starts WHERE command_id=$id;";
            query.Parameters.AddWithValue("$id", commandId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                memberId = reader.GetString(0); rootId = reader.GetString(1); payload = (byte[])reader[2]; fingerprint = (byte[])reader[3];
            }
        }
        if (payload is null)
        {
            await using var orphan = _connection.CreateCommand(); orphan.Transaction = transaction;
            orphan.CommandText = "SELECT 1 FROM conflict_keep_remote_file_pending WHERE command_id=$id;";
            orphan.Parameters.AddWithValue("$id", commandId.ToString("D"));
            if (await orphan.ExecuteScalarAsync(token).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An unfinished KeepRemote file application lost its original start facts.");
            return null;
        }
        if (payload.Length > 1_048_576 || !SHA256.HashData(payload).AsSpan().SequenceEqual(fingerprint))
            throw new InvalidDataException("The first KeepRemote file start fingerprint changed.");
        MirrorPulseConflictKeepRemoteFileStart start;
        try
        {
            start = JsonSerializer.Deserialize<MirrorPulseConflictKeepRemoteFileStart>(payload, TopologyJsonOptions)
                ?? throw new InvalidDataException("The first KeepRemote file start is empty.");
            if (start.CommandId != commandId || start.Original?.Context?.Member?.MemberId.ToString("D") != memberId ||
                start.Original?.Context?.Member?.RootId?.ToString() != rootId ||
                !KeepRemoteFileStartPayload(start).AsSpan().SequenceEqual(payload))
                throw new InvalidDataException("The first KeepRemote file start lost its canonical owner or member.");
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException)
        { throw new InvalidDataException("The first KeepRemote file start is invalid.", failure); }
        bool completed = await HasKeepRemoteAcknowledgementResultAsync(commandId, transaction, token).ConfigureAwait(false);
        await using (var pending = _connection.CreateCommand())
        {
            pending.Transaction = transaction; pending.CommandText = "SELECT root_id FROM conflict_keep_remote_file_pending WHERE command_id=$id;";
            pending.Parameters.AddWithValue("$id", commandId.ToString("D"));
            var pendingRoot = await pending.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (completed ? pendingRoot is not null : pendingRoot is not string value || value != rootId)
                throw new InvalidDataException("The KeepRemote file start does not match its original root fence state.");
        }
        var journal = await ReadKeepRemoteJournalBindingCoreAsync(commandId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original KeepRemote journal correlations disappeared.");
        await RequireKeepRemoteFileStartRelationsAsync(start, journal, transaction, token).ConfigureAwait(false);
        return start;
    }

    private async Task RequireKeepRemoteFileStartRelationsAsync(MirrorPulseConflictKeepRemoteFileStart start,
        MirrorPulseConflictKeepRemoteJournalBinding journal, SqliteTransaction transaction, CancellationToken token)
    {
        var preparation = await ReadConflictKeepRemoteCoreAsync(start.CommandId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The selected KeepRemote preparation disappeared.");
        var intent = preparation.Intent;
        var plan = await ReadCoalescingPlanCoreAsync(intent.PlanId, transaction, token).ConfigureAwait(false);
        var child = start.Original; var parent = start.Parent;
        // The snapshot retains the public address. Backend correspondence is verified by the
        // CfSharp boundary; those identifiers can differ while the native object stays the same.
        if (start.Version != 1 || start.StoreFileId == Guid.Empty || start.StartedAt < journal.ObservedAt ||
            start.JournalBindingSha256 != Convert.ToHexString(SHA256.HashData(KeepRemoteJournalPayload(journal))) ||
            plan.InstanceId != intent.InstanceId || plan.RootKey != intent.RemoteSource.RootKey || plan.ItemId != intent.ItemId ||
            intent.RejectedEffect is not { IsDirectory: false } rejected ||
            rejected.Kind != MirrorPulseWorkerChangeKind.ContentUpdate &&
                (rejected.Kind != MirrorPulseWorkerChangeKind.Create || rejected.ExpectedRevision is not null ||
                 plan.Effect != MirrorPulseCoalescedEffect.CreateFile) || intent.LocalContent is null ||
            preparation.LocalPreservation?.Copy is null || preparation.RemoteCopy is null ||
            child?.Context?.Member is not { IsDirectory: false, RootId: not null } member || child.Identity is null ||
            child.Context.Location is null || child.Context.Protection is null || child.Context.Verification is null ||
            parent?.Context?.Member is not { IsDirectory: true } parentMember || parent.Identity is null ||
            parent.Context.Location is null || parent.Context.Protection is null || parent.Context.Verification is null ||
            member.MemberId == Guid.Empty || child.Identity.ItemId != intent.ItemId || string.IsNullOrWhiteSpace(child.Identity.RemoteId) ||
            member.LocalObject != intent.LocalContent.UploadBinding.LocalObject || parentMember.RootId != member.RootId ||
            child.Context.Location.ParentMemberId != parentMember.MemberId || child.Context.Location.MemberId != member.MemberId ||
            child.Context.Protection.RoleSid != parent.Context.Protection.RoleSid || member.OwnerSid != parentMember.OwnerSid)
            throw new InvalidDataException("A file start requires the complete original file decision, copies, journal, object, identity and direct parent.");
        // The retained preparation already revalidates the exact current rejected program.
        // A prior Move can be acknowledged while this same group's ContentUpdate is rejected;
        // preserve that prefix instead of treating its raw plan kind as a new file operation.
        // A rejected Create keeps its original absence condition and complete file
        // program. KeepRemote preserves that history instead of retrying an upload
        // against a newly observed baseline.
    }

    private async Task RequireKeepRemoteFileStartRootAsync(MirrorPulseConflictKeepRemoteFileStart start,
        SqliteTransaction transaction, CancellationToken token)
    {
        var preparation = await ReadConflictKeepRemoteCoreAsync(start.CommandId, transaction, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original KeepRemote decision disappeared.");
        await using var query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload FROM adapter_topology WHERE id=1;";
        if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not string payload)
            throw new InvalidOperationException("A file start requires its owning Adapter topology.");
        var roots = DeserializeTopology(payload).Roots.Where(root => root.RootId == start.Original.Context.Member.RootId).ToArray();
        var intent = preparation.Intent;
        if (roots.Length != 1 || roots[0].InstanceId != intent.InstanceId || roots[0].UniquenessKey != intent.RemoteSource.RootKey ||
            (start.Original.Context.Location.RelativePath != intent.LocalRelativePath &&
             start.Original.Context.Location.RelativePath != roots[0].Label + "/" + intent.LocalRelativePath))
            throw new InvalidOperationException("A file start must retain this instance/root and its current full native location.");
    }

    private static MirrorPulseNamespaceMemberProtectionSnapshot FreezeKeepRemoteSnapshot(MirrorPulseNamespaceMemberProtectionSnapshot value) =>
        JsonSerializer.Deserialize<MirrorPulseNamespaceMemberProtectionSnapshot>(JsonSerializer.SerializeToUtf8Bytes(value, TopologyJsonOptions), TopologyJsonOptions)
        ?? throw new ArgumentException("Complete immutable member facts are required.", nameof(value));

    private static bool SameKeepRemoteSnapshot(MirrorPulseNamespaceMemberProtectionSnapshot expected, MirrorPulseNamespaceMemberProtectionSnapshot? current) =>
        current is not null && JsonSerializer.SerializeToUtf8Bytes(expected, TopologyJsonOptions).AsSpan()
            .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(current, TopologyJsonOptions));

    private static byte[] KeepRemoteFileStartPayload(MirrorPulseConflictKeepRemoteFileStart start)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(start, TopologyJsonOptions);
        if (payload.Length > 1_048_576) throw new InvalidDataException("The first KeepRemote file start exceeds its bound.");
        return payload;
    }
}
