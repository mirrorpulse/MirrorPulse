using System.Runtime.Versioning;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Host;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Keeps source access and Worker RPC outside the Host's namespace role.</summary>
/// <remarks>
/// The Host owns the execution session and the underlying transports. A range body is fully
/// read and disposed as the normal user before a detached, bounded buffer is returned. Merely
/// opening a lazy source stream in the normal context would leak the role into later I/O.
/// This boundary does not enable protection or authorize local namespace changes.
/// </remarks>
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNormalUserWorkerTransport : IMirrorPulseWorkerRangeTransport,
    IMirrorPulseWorkerUploadTransport, IMirrorPulseWorkerStatTransport,
    IMirrorPulseWorkerDirectoryPageSource, IMirrorPulseWorkerMutationTransport
{
    private readonly MirrorPulseNamespaceExecutionSession _session;
    private readonly IMirrorPulseWorkerRangeTransport _ranges;
    private readonly IMirrorPulseWorkerUploadTransport _uploads;
    private readonly IMirrorPulseWorkerStatTransport _stats;
    private readonly IMirrorPulseWorkerDirectoryPageSource _directories;
    private readonly IMirrorPulseWorkerMutationTransport _mutations;

    public MirrorPulseNormalUserWorkerTransport(MirrorPulseNamespaceExecutionSession session,
        IMirrorPulseWorkerRangeTransport ranges, IMirrorPulseWorkerUploadTransport uploads,
        IMirrorPulseWorkerStatTransport stats, IMirrorPulseWorkerDirectoryPageSource directories,
        IMirrorPulseWorkerMutationTransport mutations)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _ranges = ranges ?? throw new ArgumentNullException(nameof(ranges));
        _uploads = uploads ?? throw new ArgumentNullException(nameof(uploads));
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _directories = directories ?? throw new ArgumentNullException(nameof(directories));
        _mutations = mutations ?? throw new ArgumentNullException(nameof(mutations));
    }

    public async ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Offset);
        if (request.Length is < 1 or > AdapterWorkerReadRangeClient.MaximumRangeBytes)
            throw new ArgumentOutOfRangeException(nameof(request), "A source range must fit the Worker range limit.");
        return await _session.RunNormalUserOperationAsync<Stream>(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using Stream source = await _ranges.ReadRangeAsync(request, cancellationToken).ConfigureAwait(false);
            if (!source.CanRead) throw new InvalidDataException("The Worker returned an unreadable source range.");
            byte[] content = new byte[checked((int)request.Length)];
            await source.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
            return new MemoryStream(content, writable: false);
        }).ConfigureAwait(false);
    }

    public async ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken cancellationToken) =>
        await _session.RunNormalUserOperationAsync(async () =>
            await _uploads.UploadAsync(request, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    public async ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken cancellationToken) =>
        await _session.RunNormalUserOperationAsync(async () =>
            await _stats.StatAsync(request, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    public async ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(
        MirrorPulseWorkerDirectoryPageRequest request, CancellationToken cancellationToken) =>
        await _session.RunNormalUserOperationAsync(async () =>
            await _directories.ReadDirectoryPageAsync(request, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    public async ValueTask<string> CreateDirectoryAsync(MirrorPulseWorkerCreateDirectoryRequest request,
        CancellationToken cancellationToken) =>
        await _session.RunNormalUserOperationAsync(async () =>
            await _mutations.CreateDirectoryAsync(request, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    public async ValueTask<string?> DeleteAsync(MirrorPulseWorkerDeleteRequest request, CancellationToken cancellationToken) =>
        await _session.RunNormalUserOperationAsync(async () =>
            await _mutations.DeleteAsync(request, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);

    public async ValueTask<string> MoveAsync(MirrorPulseWorkerMoveRequest request, CancellationToken cancellationToken) =>
        await _session.RunNormalUserOperationAsync(async () =>
            await _mutations.MoveAsync(request, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
}
