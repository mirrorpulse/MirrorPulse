using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MirrorPulse.CfSharp.CrashProbe;

/// <summary>Exposes Cloud Files metadata to an independent native fixture observer.</summary>
/// <remarks>Windows can disguise placeholder attributes in processes outside the provider.</remarks>
[SupportedOSPlatform("windows10.0.17134")]
internal sealed partial class CliFixturePlaceholderVisibility : IDisposable
{
    private readonly sbyte _previous;
    private bool _disposed;

    internal CliFixturePlaceholderVisibility()
    {
        _previous = RtlSetProcessPlaceholderCompatibilityMode(2);
        if (_previous < 0)
            throw new IOException($"Placeholder visibility could not be selected ({_previous}).");
        if (CurrentMode != 2)
        {
            _ = RtlSetProcessPlaceholderCompatibilityMode(_previous);
            throw new IOException("The native observer could not verify exposed placeholder metadata.");
        }
    }

    internal sbyte PreviousMode => _previous;
    internal static sbyte CurrentMode => RtlQueryProcessPlaceholderCompatibilityMode();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        sbyte result = RtlSetProcessPlaceholderCompatibilityMode(_previous);
        if (result < 0)
            throw new IOException($"The native observer could not restore placeholder visibility ({result}).");
    }

    [LibraryImport("ntdll.dll")]
    private static partial sbyte RtlSetProcessPlaceholderCompatibilityMode(sbyte mode);

    [LibraryImport("ntdll.dll")]
    private static partial sbyte RtlQueryProcessPlaceholderCompatibilityMode();
}
