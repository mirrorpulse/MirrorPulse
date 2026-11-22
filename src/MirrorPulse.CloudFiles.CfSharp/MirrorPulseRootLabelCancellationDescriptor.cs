using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

internal static class MirrorPulseRootLabelCancellationDescriptor
{
    internal static bool MatchesVerified(string requested, string verified, string observed)
        => MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(verified, observed) &&
            MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(requested, observed);
}
