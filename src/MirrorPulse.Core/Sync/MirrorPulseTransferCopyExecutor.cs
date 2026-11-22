using System.Security.Cryptography;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Sync;

/// <summary>Dispatches the original target copy from retained private bytes and records the correlated receipt.</summary>
public sealed class MirrorPulseTransferCopyExecutor(MirrorPulseProductCatalog catalog,
    IMirrorPulseWorkerUploadTransport uploads, TimeProvider? timeProvider = null)
{
    private readonly MirrorPulseProductCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    private readonly IMirrorPulseWorkerUploadTransport _uploads = uploads ?? throw new ArgumentNullException(nameof(uploads));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    internal IMirrorPulseWorkerMutationTransport? Mutations { get; init; }
    internal MirrorPulseFileTransferRecovery? Recovery { get; init; }

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
        var plan = copy.Plan;
        var target = plan.TargetRoute;
        bool started = copy.CopyStartedAt is not null;
        bool canReplay = Mutations?.SupportsDurableDirectoryCopy(target.InstanceId, target.RootKey) == true;
        if (started && (!copy.CopyUsesWorkerPreparation || !canReplay))
            throw new MirrorPulseMutationAmbiguousException("The original target copy needs its owned acceptance receipt before recovery.");
        if (started && Recovery is { } recovery)
            _ = await _catalog.ReadFileTransferRecoveryMembersAsync(operationId, recovery, token).ConfigureAwait(false);
        bool canPrepare = !started && canReplay && Mutations!.SupportsDurableDirectoryCopyPreparation(target.InstanceId, target.RootKey);
        if (!started && copy.CopyUsesWorkerPreparation && !canPrepare)
            throw new NotSupportedException("The original target must reconnect with its selected preparation capability.");
        if (!privateContent.CanRead || !privateContent.CanSeek || privateContent.CanWrite || privateContent.Length != copy.Plan.Length)
            throw new InvalidDataException("The transfer requires its complete retained read-only private content handle.");
        privateContent.Position = 0;
        string sha256 = Convert.ToHexString(await SHA256.HashDataAsync(privateContent, token).ConfigureAwait(false));
        if (sha256 != copy.Plan.Sha256 || privateContent.Position != copy.Plan.Length || privateContent.Length != copy.Plan.Length)
            throw new InvalidDataException("The private content changed or does not match the original transfer.");
        privateContent.Position = 0;
        string targetPath = plan.TargetRelativePath[(target.DirectoryName.Length + 1)..];
        if (!started)
        {
            if (canPrepare)
            {
                if (!copy.CopyUsesWorkerPreparation)
                    await _catalog.SelectNamespaceTransferCopyPreparationAsync(operationId, token, Recovery).ConfigureAwait(false);
                await Mutations!.PrepareDirectoryCopyAsync(new(target.InstanceId, target.RootKey, targetPath,
                    plan.CopyOperationId, null, plan.Length, plan.Sha256), token).ConfigureAwait(false);
            }
            copy = await _catalog.StartNamespaceTransferCopyAsync(operationId, _time.GetUtcNow(), canPrepare, token, Recovery).ConfigureAwait(false);
        }
        string revision = await _uploads.UploadAsync(new(target.InstanceId, targetPath, null, privateContent,
            plan.Length, plan.CopyOperationId, plan.Sha256, target.RootKey, DestinationMustBeAbsent: true)
        {
            RequireConditionalTargetSupport = true,
            DurableCopyMode = copy.CopyUsesWorkerPreparation ? MirrorPulseDurableDirectoryCopyMode.Resume : MirrorPulseDurableDirectoryCopyMode.Ordinary
        }, token).ConfigureAwait(false);
        // A successful correlated reply must be persisted even if cancellation raced its return.
        return await _catalog.RecordNamespaceTransferCopyAcceptedAsync(operationId, revision, _time.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
    }
}
