using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>The stable managed-root scope whose permission reconciliation must drain before capture.</summary>
public readonly record struct MirrorPulseNamespacePermissionCaptureScope(ulong VolumeSerialNumber,
    Guid SyncRootFileId, RootId RootId);

public sealed partial class MirrorPulseNamespacePermissionCoordinator
{
    private readonly Dictionary<PermissionAdmissionScope, int> _applications = [];
    private readonly HashSet<CaptureAdmission> _captures = [];

    /// <summary>Fences the root and its sync-root parent, drains admitted work, then captures originals.</summary>
    /// <remarks>
    /// The Host must own one coordinator for its permission applications and capture callbacks.
    /// The definition callback runs after drain; new anchors must be freshly inspected then.
    /// Resuming a durable capture preserves its original definition and evidence. Callbacks
    /// may use the catalog to append, seal or cancel bounded pages; failure leaves any durable
    /// open capture fenced for recovery. Do not create captures concurrently outside this owner
    /// or await this coordinator's disposal inside a callback. No physical object freeze,
    /// permission write, whole-tree audit or namespace authorization is supplied by this method.
    /// </remarks>
    public async Task<MirrorPulseNamespacePermissionTree> CaptureNamespacePermissionTreeAsync(
        MirrorPulseNamespacePermissionCaptureScope scope,
        Func<CancellationToken, Task<MirrorPulseNamespacePermissionTreeDefinition>> define,
        Func<MirrorPulseNamespacePermissionTree, CancellationToken, Task> capture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(define);
        ArgumentNullException.ThrowIfNull(capture);
        if (scope.VolumeSerialNumber == 0 || scope.SyncRootFileId == Guid.Empty || scope.RootId.Value == Guid.Empty)
            throw new ArgumentException("The original permission capture scope is incomplete.", nameof(scope));
        cancellationToken.ThrowIfCancellationRequested();
        var admission = new CaptureAdmission(new(scope.VolumeSerialNumber, scope.SyncRootFileId, scope.RootId));
        lock (_admission)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            if (_captures.Any(existing => existing.Scope == admission.Scope))
                throw new InvalidOperationException("This root already has an admitted permission capture.");
            _captures.Add(admission);
            _operations++;
            SignalCaptureDrain(admission);
        }
        try
        {
            await admission.Drained.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Never detach an in-flight callback when cancellation or disposal arrives: it
            // may still own object leases or be committing immutable original evidence.
            MirrorPulseNamespacePermissionTreeDefinition definition = await define(cancellationToken).ConfigureAwait(false);
            ArgumentNullException.ThrowIfNull(definition);
            if (definition.Anchor?.Intent is not { } intent || PermissionAdmissionScope.From(intent) != admission.Scope)
                throw new ArgumentException("The original permission definition does not match the drained root scope.", nameof(define));
            MirrorPulseNamespacePermissionTree retained = await _catalog.CreateNamespacePermissionTreeAsync(
                definition, cancellationToken).ConfigureAwait(false);
            if (retained.Phase != MirrorPulseNamespacePermissionTreePhase.Capturing)
                throw new InvalidOperationException("A terminal permission capture cannot be reopened.");
            await capture(retained, cancellationToken).ConfigureAwait(false);
            // Read back the real durable outcome even if cancellation arrived after commit.
            return await _catalog.ReadNamespacePermissionTreeAsync(definition.ManifestId, CancellationToken.None).ConfigureAwait(false)
                ?? throw new InvalidDataException("The retained permission capture is missing.");
        }
        finally
        {
            lock (_admission)
            {
                _captures.Remove(admission);
                ExitOperation();
            }
        }
    }

    private void EnterApplication(PermissionAdmissionScope scope)
    {
        lock (_admission)
        {
            ObjectDisposedException.ThrowIf(_disposing, this);
            if (_captures.Any(capture => scope.ConflictsWith(capture.Scope)))
                throw new InvalidOperationException("Permission capture has fenced this root or its sync-root parent.");
            _operations++;
            _applications.TryGetValue(scope, out int count);
            _applications[scope] = count + 1;
        }
    }

    private void ExitApplication(PermissionAdmissionScope scope)
    {
        lock (_admission)
        {
            int remaining = _applications[scope] - 1;
            if (remaining == 0) _applications.Remove(scope);
            else _applications[scope] = remaining;
            foreach (CaptureAdmission capture in _captures) SignalCaptureDrain(capture);
            ExitOperation();
        }
    }

    // Called only under _admission. No callback, native operation or catalog transaction
    // runs while this lock is held; unrelated roots remain independently admissible.
    private void SignalCaptureDrain(CaptureAdmission capture)
    {
        if (!_applications.Keys.Any(scope => scope.ConflictsWith(capture.Scope)))
            capture.Drained.TrySetResult();
    }

    private void ExitOperation()
    {
        _operations--;
        if (_disposing && _operations == 0) _drained?.TrySetResult();
    }

    private sealed class CaptureAdmission(PermissionAdmissionScope scope)
    {
        public PermissionAdmissionScope Scope { get; } = scope;
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly record struct PermissionAdmissionScope(ulong VolumeSerialNumber, Guid SyncRootFileId, RootId? RootId)
    {
        public static PermissionAdmissionScope From(MirrorPulseNamespacePermissionIntent intent) =>
            new(intent.LocalObject.VolumeSerialNumber, intent.LocalObject.SyncRootFileId, intent.RootId);

        public bool ConflictsWith(PermissionAdmissionScope capture) =>
            VolumeSerialNumber == capture.VolumeSerialNumber && SyncRootFileId == capture.SyncRootFileId &&
            (RootId is null || RootId == capture.RootId);
    }
}
