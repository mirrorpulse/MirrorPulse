using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

internal sealed record MirrorPulseNamespaceReplacementPermissionResult(CloudProtectedLocalOperationResult Receipt,
    bool ExistingPlaceholderIdentityVerified);

/// <summary>Selects existing-file access without proposing a new accepted identity for local conversion.</summary>
[SupportedOSPlatform("windows10.0.26100")]
internal static class MirrorPulseNamespaceReplacementPermissionScope
{
    internal static async Task<MirrorPulseNamespaceReplacementPermissionResult> RunAsync(CloudFile file,
        MirrorPulseRootRouter router, MirrorPulseLocalFileBinding originalBinding, CloudPlaceholderIdentity originalIdentity,
        Func<IMirrorPulseNamespacePermissionLease, CancellationToken, ValueTask> callback, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(file); ArgumentNullException.ThrowIfNull(originalIdentity);
        var before = await file.InspectAsync(token).ConfigureAwait(false);
        if (!before.Exists || before.IsTombstone || before.LocalBinding is not { } binding ||
            new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) != originalBinding)
            throw new InvalidDataException("Replacement permissions require the retained original file binding.");
        byte[] expected = originalIdentity.Encode();
        if (before.IsPlaceholder && !before.PlaceholderIdentity.Span.SequenceEqual(expected))
            throw new InvalidDataException("Existing replacement permissions require the admitted original public identity.");
        if (originalIdentity.RemoteRevision is null)
        {
            // Preserve the original unaccepted local-conversion boundary and its receipt.
            var converted = await MirrorPulseWindowsNamespacePermissionLease.RunProtectedAsync(file, router,
                originalBinding, originalIdentity, callback, token).ConfigureAwait(false);
            return new(converted, false);
        }
        if (!before.IsPlaceholder || !before.PlaceholderIdentity.Span.SequenceEqual(expected))
            throw new InvalidDataException("Existing replacement permissions require the admitted original public identity.");
        bool verified = false;
        var receipt = await MirrorPulseWindowsNamespacePermissionLease.RunOwnedProtectedAsync(file, router, originalBinding,
            async (lease, stop) =>
            {
                var current = await lease.InspectLocalIdentityAsync(stop).ConfigureAwait(false);
                if (!current.IsPlaceholder || current.PlaceholderIdentity is null || !current.PlaceholderIdentity.Encode().AsSpan().SequenceEqual(expected))
                    throw new InvalidDataException("The retained original identity changed before the replacement permission callback.");
                verified = true;
                await callback(lease, stop).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
        return new(receipt, verified);
    }

    internal static bool HasCompletedPermissionProof(MirrorPulseNamespaceReplacementPermissionResult result)
    {
        var receipt = result.Receipt;
        return receipt.Outcome == CloudProtectedLocalOperationOutcome.Completed && receipt.CallbackStarted &&
            receipt.CallbackCompleted && receipt.Drained && receipt.AccessDescriptorApplied && receipt.AccessDescriptorReadBack &&
            receipt.Error?.HResult is null or 0 &&
            (result.ExistingPlaceholderIdentityVerified
                ? !receipt.NativeIdentityPrepared && !receipt.NativeConverted && !receipt.DurableProjectionCommitted
                : receipt.NativeIdentityPrepared && receipt.DurableProjectionCommitted);
    }
}
