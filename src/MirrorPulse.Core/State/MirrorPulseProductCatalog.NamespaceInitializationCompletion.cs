using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Retains an audited original inventory and releases only its initialization fences atomically.</summary>
    /// <remarks>The Host supplies fresh native checks under its actor and guards; these catalog facts alone cannot perform that audit.</remarks>
    public async Task<MirrorPulseNamespaceInitializationCompletion> CompleteNamespaceInitializationAsync(Guid operationId,
        IReadOnlyList<MirrorPulseNamespaceMemberProtectionSnapshot> auditedMembers, DateTimeOffset observedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditedMembers);
        if (operationId == Guid.Empty || observedAt == default || auditedMembers.Count is < 2 or > 1_000_001)
            throw new ArgumentException("Initialization completion requires an anchor and a bounded native-audited original inventory.");
        var supplied = auditedMembers.ToArray();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using SqliteTransaction transaction = _connection.BeginTransaction();
            var intent = await ReadNamespaceInitializationCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original initialization admission is missing.");
            var completed = await ReadNamespaceInitializationCompletionCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            if (completed is not null)
            {
                await RequireNamespaceInitializationTerminalAsync(intent, completed, transaction, cancellationToken).ConfigureAwait(false);
                transaction.Commit(); return completed;
            }
            await RequireNamespaceInitializationRootsAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            if (observedAt < intent.PreparedAt || supplied.Any(member => member is null) ||
                supplied.Select(member => member.Context.Member.MemberId).Distinct().Count() != supplied.Length)
                throw new InvalidDataException("The audited initialization inventory is inconsistent.");
            var anchor = supplied.SingleOrDefault(member => member.Context.Member.ParentMemberId is null)
                ?? throw new InvalidDataException("The audited inventory has no sync-root anchor.");
            string role = anchor.Context.Protection.RoleSid;
            if (anchor.Context.Member.RootId is not null || anchor.Context.Location.RelativePath.Length != 0 ||
                !anchor.Context.Member.IsDirectory) throw new InvalidDataException("The initialization anchor is invalid.");
            var originals = supplied.Where(member => member.Context.Member.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal).ToArray();
            if (originals.Select(member => member.Context.Member.OriginId).Distinct().Count() != originals.Length)
                throw new InvalidDataException("The audited inventory duplicates original provenance.");
            var byOriginal = originals.ToDictionary(member => member.Context.Member.OriginId);
            var expected = new HashSet<Guid> { anchor.Context.Member.MemberId };
            foreach (var root in intent.Roots)
            {
                var tree = await ReadPermissionTreeCoreAsync(root.ManifestId, cancellationToken, transaction).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The initialization lost its original capture.");
                if (tree.Phase != MirrorPulseNamespacePermissionTreePhase.Sealed || tree.Definition.Anchor.Intent.RootId != root.RootId)
                    throw new InvalidDataException("Initialization requires the original sealed capture of each admitted root.");
                string seal = await ComputePermissionTreeFingerprintCoreAsync(tree, tree.Seal!.SealedAt, cancellationToken, transaction).ConfigureAwait(false);
                if (seal != tree.Seal.Fingerprint) throw new InvalidDataException("An initialized original capture changed.");
                for (int after = -1; after + 1 < tree.CapturedMembers;)
                {
                    var page = await ReadPermissionTreeMembersCoreAsync(tree.Definition, after, 256, cancellationToken, transaction).ConfigureAwait(false);
                    if (page.Count == 0) throw new InvalidDataException("An initialized original capture lost a page.");
                    foreach (var capture in page)
                    {
                        var original = capture.Preparation.Baseline;
                        if (!byOriginal.TryGetValue(original.EvidenceId, out var match))
                            throw new InvalidDataException("An audited original has no registered membership.");
                        if (match.Context.Member.LocalObject != original.LocalObject || match.Context.Member.RootId != root.RootId ||
                            match.Context.Location.RelativePath != original.RelativePath || !expected.Add(match.Context.Member.MemberId))
                            throw new InvalidDataException("The audited original inventory changed its binding or placement.");
                    }
                    after = page[^1].Sequence;
                }
                var rootMember = byOriginal[tree.Definition.Anchor.Baseline.EvidenceId];
                if (rootMember.Context.Location.ParentMemberId != anchor.Context.Member.MemberId ||
                    tree.Definition.Anchor.Baseline.LocalObject.VolumeSerialNumber != anchor.Context.Member.LocalObject.VolumeSerialNumber ||
                    tree.Definition.Anchor.Baseline.LocalObject.SyncRootFileId != anchor.Context.Member.LocalObject.SyncRootFileId)
                    throw new InvalidDataException("The initialized root belongs to another sync-root anchor.");
                var graph = await ReadNamespaceMemberTreeCoreAsync(rootMember.Context.Member.MemberId, transaction, cancellationToken).ConfigureAwait(false);
                if (!graph.Select(member => member.MemberId).ToHashSet().SetEquals(supplied.Where(member => member.Context.Member.RootId == root.RootId)
                    .Select(member => member.Context.Member.MemberId)))
                    throw new InvalidDataException("The registered root graph disagrees with its audited original inventory.");
            }
            if (!expected.SetEquals(supplied.Select(member => member.Context.Member.MemberId)))
                throw new InvalidDataException("An initialization cannot omit or add members to its original inventory.");
            foreach (var member in supplied)
            {
                var current = await ReadNamespaceMemberProtectionSnapshotCoreAsync(member.Context.Member.MemberId, transaction, cancellationToken).ConfigureAwait(false);
                if (current != member || member.Context.Protection.RoleSid != role || observedAt < member.Context.Protection.VerifiedAt)
                    throw new InvalidDataException("The native-audited permission, identity or member placement changed before completion.");
            }
            byte[] inventory = JsonSerializer.SerializeToUtf8Bytes(supplied.OrderBy(member => member.Context.Member.MemberId).ToArray(), TopologyJsonOptions);
            completed = new(operationId, anchor.Context.Member.MemberId, role, supplied.Length,
                Convert.ToHexString(SHA256.HashData(inventory)), observedAt);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(completed, TopologyJsonOptions);
            await using (SqliteCommand insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO namespace_initialization_completions(operation_id,payload,fingerprint) VALUES($id,$payload,$hash);";
                insert.Parameters.AddWithValue("$id", operationId.ToString("D")); insert.Parameters.AddWithValue("$payload", payload);
                insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (SqliteCommand release = _connection.CreateCommand())
            {
                release.Transaction = transaction; release.CommandText = "DELETE FROM namespace_initialization_roots WHERE operation_id=$id;";
                release.Parameters.AddWithValue("$id", operationId.ToString("D"));
                if (await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != intent.Roots.Count)
                    throw new InvalidDataException("Initialization lost its owned root reservations during completion.");
            }
            transaction.Commit(); return completed;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceInitializationCompletion?> ReadNamespaceInitializationCompletionAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using SqliteTransaction transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadNamespaceInitializationCompletionCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            if (result is not null)
            {
                var intent = await ReadNamespaceInitializationCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The initialization completion lost its original admission.");
                await RequireNamespaceInitializationTerminalAsync(intent, result, transaction, cancellationToken).ConfigureAwait(false);
            }
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseNamespaceInitializationCompletion?> ReadNamespaceInitializationCompletionCoreAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        await using SqliteCommand query = _connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT payload,fingerprint FROM namespace_initialization_completions WHERE operation_id=$id;";
        query.Parameters.AddWithValue("$id", operationId.ToString("D"));
        await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        byte[] payload = (byte[])reader.GetValue(0), hash = (byte[])reader.GetValue(1);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), hash))
            throw new InvalidDataException("The initialization completion fingerprint changed.");
        var result = JsonSerializer.Deserialize<MirrorPulseNamespaceInitializationCompletion>(payload, TopologyJsonOptions)
            ?? throw new InvalidDataException("The initialization completion is missing.");
        if (result.OperationId != operationId || result.AnchorMemberId == Guid.Empty || result.MemberCount < 2 || result.CompletedAt == default ||
            result.CatalogFingerprint is not { Length: 64 } || !result.CatalogFingerprint.All(char.IsAsciiHexDigitUpper) ||
            !new SecurityIdentifier(result.RoleSid).IsWellKnown(WellKnownSidType.LogonIdsSid))
            throw new InvalidDataException("The initialization completion is inconsistent.");
        return result;
    }
}
