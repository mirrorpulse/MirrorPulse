using System.Globalization;
using MirrorPulse.Control.Contracts;

namespace MirrorPulse.Cli;

internal static class MirrorPulseRemoteDeleteCliArguments
{
    internal static Guid ParseCommand(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 1 || !Guid.TryParse(arguments[0], out Guid command) || command == Guid.Empty)
            throw new ArgumentException("Explicit Delete status and recovery require one non-empty command ID.");
        return command;
    }

    internal static NamespaceRemoteDeletesArguments ParsePage(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 1 || !Guid.TryParse(arguments[0], out Guid root) || root == Guid.Empty)
            throw new ArgumentException("namespace remote deletes requires a non-empty root ID.");
        Guid? after = null; int limit = 64; bool limitSpecified = false;
        for (int index = 1; index < arguments.Count; index += 2)
        {
            if (index + 1 >= arguments.Count) throw new ArgumentException("Each Delete page option requires one value.");
            string option = arguments[index], value = arguments[index + 1];
            if (option.Equals("--after-command-id", StringComparison.OrdinalIgnoreCase) && after is null &&
                Guid.TryParse(value, out Guid command) && command != Guid.Empty) after = command;
            else if (option.Equals("--limit", StringComparison.OrdinalIgnoreCase) && !limitSpecified &&
                int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int requested) && requested is >= 1 and <= 64)
            { limit = requested; limitSpecified = true; }
            else throw new ArgumentException("Delete paging accepts one --after-command-id and one --limit between1 and64.");
        }
        return new(root, after, limit);
    }

    internal static string Format(MirrorPulseControlRemoteDeleteStatus value) =>
        $"Explicit Delete {value.Stage} · {value.CommandId:D} · {value.SettledMembers}/{value.MemberCount} settled · " +
        $"SDK outcome: {value.Outcome ?? "unknown"} · command complete: {(value.CommandCompleted ? "yes" : "no")}";
}
