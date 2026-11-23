using System.Security.Principal;
using MirrorPulse.Core;

namespace MirrorPulse.Control.Transport;

/// <summary>
/// Names for versioned current-user control channels.
/// </summary>
public static class MirrorPulseControlPipeNames
{
    public static string CurrentUserV1()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value
            ?? throw new InvalidOperationException("The current user has no security identifier.");
        return $"{ProductInfo.Edition.IdentityPrefix}-control-v1-{sid}";
    }
}
