using System.Globalization;
using MirrorPulse.Control.Contracts;

namespace MirrorPulse.Cli;

internal static class MirrorPulseRemoteRetirementCliArguments
{
    internal static NamespaceRemoteRetirementsArguments ParsePage(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 1 || !Guid.TryParse(arguments[0], out Guid root) || root == Guid.Empty)
            throw new ArgumentException("namespace remote retirements requires a non-empty root ID.");
        Guid? after = null; int limit = 64; bool limitSpecified = false;
        for (int index = 1; index < arguments.Count; index += 2)
        {
            if (index + 1 >= arguments.Count) throw new ArgumentException("Every remote retirement page option requires one value.");
            string option = arguments[index], value = arguments[index + 1];
            if (option.Equals("--after-operation-id", StringComparison.OrdinalIgnoreCase) && after is null &&
                Guid.TryParse(value, out Guid operation) && operation != Guid.Empty) after = operation;
            else if (option.Equals("--limit", StringComparison.OrdinalIgnoreCase) && !limitSpecified &&
                int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int requested) && requested is >= 1 and <= 64)
            { limit = requested; limitSpecified = true; }
            else throw new ArgumentException("Remote retirement paging accepts one --after-operation-id and one --limit between 1 and 64.");
        }
        return new(root, after, limit);
    }

    internal static string FormatStatus(MirrorPulseControlRemoteRetirementStatus status)
    {
        string deletion = status.RootNativeDeleted is { } deleted ? deleted ? "yes" : "no" : "unknown";
        return $"Remote directory {status.Stage} · {status.OperationId:D} · {status.SettledMembers}/{status.MemberCount} settled · root deleted: {deletion}" +
            (status.ConflictId is { } conflict ? $" · conflict {conflict:D}" : string.Empty);
    }
}
