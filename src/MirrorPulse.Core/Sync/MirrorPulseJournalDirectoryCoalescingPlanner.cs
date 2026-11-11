using System.Security.Cryptography;
using System.Text.Json;

namespace MirrorPulse.Core.Sync;

/// <summary>Retains one original directory chain from a complete observation window.</summary>
/// <remarks>Dependency-free planning is not proof of native emptiness, identity, backend acceptance or permission to acknowledge an original.</remarks>
public static class MirrorPulseJournalDirectoryCoalescingPlanner
{
    public static MirrorPulseJournalCoalescingPlan? TryPlan(IReadOnlyList<MirrorPulseWorkerChangeCommand> window,
        Guid itemId, long windowUpperSequence, IReadOnlySet<Guid> committedOperations)
    {
        ArgumentNullException.ThrowIfNull(window); ArgumentNullException.ThrowIfNull(committedOperations);
        if (itemId == Guid.Empty || windowUpperSequence <= 0 || window.Any(command => command.OperationId == Guid.Empty ||
            command.Sequence <= 0 || command.Sequence > windowUpperSequence) ||
            window.Select(command => command.OperationId).Distinct().Count() != window.Count ||
            window.Select(command => command.Sequence).Distinct().Count() != window.Count)
            throw new ArgumentException("The complete directory observation window is invalid.", nameof(window));
        var chain = window.Where(command => command.ItemId == itemId).OrderBy(command => command.Sequence).ToArray();
        if (chain.Length < 2 || chain.Any(command => !command.IsDirectory || committedOperations.Contains(command.OperationId) ||
            command.Kind is not (MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.Move or MirrorPulseWorkerChangeKind.Delete))) return null;
        var first = chain[0];
        if (chain.Any(command => command.InstanceId != first.InstanceId || command.RootKey != first.RootKey ||
            command.PreviousInstanceId is { } previous && previous != first.InstanceId ||
            command.PreviousRootKey is not null && command.PreviousRootKey != first.RootKey)) return null;
        if (first.Kind is not (MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.Move) ||
            first.Kind == MirrorPulseWorkerChangeKind.Move && first.PreviousRelativePath is null) return null;
        string original = first.Kind == MirrorPulseWorkerChangeKind.Move ? first.PreviousRelativePath! : first.RelativePath;
        string current = original; bool created = first.Kind == MirrorPulseWorkerChangeKind.Create, deleted = false;
        foreach (var command in chain)
        {
            if (deleted) return null;
            switch (command.Kind)
            {
                case MirrorPulseWorkerChangeKind.Create:
                    if (command != first || !Same(command.RelativePath, current)) return null;
                    break;
                case MirrorPulseWorkerChangeKind.Move:
                    if (command.PreviousRelativePath is null || !Same(command.PreviousRelativePath, current)) return null;
                    current = command.RelativePath;
                    break;
                case MirrorPulseWorkerChangeKind.Delete:
                    if (!Same(command.RelativePath, current)) return null;
                    deleted = true;
                    break;
                default: return null;
            }
        }
        // A complete child chain, even if it later disappears, remains a dependency.
        // Recursive execution must supply its own sealed parent/child witnesses.
        var affected = chain.DistinctBy(command => (command.RelativePath.Replace('\\', '/'), command.PreviousRelativePath?.Replace('\\', '/'))).ToArray();
        if (window.Any(other => other.ItemId != itemId && affected.Any(member =>
            MirrorPulseJournalOrderPolicy.DependsOn(other, member) || MirrorPulseJournalOrderPolicy.DependsOn(member, other)))) return null;
        bool moved = !string.Equals(original.Replace('\\', '/'), current.Replace('\\', '/'), StringComparison.Ordinal);
        var effect = deleted ? created ? MirrorPulseCoalescedEffect.VerifyDirectoryAbsence : MirrorPulseCoalescedEffect.DeleteDirectory :
            created ? MirrorPulseCoalescedEffect.CreateDirectory : moved ? MirrorPulseCoalescedEffect.MoveDirectory : MirrorPulseCoalescedEffect.VerifyDirectoryUnchanged;
        var members = chain.Select(command => new MirrorPulseJournalOperationReference(command.OperationId, command.Sequence,
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command))))).ToArray();
        byte[] identity = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            first.InstanceId,
            first.RootKey,
            ItemId = itemId,
            WindowUpperSequence = windowUpperSequence,
            OriginalPath = original,
            FinalPath = current,
            Effect = effect,
            Members = members
        }));
        return new(new Guid(identity.AsSpan(0, 16)), first.InstanceId, first.RootKey, itemId, windowUpperSequence,
            original, current, effect, Array.AsReadOnly(members));
    }

    private static bool Same(string left, string right) => string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
