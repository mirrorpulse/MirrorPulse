using MirrorPulse.Control.Contracts;

namespace MirrorPulse.Cli;

internal static class MirrorPulseTransferCliArguments
{
    internal static Guid ParseId(IReadOnlyList<string> arguments, string kind)
    {
        if (arguments.Count != 1 || !Guid.TryParse(arguments[0], out Guid id) || id == Guid.Empty)
            throw new ArgumentException($"Transfer queries require one non-empty {kind} ID.");
        return id;
    }

    internal static string Format(MirrorPulseControlTransferStatus value) =>
        $"Transfer {value.Stage} · {value.OperationId:D} · completed: {value.Completed} · " +
        $"journal acknowledged: {value.JournalAcknowledged} · roots fenced: {value.RootsFenced}";
}
