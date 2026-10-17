namespace MirrorPulse.Core.Conflicts;

/// <summary>The exact Worker object and version selected before preserving remote conflict bytes.</summary>
public sealed record MirrorPulseConflictRemoteSource(int Version, string InstanceId, string RootKey,
    string RelativePath, string RemoteId, string RemoteRevision, long Length)
{
    public void Validate(MirrorPulseConflictRecord conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        if (Version != 1 || InstanceId != conflict.InstanceId.ToString() || string.IsNullOrWhiteSpace(RootKey) ||
            string.IsNullOrWhiteSpace(RemoteId) || string.IsNullOrWhiteSpace(RemoteRevision) ||
            RemoteRevision != conflict.RemoteRevision || Length < 0)
            throw new InvalidDataException("The selected remote version does not belong to this conflict.");
        ValidatePath(RelativePath);
    }

    internal static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains(':') || path.Contains('\0') ||
            path.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("A remote conflict source requires a canonical relative Worker path.");
    }
}
