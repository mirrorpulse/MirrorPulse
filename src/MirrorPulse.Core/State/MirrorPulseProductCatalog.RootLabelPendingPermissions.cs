using System.Text;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>Retained original permission intents that must settle before guarded cancellation.</summary>
/// <remarks>These are recovery inputs only; native facts and tree readiness are not implied.</remarks>
public sealed record MirrorPulseRootLabelPendingPermissionRecovery(MirrorPulseRootLabelCancellationRecoveryOwner Owner,
    Guid RootMemberId, IReadOnlyList<MirrorPulseNamespaceMemberRecoveryContext> PendingOriginals)
{
    /// <summary>Registered born member IDs in direct parent-first order; no native protection is implied.</summary>
    public IReadOnlyList<Guid> BornMembers { get; init; } = [];
}

public sealed partial class MirrorPulseProductCatalog
{
    internal const string RootLabelPendingPermissionChildrenPageSql = """
        SELECT member_id,child_name_key FROM namespace_member_names
        WHERE parent_member_id=$parent AND child_name_key>$after
        ORDER BY child_name_key LIMIT 16;
        """;

    /// <summary>Reads a bounded original-source inventory under its exact pending current-inventory initializer.</summary>
    public async Task<MirrorPulseRootLabelPendingPermissionRecovery?> ReadRootLabelPendingPermissionRecoveryAsync(
        Guid operationId, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("The original Label operation is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var owner = await ReadRootLabelCancellationRecoveryOwnerCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            if (owner is null) { transaction.Commit(); return null; }
            var history = await ReadManagedRootRenameHistoryCoreAsync(operationId, cancellationToken, transaction).ConfigureAwait(false);
            await using var query = _connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT member_id FROM namespace_members WHERE volume_serial=$volume AND sync_root_file_id=$sync AND local_file_id=$file;";
            AddNamespaceBirthBindingParameters(query, history.Proof!.LocalObject);
            object? id = await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var root = id is null ? null : await ReadNamespaceMemberCoreAsync(ParseNamespaceMemberId(id), transaction, cancellationToken).ConfigureAwait(false);
            if (root is null || !root.IsDirectory || root.RootId != history.Intent.RootId || root.LocalObject != history.Proof.LocalObject)
                throw new InvalidDataException("Pending cancellation permission recovery lost its exact original root binding.");
            var rootLocation = await ReadNamespaceMemberLocationCoreAsync(root, transaction, cancellationToken).ConfigureAwait(false);
            if (rootLocation.ParentMemberId is null || rootLocation.RelativePath != history.Intent.SourceName)
                throw new InvalidOperationException("Pending permission recovery requires the unchanged original first-level source.");
            query.Parameters.Clear(); query.CommandText = "SELECT payload FROM adapter_topology WHERE id=1;";
            string? payload = (string?)await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            var registration = payload is null ? null : DeserializeTopology(payload).Roots.SingleOrDefault(item => item.RootId == root.RootId);
            if (registration is null || registration.DirectoryName != history.Intent.SourceName ||
                registration.State is not (MirrorPulse.Core.Contracts.RootRegistrationState.Active or MirrorPulse.Core.Contracts.RootRegistrationState.Disabled))
                throw new InvalidOperationException("Pending permission recovery cannot reinterpret the original root mapping or lifecycle.");
            var pending = new List<MirrorPulseNamespaceMemberRecoveryContext>();
            var born = new List<Guid>();
            var admitted = new HashSet<Guid> { root.MemberId };
            var queue = new Queue<(Guid MemberId, Guid? ParentId)>();
            queue.Enqueue((root.MemberId, rootLocation.ParentMemberId));
            while (queue.TryDequeue(out var selected))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var context = (await ReadNamespaceMemberRecoveryLineageCoreAsync(owner.InitializationOperationId,
                    selected.MemberId, transaction, cancellationToken).ConfigureAwait(false))[0];
                if (context.Member.RootId != root.RootId || context.Member.OwnerSid != root.OwnerSid ||
                    context.Location.ParentMemberId != selected.ParentId)
                    throw new InvalidDataException("Pending permission recovery cannot replace any original member or committed parent.");
                if (context.Permission is { Phase: not MirrorPulseNamespacePermissionPhase.Verified } permission)
                {
                    if (permission.Phase == MirrorPulseNamespacePermissionPhase.RecoveryRequired ||
                        permission.Intent.Kind == MirrorPulseNamespacePermissionChangeKind.Restore)
                        throw new InvalidOperationException("A conflicted original permission or explicit restoration requires separate recovery.");
                    pending.Add(context);
                }
                if (context.Member.Origin == MirrorPulseNamespaceMemberOrigin.ProtectedBirth) born.Add(context.Member.MemberId);
                string after = string.Empty;
                while (true)
                {
                    var page = new List<(Guid MemberId, string NameKey)>(16);
                    await using (var children = _connection.CreateCommand())
                    {
                        children.Transaction = transaction; children.CommandText = RootLabelPendingPermissionChildrenPageSql;
                        children.Parameters.AddWithValue("$parent", selected.MemberId.ToString("D"));
                        children.Parameters.AddWithValue("$after", after);
                        await using var reader = await children.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            string key = reader.GetString(1);
                            if (page.Count == 16 || Encoding.UTF8.GetBytes(key).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(page.Count == 0 ? after : page[^1].NameKey)) <= 0)
                                throw new InvalidDataException("Pending permission children lost their strict bounded continuation.");
                            page.Add((ParseNamespaceMemberId(reader.GetString(0)), key));
                        }
                    }
                    if (page.Count != 0 && !context.Member.IsDirectory)
                        throw new InvalidDataException("A non-directory pending recovery member owns children.");
                    foreach (var child in page)
                    {
                        if (admitted.Count == 65_536)
                            throw new InvalidOperationException("Pending permission recovery exceeds its complete selected-root inventory bound.");
                        if (!admitted.Add(child.MemberId)) throw new InvalidDataException("Pending permission recovery contains a duplicate member or cycle.");
                        queue.Enqueue((child.MemberId, selected.MemberId));
                    }
                    if (page.Count < 16) break;
                    after = page[^1].NameKey;
                }
            }
            transaction.Commit(); return new(owner, root.MemberId, pending.AsReadOnly()) { BornMembers = born.AsReadOnly() };
        }
        finally { _gate.Release(); }
    }
}
