using System.Security.Cryptography;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Sync;

/// <summary>Dispatches the first target copy from retained private bytes. It never deletes or acknowledges the source.</summary>
public sealed class MirrorPulseTransferCopyExecutor(MirrorPulseProductCatalog catalog,
    IMirrorPulseWorkerUploadTransport uploads, TimeProvider? timeProvider = null)
{
    private readonly MirrorPulseProductCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IMirrorPulseWorkerUploadTransport _uploads = uploads ?? throw new ArgumentNullException(nameof(uploads));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <remarks>The Host must hold its actor, both ordered instance admissions and the stable private read handle.
    /// The stream must remain immutable throughout hashing and upload. This method retains no filesystem path,
    /// performs no source readback/deletion and cannot turn matching target content into an owned receipt.</remarks>
    public async Task<MirrorPulseNamespaceTransferCopy> CopyAsync(Guid operationId, Stream privateContent,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(privateContent);
        var copy = await _catalog.ReadNamespaceTransferAsync(operationId, token).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The original transfer is missing.");
        if (copy.CopyAcceptedAt is not null) return copy;
        if (copy.CopyStartedAt is not null) throw new MirrorPulseMutationAmbiguousException("The original target copy needs its owned acceptance receipt before recovery.");
        if (!privateContent.CanRead || !privateContent.CanSeek || privateContent.CanWrite || privateContent.Length != copy.Plan.Length)
            throw new InvalidDataException("The transfer requires its complete retained read-only private content handle.");
        privateContent.Position = 0;
        string sha256 = Convert.ToHexString(await SHA256.HashDataAsync(privateContent, token).ConfigureAwait(false));
        if (sha256 != copy.Plan.Sha256 || privateContent.Position != copy.Plan.Length || privateContent.Length != copy.Plan.Length)
            throw new InvalidDataException("The private content changed or does not match the original transfer.");
        privateContent.Position = 0;
        copy = await _catalog.StartNamespaceTransferCopyAsync(operationId, _time.GetUtcNow(), token).ConfigureAwait(false);
        var plan = copy.Plan;
        string targetPath = plan.TargetRelativePath[(plan.TargetRoute.DirectoryName.Length + 1)..];
        string revision = await _uploads.UploadAsync(new(plan.TargetRoute.InstanceId, targetPath, null, privateContent,
            plan.Length, plan.CopyOperationId, plan.Sha256, plan.TargetRoute.RootKey, DestinationMustBeAbsent: true)
        { RequireConditionalTargetSupport = true }, token).ConfigureAwait(false);
        // A successful correlated reply must be persisted even if cancellation raced its return.
        return await _catalog.RecordNamespaceTransferCopyAcceptedAsync(operationId, revision, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
    }
}
