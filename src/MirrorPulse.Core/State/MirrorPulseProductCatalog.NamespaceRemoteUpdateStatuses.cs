using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>Product settlement of an original update; this does not establish publication of an Adapter cursor.</summary>
public sealed record MirrorPulseNamespaceRemoteUpdateStatus(Guid OperationId, RootId RootId, InstanceId InstanceId,
    Guid MemberId, string RelativePath, string BatchId, int Ordinal, string ChangeId, string Stage,
    DateTimeOffset PreparedAt, DateTimeOffset? StartedAt, string? Outcome, string? EvidenceSource,
    DateTimeOffset? OutcomeObservedAt, string PackageVersion, string PackageSource);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseNamespaceRemoteUpdate?> ReadNamespaceRemoteUpdateAsync(Guid operationId,
        CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var retained = await ReadRemoteUpdateCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return retained;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseNamespaceRemoteUpdateStatus?> ReadNamespaceRemoteUpdateStatusAsync(Guid operationId,
        CancellationToken token = default)
    {
        var retained = await ReadNamespaceRemoteUpdateAsync(operationId, token).ConfigureAwait(false);
        if (retained is null) return null;
        var plan = retained.Plan;
        return new(plan.OperationId, plan.Child.Context.Member.RootId!.Value, plan.InstanceId,
            plan.Child.Context.Member.MemberId, plan.Child.Context.Location.RelativePath, plan.BatchId, plan.Ordinal,
            plan.ChangeId, retained.Outcome is not null ? "ProductSettled" : retained.StartedAt is not null ? "InvocationMayHaveBegun" : "Prepared",
            plan.PreparedAt, retained.StartedAt, retained.Outcome?.Disposition.ToString(), retained.Outcome?.Source.ToString(),
            retained.Outcome?.VerifiedAt, plan.PackageVersion, plan.PackageSource);
    }
}
