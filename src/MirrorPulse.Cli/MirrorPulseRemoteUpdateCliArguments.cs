using MirrorPulse.Control.Contracts;

namespace MirrorPulse.Cli;

internal static class MirrorPulseRemoteUpdateCliArguments
{
    internal static Guid ParseId(IReadOnlyList<string> arguments, string kind)
    {
        if (arguments.Count != 1 || !Guid.TryParse(arguments[0], out Guid id) || id == Guid.Empty)
            throw new ArgumentException($"Remote update commands require one non-empty {kind} ID.");
        return id;
    }

    internal static string Format(MirrorPulseControlRemoteUpdateStatus value) =>
        $"Remote update {value.Stage} · {value.OperationId:D} · product outcome: {value.Outcome ?? "unknown"} · " +
        $"evidence: {value.EvidenceSource ?? "unavailable"}";
}
