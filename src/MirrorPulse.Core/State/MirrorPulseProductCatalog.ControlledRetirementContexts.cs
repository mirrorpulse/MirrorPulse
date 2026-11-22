namespace MirrorPulse.Core.State;

/// <summary>Historical ownership and committed position of one completed controlled local deletion.</summary>
/// <remarks>This context does not restore membership, admit a root, prove current native absence or authorize a journal ACK.</remarks>
public sealed record MirrorPulseNamespaceControlledRetirementContext(MirrorPulseNamespaceRetirement Retirement,
    MirrorPulseNamespaceMemberRegistration Member, MirrorPulseNamespaceMemberLocation Location,
    MirrorPulseNamespaceMemberIdentity Identity);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Reads the original deletion context without reviving the retired member's active name.</summary>
    public async Task<MirrorPulseNamespaceControlledRetirementContext> ReadNamespaceControlledRetirementContextAsync(
        Guid operationId, CancellationToken token = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An original controlled retirement is required.", nameof(operationId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var retirement = await ReadNamespaceRetirementCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original controlled retirement is missing.");
            if (retirement.Phase != MirrorPulseNamespaceRetirementPhase.Completed || retirement.StartedAt is null ||
                retirement.StartedItemId is null || retirement.NativeDeletedAt is null || retirement.CompletedAt is null ||
                retirement.Intent.RemoteEntry is not null || retirement.Intent.TransferEntry is not null || retirement.Intent.BatchOperationId is not null)
                throw new InvalidOperationException("Historical controlled context requires its complete original local invocation and native receipts.");
            var member = await ReadNamespaceMemberCoreAsync(retirement.Intent.MemberId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original controlled retirement lost its member.");
            var location = await ReadNamespaceMemberLocationCoreAsync(member, transaction, token).ConfigureAwait(false);
            var identity = await ReadNamespaceMemberIdentityCoreAsync(member, transaction, token).ConfigureAwait(false);
            if (member.RootId != retirement.Intent.RootId || member.LocalObject != retirement.Intent.LocalObject ||
                member.IsDirectory != retirement.Intent.IsDirectory || member.OwnerSid != retirement.Intent.OwnerSid ||
                location.RelativePath != retirement.Intent.RelativePath || location.ParentMemberId is null ||
                identity.ItemId != retirement.StartedItemId ||
                !await ReadCompletedNamespaceRetirementForMemberCoreAsync(member.MemberId, transaction, token).ConfigureAwait(false))
                throw new InvalidDataException("The controlled retirement context no longer matches its original object, identity and committed position.");
            var result = new MirrorPulseNamespaceControlledRetirementContext(retirement, member, location, identity);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }
}
