using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Matches a native callback to one current witnessed root rename without changing its progress.</summary>
/// <remarks>Approval does not verify today's native binding, move an object, settle membership or ACK a journal.
/// The Host's protected execution and original public SDK reconciliation remain separate requirements.</remarks>
[SupportedOSPlatform("windows10.0.16299")]
internal sealed class MirrorPulseManagedRootRenameApproval(MirrorPulseRootRouter router, MirrorPulseProductCatalog catalog)
{
    internal async ValueTask<CloudProviderPolicyDecision> EvaluateAsync(string sourcePath, ReadOnlyMemory<byte> identity,
        string targetPath, bool isDirectory, bool sourceInScope, bool targetInScope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!isDirectory || !sourceInScope || !targetInScope || identity.IsEmpty ||
            identity.Length > SyncRootRegistrationOptions.MaxFileIdentityLength)
            return CloudProviderPolicyDecision.Deny;
        try
        {
            string source = router.GetRelativePath(sourcePath);
            string target = router.GetRelativePath(targetPath);
            if (source.Length == 0 || target.Length == 0 || source.IndexOfAny(['/', '\\']) >= 0 || target.IndexOfAny(['/', '\\']) >= 0)
                return CloudProviderPolicyDecision.Deny;
            var routed = router.ResolveCurrentPath(sourcePath);
            if (routed.RelativePath.Length != 0) return CloudProviderPolicyDecision.Deny;
            RootRegistration root = router.GetRegistration(routed.InstanceId, routed.RootKey);
            var current = await catalog.ReadManagedRootRenameAsync(root.RootId, token).ConfigureAwait(false);
            if (current is not { Phase: MirrorPulseRootRenamePhase.Prepared } || current.SourceName != root.DirectoryName ||
                !string.Equals(current.SourceName, source, StringComparison.OrdinalIgnoreCase) || current.TargetName != target)
                return CloudProviderPolicyDecision.Deny;
            var original = await catalog.ReadManagedRootRenameHistoryAsync(current.OperationId, token).ConfigureAwait(false);
            if (original?.Intent != current || original.Proof is null ||
                Convert.ToBase64String(identity.Span) != original.Proof.PlaceholderIdentity)
                return CloudProviderPolicyDecision.Deny;
            // Reuse every original SDK source/target/root/binding/identity decode guard.
            // This callback performs no SDK operation or retrospective proof capture.
            _ = MirrorPulseManagedRootRenameCoordinator.Decode(original, root);
            return CloudProviderPolicyDecision.Allow;
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or InvalidOperationException or
            NotSupportedException or ArgumentException or FormatException)
        {
            return CloudProviderPolicyDecision.Deny;
        }
    }
}
