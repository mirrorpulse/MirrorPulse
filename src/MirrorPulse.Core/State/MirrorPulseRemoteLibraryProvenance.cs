namespace MirrorPulse.Core.State;

/// <summary>Identifies the SDK publications supported by retained product protocol evidence.</summary>
public static class MirrorPulseRemoteLibraryProvenance
{
    public const string CurrentPackageVersion = "0.1.0-preview.7";
    public const string CurrentSourceCommit = "137d6e2a9c1b15f337e8e7ca3566d5e3f29fcdb9";

    /// <summary>Checks original publication identity without rewriting its evidence or authorizing native work.</summary>
    public static bool IsSupported(string packageVersion, string sourceCommit) =>
        (packageVersion, sourceCommit) is
            (CurrentPackageVersion, CurrentSourceCommit) or
            ("0.1.0-preview.6", "3caa382e341d260543d3afc05e25fa034fb32e16");
}
