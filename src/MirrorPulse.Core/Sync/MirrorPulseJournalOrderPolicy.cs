namespace MirrorPulse.Core.Sync;

/// <summary>Preserves official journal order for objects and their affected directory subtrees.</summary>
public static class MirrorPulseJournalOrderPolicy
{
    public static bool DependsOn(MirrorPulseWorkerChangeCommand later, MirrorPulseWorkerChangeCommand earlier)
    {
        ArgumentNullException.ThrowIfNull(later);
        ArgumentNullException.ThrowIfNull(earlier);
        if (later.InstanceId != earlier.InstanceId) return false;
        foreach ((string root, string path) in Paths(later))
            foreach ((string previousRoot, string previousPath) in Paths(earlier))
            {
                if (root != previousRoot) continue;
                if (string.Equals(path, previousPath, StringComparison.OrdinalIgnoreCase) ||
                    earlier.IsDirectory && IsChild(path, previousPath) || later.IsDirectory && IsChild(previousPath, path))
                    return true;
            }
        return false;
    }

    private static IEnumerable<(string Root, string Path)> Paths(MirrorPulseWorkerChangeCommand command)
    {
        yield return (command.RootKey, command.RelativePath.Replace('\\', '/').TrimEnd('/'));
        if (command.PreviousRelativePath is not null)
            yield return (command.PreviousRootKey ?? command.RootKey, command.PreviousRelativePath.Replace('\\', '/').TrimEnd('/'));
    }

    private static bool IsChild(string path, string parent) =>
        path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
}
