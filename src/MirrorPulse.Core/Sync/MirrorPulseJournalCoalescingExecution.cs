using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Sync;

public enum MirrorPulseCoalescingStepKind { VerifyAbsence, VerifyUnchanged, Upload, Move, Delete }

/// <summary>The final bytes and historical object captured before any coalesced remote effect.</summary>
public sealed record MirrorPulseCoalescingContent(long Length, string Sha256, MirrorPulseUploadBinding UploadBinding);

/// <summary>A stable remote-effect identity, distinct from every original journal acknowledgement ID.</summary>
public sealed record MirrorPulseCoalescingStep(Guid OperationId, int Ordinal, MirrorPulseCoalescingStepKind Kind,
    string RelativePath, string? PreviousRelativePath = null);

/// <summary>A fingerprint of immutable, proven never-started intent transferred to the original plan.</summary>
public sealed record MirrorPulseCoalescingOriginalMutation(Guid OperationId, string IntentFingerprint);

/// <summary>Immutable execution preparation. It does not establish remote acceptance or acknowledge the journal.</summary>
public sealed record MirrorPulseJournalCoalescingExecution(int Version, Guid PlanId, string PlanFingerprint,
    string? ExpectedRevision, MirrorPulseCoalescingContent? Content, IReadOnlyList<MirrorPulseCoalescingStep> Steps,
    IReadOnlyList<MirrorPulseCoalescingOriginalMutation> OriginalMutations);

public static class MirrorPulseJournalCoalescingExecutionPlanner
{
    /// <summary>Validates the complete observation window before binding final content and remote-effect identities.</summary>
    public static MirrorPulseJournalCoalescingExecution Create(MirrorPulseJournalCoalescingPlan plan,
        IReadOnlyList<MirrorPulseWorkerChangeCommand> completeWindow, string? expectedRevision,
        MirrorPulseCoalescingContent? content = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.Members);
        ArgumentNullException.ThrowIfNull(completeWindow);
        plan = plan with { Members = Array.AsReadOnly(plan.Members.ToArray()) };
        completeWindow = Array.AsReadOnly(completeWindow.ToArray());
        MirrorPulseJournalCoalescingPlan? expected = MirrorPulseJournalCoalescingPlanner.TryPlan(
            completeWindow, plan.ItemId, plan.WindowUpperSequence, new HashSet<Guid>());
        if (expected is null || !JsonSerializer.SerializeToUtf8Bytes(plan).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(expected)))
            throw new InvalidDataException("Execution requires the unchanged complete original observation window.");
        MirrorPulseJournalCoalescingExecution execution = FromPlan(plan, expectedRevision, content);
        if (execution.Steps.Any(step => completeWindow.Any(command => command.OperationId == step.OperationId)))
            throw new InvalidDataException("A remote-effect identity cannot replace an original journal identity.");
        return execution;
    }

    internal static MirrorPulseJournalCoalescingExecution FromPlan(MirrorPulseJournalCoalescingPlan plan,
        string? expectedRevision, MirrorPulseCoalescingContent? content)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.PlanId == Guid.Empty || !Enum.IsDefined(plan.Effect))
            throw new InvalidDataException("The retained coalescing decision is invalid.");
        ValidatePath(plan.OriginalPath);
        ValidatePath(plan.FinalPath);
        bool created = plan.Effect is MirrorPulseCoalescedEffect.CreateFile or MirrorPulseCoalescedEffect.VerifyRemoteAbsence;
        if (created ? expectedRevision is not null : string.IsNullOrEmpty(expectedRevision))
            throw new ArgumentException("The coalesced effect requires an explicit unchanged remote baseline.", nameof(expectedRevision));
        bool uploads = plan.Effect is MirrorPulseCoalescedEffect.CreateFile or MirrorPulseCoalescedEffect.UpdateFile or MirrorPulseCoalescedEffect.MoveAndUpdateFile;
        if (uploads != (content is not null))
            throw new ArgumentException("Only an upload effect requires final local content proof.", nameof(content));
        if (content is not null)
        {
            if (content.Length < 0 || content.Sha256 is not { Length: 64 } || !content.Sha256.All(Uri.IsHexDigit) || content.UploadBinding is null)
                throw new ArgumentException("The final coalesced content proof is incomplete.", nameof(content));
            content.UploadBinding.Validate();
        }
        var steps = new List<MirrorPulseCoalescingStep>(2);
        void Add(MirrorPulseCoalescingStepKind kind, string path, string? previous = null)
        {
            int ordinal = steps.Count;
            byte[] identity = SHA256.HashData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"MirrorPulse.Coalescing.v1/{plan.PlanId:D}/{ordinal}")));
            steps.Add(new(new Guid(identity.AsSpan(0, 16)), ordinal, kind, path, previous));
        }
        switch (plan.Effect)
        {
            case MirrorPulseCoalescedEffect.VerifyRemoteAbsence:
                Add(MirrorPulseCoalescingStepKind.VerifyAbsence, plan.OriginalPath);
                if (!string.Equals(plan.OriginalPath, plan.FinalPath, StringComparison.Ordinal))
                    Add(MirrorPulseCoalescingStepKind.VerifyAbsence, plan.FinalPath);
                break;
            case MirrorPulseCoalescedEffect.VerifyRemoteUnchanged:
                Add(MirrorPulseCoalescingStepKind.VerifyUnchanged, plan.OriginalPath);
                break;
            case MirrorPulseCoalescedEffect.CreateFile:
            case MirrorPulseCoalescedEffect.UpdateFile:
                Add(MirrorPulseCoalescingStepKind.Upload, plan.FinalPath);
                break;
            case MirrorPulseCoalescedEffect.MoveFile:
            case MirrorPulseCoalescedEffect.MoveAndUpdateFile:
                Add(MirrorPulseCoalescingStepKind.Move, plan.FinalPath, plan.OriginalPath);
                if (plan.Effect == MirrorPulseCoalescedEffect.MoveAndUpdateFile)
                    Add(MirrorPulseCoalescingStepKind.Upload, plan.FinalPath);
                break;
            case MirrorPulseCoalescedEffect.DeleteFile:
                // Intermediate local names were never sent. Delete the original remote name.
                Add(MirrorPulseCoalescingStepKind.Delete, plan.OriginalPath);
                break;
            default: throw new InvalidDataException("The retained coalesced effect is unsupported.");
        }
        if (steps.Any(step => step.OperationId == Guid.Empty || step.OperationId == plan.PlanId ||
            plan.Members.Any(member => member.OperationId == step.OperationId)) || steps.Select(step => step.OperationId).Distinct().Count() != steps.Count)
            throw new InvalidDataException("The coalesced remote-effect identities collide with retained ownership.");
        return new(1, plan.PlanId, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan))),
            expectedRevision, content, steps.AsReadOnly(), Array.Empty<MirrorPulseCoalescingOriginalMutation>());
    }

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_767 || path.Contains(':') ||
            path.Replace('\\', '/').Split('/').Any(part => part.Length is 0 or > 255 || part is "." or ".."))
            throw new InvalidDataException("A coalesced remote effect requires a bounded relative file path.");
    }
}
