using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Sync;

public enum MirrorPulseCoalescedEffect { VerifyRemoteAbsence, CreateFile, UpdateFile, MoveFile, MoveAndUpdateFile, DeleteFile, VerifyRemoteUnchanged }

/// <summary>References an official journal observation without copying its private payload.</summary>
public sealed record MirrorPulseJournalOperationReference(Guid OperationId, long Sequence, string Fingerprint);

/// <summary>An immutable decision for one unsent file chain within a complete observation window.</summary>
public sealed record MirrorPulseJournalCoalescingPlan(Guid PlanId, InstanceId InstanceId, string RootKey,
    Guid ItemId, long WindowUpperSequence, string OriginalPath, string FinalPath, MirrorPulseCoalescedEffect Effect,
    IReadOnlyList<MirrorPulseJournalOperationReference> Members);

public static class MirrorPulseJournalCoalescingPlanner
{
    /// <summary>The caller must supply the complete journal window, including other objects' operations.</summary>
    public static MirrorPulseJournalCoalescingPlan? TryPlan(IReadOnlyList<MirrorPulseWorkerChangeCommand> window,
        Guid itemId, long windowUpperSequence, IReadOnlySet<Guid> committedOperations)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(committedOperations);
        if (itemId == Guid.Empty || windowUpperSequence <= 0 || window.Any(command => command.OperationId == Guid.Empty || command.Sequence <= 0 || command.Sequence > windowUpperSequence) ||
            window.Select(command => command.OperationId).Distinct().Count() != window.Count ||
            window.Select(command => command.Sequence).Distinct().Count() != window.Count)
            throw new ArgumentException("The complete observation window is invalid.", nameof(window));
        MirrorPulseWorkerChangeCommand[] chain = window.Where(command => command.ItemId == itemId).OrderBy(command => command.Sequence).ToArray();
        if (chain.Length < 2 || chain.Any(command => command.IsDirectory || committedOperations.Contains(command.OperationId) ||
            command.Kind == MirrorPulseWorkerChangeKind.MetadataUpdate)) return null;
        MirrorPulseWorkerChangeCommand first = chain[0];
        if (chain.Any(command => command.InstanceId != first.InstanceId || command.RootKey != first.RootKey ||
            command.PreviousRootKey is not null && command.PreviousRootKey != first.RootKey)) return null;
        if (first.Kind == MirrorPulseWorkerChangeKind.Move && first.PreviousRelativePath is null) return null;
        string original = first.Kind == MirrorPulseWorkerChangeKind.Move ? first.PreviousRelativePath! : first.RelativePath;
        string current = original;
        bool created = first.Kind == MirrorPulseWorkerChangeKind.Create;
        bool deleted = false;
        bool content = false;
        foreach (MirrorPulseWorkerChangeCommand command in chain)
        {
            if (deleted) return null; // Re-creation or replacement needs a new ownership decision.
            switch (command.Kind)
            {
                case MirrorPulseWorkerChangeKind.Move:
                    if (command.PreviousRelativePath is null || !SamePath(command.PreviousRelativePath, current)) return null;
                    current = command.RelativePath;
                    break;
                case MirrorPulseWorkerChangeKind.Create:
                    if (command != first || !SamePath(command.RelativePath, current)) return null;
                    content = true;
                    break;
                case MirrorPulseWorkerChangeKind.ContentUpdate:
                    if (!SamePath(command.RelativePath, current)) return null;
                    content = true;
                    break;
                case MirrorPulseWorkerChangeKind.Delete:
                    if (!SamePath(command.RelativePath, current)) return null;
                    deleted = true;
                    break;
                default: return null;
            }
        }
        // A colliding object or parent mutation is not evidence that this file's chain is independent.
        // Repeated content observations at one name retain all acknowledgement IDs,
        // but only one representative path pair is needed for dependency checks.
        MirrorPulseWorkerChangeCommand[] affectedNames = chain.DistinctBy(command =>
            (command.RelativePath.Replace('\\', '/'), command.PreviousRelativePath?.Replace('\\', '/'))).ToArray();
        if (window.Any(other => other.ItemId != itemId && affectedNames.Any(member =>
            MirrorPulseJournalOrderPolicy.DependsOn(other, member) || MirrorPulseJournalOrderPolicy.DependsOn(member, other)))) return null;
        bool moved = !string.Equals(original.Replace('\\', '/'), current.Replace('\\', '/'), StringComparison.Ordinal);
        MirrorPulseCoalescedEffect effect = deleted ? (created ? MirrorPulseCoalescedEffect.VerifyRemoteAbsence : MirrorPulseCoalescedEffect.DeleteFile)
            : created ? MirrorPulseCoalescedEffect.CreateFile
            : moved ? (content ? MirrorPulseCoalescedEffect.MoveAndUpdateFile : MirrorPulseCoalescedEffect.MoveFile)
            : content ? MirrorPulseCoalescedEffect.UpdateFile : MirrorPulseCoalescedEffect.VerifyRemoteUnchanged;
        MirrorPulseJournalOperationReference[] members = chain.Select(command => new MirrorPulseJournalOperationReference(
            command.OperationId, command.Sequence, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command))))).ToArray();
        byte[] identity = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { first.InstanceId, first.RootKey, ItemId = itemId, WindowUpperSequence = windowUpperSequence, OriginalPath = original, FinalPath = current, Effect = effect, Members = members }));
        return new(new Guid(identity.AsSpan(0, 16)), first.InstanceId, first.RootKey, itemId, windowUpperSequence,
            original, current, effect, Array.AsReadOnly(members));
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
}
