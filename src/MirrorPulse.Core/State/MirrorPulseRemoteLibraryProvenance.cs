namespace MirrorPulse.Core.State;

/// <summary>Identifies the SDK publications supported by retained product protocol evidence.</summary>
public static class MirrorPulseRemoteLibraryProvenance
{
    public const string CurrentPackageVersion = "0.1.0-preview.16";
    public const string CurrentSourceCommit = "e0d5d32a7c24b2812d16315897524afd4b54e050";

    /// <summary>Publications with the durable public original-entry tombstone rejection API.</summary>
    public static bool SupportsDeleteReconciliation(string packageVersion, string sourceCommit) =>
        (packageVersion, sourceCommit) is (CurrentPackageVersion, CurrentSourceCommit) or
            ("0.1.0-preview.15", "6d34178a84949d2eae2f1d5dafa85b3620619589") or
            ("0.1.0-preview.14", "663c3dd0bbc8e6785fb2a9817f5ae0738499cf3f") or
            ("0.1.0-preview.13", "825f9ebd90eff9db1686744e602fe4c6d653eeaa") or
            ("0.1.0-preview.12", "cfbc7f83b26b5b6515710a8c490428d2989d8c06");

    /// <summary>Publications qualified to return a non-mutating conflict for a preserving missing-item Delete.</summary>
    public static bool SupportsPreservingDeleteRejection(string packageVersion, string sourceCommit) =>
        (packageVersion, sourceCommit) is (CurrentPackageVersion, CurrentSourceCommit) or
            ("0.1.0-preview.15", "6d34178a84949d2eae2f1d5dafa85b3620619589") or
            ("0.1.0-preview.14", "663c3dd0bbc8e6785fb2a9817f5ae0738499cf3f") or
            ("0.1.0-preview.13", "825f9ebd90eff9db1686744e602fe4c6d653eeaa") or
            ("0.1.0-preview.12", "cfbc7f83b26b5b6515710a8c490428d2989d8c06") or
            ("0.1.0-preview.11", "c28895203e20d588aa3d0eb1bb4c0e55c25896fb");

    /// <summary>Checks original publication identity without rewriting its evidence or authorizing native work.</summary>
    public static bool IsSupported(string packageVersion, string sourceCommit) =>
        (packageVersion, sourceCommit) is
            (CurrentPackageVersion, CurrentSourceCommit) or
            ("0.1.0-preview.15", "6d34178a84949d2eae2f1d5dafa85b3620619589") or
            ("0.1.0-preview.14", "663c3dd0bbc8e6785fb2a9817f5ae0738499cf3f") or
            ("0.1.0-preview.13", "825f9ebd90eff9db1686744e602fe4c6d653eeaa") or
            ("0.1.0-preview.12", "cfbc7f83b26b5b6515710a8c490428d2989d8c06") or
            ("0.1.0-preview.11", "c28895203e20d588aa3d0eb1bb4c0e55c25896fb") or
            ("0.1.0-preview.10", "cfd98515dcc03ac5767a6820aed046a178410a53") or
            ("0.1.0-preview.9", "a252ea88439475986242691797bc3c1453d30118") or
            ("0.1.0-preview.8", "ee593578dba3c666cf59f4a922f0e220f115bf7f") or
            ("0.1.0-preview.7", "137d6e2a9c1b15f337e8e7ca3566d5e3f29fcdb9") or
            ("0.1.0-preview.6", "3caa382e341d260543d3afc05e25fa034fb32e16");
}
