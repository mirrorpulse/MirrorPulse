using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>Current live and retired membership, protection and dispatch fences from one transaction.</summary>
/// <remarks>This retained inventory grants no native authorization, Host readiness or journal acknowledgment.</remarks>
public sealed record MirrorPulseNamespaceCurrentTreeSnapshot(Guid AnchorMemberId,
    IReadOnlyList<MirrorPulseNamespaceMemberProtectionSnapshot> Members,
    IReadOnlyList<MirrorPulseNamespaceRetirement> RetiredMembers,
    IReadOnlyList<RootId> FencedRoots, string Fingerprint)
{
    /// <summary>The explicitly selected root; null retains the complete-inventory contract.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RootId? ScopeRoot { get; init; }

    /// <summary>Removed original members settled by one SDK directory invocation, without invented leaf intents.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MirrorPulseNamespaceRemoteRetirementTreeRetiredMember>? RetiredTreeMembers { get; init; }
}

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads the complete current graph, preserving first provenance and completed positions.</summary>
    /// <remarks>Lost names, disconnected members and damaged retirement or pending evidence fail closed.</remarks>
    public async Task<MirrorPulseNamespaceCurrentTreeSnapshot> ReadNamespaceCurrentTreeSnapshotAsync(Guid anchorMemberId,
        CancellationToken cancellationToken = default)
    {
        if (anchorMemberId == Guid.Empty) throw new ArgumentException("A sync-root anchor member ID is required.", nameof(anchorMemberId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadNamespaceCurrentTreeSnapshotCoreAsync(anchorMemberId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Reads one selected root and its anchor without requiring other roots' current protection.</summary>
    /// <remarks>Structural membership and name integrity remain global. Protection and dispatch
    /// fences are correlated only for the selected root; no native audit is performed.</remarks>
    public async Task<MirrorPulseNamespaceCurrentTreeSnapshot> ReadNamespaceCurrentRootSnapshotAsync(Guid anchorMemberId,
        RootId rootId, CancellationToken cancellationToken = default)
    {
        if (anchorMemberId == Guid.Empty || rootId.Value == Guid.Empty)
            throw new ArgumentException("A scoped current inventory requires an anchor and a selected root.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadNamespaceCurrentTreeSnapshotCoreAsync(anchorMemberId, transaction,
                cancellationToken, rootId).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceCurrentTreeSnapshot> ReadNamespaceCurrentTreeSnapshotCoreAsync(Guid anchorMemberId,
        SqliteTransaction transaction, CancellationToken cancellationToken, RootId? scopeRoot = null)
    {
        var memberIds = new List<Guid>();
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction; query.CommandText = "SELECT member_id FROM namespace_members ORDER BY member_id LIMIT 1000002;";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) memberIds.Add(ParseNamespaceMemberId(reader.GetString(0)));
        }
        if (memberIds.Count is < 1 or > 1_000_001) throw new InvalidDataException("The current membership inventory is empty or exceeds its bound.");
        var live = new HashSet<Guid>();
        var retired = new List<MirrorPulseNamespaceRetirement>();
        var retiredTrees = new List<MirrorPulseNamespaceRemoteRetirementTreeRetiredMember>();
        var registrations = new Dictionary<Guid, MirrorPulseNamespaceMemberRegistration>();
        foreach (Guid id in memberIds)
        {
            var member = await ReadNamespaceMemberCoreAsync(id, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("A current inventory member lost its retained provenance.");
            registrations.Add(id, member);
            Guid? retirementId = null;
            await using (var query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = "SELECT operation_id FROM namespace_member_retirements WHERE member_id=$member ORDER BY rowid DESC LIMIT 1;";
                query.Parameters.AddWithValue("$member", id.ToString("D"));
                if (await query.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is { } value) retirementId = ParseNamespaceMemberId(value);
            }
            var retirement = retirementId is { } operation
                ? await ReadNamespaceRetirementCoreAsync(operation, transaction, cancellationToken).ConfigureAwait(false) : null;
            var treeRetirement = await ReadRemovedRemoteTreeMemberCoreAsync(id, transaction, cancellationToken).ConfigureAwait(false);
            if (retirement?.Phase == MirrorPulseNamespaceRetirementPhase.Completed)
            {
                if (treeRetirement is not null) throw new InvalidDataException("A retired inventory member has competing leaf and tree owners.");
                if (retirement.Intent.MemberId != id || retirement.Intent.LocalObject != member.LocalObject ||
                    retirement.StartedAt is null || retirement.NativeDeletedAt is null || retirement.CompletedAt is null)
                    throw new InvalidDataException("A retired inventory member lost its correlated deletion receipt.");
                retired.Add(retirement);
            }
            else if (treeRetirement is not null)
            {
                if (treeRetirement.Original.Context.Member != member) throw new InvalidDataException("A tree-retired inventory member changed its original provenance.");
                retiredTrees.Add(treeRetirement);
            }
            else live.Add(id);
        }
        var anchor = await ReadNamespaceMemberCoreAsync(anchorMemberId, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The sync-root anchor is not registered.");
        if (anchor.ParentMemberId is not null || anchor.RootId is not null || anchor.RelativePath.Length != 0 || !anchor.IsDirectory || !live.Contains(anchorMemberId))
            throw new InvalidDataException("The current inventory requires its live sync-root anchor.");
        var graph = await ReadNamespaceMemberTreeCoreAsync(anchorMemberId, transaction, cancellationToken).ConfigureAwait(false);
        if (!live.SetEquals(graph.Select(entry => entry.MemberId)))
            throw new InvalidDataException("A live member is disconnected or a retired member still occupies the current graph.");
        var names = new HashSet<Guid>();
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction; query.CommandText = "SELECT member_id FROM namespace_member_names;";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) names.Add(ParseNamespaceMemberId(reader.GetString(0)));
        }
        if (!names.SetEquals(live.Where(id => id != anchorMemberId)))
            throw new InvalidDataException("The complete name index differs from the live inventory.");
        if (scopeRoot is { } selected)
        {
            var topRoots = graph.Where(entry => entry.ParentMemberId == anchorMemberId)
                .Select(entry => registrations[entry.MemberId]).Where(member => member.RootId is not null)
                .GroupBy(member => member.RootId!.Value).ToDictionary(group => group.Key, group => group.Count());
            if (!topRoots.TryGetValue(selected, out int count) || count != 1)
                throw new InvalidDataException("A selected root has no unique registered first-level member.");
            retired = retired.Where(member => member.Intent.RootId == selected).ToList();
            retiredTrees = retiredTrees.Where(member => member.Original.Context.Member.RootId == selected).ToList();
        }
        var snapshots = new List<MirrorPulseNamespaceMemberProtectionSnapshot>(graph.Count);
        foreach (var entry in graph)
        {
            if (scopeRoot is not null && entry.MemberId != anchorMemberId && registrations[entry.MemberId].RootId != scopeRoot) continue;
            var snapshot = await ReadNamespaceMemberProtectionSnapshotCoreAsync(entry.MemberId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("A live inventory member lost its coherent protection and identity.");
            if (snapshot.Context.Location.RelativePath != entry.RelativePath || snapshot.Context.Location.ParentMemberId != entry.ParentMemberId ||
                snapshot.Context.Member.LocalObject != entry.LocalObject || snapshot.Context.Member.IsDirectory != entry.IsDirectory)
                throw new InvalidDataException("A current inventory member changed its committed position or binding.");
            snapshots.Add(snapshot);
        }
        var fences = await ReadNamespaceRootFencesCoreAsync(true, transaction, cancellationToken).ConfigureAwait(false);
        var result = new MirrorPulseNamespaceCurrentTreeSnapshot(anchorMemberId, snapshots.AsReadOnly(), retired.AsReadOnly(),
            scopeRoot is null ? fences : Array.AsReadOnly(fences.Where(root => root == scopeRoot).ToArray()), string.Empty)
        { ScopeRoot = scopeRoot, RetiredTreeMembers = retiredTrees.Count == 0 ? null : retiredTrees.AsReadOnly() };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(result, TopologyJsonOptions);
        result = result with { Fingerprint = Convert.ToHexString(SHA256.HashData(payload)) };
        return result;
    }
}
