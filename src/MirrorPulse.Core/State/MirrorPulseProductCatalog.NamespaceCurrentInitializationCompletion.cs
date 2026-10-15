using System.Text.Json;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Completes a fresh audit of the evolved current graph and releases only its initializer's fences.</summary>
    /// <remarks>The Host must supply actual native checks under its shared actor, role and retained anchor guard.
    /// Catalog correlation cannot substitute for that audit or supply enduring native authorization.</remarks>
    public async Task<MirrorPulseNamespaceInitializationCompletion> CompleteNamespaceCurrentInitializationAsync(Guid operationId,
        MirrorPulseNamespaceCurrentTreeSnapshot auditedInventory, DateTimeOffset observedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditedInventory);
        if (operationId == Guid.Empty || observedAt == default || auditedInventory.AnchorMemberId == Guid.Empty)
            throw new ArgumentException("Current initialization requires its operation ID and native-audited current inventory.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var intent = await ReadNamespaceInitializationCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The current initialization admission is missing.");
            if (intent.RequestedInventoryKind is { } requested && requested != MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory)
                throw new InvalidOperationException("The pending initializer requires original-inventory recovery.");
            var replay = await ReadNamespaceInitializationCompletionCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay.InventoryKind != MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory)
                    throw new InvalidOperationException("This operation completed the original inventory rather than the current inventory.");
                await RequireNamespaceInitializationTerminalAsync(intent, replay, transaction, cancellationToken).ConfigureAwait(false);
                transaction.Commit(); return replay;
            }
            await RequireNamespaceInitializationRootsAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            var current = await ReadNamespaceCurrentTreeSnapshotCoreAsync(auditedInventory.AnchorMemberId, transaction, cancellationToken).ConfigureAwait(false);
            if (!SameNamespaceReplacementPayload(current, auditedInventory) || observedAt < intent.PreparedAt ||
                current.Members.Any(member => observedAt < member.Context.Protection.VerifiedAt))
                throw new InvalidDataException("The native-audited current graph, identity, protection or fences changed before completion.");
            var anchor = current.Members.Single(member => member.Context.Member.MemberId == current.AnchorMemberId);
            string role = anchor.Context.Protection.RoleSid;
            if (current.Members.Any(member => member.Context.Protection.RoleSid != role || member.Context.Member.OwnerSid != anchor.Context.Member.OwnerSid))
                throw new InvalidDataException("The audited current inventory does not have one coherent Host role and original owner.");
            var roots = current.Members.Where(member => member.Context.Location.ParentMemberId == current.AnchorMemberId).ToArray();
            if (roots.Length != intent.Roots.Count || !roots.Select(member => member.Context.Member.RootId).ToHashSet()
                .SetEquals(intent.Roots.Select(root => (Contracts.RootId?)root.RootId)) ||
                !current.FencedRoots.ToHashSet().SetEquals(intent.Roots.Select(root => root.RootId)))
                throw new InvalidDataException("Current completion requires every admitted root and only its owned dispatch fences.");
            foreach (var root in intent.Roots)
            {
                // Initialization alone is allowed; no unfinished move, retirement, replacement,
                // born recovery or unregistered birth may disappear behind that same root ID.
                await RequireNamespaceMoveRootUnfencedAsync(root.RootId, null, transaction, cancellationToken).ConfigureAwait(false);
                var rootMember = roots.Single(member => member.Context.Member.RootId == root.RootId).Context.Member;
                if (root.BornRootMemberId is not null)
                {
                    var admitted = await RequireNamespaceInitializationBornRootAsync(root, intent.PreparedAt, transaction, cancellationToken).ConfigureAwait(false);
                    if (admitted != rootMember || current.Members.Any(member => member.Context.Member.RootId == root.RootId &&
                        member.Context.Member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth))
                        throw new InvalidDataException("The audited born root cannot adopt captured originals or another root member.");
                    foreach (var retired in current.RetiredMembers.Where(member => member.Intent.RootId == root.RootId))
                        if ((await ReadNamespaceMemberCoreAsync(retired.Intent.MemberId, transaction, cancellationToken).ConfigureAwait(false))
                            is not { Origin: MirrorPulseNamespaceMemberOrigin.ProtectedBirth })
                            throw new InvalidDataException("Retired originals cannot be hidden below a born-root admission.");
                    continue;
                }
                var tree = await ReadPermissionTreeCoreAsync(root.ManifestId, cancellationToken, transaction).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The current initialization lost its original root capture.");
                if (tree.Phase != MirrorPulseNamespacePermissionTreePhase.Sealed || tree.Definition.Anchor.Baseline.RootId != root.RootId ||
                    await ComputePermissionTreeFingerprintCoreAsync(tree, tree.Seal!.SealedAt, cancellationToken, transaction).ConfigureAwait(false) != tree.Seal.Fingerprint)
                    throw new InvalidDataException("The current initialization requires its unchanged sealed original root capture.");
                if (rootMember.Origin != MirrorPulseNamespaceMemberOrigin.CapturedOriginal || rootMember.OriginId != tree.Definition.Anchor.Baseline.EvidenceId)
                    throw new InvalidDataException("The live root lost its captured original provenance.");
                var registered = new List<MirrorPulseNamespaceMemberRegistration>();
                registered.AddRange(current.Members.Where(member => member.Context.Member.RootId == root.RootId).Select(member => member.Context.Member));
                foreach (var retired in current.RetiredMembers.Where(member => member.Intent.RootId == root.RootId))
                    registered.Add((await ReadNamespaceMemberCoreAsync(retired.Intent.MemberId, transaction, cancellationToken).ConfigureAwait(false))!);
                var originals = registered.Where(member => member.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal).ToDictionary(member => member.OriginId);
                if (originals.Count != tree.CapturedMembers)
                    throw new InvalidDataException("The current graph contains original membership outside its retained capture.");
                for (int after = -1; after + 1 < tree.CapturedMembers;)
                {
                    var page = await ReadPermissionTreeMembersCoreAsync(tree.Definition, after, 256, cancellationToken, transaction).ConfigureAwait(false);
                    if (page.Count == 0) throw new InvalidDataException("The original capture lost an inventory page.");
                    foreach (var capture in page)
                    {
                        var first = capture.Preparation.Baseline;
                        if (!originals.TryGetValue(first.EvidenceId, out var member) || member.LocalObject != first.LocalObject ||
                            member.OwnerSid != first.OwnerSid || member.IsDirectory != first.IsDirectory)
                            throw new InvalidDataException("A captured original disappeared from both live and completed retired membership.");
                    }
                    after = page[^1].Sequence;
                }
            }
            var unpublished = new HashSet<Contracts.RootId>();
            await AddUnregisteredBirthRootFencesAsync(unpublished, transaction, cancellationToken).ConfigureAwait(false);
            if (unpublished.Overlaps(intent.Roots.Select(root => root.RootId)))
                throw new InvalidOperationException("An unresolved unregistered birth prevents current initialization completion.");
            var completed = new MirrorPulseNamespaceInitializationCompletion(operationId, current.AnchorMemberId, role,
                current.Members.Count, current.Fingerprint, observedAt)
            { InventoryKind = MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory };
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(completed, TopologyJsonOptions);
            await using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO namespace_initialization_completions(operation_id,payload,fingerprint) VALUES($id,$payload,$hash);";
                insert.Parameters.AddWithValue("$id", operationId.ToString("D")); insert.Parameters.AddWithValue("$payload", payload);
                insert.Parameters.AddWithValue("$hash", System.Security.Cryptography.SHA256.HashData(payload));
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var release = _connection.CreateCommand())
            {
                release.Transaction = transaction; release.CommandText = "DELETE FROM namespace_initialization_roots WHERE operation_id=$id;";
                release.Parameters.AddWithValue("$id", operationId.ToString("D"));
                if (await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != intent.Roots.Count)
                    throw new InvalidDataException("Current completion lost its owned initialization reservations.");
            }
            transaction.Commit(); return completed;
        }
        finally { _gate.Release(); }
    }
}
