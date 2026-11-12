using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>One original source effect and its backend generation and parent witnesses.</summary>
public sealed record MirrorPulseOriginalDirectoryStep(string OperationKind, AdapterOperationRequest Operation,
    string OriginalGeneration, AdapterDirectoryGenerationWitness? Directory,
    IReadOnlyList<AdapterDirectoryGenerationWitness> Parents);

/// <summary>A sealed recursive operation in execution order. The last step retires the original root of this subtree.</summary>
public sealed record MirrorPulseOriginalDirectoryPlan(InstanceId InstanceId, Guid AdmissionId,
    IReadOnlyList<MirrorPulseOriginalDirectoryStep> Steps)
{
    internal void Validate()
    {
        if (InstanceId.Value == Guid.Empty || AdmissionId == Guid.Empty || Steps is not { Count: >= 1 and <= 2048 } ||
            Steps[^1]?.Directory is null)
            throw new InvalidDataException("The original directory plan has no valid subtree owner.");

        var operations = new HashSet<Guid>();
        var sources = new Dictionary<AdapterFileAddress, int>();
        var destinations = new HashSet<AdapterFileAddress>();
        var witnesses = new Dictionary<AdapterFileAddress, AdapterDirectoryGenerationWitness>();
        var subtree = Steps[^1].Directory!.Directory;
        for (int ordinal = 0; ordinal < Steps.Count; ordinal++)
        {
            var step = Steps[ordinal];
            if (step?.Operation is null || step.OperationKind is not ("Delete" or "Move") ||
                string.IsNullOrWhiteSpace(step.OriginalGeneration) || step.OriginalGeneration.Length > 4096 ||
                step.OriginalGeneration.Any(char.IsControl) || step.Operation.IsDirectory != (step.Directory is not null) ||
                step.Operation.Preconditions is not { DestinationMustBeAbsent: true, ExpectedRevision: { Length: > 0 } })
                throw new InvalidDataException("An original directory plan step has incomplete generation evidence.");

            var operation = step.Operation;
            _ = AdapterDirectoryEffects.FingerprintRequest(step.OperationKind, InstanceId.Value, AdmissionId, operation, step.Parents);
            var source = new AdapterFileAddress(operation.RootKey, operation.Path);
            if (!operations.Add(operation.OperationId) || !sources.TryAdd(source, ordinal))
                throw new InvalidDataException("The original directory plan reuses an operation or source address.");

            if (step.OperationKind == "Move")
            {
                var destination = new AdapterFileAddress(operation.DestinationRootKey!, operation.DestinationPath!);
                if (destination.Path.Length == 0 || !destinations.Add(destination) ||
                    destination.RootKey == subtree.RootKey && (destination.Path == subtree.Path ||
                        destination.Path.StartsWith(subtree.Path + "/", StringComparison.Ordinal)))
                    throw new InvalidDataException("The original directory plan has an invalid move destination.");
            }
            foreach (var witness in step.Parents)
                if (witnesses.TryGetValue(witness.Directory, out var existing) && existing != witness)
                    throw new InvalidDataException("The original directory plan changes a parent witness.");
                else
                    witnesses[witness.Directory] = witness;

            if (step.Directory is { } directory)
            {
                directory.Validate();
                var parent = step.Parents.Single(value => value.Directory == ParentOf(operation));
                if (directory.InstanceId != InstanceId.Value || directory.AdmissionId != AdmissionId ||
                    directory.Directory != source || directory.Generation != step.OriginalGeneration ||
                    directory.ParentGeneration != parent.Generation || directory.OriginalRevision != operation.Preconditions.ExpectedRevision)
                    throw new InvalidDataException("The original directory plan changed the admitted directory.");
            }
        }

        for (int ordinal = 0; ordinal < Steps.Count - 1; ordinal++)
        {
            var step = Steps[ordinal];
            var parent = ParentOf(step.Operation);
            if (!sources.TryGetValue(parent, out int parentOrdinal) || parentOrdinal <= ordinal ||
                Steps[parentOrdinal].Directory is not { } expected || witnesses[parent] != expected)
                throw new InvalidDataException("The original directory plan must retire every child before its original parent.");
        }
    }

    internal static AdapterFileAddress ParentOf(AdapterOperationRequest operation)
    {
        int separator = operation.Path.LastIndexOf('/');
        return new(operation.RootKey, separator < 0 ? string.Empty : operation.Path[..separator]);
    }
}

/// <summary>A leaf operation bound to the immutable plan and the current owned parent transitions.</summary>
public sealed record MirrorPulseOriginalDirectoryLeaf(MirrorPulseOriginalDirectoryStep Step, string ManifestFingerprint,
    string RequestFingerprint, IReadOnlyList<MirrorPulseOriginalDirectoryPredecessor> ParentPredecessors,
    AdapterOwnedChildEffectReceipt? Receipt);

public sealed record MirrorPulseOriginalDirectoryPredecessor(AdapterDirectoryGenerationWitness Parent,
    AdapterOwnedChildEffectReceipt Receipt);
