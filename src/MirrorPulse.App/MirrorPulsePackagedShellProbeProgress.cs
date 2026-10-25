namespace MirrorPulse.App;

internal enum MirrorPulsePackagedShellProbeStage
{
    AppConstructing, AppInitialized, AppLaunched, ProbeSelected, ProbeEntered,
    PackageValidated, RootCreated, CustomRegistrationStarted, CustomRegistrationCompleted,
    CustomReadbackStarted, CustomReadbackVerified, UnifiedRegistrationStarted,
    UnifiedRegistrationCompleted, UnifiedReadbackStarted, UnifiedReadbackVerified,
    WritingSuccess, SuccessWritten, FailureHandling, MinimalFolderStarted, MinimalFolderReady,
    MinimalRegistrationStarted, MinimalRegistrationCompleted, MinimalReadbackStarted,
    MinimalReadbackCompleted, MinimalUnregisterStarted, MinimalUnregisterCompleted,
    WritingFailure, FailureWritten, UnregisterStarted, UnregisterCompleted,
    DirectoryCleanupStarted, DirectoryCleanupCompleted, Exiting,
    ProcessEntering, GeneratedStartupStarting, GeneratedStartupReturned
}

/// <summary>Optional fixed-stage progress for the disposable installed Shell probe.</summary>
internal static class MirrorPulsePackagedShellProbeProgress
{
    internal static void RecordFromEnvironment(MirrorPulsePackagedShellProbeStage stage)
    {
        string? resultPath = Environment.GetEnvironmentVariable("MIRRORPULSE_PACKAGED_SHELL_PROBE_RESULT");
        if (!string.IsNullOrWhiteSpace(resultPath)) Record(resultPath, stage);
    }

    internal static void Record(string resultPath, MirrorPulsePackagedShellProbeStage stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultPath);
        if (!Enum.IsDefined(stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        try { File.WriteAllText(resultPath + ".progress", "1:" + stage); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Diagnostic progress cannot replace the actual registration result or failure.
        }
    }
}
