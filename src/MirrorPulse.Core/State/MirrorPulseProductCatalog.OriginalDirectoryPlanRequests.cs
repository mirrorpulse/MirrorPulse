using Microsoft.Data.Sqlite;
using MirrorPulse.Adapter.Sdk;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task<AdapterDirectoryRetirementRequest> BuildDirectoryPlanRetirementAsync(DirectoryPlanStep step,
        SqliteTransaction transaction, CancellationToken token)
    {
        var directory = step.Step.Directory ?? throw new InvalidOperationException("A leaf cannot dispatch final directory retirement.");
        await RequireDirectoryPlanPrefixAsync(step, transaction, token).ConfigureAwait(false);
        var required = new List<AdapterDirectoryChildRequirement>();
        var accepted = new List<AdapterOwnedChildEffectReceipt>();
        for (int ordinal = 0; ordinal < step.Ordinal; ordinal++)
        {
            var child = step.Plan.Steps[ordinal];
            if (MirrorPulseOriginalDirectoryPlan.ParentOf(child.Operation) != directory.Directory) continue;
            var receipt = await ReadAcceptedDirectoryPlanMemberAsync(step, ordinal, transaction, token).ConfigureAwait(false);
            required.Add(new(child.Operation.OperationId, receipt.RequestFingerprint, receipt.Child, child.OriginalGeneration));
            accepted.Add(receipt);
        }
        var terminal = accepted.Count == 0 ? null : accepted[^1];
        string revision = terminal?.Parents.Single(value => value.OriginalParent == directory).AfterRevision ?? directory.OriginalRevision;
        var evidence = new AdapterDirectoryRetirementEvidence(AdapterDirectoryEffects.Version, directory, step.Fingerprint,
            required, accepted, revision, terminal is null ? null : AdapterDirectoryEffects.Fingerprint(terminal));
        var predecessors = await ReadDirectoryPlanPredecessorsAsync(step, transaction, token).ConfigureAwait(false);
        var operation = step.Step.Operation with { Preconditions = new(revision) };
        var request = new AdapterDirectoryRetirementRequest(AdapterDirectoryEffects.Version, step.Step.OperationKind, operation,
            evidence, step.Step.Parents, predecessors.Select(value => value.Receipt).DistinctBy(value => value.OperationId).ToArray(), "");
        return request with { RequestFingerprint = request.CalculateFingerprint() };
    }

    private async Task<MirrorPulseOriginalDirectoryLeaf> BuildDirectoryPlanLeafAsync(DirectoryPlanStep step,
        SqliteTransaction transaction, CancellationToken token)
    {
        if (step.Step.Directory is not null) throw new InvalidOperationException("A directory requires final retirement.");
        await RequireDirectoryPlanPrefixAsync(step, transaction, token).ConfigureAwait(false);
        var predecessors = await ReadDirectoryPlanPredecessorsAsync(step, transaction, token).ConfigureAwait(false);
        return new(step.Step, step.Fingerprint, DirectoryPlanLeafFingerprint(step), predecessors, step.Receipt);
    }

    private async Task<IReadOnlyList<MirrorPulseOriginalDirectoryPredecessor>> ReadDirectoryPlanPredecessorsAsync(
        DirectoryPlanStep step, SqliteTransaction transaction, CancellationToken token)
    {
        var predecessors = new List<MirrorPulseOriginalDirectoryPredecessor>(step.Step.Parents.Count);
        foreach (var parent in step.Step.Parents)
        {
            for (int ordinal = step.Ordinal - 1; ordinal >= 0; ordinal--)
            {
                if (!step.Plan.Steps[ordinal].Parents.Contains(parent)) continue;
                var receipt = await ReadAcceptedDirectoryPlanMemberAsync(step, ordinal, transaction, token).ConfigureAwait(false);
                predecessors.Add(new(parent, receipt));
                break;
            }
        }
        return predecessors;
    }

    private async Task<AdapterOwnedChildEffectReceipt> ReadAcceptedDirectoryPlanMemberAsync(DirectoryPlanStep current,
        int ordinal, SqliteTransaction transaction, CancellationToken token)
    {
        var accepted = await ReadDirectoryPlanMemberAsync(current.Plan, current.Fingerprint, ordinal, transaction, token).ConfigureAwait(false);
        return accepted.Receipt ?? throw new InvalidOperationException("An original directory plan dependency has no accepted receipt.");
    }

    private static string DirectoryPlanLeafFingerprint(DirectoryPlanStep step) =>
        AdapterDirectoryEffects.FingerprintRequest(step.Step.OperationKind, step.Plan.InstanceId.Value,
            step.Plan.AdmissionId, step.Step.Operation, step.Step.Parents);

    private static void RequirePlanReceipt(DirectoryPlanStep step, AdapterOwnedChildEffectReceipt receipt, string requestFingerprint)
    {
        var operation = step.Step.Operation;
        AdapterDirectoryEffects.RequireOriginalRequest(receipt, step.Plan.InstanceId.Value, step.Plan.AdmissionId,
            operation.OperationId, requestFingerprint);
        var destination = step.Step.OperationKind == "Move"
            ? new AdapterFileAddress(operation.DestinationRootKey!, operation.DestinationPath!) : null;
        if (receipt.OperationKind != step.Step.OperationKind || receipt.OriginalChildGeneration != step.Step.OriginalGeneration ||
            receipt.Child != new AdapterFileAddress(operation.RootKey, operation.Path) || receipt.Destination != destination ||
            receipt.Parents.Count != step.Step.Parents.Count ||
            receipt.Parents.Any(transition => !step.Step.Parents.Contains(transition.OriginalParent)))
            throw new InvalidDataException("The receipt changed its original directory plan operation.");
    }
}
