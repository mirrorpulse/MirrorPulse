using MirrorPulse.Control.Contracts;

namespace MirrorPulse.Cli;

internal static class MirrorPulseTransferCliArguments
{
    internal static (Guid OperationId, Guid SourceMemberId, Guid TargetParentMemberId, string TargetName)
        ParsePlacement(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 4 || !Guid.TryParse(arguments[0], out Guid operation) || operation == Guid.Empty ||
            !Guid.TryParse(arguments[1], out Guid source) || source == Guid.Empty ||
            !Guid.TryParse(arguments[2], out Guid target) || target == Guid.Empty || string.IsNullOrWhiteSpace(arguments[3]) ||
            arguments[3].IndexOfAny(['/', '\\']) >= 0 || arguments[3] is "." or "..")
            throw new ArgumentException("Transfer prepare and move require operation, source member, target parent IDs and one direct child name.");
        return (operation, source, target, arguments[3]);
    }

    internal static Guid ParseId(IReadOnlyList<string> arguments, string kind)
    {
        if (arguments.Count != 1 || !Guid.TryParse(arguments[0], out Guid id) || id == Guid.Empty)
            throw new ArgumentException($"Transfer commands require one non-empty {kind} ID.");
        return id;
    }

    internal static string Format(MirrorPulseControlTransferStatus value) =>
        $"Transfer {value.Stage} · {value.OperationId:D} · completed: {value.Completed} · " +
        $"journal acknowledged: {value.JournalAcknowledged} · roots fenced: {value.RootsFenced}" +
        (value.PendingTargetBinding is { } pending ? $" · target binding pending: {pending.BirthOperationId:D}" : string.Empty);
}
