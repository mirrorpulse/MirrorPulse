using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed partial class MirrorPulseNamespaceMemberMoveCoordinator
{
    /// <summary>Retains the original remote entry with the current native move admission, without moving an object.</summary>
    /// <remarks>The original batch must later own native execution and settlement. Ordinary ExecuteAsync refuses this admission.</remarks>
    public ValueTask<MirrorPulseNamespaceMemberMoveIntent> PrepareRemoteAsync(Guid operationId, Guid memberId,
        Guid targetParentMemberId, string targetName, InstanceId instanceId, CloudRemoteChangeBatch batch, int ordinal,
        CancellationToken cancellationToken = default)
    {
        var request = new RemoteMoveRequest(instanceId, batch, ordinal);
        _ = RequireRemoteChange(request);
        return PrepareCoreAsync(operationId, memberId, targetParentMemberId, targetName, request, cancellationToken);
    }

    private sealed record RemoteMoveRequest(InstanceId InstanceId, CloudRemoteChangeBatch Batch, int Ordinal);

    private static CloudRemoteChange RequireRemoteChange(RemoteMoveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Batch);
        if (request.InstanceId.Value == Guid.Empty || !request.Batch.BatchId.StartsWith($"{request.InstanceId}/", StringComparison.Ordinal))
            throw new InvalidDataException("A remote move batch requires its owning Adapter instance.");
        if (request.Ordinal < 0 || request.Ordinal >= request.Batch.Changes.Count)
            throw new ArgumentOutOfRangeException(nameof(request));
        var change = request.Batch.Changes[request.Ordinal];
        if (change.Kind != CloudRemoteChangeKind.Move || change.ItemId is null || change.PreviousRelativePath is null)
            throw new InvalidDataException("A remote move admission requires an explicit public item identity and source path.");
        return change;
    }

    private static MirrorPulseNamespaceRemoteMoveReference Reference(RemoteMoveRequest request, string? originalRevision)
    {
        var change = RequireRemoteChange(request);
        return new(1, request.InstanceId, request.Batch.BatchId, Convert.ToHexString(request.Batch.Fingerprint.Span),
            request.Batch.Changes.Count, request.Ordinal, change.ChangeId, change.PreviousRemoteRevision,
            change.RemoteRevision, originalRevision);
    }

    private static void RequireRemoteRequestReplay(MirrorPulseNamespaceMemberMoveIntent retained, RemoteMoveRequest? request)
    {
        if (request is null)
        {
            if (retained.RemoteEntry is not null) throw new InvalidOperationException("The move ID is owned by an original remote batch.");
            return;
        }
        if (retained.RemoteEntry is not { } remote || remote != Reference(request, remote.OriginalRemoteRevision))
            throw new InvalidOperationException("The retained move belongs to a different remote batch entry.");
        RequireRemoteChangeIdentity(retained, RequireRemoteChange(request));
    }

    private MirrorPulseNamespaceMemberMoveIntent AttachRemoteReference(MirrorPulseNamespaceMemberMoveIntent intent,
        RemoteMoveRequest? request, CloudPlaceholderIdentity originalIdentity)
    {
        if (request is null) return intent;
        var change = RequireRemoteChange(request);
        RequireRemoteChangeIdentity(intent, change);
        if (originalIdentity.ItemId != intent.ItemId || originalIdentity.RemoteId != intent.RemoteId)
            throw new InvalidDataException("The remote move lost its original native public identity.");
        var source = _router.Resolve(intent.Source.RelativePath, originalIdentity.Encode());
        var target = _router.Resolve(intent.Target.RelativePath, originalIdentity.Encode());
        if (source.InstanceId != request.InstanceId || target.InstanceId != request.InstanceId ||
            _router.GetRegistration(source.InstanceId, source.RootKey).RootId != intent.RootId ||
            _router.GetRegistration(target.InstanceId, target.RootKey).RootId != intent.RootId)
            throw new InvalidDataException("A remote move admission must stay within its original managed root.");
        return intent with { Version = 2, RemoteEntry = Reference(request, originalIdentity.RemoteRevision) };
    }

    private static void RequireRemoteChangeIdentity(MirrorPulseNamespaceMemberMoveIntent intent, CloudRemoteChange change)
    {
        if (change.ItemId != intent.ItemId || change.RemoteId != intent.RemoteId ||
            !string.Equals(change.PreviousRelativePath?.Replace('\\', '/'), intent.Source.RelativePath, StringComparison.OrdinalIgnoreCase) ||
            change.RelativePath.Replace('\\', '/') != intent.Target.RelativePath ||
            (change.ItemKind == CloudItemKind.Directory) != (intent.DirectoryMoveEvidence is not null))
            throw new InvalidDataException("The original remote entry disagrees with the admitted member and endpoints.");
    }
}
