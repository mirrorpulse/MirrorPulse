using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads this rejected Create's original protected local birth without adopting another file or backend generation.</summary>
    /// <remarks>The explicit KeepRemote choice retains its selected remote source separately; this proves only original local provenance.</remarks>
    public async Task<MirrorPulseNamespaceBirthPlan?> ReadConflictKeepRemoteOriginalBirthAsync(Guid commandId,
        CancellationToken token = default)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("The original KeepRemote command is required.", nameof(commandId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var preparation = await ReadConflictKeepRemoteCoreAsync(commandId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original KeepRemote preparation is missing.");
            var intent = preparation.Intent;
            var program = await ReadCoalescingPlanCoreAsync(intent.PlanId, transaction, token).ConfigureAwait(false);
            if (program.Effect != MirrorPulseCoalescedEffect.CreateFile ||
                intent.RejectedEffect is not { Kind: MirrorPulseWorkerChangeKind.Create, IsDirectory: false, ExpectedRevision: null } ||
                intent.LocalContent is null)
            { transaction.Commit(); return null; }
            var binding = intent.LocalContent.UploadBinding.LocalObject;
            await using var query = _connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT member_id FROM namespace_members WHERE volume_serial=$volume AND sync_root_file_id=$sync AND local_file_id=$file;";
            AddNamespaceBirthBindingParameters(query, binding);
            object? id = await query.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (id is null) { transaction.Commit(); return null; }
            var member = await ReadNamespaceMemberCoreAsync(ParseNamespaceMemberId(id), transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original birth binding lost its member.");
            if (member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth) { transaction.Commit(); return null; }
            var birth = await ReadNamespaceBirthCoreAsync(member.OriginId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original local birth is missing.");
            var plan = await ReadNamespaceBirthPlanCoreAsync(member.OriginId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original local identity plan is missing.");
            var conversion = await ReadNamespaceBirthConversionCoreAsync(member.OriginId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original ordinary birth binding is missing.");
            var observed = await ReadNamespaceBirthObservationCoreAsync(member.OriginId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original local birth observation is missing.");
            query.CommandText = "SELECT payload FROM adapter_topology WHERE id=1;"; query.Parameters.Clear();
            if (await query.ExecuteScalarAsync(token).ConfigureAwait(false) is not string topology)
                throw new InvalidDataException("The original local birth root topology is missing.");
            var roots = DeserializeTopology(topology).Roots.Where(root => root.InstanceId == intent.InstanceId &&
                root.UniquenessKey == intent.RemoteSource.RootKey).ToArray();
            if (roots.Length != 1 || roots[0].RootId != birth.RootId || member.RootId != birth.RootId ||
                birth.Origin is not (MirrorPulseNamespaceBirthOrigin.ControlledCreation or MirrorPulseNamespaceBirthOrigin.Import) ||
                birth.IsDirectory || member.IsDirectory || member.LocalObject != binding || conversion.LocalObject != binding ||
                observed.LocalObject != binding || observed.RootId != birth.RootId || !observed.IsPlaceholder || observed.IsDirectory ||
                observed.IsInSync || observed.LinkCount != 1 || plan.ItemId != intent.ItemId || observed.ItemId != plan.ItemId ||
                observed.RemoteId != plan.RemoteId || plan.RemoteRevision is not null || observed.RemoteRevision is not null)
                throw new InvalidDataException("KeepRemote requires this Create's exact original unaccepted local birth and complete native provenance.");
            transaction.Commit(); return plan;
        }
        finally { _gate.Release(); }
    }
}
