using System.Runtime.Versioning;
using CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Protects MP-owned entry directories before native namespace mutation.</summary>
[SupportedOSPlatform("windows10.0.16299")]
public static class MirrorPulseRootNamespacePolicy
{
    public static CloudProviderPolicyDecision ApproveDelete(MirrorPulseRootRouter router, string normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(normalizedPath);
        try
        {
            if (router.IsSyncRoot(normalizedPath)) return CloudProviderPolicyDecision.Deny;
            return router.ResolvePath(normalizedPath).RelativePath.Length == 0
                ? CloudProviderPolicyDecision.Deny : CloudProviderPolicyDecision.Allow;
        }
        catch (FileNotFoundException)
        {
            // Scope was verified above. Unregistered local paths are not
            // managed entry directories, and their deletion is never a source
            // mutation authorized by this callback.
            return CloudProviderPolicyDecision.Allow;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            return CloudProviderPolicyDecision.Deny;
        }
    }
}
