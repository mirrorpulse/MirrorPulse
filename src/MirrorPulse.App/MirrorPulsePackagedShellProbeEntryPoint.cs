#if MIRRORPULSE_SHELL_PROBE
using System.Runtime.CompilerServices;

namespace MirrorPulse.App;

internal static class MirrorPulsePackagedShellProbeEntryPoint
{
    [STAThread]
    private static void Main() => MirrorPulsePackagedShellProbeStartup.Run(StartGeneratedApplication);

    // Keep framework startup out of the first managed entry method's JIT boundary.
    // Module initialization can still precede Main; progress does not diagnose that boundary.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void StartGeneratedApplication() => XamlGeneratedProgram.XamlGeneratedMain();
}
#endif
