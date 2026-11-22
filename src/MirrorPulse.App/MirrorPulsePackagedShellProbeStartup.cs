namespace MirrorPulse.App;

/// <summary>Observes the probe build's original generated startup without replacing it.</summary>
internal static class MirrorPulsePackagedShellProbeStartup
{
    internal static void Run(Action generatedStartup)
    {
        ArgumentNullException.ThrowIfNull(generatedStartup);
        MirrorPulsePackagedShellProbeProgress.RecordFromEnvironment(MirrorPulsePackagedShellProbeStage.ProcessEntering);
        MirrorPulsePackagedShellProbeProgress.RecordFromEnvironment(MirrorPulsePackagedShellProbeStage.GeneratedStartupStarting);
        generatedStartup();
        MirrorPulsePackagedShellProbeProgress.RecordFromEnvironment(MirrorPulsePackagedShellProbeStage.GeneratedStartupReturned);
    }
}
