using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Sync;

/// <summary>Preserves official journal order for objects and their affected directory subtrees.</summary>
public static class MirrorPulseJournalOrderPolicy
{
    public static bool DependsOn(MirrorPulseWorkerChangeCommand later, MirrorPulseWorkerChangeCommand earlier)
    {
        ArgumentNullException.ThrowIfNull(later);
        ArgumentNullException.ThrowIfNull(earlier);
        foreach ((InstanceId instance, string root, string path) in Paths(later))
            foreach ((InstanceId previousInstance, string previousRoot, string previousPath) in Paths(earlier))
            {
                if (instance != previousInstance || root != previousRoot) continue;
                if (string.Equals(path, previousPath, StringComparison.OrdinalIgnoreCase) ||
                    earlier.IsDirectory && IsChild(path, previousPath) || later.IsDirectory && IsChild(previousPath, path))
                    return true;
            }
        return false;
    }

    internal static IEnumerable<(InstanceId Instance, string Root, string Path)> Paths(MirrorPulseWorkerChangeCommand command)
    {
        yield return (command.InstanceId, command.RootKey, command.RelativePath.Replace('\\', '/').TrimEnd('/'));
        if (command.PreviousRelativePath is not null)
            yield return (command.PreviousInstanceId ?? command.InstanceId, command.PreviousRootKey ?? command.RootKey,
                command.PreviousRelativePath.Replace('\\', '/').TrimEnd('/'));
    }

    private static bool IsChild(string path, string parent) =>
        parent.Length == 0 ? path.Length != 0 : path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
}
